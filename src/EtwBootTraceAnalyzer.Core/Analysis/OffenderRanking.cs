namespace EtwBootTraceAnalyzer.Core.Analysis;

public sealed record RankedOffender
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required double AttributedDelayMs { get; init; }
    public required double PercentOfCriticalPath { get; init; }
    public required IReadOnlyList<string> TopReasons { get; init; }
}

/// <summary>Collapses a critical path's segments into a ranked "who cost the most time" list.</summary>
public static class OffenderRanking
{
    public static IReadOnlyList<RankedOffender> Rank(IReadOnlyList<CriticalPathSegment> criticalPath)
    {
        var totalMs = criticalPath.Sum(s => s.DurationMs);

        return criticalPath
            .GroupBy(s => (s.ProcessId, s.ProcessName))
            .Select(g =>
            {
                var attributedMs = g.Sum(s => s.DurationMs);
                return new RankedOffender
                {
                    ProcessId = g.Key.ProcessId,
                    ProcessName = g.Key.ProcessName,
                    AttributedDelayMs = attributedMs,
                    PercentOfCriticalPath = totalMs > 0 ? 100.0 * attributedMs / totalMs : 0,
                    TopReasons = g.OrderByDescending(s => s.DurationMs).Take(3).Select(s => s.Explanation).ToList(),
                };
            })
            .OrderByDescending(r => r.AttributedDelayMs)
            .ToList();
    }
}
