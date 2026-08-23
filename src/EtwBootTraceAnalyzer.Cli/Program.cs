using System.Diagnostics;
using EtwBootTraceAnalyzer.Cli.Reporting;
using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Ingestion;
using EtwBootTraceAnalyzer.Core.Model;
using EtwBootTraceAnalyzer.Core.Storage;
using EtwBootTraceAnalyzer.Core.Synthetic;
#if ETW_CAPTURE
using EtwBootTraceAnalyzer.Capture;
#endif

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "demo":
            return RunDemo(args[1..]);
        case "analyze":
            return RunAnalyze(args[1..]);
        case "compare":
            return RunCompare(args[1..]);
        case "benchmark":
            return RunBenchmark(args[1..]);
#if ETW_CAPTURE
        case "capture":
            return RunCapture(args[1..]);
        case "import-etl":
            return RunImportEtl(args[1..]);
#endif
        case "-h" or "--help" or "help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int RunDemo(string[] args)
{
    var trace = SyntheticBootTraceGenerator.Generate();
    var report = Analyze(trace, milestoneProcess: null);
    if (report is not null)
    {
        ConsoleReportPrinter.Print(trace, report);
    }

    var saveJson = GetOption(args, "--save-json");
    if (saveJson is not null)
    {
        JsonTraceIo.Export(trace, saveJson);
        Console.WriteLine($"\nSaved synthetic trace to {saveJson}");
    }

    var saveSqlite = GetOption(args, "--save-sqlite");
    if (saveSqlite is not null)
    {
        using var store = new SqliteTraceStore(saveSqlite);
        store.Save(trace);
        Console.WriteLine($"\nSaved synthetic trace to {saveSqlite} (session '{trace.SessionName}')");
    }

    return 0;
}

static int RunAnalyze(string[] args)
{
    var trace = LoadTraceOrNull(args);
    if (trace is null)
    {
        return 1;
    }

    var report = Analyze(trace, GetOption(args, "--milestone-process"));
    if (report is null)
    {
        return 1;
    }

    ConsoleReportPrinter.Print(trace, report);
    return 0;
}

static int RunCompare(string[] args)
{
    var beforePath = GetOption(args, "--before") ?? throw new ArgumentException("--before <path.json|path.db> is required.");
    var afterPath = GetOption(args, "--after") ?? throw new ArgumentException("--after <path.json|path.db> is required.");
    var milestoneProcess = GetOption(args, "--milestone-process");

    var beforeTrace = LoadTraceFromPath(beforePath, GetOption(args, "--before-session"));
    var afterTrace = LoadTraceFromPath(afterPath, GetOption(args, "--after-session"));

    var beforeReport = Analyze(beforeTrace, milestoneProcess);
    var afterReport = Analyze(afterTrace, milestoneProcess);
    if (beforeReport is null || afterReport is null)
    {
        return 1;
    }

    var comparison = TraceComparer.Compare(beforeReport, afterReport);
    ConsoleReportPrinter.PrintComparison(comparison);
    return 0;
}

static int RunBenchmark(string[] args)
{
    var eventCount = long.Parse(GetOption(args, "--events") ?? "2000000");

    Console.WriteLine($"Generating a synthetic trace targeting ~{eventCount:N0} events...");
    var generated = LargeScaleTraceGenerator.Generate(eventCount);
    var trace = generated.Trace;
    Console.WriteLine($"  {trace.TotalEventCount:N0} events generated and timestamp-sorted in {generated.GenerationTime.TotalSeconds:F2}s");

    var sw = Stopwatch.StartNew();
    var report = new BootAnalysisEngine().Analyze(trace, generated.MilestoneThreadId, generated.MilestoneTimeMs);
    sw.Stop();
    var topOffender = report.RankedOffenders.Count > 0 ? report.RankedOffenders[0] : null;
    Console.WriteLine(
        $"  Critical-path analysis: {sw.Elapsed.TotalMilliseconds:F0} ms -> {report.CriticalPath.Count} hop(s), " +
        $"{report.CriticalPathTotalMs:F0} ms critical path" +
        (topOffender is not null ? $", top offender {topOffender.ProcessName} ({topOffender.PercentOfCriticalPath:F1}%)" : ""));

    var dbPath = Path.Combine(Path.GetTempPath(), $"etwboot-benchmark-{Guid.NewGuid():N}.db");
    try
    {
        sw.Restart();
        using (var store = new SqliteTraceStore(dbPath))
        {
            store.Save(trace);
        }
        sw.Stop();
        var saveEventsPerSec = trace.TotalEventCount / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"  SQLite save: {sw.Elapsed.TotalSeconds:F2}s ({saveEventsPerSec:N0} events/sec)");

        sw.Restart();
        using var reload = new SqliteTraceStore(dbPath);
        var reloaded = reload.Load(trace.SessionName);
        sw.Stop();
        Console.WriteLine($"  SQLite load: {sw.Elapsed.TotalSeconds:F2}s ({reloaded.TotalEventCount:N0} events)");
    }
    finally
    {
        File.Delete(dbPath);
    }

    return 0;
}

static BootTrace? LoadTraceOrNull(string[] args)
{
    var jsonPath = GetOption(args, "--json");
    var sqlitePath = GetOption(args, "--sqlite");

    if (jsonPath is not null)
    {
        return JsonTraceIo.Import(jsonPath);
    }
    if (sqlitePath is not null)
    {
        var sessionName = GetOption(args, "--session");
        if (sessionName is null)
        {
            Console.Error.WriteLine("--session is required when loading from --sqlite.");
            return null;
        }
        using var store = new SqliteTraceStore(sqlitePath);
        return store.Load(sessionName);
    }

    Console.Error.WriteLine("Specify a trace with --json <path> or --sqlite <path> --session <name>.");
    return null;
}

static BootTrace LoadTraceFromPath(string path, string? sessionName)
{
    if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
    {
        if (sessionName is null)
        {
            throw new ArgumentException($"A --*-session name is required to load '{path}' from SQLite.");
        }
        using var store = new SqliteTraceStore(path);
        return store.Load(sessionName);
    }
    return JsonTraceIo.Import(path);
}

static BootAnalysisReport? Analyze(BootTrace trace, string? milestoneProcess)
{
    var milestone = milestoneProcess is not null
        ? MilestoneSelector.FirstReadyForProcess(trace, milestoneProcess)
        : MilestoneSelector.LastReadiedThread(trace);

    if (milestone is null)
    {
        Console.Error.WriteLine(
            milestoneProcess is not null
                ? $"No process matching '{milestoneProcess}' was ever readied in trace '{trace.SessionName}'."
                : $"Trace '{trace.SessionName}' has no ReadyThread events to build a critical path from.");
        return null;
    }

    var engine = new BootAnalysisEngine();
    return engine.Analyze(trace, milestone.AwakenedThreadId, milestone.TimestampMs);
}

#if ETW_CAPTURE
static int RunCapture(string[] args)
{
    var outPath = GetOption(args, "--out") ?? throw new ArgumentException("--out <path.json|path.db> is required.");
    var durationSec = double.Parse(GetOption(args, "--duration") ?? "20");
    var sessionName = GetOption(args, "--session") ?? "boot-trace";

    Console.WriteLine($"Capturing kernel ETW session '{sessionName}' for {durationSec:F0}s (requires Administrator)...");
    var trace = EtwBootTraceCapture.CaptureLive(sessionName, TimeSpan.FromSeconds(durationSec));
    SaveToPath(trace, outPath);
    Console.WriteLine($"Captured {trace.TotalEventCount:N0} events -> {outPath}");
    return 0;
}

static int RunImportEtl(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: import-etl <trace.etl> --out <path.json|path.db>");
        return 1;
    }
    var etlPath = args[0];
    var outPath = GetOption(args, "--out") ?? throw new ArgumentException("--out <path.json|path.db> is required.");

    var trace = EtwBootTraceCapture.LoadFromEtl(etlPath);
    SaveToPath(trace, outPath);
    Console.WriteLine($"Imported {trace.TotalEventCount:N0} events from {etlPath} -> {outPath}");
    return 0;
}

static void SaveToPath(BootTrace trace, string outPath)
{
    if (outPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || outPath.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
    {
        using var store = new SqliteTraceStore(outPath);
        store.Save(trace);
    }
    else
    {
        JsonTraceIo.Export(trace, outPath);
    }
}
#endif

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }
    return null;
}

static void PrintUsage()
{
    Console.WriteLine("""
        EtwBootTraceAnalyzer - attribute boot/app-launch latency to processes from an ETW trace.

        Usage:
          etwboot demo [--save-json <path>] [--save-sqlite <path>]
              Run the full pipeline against a built-in synthetic trace. Works on any OS.

          etwboot analyze --json <path> [--milestone-process <name>]
          etwboot analyze --sqlite <path> --session <name> [--milestone-process <name>]
              Analyze a previously captured/exported trace. Without --milestone-process, the
              last thread readied in the trace is used as the boot-complete milestone.

          etwboot compare --before <path> --after <path> [--milestone-process <name>]
              [--before-session <name>] [--after-session <name>]
              Diff two independently-analyzed traces (e.g. before/after a fix) and report the
              change in critical-path time, overall and per offending process. Paths ending in
              .db/.sqlite need the matching --before-session/--after-session.

          etwboot benchmark [--events <count>]
              Generate a synthetic trace at the given scale (default ~2,000,000 events) and time
              generation, critical-path analysis, and SQLite save/load - a real measurement to
              back up a throughput claim, not an assumed one.
        """);
#if ETW_CAPTURE
    Console.WriteLine("""
          etwboot capture --out <path.json|path.db> [--duration <seconds>] [--session <name>]
              Capture a live kernel ETW session (Windows, Administrator required).

          etwboot import-etl <trace.etl> --out <path.json|path.db>
              Parse a previously captured .etl file (from wpr/xperf) offline.
        """);
#endif
}
