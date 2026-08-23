using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Synthetic;

/// <summary>
/// Builds a plausible boot trace without needing an actual Windows machine or .etl file: a
/// wait-chain of svchost/service processes where two disk-bound services and one CPU-bound
/// service dominate the time before the shell's thread is readied. Lets the analysis pipeline,
/// CLI, and demo run end-to-end on any OS, and gives the unit tests a fixture whose expected
/// answer ("service B is the top offender") is known by construction.
/// </summary>
public static class SyntheticBootTraceGenerator
{
    public static BootTrace Generate()
    {
        var builder = new BootTraceBuilder
        {
            SessionName = "synthetic-boot",
            BootStartUtc = DateTime.UtcNow,
            CpuSampleIntervalMs = 1.0,
        };

        // pids/tids are arbitrary but stable so the narrative below and the tests agree on them.
        const int wininitPid = 500, wininitTid = 501;
        const int servicesPid = 600, servicesTid = 601;
        const int svcHostDiskPid = 700, svcHostDiskTid = 701; // the slow offender: two chained disk reads
        const int svcHostCpuPid = 800, svcHostCpuTid = 801; // a secondary offender: pegs the CPU
        const int explorerPid = 900, explorerTid = 901; // boot-complete milestone

        builder.Add(new ProcessStartEvent { TimestampMs = 0, ProcessId = wininitPid, ParentProcessId = 4, ImageFileName = "wininit.exe" });
        builder.Add(new ProcessStartEvent { TimestampMs = 5, ProcessId = servicesPid, ParentProcessId = wininitPid, ImageFileName = "services.exe" });
        builder.Add(new ProcessStartEvent { TimestampMs = 10, ProcessId = svcHostDiskPid, ParentProcessId = servicesPid, ImageFileName = "svchost.exe (DiskSvc)" });
        builder.Add(new ProcessStartEvent { TimestampMs = 10, ProcessId = svcHostCpuPid, ParentProcessId = servicesPid, ImageFileName = "svchost.exe (IndexingSvc)" });
        builder.Add(new ProcessStartEvent { TimestampMs = 15, ProcessId = explorerPid, ParentProcessId = wininitPid, ImageFileName = "explorer.exe" });

        // services.exe (601) runs briefly, then blocks on wininit (501) - a plain CPU segment for the chain to end on.
        builder.Add(new ContextSwitchEvent { TimestampMs = 10, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = wininitTid, NewProcessId = wininitPid, OldThreadWaitReason = "WrDispatchInt" });
        builder.Add(new ReadyThreadEvent { TimestampMs = 20, AwakenedThreadId = servicesTid, AwakenedProcessId = servicesPid, ReadyingThreadId = wininitTid, ReadyingProcessId = wininitPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 20, ProcessorNumber = 0, OldThreadId = wininitTid, OldProcessId = wininitPid, NewThreadId = servicesTid, NewProcessId = servicesPid, OldThreadWaitReason = "Executive" });

        // services.exe wakes svcHostDiskTid, which issues two chained disk reads (image load + config file) - the dominant offender.
        builder.Add(new ReadyThreadEvent { TimestampMs = 30, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = servicesTid, ReadyingProcessId = servicesPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 30, ProcessorNumber = 0, OldThreadId = servicesTid, OldProcessId = servicesPid, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // First disk read: 30ms -> 250ms (220ms stall) reading the service DLL.
        builder.Add(new DiskIoEvent
        {
            TimestampMs = 250, Kind = DiskIoKind.Read, IssuingProcessId = svcHostDiskPid, IssuingThreadId = svcHostDiskTid,
            DurationMs = 220, ByteOffset = 0x10000, TransferSizeBytes = 4096, FileName = @"C:\Windows\System32\diskservice.dll", DiskNumber = 0,
        });
        builder.Add(new ReadyThreadEvent { TimestampMs = 250, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 250, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // Second disk read: 250ms -> 370ms (120ms stall) reading its config.
        builder.Add(new DiskIoEvent
        {
            TimestampMs = 370, Kind = DiskIoKind.Read, IssuingProcessId = svcHostDiskPid, IssuingThreadId = svcHostDiskTid,
            DurationMs = 120, ByteOffset = 0x20000, TransferSizeBytes = 8192, FileName = @"C:\ProgramData\DiskService\config.dat", DiskNumber = 0,
        });
        builder.Add(new ReadyThreadEvent { TimestampMs = 370, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 370, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // DiskSvc wakes the indexing service, which pegs the CPU for 60ms before waking explorer's thread.
        builder.Add(new ReadyThreadEvent { TimestampMs = 400, AwakenedThreadId = svcHostCpuTid, AwakenedProcessId = svcHostCpuPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 400, ProcessorNumber = 0, OldThreadId = svcHostDiskTid, OldProcessId = svcHostDiskPid, NewThreadId = svcHostCpuTid, NewProcessId = svcHostCpuPid, OldThreadWaitReason = "Executive" });
        for (var t = 400.0; t < 460.0; t += 1.0)
        {
            builder.Add(new CpuSampleEvent { TimestampMs = t, ProcessId = svcHostCpuPid, ThreadId = svcHostCpuTid, ProcessorNumber = 0 });
        }

        builder.Add(new ReadyThreadEvent { TimestampMs = 460, AwakenedThreadId = explorerTid, AwakenedProcessId = explorerPid, ReadyingThreadId = svcHostCpuTid, ReadyingProcessId = svcHostCpuPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = 460, ProcessorNumber = 0, OldThreadId = svcHostCpuTid, OldProcessId = svcHostCpuPid, NewThreadId = explorerTid, NewProcessId = explorerPid, OldThreadWaitReason = "Executive" });

        return builder.Build();
    }
}
