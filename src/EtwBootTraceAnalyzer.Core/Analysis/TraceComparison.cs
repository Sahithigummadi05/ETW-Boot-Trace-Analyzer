namespace EtwBootTraceAnalyzer.Core.Analysis;

public sealed record OffenderDelta
{
    public required string ProcessName { get; init; }
    public double BeforeMs { get; init; }
    public double AfterMs { get; init; }
    public double DeltaMs => AfterMs - BeforeMs;
}

/// <summary>
/// Before/after diff between two independently-analyzed boots. Matches offenders by process
/// name rather than pid, since pids aren't stable across boots - which means two distinct
/// generic "svchost.exe" instances doing unrelated work would merge into one row here. That's a
/// real limitation: it's precise for named/tagged services (e.g. "svchost.exe (DiskSvc)") and
/// approximate for bare "svchost.exe"/"System" style names.
/// </summary>
public sealed record TraceComparison
{
    public required double BeforeCriticalPathMs { get; init; }
    public required double AfterCriticalPathMs { get; init; }
    public double ImprovementMs => BeforeCriticalPathMs - AfterCriticalPathMs;
    public double ImprovementPercent => BeforeCriticalPathMs > 0 ? 100.0 * ImprovementMs / BeforeCriticalPathMs : 0;
    public required IReadOnlyList<OffenderDelta> OffenderDeltas { get; init; }
}

public static class TraceComparer
{
    public static TraceComparison Compare(BootAnalysisReport before, BootAnalysisReport after)
    {
        var beforeByName = before.RankedOffenders.ToDictionary(o => o.ProcessName, o => o.AttributedDelayMs);
        var afterByName = after.RankedOffenders.ToDictionary(o => o.ProcessName, o => o.AttributedDelayMs);

        var deltas = beforeByName.Keys.Union(afterByName.Keys)
            .Select(name => new OffenderDelta
            {
                ProcessName = name,
                BeforeMs = beforeByName.GetValueOrDefault(name),
                AfterMs = afterByName.GetValueOrDefault(name),
            })
            .OrderByDescending(d => Math.Abs(d.DeltaMs))
            .ToList();

        return new TraceComparison
        {
            BeforeCriticalPathMs = before.CriticalPathTotalMs,
            AfterCriticalPathMs = after.CriticalPathTotalMs,
            OffenderDeltas = deltas,
        };
    }
}
