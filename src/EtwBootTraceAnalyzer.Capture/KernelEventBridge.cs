using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace EtwBootTraceAnalyzer.Capture;

/// <summary>
/// Wires a <see cref="KernelTraceEventParser"/>'s callbacks into a <see cref="BootTraceBuilder"/>.
/// Shared by both the real-time session path and the offline .etl path in
/// <see cref="EtwBootTraceCapture"/> - the parser doesn't care whether events are arriving live
/// or being replayed from a file, so neither does this class.
/// </summary>
internal sealed class KernelEventBridge
{
    private readonly BootTraceBuilder _builder;
    private readonly ModuleRangeResolver _modules = new();

    public KernelEventBridge(BootTraceBuilder builder)
    {
        _builder = builder;
    }

    public void Attach(KernelTraceEventParser kernel)
    {
        // The *Group callbacks register both the plain event and its DCStart rundown
        // counterpart (see TraceEvent's own doc comments on ProcessStartGroup/ImageLoadGroup);
        // subscribing to both the Group and the plain event would double-count every process
        // start and image load, so only the Group form is used here.
        kernel.ProcessStartGroup += OnProcessStart;
        kernel.ProcessStop += OnProcessStop;

        kernel.ImageLoadGroup += e => _modules.AddModule(e.ImageBase, e.ImageSize, e.FileName);

        kernel.PerfInfoSample += OnCpuSample;

        kernel.ThreadCSwitch += OnContextSwitch;
        kernel.DispatcherReadyThread += OnReadyThread;

        kernel.DiskIORead += e => OnDiskIo(e, DiskIoKind.Read);
        kernel.DiskIOWrite += e => OnDiskIo(e, DiskIoKind.Write);
        kernel.DiskIOFlushBuffers += OnDiskFlush;

        kernel.PerfInfoDPC += e => OnInterrupt(e.TimeStampRelativeMSec, e.ProcessorNumber, e.ElapsedTimeMSec, e.Routine, InterruptKind.Dpc);
        kernel.PerfInfoThreadedDPC += e => OnInterrupt(e.TimeStampRelativeMSec, e.ProcessorNumber, e.ElapsedTimeMSec, e.Routine, InterruptKind.Dpc);
        kernel.PerfInfoTimerDPC += e => OnInterrupt(e.TimeStampRelativeMSec, e.ProcessorNumber, e.ElapsedTimeMSec, e.Routine, InterruptKind.Dpc);
        kernel.PerfInfoISR += e => OnInterrupt(e.TimeStampRelativeMSec, e.ProcessorNumber, e.ElapsedTimeMSec, e.Routine, InterruptKind.Isr);
    }

    private void OnProcessStart(ProcessTraceData e) => _builder.Add(new ProcessStartEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        ProcessId = e.ProcessID,
        ParentProcessId = e.ParentID,
        ImageFileName = e.ImageFileName,
        CommandLine = string.IsNullOrEmpty(e.CommandLine) ? null : e.CommandLine,
    });

    private void OnProcessStop(ProcessTraceData e) => _builder.Add(new ProcessStopEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        ProcessId = e.ProcessID,
        ExitStatus = e.ExitStatus,
    });

    private void OnCpuSample(SampledProfileTraceData e) => _builder.Add(new CpuSampleEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        ProcessId = e.ProcessID,
        ThreadId = e.ThreadID,
        ProcessorNumber = e.ProcessorNumber,
        InstructionPointer = e.InstructionPointer,
    });

    private void OnContextSwitch(CSwitchTraceData e) => _builder.Add(new ContextSwitchEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        ProcessorNumber = e.ProcessorNumber,
        OldThreadId = e.OldThreadID,
        OldProcessId = e.OldProcessID,
        NewThreadId = e.NewThreadID,
        NewProcessId = e.NewProcessID,
        OldThreadWaitReason = e.OldThreadWaitReason.ToString(),
        NewThreadWaitTimeMs = e.NewThreadWaitTime,
    });

    private void OnReadyThread(DispatcherReadyThreadTraceData e) => _builder.Add(new ReadyThreadEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        AwakenedThreadId = e.AwakenedThreadID,
        AwakenedProcessId = e.AwakenedProcessID,
        // The thread executing when a ReadyThread event fires is the one doing the waking -
        // TraceEvent surfaces that as the event's own ThreadID/ProcessID, distinct from the
        // AwakenedThreadID/AwakenedProcessID fields that name the thread being woken up.
        ReadyingThreadId = e.ThreadID,
        ReadyingProcessId = e.ProcessID,
    });

    private void OnDiskIo(DiskIOTraceData e, DiskIoKind kind) => _builder.Add(new DiskIoEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        Kind = kind,
        IssuingProcessId = e.ProcessID,
        IssuingThreadId = e.ThreadID,
        DurationMs = e.ElapsedTimeMSec,
        ByteOffset = e.ByteOffset,
        TransferSizeBytes = e.TransferSize,
        FileName = string.IsNullOrEmpty(e.FileName) ? null : e.FileName,
        DiskNumber = e.DiskNumber,
    });

    private void OnDiskFlush(DiskIOFlushBuffersTraceData e) => _builder.Add(new DiskIoEvent
    {
        TimestampMs = e.TimeStampRelativeMSec,
        Kind = DiskIoKind.Flush,
        IssuingProcessId = e.ProcessID,
        IssuingThreadId = e.ThreadID,
        DurationMs = e.ElapsedTimeMSec,
        ByteOffset = 0,
        TransferSizeBytes = 0,
        FileName = null,
        DiskNumber = e.DiskNumber,
    });

    private void OnInterrupt(double timestampMs, int processorNumber, double durationMs, ulong routineAddress, InterruptKind kind) =>
        _builder.Add(new DpcIsrEvent
        {
            TimestampMs = timestampMs,
            Kind = kind,
            ProcessorNumber = processorNumber,
            DurationMs = durationMs,
            RoutineModule = _modules.Resolve(routineAddress),
        });
}
