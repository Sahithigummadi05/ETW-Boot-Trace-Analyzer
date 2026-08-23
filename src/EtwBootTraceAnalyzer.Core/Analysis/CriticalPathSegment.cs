namespace EtwBootTraceAnalyzer.Core.Analysis;

public enum CriticalPathCause
{
    /// <summary>The attributed process/thread was simply running on a CPU.</summary>
    CpuExecution,

    /// <summary>The attributed process was blocked waiting on a disk I/O completion.</summary>
    DiskIo,

    /// <summary>A driver's DPC/ISR held a processor, delaying scheduling of the waiting thread.</summary>
    Interrupt,
}

/// <summary>
/// One hop of the critical path: "from StartMs to EndMs, forward progress depended on this
/// process/driver, for this reason." Consecutive segments read backward-in-time-then-reversed
/// tell the causal story of what delayed the milestone the path was built from.
/// </summary>
public sealed record CriticalPathSegment
{
    public required double StartMs { get; init; }
    public required double EndMs { get; init; }
    public double DurationMs => EndMs - StartMs;
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required CriticalPathCause Cause { get; init; }
    public required string Explanation { get; init; }
}
