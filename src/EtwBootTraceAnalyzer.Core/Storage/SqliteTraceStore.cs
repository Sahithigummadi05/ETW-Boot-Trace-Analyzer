using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;
using Microsoft.Data.Sqlite;

namespace EtwBootTraceAnalyzer.Core.Storage;

/// <summary>
/// Persists a <see cref="BootTrace"/> to SQLite and reloads it. One database can hold many
/// sessions (rows are keyed by session name), which is what lets the CLI compare boots or
/// re-run analysis without re-ingesting the original ETL.
///
/// Inserts run inside a single transaction with one prepared command per table, its parameter
/// *values* swapped per row rather than the parameters themselves recreated. Measured against a
/// 2M-event synthetic trace (`etwboot benchmark`) at ~280K events/sec end to end - see the
/// long comment on <see cref="InsertRows{TEvent}"/> for what was tried and measured along the
/// way to that number, including an attempted batched-insert version that turned out slower.
/// </summary>
public sealed class SqliteTraceStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTraceStore(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        // WAL + synchronous=NORMAL is SQLite's own recommended combination for bulk-write
        // workloads: still crash-safe (WAL is only lost on an OS-level crash, not a process
        // crash), just without fsync-ing on every one of a ~2M-row import's inserts.
        using (var pragmaCmd = _connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragmaCmd.ExecuteNonQuery();
        }

        CreateSchema();
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS session (
                session_name TEXT PRIMARY KEY,
                boot_start_utc TEXT NOT NULL,
                cpu_sample_interval_ms REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS process_start (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                process_id INTEGER NOT NULL, parent_process_id INTEGER NOT NULL,
                image_file_name TEXT NOT NULL, command_line TEXT
            );
            CREATE TABLE IF NOT EXISTS process_stop (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                process_id INTEGER NOT NULL, exit_status INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cpu_sample (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                process_id INTEGER NOT NULL, thread_id INTEGER NOT NULL,
                processor_number INTEGER NOT NULL, instruction_pointer INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS context_switch (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                processor_number INTEGER NOT NULL,
                old_thread_id INTEGER NOT NULL, old_process_id INTEGER NOT NULL,
                new_thread_id INTEGER NOT NULL, new_process_id INTEGER NOT NULL,
                old_thread_wait_reason TEXT NOT NULL, new_thread_wait_time_ms REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ready_thread (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                awakened_thread_id INTEGER NOT NULL, awakened_process_id INTEGER NOT NULL,
                readying_thread_id INTEGER NOT NULL, readying_process_id INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS disk_io (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                kind TEXT NOT NULL, issuing_process_id INTEGER NOT NULL, issuing_thread_id INTEGER NOT NULL,
                duration_ms REAL NOT NULL, byte_offset INTEGER NOT NULL, transfer_size_bytes INTEGER NOT NULL,
                file_name TEXT, disk_number INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dpc_isr (
                session_name TEXT NOT NULL, timestamp_ms REAL NOT NULL,
                kind TEXT NOT NULL, processor_number INTEGER NOT NULL,
                duration_ms REAL NOT NULL, routine_module TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_process_start_session ON process_start(session_name);
            CREATE INDEX IF NOT EXISTS ix_process_stop_session ON process_stop(session_name);
            CREATE INDEX IF NOT EXISTS ix_cpu_sample_session ON cpu_sample(session_name);
            CREATE INDEX IF NOT EXISTS ix_context_switch_session ON context_switch(session_name);
            CREATE INDEX IF NOT EXISTS ix_ready_thread_session ON ready_thread(session_name);
            CREATE INDEX IF NOT EXISTS ix_disk_io_session ON disk_io(session_name);
            CREATE INDEX IF NOT EXISTS ix_dpc_isr_session ON dpc_isr(session_name);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Save(BootTrace trace)
    {
        using var transaction = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO session (session_name, boot_start_utc, cpu_sample_interval_ms)
                VALUES ($name, $boot, $interval);
                """;
            cmd.Parameters.AddWithValue("$name", trace.SessionName);
            cmd.Parameters.AddWithValue("$boot", trace.BootStartUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$interval", trace.CpuSampleIntervalMs);
            cmd.ExecuteNonQuery();
        }

        DeleteSessionRows(trace.SessionName, transaction);

        InsertRows(transaction, "process_start", trace.SessionName, trace.ProcessStarts,
            ["process_id", "parent_process_id", "image_file_name", "command_line"],
            e => [e.ProcessId, e.ParentProcessId, e.ImageFileName, e.CommandLine]);

        InsertRows(transaction, "process_stop", trace.SessionName, trace.ProcessStops,
            ["process_id", "exit_status"],
            e => [e.ProcessId, e.ExitStatus]);

        InsertRows(transaction, "cpu_sample", trace.SessionName, trace.CpuSamples,
            ["process_id", "thread_id", "processor_number", "instruction_pointer"],
            e => [e.ProcessId, e.ThreadId, e.ProcessorNumber, (long)e.InstructionPointer]);

        InsertRows(transaction, "context_switch", trace.SessionName, trace.ContextSwitches,
            ["processor_number", "old_thread_id", "old_process_id", "new_thread_id", "new_process_id", "old_thread_wait_reason", "new_thread_wait_time_ms"],
            e => [e.ProcessorNumber, e.OldThreadId, e.OldProcessId, e.NewThreadId, e.NewProcessId, e.OldThreadWaitReason, e.NewThreadWaitTimeMs]);

        InsertRows(transaction, "ready_thread", trace.SessionName, trace.ReadyThreadEvents,
            ["awakened_thread_id", "awakened_process_id", "readying_thread_id", "readying_process_id"],
            e => [e.AwakenedThreadId, e.AwakenedProcessId, e.ReadyingThreadId, e.ReadyingProcessId]);

        InsertRows(transaction, "disk_io", trace.SessionName, trace.DiskIoEvents,
            ["kind", "issuing_process_id", "issuing_thread_id", "duration_ms", "byte_offset", "transfer_size_bytes", "file_name", "disk_number"],
            e => [e.Kind.ToString(), e.IssuingProcessId, e.IssuingThreadId, e.DurationMs, e.ByteOffset, e.TransferSizeBytes, e.FileName, e.DiskNumber]);

        InsertRows(transaction, "dpc_isr", trace.SessionName, trace.DpcIsrEvents,
            ["kind", "processor_number", "duration_ms", "routine_module"],
            e => [e.Kind.ToString(), e.ProcessorNumber, e.DurationMs, e.RoutineModule]);

        transaction.Commit();
    }

    private void DeleteSessionRows(string sessionName, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "process_start", "process_stop", "cpu_sample", "context_switch", "ready_thread", "disk_io", "dpc_isr" })
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {table} WHERE session_name = $s";
            cmd.Parameters.AddWithValue("$s", sessionName);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One prepared command, reused across every row of a table, with parameter *values* (not
    /// parameter objects) replaced per row via direct array indexing.
    ///
    /// This looks like the "obviously slower" option next to a multi-row batched
    /// "INSERT ... VALUES (r0), (r1), ..." statement, and that was the first thing tried here -
    /// but measured on a ~2M-row synthetic trace, batching was worse, not better: 25-row batches
    /// matched this version's ~9s/~2M rows, and 200-row batches made it ~4x *slower* (~36s).
    /// The likely cause is that Microsoft.Data.Sqlite's per-statement bind/step overhead doesn't
    /// dominate here the way it would over a real network round trip - SQLite is in-process - so
    /// collapsing many round trips into one large statement mostly just made each statement's
    /// VDBE program bigger and slower to run, with no corresponding round-trip savings to pay for
    /// it. Reused single-row execution, kept simple, is the empirically faster - and much
    /// simpler - approach for this workload. Don't reintroduce batching here without re-measuring.
    /// </summary>
    private void InsertRows<TEvent>(
        SqliteTransaction transaction,
        string tableName,
        string sessionName,
        IReadOnlyList<TEvent> rows,
        string[] valueColumns,
        Func<TEvent, object?[]> toValues)
        where TEvent : BootEvent
    {
        if (rows.Count == 0)
        {
            return;
        }

        var allColumns = new string[valueColumns.Length + 2];
        allColumns[0] = "session_name";
        allColumns[1] = "timestamp_ms";
        Array.Copy(valueColumns, 0, allColumns, 2, valueColumns.Length);

        var columnList = string.Join(", ", allColumns);
        var placeholders = string.Join(", ", allColumns.Select((_, i) => $"$p{i}"));

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT INTO {tableName} ({columnList}) VALUES ({placeholders})";

        var parameters = new SqliteParameter[allColumns.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = new SqliteParameter($"$p{i}", DBNull.Value);
        }
        cmd.Parameters.AddRange(parameters);
        cmd.Prepare();

        foreach (var row in rows)
        {
            parameters[0].Value = sessionName;
            parameters[1].Value = row.TimestampMs;
            var values = toValues(row);
            for (var i = 0; i < values.Length; i++)
            {
                parameters[i + 2].Value = values[i] ?? DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }
    }

    public BootTrace Load(string sessionName)
    {
        using var sessionCmd = _connection.CreateCommand();
        sessionCmd.CommandText = "SELECT boot_start_utc, cpu_sample_interval_ms FROM session WHERE session_name = $s";
        sessionCmd.Parameters.AddWithValue("$s", sessionName);
        using var sessionReader = sessionCmd.ExecuteReader();
        if (!sessionReader.Read())
        {
            throw new InvalidOperationException($"No session named '{sessionName}' found in the trace store.");
        }
        var bootStartUtc = DateTime.Parse(sessionReader.GetString(0));
        var cpuSampleIntervalMs = sessionReader.GetDouble(1);
        sessionReader.Close();

        return new BootTrace
        {
            SessionName = sessionName,
            BootStartUtc = bootStartUtc,
            CpuSampleIntervalMs = cpuSampleIntervalMs,
            ProcessStarts = Query(sessionName,
                "SELECT timestamp_ms, process_id, parent_process_id, image_file_name, command_line FROM process_start WHERE session_name = $s ORDER BY timestamp_ms",
                r => new ProcessStartEvent
                {
                    TimestampMs = r.GetDouble(0),
                    ProcessId = r.GetInt32(1),
                    ParentProcessId = r.GetInt32(2),
                    ImageFileName = r.GetString(3),
                    CommandLine = r.IsDBNull(4) ? null : r.GetString(4),
                }),
            ProcessStops = Query(sessionName,
                "SELECT timestamp_ms, process_id, exit_status FROM process_stop WHERE session_name = $s ORDER BY timestamp_ms",
                r => new ProcessStopEvent { TimestampMs = r.GetDouble(0), ProcessId = r.GetInt32(1), ExitStatus = r.GetInt32(2) }),
            CpuSamples = Query(sessionName,
                "SELECT timestamp_ms, process_id, thread_id, processor_number, instruction_pointer FROM cpu_sample WHERE session_name = $s ORDER BY timestamp_ms",
                r => new CpuSampleEvent
                {
                    TimestampMs = r.GetDouble(0),
                    ProcessId = r.GetInt32(1),
                    ThreadId = r.GetInt32(2),
                    ProcessorNumber = r.GetInt32(3),
                    InstructionPointer = (ulong)r.GetInt64(4),
                }),
            ContextSwitches = Query(sessionName,
                "SELECT timestamp_ms, processor_number, old_thread_id, old_process_id, new_thread_id, new_process_id, old_thread_wait_reason, new_thread_wait_time_ms FROM context_switch WHERE session_name = $s ORDER BY timestamp_ms",
                r => new ContextSwitchEvent
                {
                    TimestampMs = r.GetDouble(0),
                    ProcessorNumber = r.GetInt32(1),
                    OldThreadId = r.GetInt32(2),
                    OldProcessId = r.GetInt32(3),
                    NewThreadId = r.GetInt32(4),
                    NewProcessId = r.GetInt32(5),
                    OldThreadWaitReason = r.GetString(6),
                    NewThreadWaitTimeMs = r.GetDouble(7),
                }),
            ReadyThreadEvents = Query(sessionName,
                "SELECT timestamp_ms, awakened_thread_id, awakened_process_id, readying_thread_id, readying_process_id FROM ready_thread WHERE session_name = $s ORDER BY timestamp_ms",
                r => new ReadyThreadEvent
                {
                    TimestampMs = r.GetDouble(0),
                    AwakenedThreadId = r.GetInt32(1),
                    AwakenedProcessId = r.GetInt32(2),
                    ReadyingThreadId = r.GetInt32(3),
                    ReadyingProcessId = r.GetInt32(4),
                }),
            DiskIoEvents = Query(sessionName,
                "SELECT timestamp_ms, kind, issuing_process_id, issuing_thread_id, duration_ms, byte_offset, transfer_size_bytes, file_name, disk_number FROM disk_io WHERE session_name = $s ORDER BY timestamp_ms",
                r => new DiskIoEvent
                {
                    TimestampMs = r.GetDouble(0),
                    Kind = Enum.Parse<DiskIoKind>(r.GetString(1)),
                    IssuingProcessId = r.GetInt32(2),
                    IssuingThreadId = r.GetInt32(3),
                    DurationMs = r.GetDouble(4),
                    ByteOffset = r.GetInt64(5),
                    TransferSizeBytes = r.GetInt32(6),
                    FileName = r.IsDBNull(7) ? null : r.GetString(7),
                    DiskNumber = r.GetInt32(8),
                }),
            DpcIsrEvents = Query(sessionName,
                "SELECT timestamp_ms, kind, processor_number, duration_ms, routine_module FROM dpc_isr WHERE session_name = $s ORDER BY timestamp_ms",
                r => new DpcIsrEvent
                {
                    TimestampMs = r.GetDouble(0),
                    Kind = Enum.Parse<InterruptKind>(r.GetString(1)),
                    ProcessorNumber = r.GetInt32(2),
                    DurationMs = r.GetDouble(3),
                    RoutineModule = r.GetString(4),
                }),
        };
    }

    private List<T> Query<T>(string sessionName, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", sessionName);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
