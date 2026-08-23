using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;
using Microsoft.Data.Sqlite;

namespace EtwBootTraceAnalyzer.Core.Storage;

/// <summary>
/// Persists a <see cref="BootTrace"/> to SQLite and reloads it. One database can hold many
/// sessions (rows are keyed by session name), which is what lets the CLI compare boots or
/// re-run analysis without re-ingesting the original ETL.
///
/// Inserts run inside a single transaction with a prepared, reused command per table - the
/// pattern that keeps a ~2M-row session import from taking minutes instead of seconds.
/// </summary>
public sealed class SqliteTraceStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTraceStore(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
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

        InsertRows(transaction, trace.SessionName, trace.ProcessStarts,
            "INSERT INTO process_start (session_name, timestamp_ms, process_id, parent_process_id, image_file_name, command_line) VALUES ($s, $ts, $pid, $ppid, $img, $cmd)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$pid", e.ProcessId);
                cmd.Parameters.AddWithValue("$ppid", e.ParentProcessId);
                cmd.Parameters.AddWithValue("$img", e.ImageFileName);
                cmd.Parameters.AddWithValue("$cmd", (object?)e.CommandLine ?? DBNull.Value);
            });

        InsertRows(transaction, trace.SessionName, trace.ProcessStops,
            "INSERT INTO process_stop (session_name, timestamp_ms, process_id, exit_status) VALUES ($s, $ts, $pid, $exit)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$pid", e.ProcessId);
                cmd.Parameters.AddWithValue("$exit", e.ExitStatus);
            });

        InsertRows(transaction, trace.SessionName, trace.CpuSamples,
            "INSERT INTO cpu_sample (session_name, timestamp_ms, process_id, thread_id, processor_number, instruction_pointer) VALUES ($s, $ts, $pid, $tid, $cpu, $ip)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$pid", e.ProcessId);
                cmd.Parameters.AddWithValue("$tid", e.ThreadId);
                cmd.Parameters.AddWithValue("$cpu", e.ProcessorNumber);
                cmd.Parameters.AddWithValue("$ip", (long)e.InstructionPointer);
            });

        InsertRows(transaction, trace.SessionName, trace.ContextSwitches,
            "INSERT INTO context_switch (session_name, timestamp_ms, processor_number, old_thread_id, old_process_id, new_thread_id, new_process_id, old_thread_wait_reason, new_thread_wait_time_ms) VALUES ($s, $ts, $cpu, $otid, $opid, $ntid, $npid, $reason, $wait)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$cpu", e.ProcessorNumber);
                cmd.Parameters.AddWithValue("$otid", e.OldThreadId);
                cmd.Parameters.AddWithValue("$opid", e.OldProcessId);
                cmd.Parameters.AddWithValue("$ntid", e.NewThreadId);
                cmd.Parameters.AddWithValue("$npid", e.NewProcessId);
                cmd.Parameters.AddWithValue("$reason", e.OldThreadWaitReason);
                cmd.Parameters.AddWithValue("$wait", e.NewThreadWaitTimeMs);
            });

        InsertRows(transaction, trace.SessionName, trace.ReadyThreadEvents,
            "INSERT INTO ready_thread (session_name, timestamp_ms, awakened_thread_id, awakened_process_id, readying_thread_id, readying_process_id) VALUES ($s, $ts, $atid, $apid, $rtid, $rpid)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$atid", e.AwakenedThreadId);
                cmd.Parameters.AddWithValue("$apid", e.AwakenedProcessId);
                cmd.Parameters.AddWithValue("$rtid", e.ReadyingThreadId);
                cmd.Parameters.AddWithValue("$rpid", e.ReadyingProcessId);
            });

        InsertRows(transaction, trace.SessionName, trace.DiskIoEvents,
            "INSERT INTO disk_io (session_name, timestamp_ms, kind, issuing_process_id, issuing_thread_id, duration_ms, byte_offset, transfer_size_bytes, file_name, disk_number) VALUES ($s, $ts, $kind, $pid, $tid, $dur, $off, $size, $file, $disk)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$kind", e.Kind.ToString());
                cmd.Parameters.AddWithValue("$pid", e.IssuingProcessId);
                cmd.Parameters.AddWithValue("$tid", e.IssuingThreadId);
                cmd.Parameters.AddWithValue("$dur", e.DurationMs);
                cmd.Parameters.AddWithValue("$off", e.ByteOffset);
                cmd.Parameters.AddWithValue("$size", e.TransferSizeBytes);
                cmd.Parameters.AddWithValue("$file", (object?)e.FileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$disk", e.DiskNumber);
            });

        InsertRows(transaction, trace.SessionName, trace.DpcIsrEvents,
            "INSERT INTO dpc_isr (session_name, timestamp_ms, kind, processor_number, duration_ms, routine_module) VALUES ($s, $ts, $kind, $cpu, $dur, $mod)",
            (cmd, e) =>
            {
                cmd.Parameters.AddWithValue("$kind", e.Kind.ToString());
                cmd.Parameters.AddWithValue("$cpu", e.ProcessorNumber);
                cmd.Parameters.AddWithValue("$dur", e.DurationMs);
                cmd.Parameters.AddWithValue("$mod", e.RoutineModule);
            });

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

    private void InsertRows<TEvent>(
        SqliteTransaction transaction,
        string sessionName,
        IReadOnlyList<TEvent> rows,
        string sqlWithSAndTsParams,
        Action<SqliteCommand, TEvent> bindRow)
        where TEvent : BootEvent
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sqlWithSAndTsParams;

        foreach (var row in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$s", sessionName);
            cmd.Parameters.AddWithValue("$ts", row.TimestampMs);
            bindRow(cmd, row);
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
