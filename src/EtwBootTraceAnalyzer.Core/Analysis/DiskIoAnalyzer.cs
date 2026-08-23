using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

public sealed record DiskIoSummary
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required double TotalWaitMs { get; init; }
    public required int OperationCount { get; init; }
    public required long TotalBytes { get; init; }
    public DiskIoEvent? LongestStall { get; init; }
}

/// <summary>Aggregates completed disk I/O per issuing process to find who waited longest on storage.</summary>
public static class DiskIoAnalyzer
{
    public static IReadOnlyList<DiskIoSummary> Summarize(BootTrace trace)
    {
        return trace.DiskIoEvents
            .GroupBy(io => io.IssuingProcessId)
            .Select(g => new DiskIoSummary
            {
                ProcessId = g.Key,
                ProcessName = trace.ProcessName(g.Key),
                TotalWaitMs = g.Sum(io => io.DurationMs),
                OperationCount = g.Count(),
                TotalBytes = g.Sum(io => (long)io.TransferSizeBytes),
                LongestStall = g.OrderByDescending(io => io.DurationMs).FirstOrDefault(),
            })
            .OrderByDescending(s => s.TotalWaitMs)
            .ToList();
    }
}
