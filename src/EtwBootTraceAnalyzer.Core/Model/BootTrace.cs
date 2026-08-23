using EtwBootTraceAnalyzer.Core.Events;

namespace EtwBootTraceAnalyzer.Core.Model;

/// <summary>
/// The full set of events pulled from one ETW session (or one .etl file), plus enough
/// metadata to turn raw counts back into wall-clock durations.
/// </summary>
public sealed class BootTrace
{
    public required string SessionName { get; init; }
    public required DateTime BootStartUtc { get; init; }

    /// <summary>Sampling interval used for CPU profile events (default ETW kernel profile rate is 1ms).</summary>
    public double CpuSampleIntervalMs { get; init; } = 1.0;

    public required IReadOnlyList<ProcessStartEvent> ProcessStarts { get; init; }
    public required IReadOnlyList<ProcessStopEvent> ProcessStops { get; init; }
    public required IReadOnlyList<CpuSampleEvent> CpuSamples { get; init; }
    public required IReadOnlyList<ContextSwitchEvent> ContextSwitches { get; init; }
    public required IReadOnlyList<ReadyThreadEvent> ReadyThreadEvents { get; init; }
    public required IReadOnlyList<DiskIoEvent> DiskIoEvents { get; init; }
    public required IReadOnlyList<DpcIsrEvent> DpcIsrEvents { get; init; }

    private Dictionary<int, string>? _processNameByPid;

    /// <summary>Best-effort process name lookup for a pid, falling back to "pid &lt;n&gt;" when unknown.</summary>
    public string ProcessName(int processId)
    {
        _processNameByPid ??= BuildProcessNameIndex();
        return _processNameByPid.TryGetValue(processId, out var name) ? name : $"pid {processId}";
    }

    private Dictionary<int, string> BuildProcessNameIndex()
    {
        var index = new Dictionary<int, string>();
        foreach (var start in ProcessStarts)
        {
            index[start.ProcessId] = start.ImageFileName;
        }
        return index;
    }

    public int TotalEventCount =>
        ProcessStarts.Count + ProcessStops.Count + CpuSamples.Count + ContextSwitches.Count
        + ReadyThreadEvents.Count + DiskIoEvents.Count + DpcIsrEvents.Count;
}
