using EtwBootTraceAnalyzer.Core.Events;

namespace EtwBootTraceAnalyzer.Core.Model;

/// <summary>
/// Mutable accumulator used while streaming events off a live ETW session, an .etl file, or a
/// portable export. Callers append events as they arrive and call <see cref="Build"/> once to
/// get an immutable <see cref="BootTrace"/> sorted by timestamp.
/// </summary>
public sealed class BootTraceBuilder
{
    private readonly List<ProcessStartEvent> _processStarts = [];
    private readonly List<ProcessStopEvent> _processStops = [];
    private readonly List<CpuSampleEvent> _cpuSamples = [];
    private readonly List<ContextSwitchEvent> _contextSwitches = [];
    private readonly List<ReadyThreadEvent> _readyThreadEvents = [];
    private readonly List<DiskIoEvent> _diskIoEvents = [];
    private readonly List<DpcIsrEvent> _dpcIsrEvents = [];

    public string SessionName { get; set; } = "boot-trace";
    public DateTime BootStartUtc { get; set; } = DateTime.UtcNow;
    public double CpuSampleIntervalMs { get; set; } = 1.0;

    public void Add(ProcessStartEvent e) => _processStarts.Add(e);
    public void Add(ProcessStopEvent e) => _processStops.Add(e);
    public void Add(CpuSampleEvent e) => _cpuSamples.Add(e);
    public void Add(ContextSwitchEvent e) => _contextSwitches.Add(e);
    public void Add(ReadyThreadEvent e) => _readyThreadEvents.Add(e);
    public void Add(DiskIoEvent e) => _diskIoEvents.Add(e);
    public void Add(DpcIsrEvent e) => _dpcIsrEvents.Add(e);

    public BootTrace Build() => new()
    {
        SessionName = SessionName,
        BootStartUtc = BootStartUtc,
        CpuSampleIntervalMs = CpuSampleIntervalMs,
        ProcessStarts = _processStarts.OrderBy(e => e.TimestampMs).ToList(),
        ProcessStops = _processStops.OrderBy(e => e.TimestampMs).ToList(),
        CpuSamples = _cpuSamples.OrderBy(e => e.TimestampMs).ToList(),
        ContextSwitches = _contextSwitches.OrderBy(e => e.TimestampMs).ToList(),
        ReadyThreadEvents = _readyThreadEvents.OrderBy(e => e.TimestampMs).ToList(),
        DiskIoEvents = _diskIoEvents.OrderBy(e => e.TimestampMs).ToList(),
        DpcIsrEvents = _dpcIsrEvents.OrderBy(e => e.TimestampMs).ToList(),
    };
}
