namespace EtwBootTraceAnalyzer.Core.Events;

/// <summary>
/// Common shape for every event pulled out of an ETW session. Timestamps are milliseconds
/// relative to the start of the capture (matches TraceEvent's TimeStampRelativeMSec), which is
/// what the analysis layer sorts and correlates on.
/// </summary>
public abstract record BootEvent
{
    public required double TimestampMs { get; init; }
}

public sealed record ProcessStartEvent : BootEvent
{
    public required int ProcessId { get; init; }
    public required int ParentProcessId { get; init; }
    public required string ImageFileName { get; init; }
    public string? CommandLine { get; init; }
}

public sealed record ProcessStopEvent : BootEvent
{
    public required int ProcessId { get; init; }
    public int ExitStatus { get; init; }
}

public sealed record CpuSampleEvent : BootEvent
{
    public required int ProcessId { get; init; }
    public required int ThreadId { get; init; }
    public required int ProcessorNumber { get; init; }
    public ulong InstructionPointer { get; init; }
}

/// <summary>Mirrors ETW's kernel Thread/CSwitch event: OldThread yields the processor to NewThread.</summary>
public sealed record ContextSwitchEvent : BootEvent
{
    public required int ProcessorNumber { get; init; }
    public required int OldThreadId { get; init; }
    public required int OldProcessId { get; init; }
    public required int NewThreadId { get; init; }
    public required int NewProcessId { get; init; }
    public required string OldThreadWaitReason { get; init; }

    /// <summary>
    /// Raw "new thread wait time" value copied from the CSwitch payload. Kept for completeness
    /// and manual inspection; the wait-chain analyzer does not rely on it because it isn't
    /// documented precisely enough to calibrate against wall-clock milliseconds - ReadyThread
    /// correlation is used instead for anything that needs to be exact.
    /// </summary>
    public double NewThreadWaitTimeMs { get; init; }
}

/// <summary>
/// Mirrors ETW's kernel Thread/ReadyThread (DispatcherReadyThread) event: whichever thread is
/// running when this fires makes AwakenedThreadId runnable again. This is the edge the
/// wait-chain analyzer walks backward across to find what a stalled thread was waiting on.
/// </summary>
public sealed record ReadyThreadEvent : BootEvent
{
    public required int AwakenedThreadId { get; init; }
    public required int AwakenedProcessId { get; init; }
    public required int ReadyingThreadId { get; init; }
    public required int ReadyingProcessId { get; init; }
}

public enum DiskIoKind
{
    Read,
    Write,
    Flush,
}

/// <summary>
/// A completed disk I/O. TimestampMs is the completion time; IssueTimeMs is derived from
/// ElapsedTimeMSec the way TraceEvent itself correlates DiskIO/*Init with DiskIO/Read|Write.
/// IssuingProcessId/IssuingThreadId are the thread that issued the request, not whichever
/// DPC context the completion happened to run in (TraceEvent back-patches this from the Irp
/// correlation, and we preserve that semantic here).
/// </summary>
public sealed record DiskIoEvent : BootEvent
{
    public required DiskIoKind Kind { get; init; }
    public required int IssuingProcessId { get; init; }
    public required int IssuingThreadId { get; init; }
    public required double DurationMs { get; init; }
    public double IssueTimeMs => TimestampMs - DurationMs;
    public required long ByteOffset { get; init; }
    public required int TransferSizeBytes { get; init; }
    public string? FileName { get; init; }
    public int DiskNumber { get; init; }
}

public enum InterruptKind
{
    Dpc,
    Isr,
}

/// <summary>
/// A DPC or ISR that ran on a processor. RoutineModule is resolved by matching the routine's
/// address against the ImageLoad ranges captured during the same session (module-level
/// attribution, the same technique WPA uses to blame a driver without needing PDBs).
/// </summary>
public sealed record DpcIsrEvent : BootEvent
{
    public required InterruptKind Kind { get; init; }
    public required int ProcessorNumber { get; init; }
    public required double DurationMs { get; init; }
    public required string RoutineModule { get; init; }
}
