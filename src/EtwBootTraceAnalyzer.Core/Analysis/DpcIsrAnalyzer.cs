using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

/// <summary>
/// Aggregates DPC/ISR time per driver module. High totals here point at a driver keeping the
/// CPU in kernel context long enough to delay scheduling of user-mode threads during boot.
/// </summary>
public static class DpcIsrAnalyzer
{
    public static IReadOnlyDictionary<string, double> TotalDurationMsByModule(BootTrace trace)
    {
        var totals = new Dictionary<string, double>();
        foreach (var e in trace.DpcIsrEvents)
        {
            totals[e.RoutineModule] = totals.GetValueOrDefault(e.RoutineModule) + e.DurationMs;
        }
        return totals;
    }
}
