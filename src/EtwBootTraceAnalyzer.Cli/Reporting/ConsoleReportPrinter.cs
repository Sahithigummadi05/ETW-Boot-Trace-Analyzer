using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Cli.Reporting;

internal static class ConsoleReportPrinter
{
    public static void Print(BootTrace trace, BootAnalysisReport report)
    {
        Console.WriteLine($"Boot trace '{trace.SessionName}' - {trace.TotalEventCount:N0} events");
        Console.WriteLine();

        Console.WriteLine($"Critical path: {report.CriticalPathTotalMs:F0} ms across {report.CriticalPath.Count} hop(s)");
        foreach (var seg in report.CriticalPath)
        {
            var tag = seg.Cause switch
            {
                CriticalPathCause.DiskIo => "DISK",
                CriticalPathCause.Interrupt => "INTR",
                _ => "CPU ",
            };
            Console.WriteLine($"  [{seg.StartMs,7:F1} - {seg.EndMs,7:F1} ms] {tag}  {seg.Explanation}");
        }
        Console.WriteLine();

        Console.WriteLine("Top offenders (share of the critical path):");
        var rank = 1;
        foreach (var offender in report.RankedOffenders.Take(10))
        {
            Console.WriteLine(
                $"  {rank,2}. {offender.ProcessName} (pid {offender.ProcessId}) - " +
                $"{offender.AttributedDelayMs:F0} ms ({offender.PercentOfCriticalPath:F1}%)");
            foreach (var reason in offender.TopReasons)
            {
                Console.WriteLine($"        - {reason}");
            }
            rank++;
        }
        Console.WriteLine();

        if (report.DiskIoSummaries.Count > 0)
        {
            Console.WriteLine("Disk I/O summary (all completed I/O, not just the critical path):");
            foreach (var s in report.DiskIoSummaries.Take(10))
            {
                var longest = s.LongestStall is { } io ? $", longest {io.DurationMs:F0} ms ({io.FileName ?? "?"})" : "";
                Console.WriteLine($"  {s.ProcessName} (pid {s.ProcessId}) - {s.TotalWaitMs:F0} ms across {s.OperationCount} op(s){longest}");
            }
            Console.WriteLine();
        }

        if (report.DpcIsrMsByModule.Count > 0)
        {
            Console.WriteLine("DPC/ISR time by driver module:");
            foreach (var (module, ms) in report.DpcIsrMsByModule.OrderByDescending(kv => kv.Value).Take(10))
            {
                Console.WriteLine($"  {module} - {ms:F2} ms");
            }
        }
    }
}
