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
    AnalyzeAndPrint(trace, milestoneProcess: null);

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
    var jsonPath = GetOption(args, "--json");
    var sqlitePath = GetOption(args, "--sqlite");
    var sessionName = GetOption(args, "--session");
    var milestoneProcess = GetOption(args, "--milestone-process");

    BootTrace trace;
    if (jsonPath is not null)
    {
        trace = JsonTraceIo.Import(jsonPath);
    }
    else if (sqlitePath is not null)
    {
        if (sessionName is null)
        {
            Console.Error.WriteLine("--session is required when loading from --sqlite.");
            return 1;
        }
        using var store = new SqliteTraceStore(sqlitePath);
        trace = store.Load(sessionName);
    }
    else
    {
        Console.Error.WriteLine("Specify a trace to analyze with --json <path> or --sqlite <path> --session <name>.");
        return 1;
    }

    AnalyzeAndPrint(trace, milestoneProcess);
    return 0;
}

static void AnalyzeAndPrint(BootTrace trace, string? milestoneProcess)
{
    var milestone = milestoneProcess is not null
        ? MilestoneSelector.FirstReadyForProcess(trace, milestoneProcess)
        : MilestoneSelector.LastReadiedThread(trace);

    if (milestone is null)
    {
        Console.Error.WriteLine(
            milestoneProcess is not null
                ? $"No process matching '{milestoneProcess}' was ever readied in this trace."
                : "This trace has no ReadyThread events to build a critical path from.");
        return;
    }

    var engine = new BootAnalysisEngine();
    var report = engine.Analyze(trace, milestone.AwakenedThreadId, milestone.TimestampMs);
    ConsoleReportPrinter.Print(trace, report);
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
