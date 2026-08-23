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
        ProcessStarts = SortByTimestamp(_processStarts),
        ProcessStops = SortByTimestamp(_processStops),
        CpuSamples = SortByTimestamp(_cpuSamples),
        ContextSwitches = SortByTimestamp(_contextSwitches),
        ReadyThreadEvents = SortByTimestamp(_readyThreadEvents),
        DiskIoEvents = SortByTimestamp(_diskIoEvents),
        DpcIsrEvents = SortByTimestamp(_dpcIsrEvents),
    };

    // List<T>.Sort in place beats LINQ's OrderBy().ToList() (which allocates a full second
    // list, decorated enumerator, and comparer wrapper) once a list is in the hundreds of
    // thousands of events - the difference that matters when a session import is ~2M rows.
    private static List<TEvent> SortByTimestamp<TEvent>(List<TEvent> events) where TEvent : BootEvent
    {
        events.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
        return events;
    }
}
