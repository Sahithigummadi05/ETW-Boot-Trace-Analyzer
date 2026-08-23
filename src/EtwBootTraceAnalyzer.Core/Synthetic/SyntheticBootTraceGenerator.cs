using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Synthetic;

/// <summary>
/// Builds a plausible boot trace without needing an actual Windows machine or .etl file: a
/// wait-chain of svchost/service processes where two disk-bound services and one CPU-bound
/// service dominate the time before the shell's thread is readied. Lets the analysis pipeline,
/// CLI, and demo run end-to-end on any OS, and gives the unit tests a fixture whose expected
/// answer ("service B is the top offender") is known by construction.
///
/// The two disk-read durations are parameterized so a test (or a demo) can generate a "before"
/// and an "after" trace representing the same boot with DiskSvc's reads sped up - e.g. by
/// caching its config or moving it off a slow disk - and feed both into
/// <see cref="Analysis.TraceComparer"/> to see a real before/after improvement number.
/// </summary>
public static class SyntheticBootTraceGenerator
{
    public static BootTrace Generate(double diskRead1Ms = 220, double diskRead2Ms = 120, double cpuBurstMs = 60)
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

        // Every later timestamp is derived from the two read durations so shortening them (to
        // model a fix) keeps the whole trace internally consistent instead of leaving stale gaps.
        const double wininitRunEndMs = 20;
        const double servicesRunEndMs = 30;
        var disk1EndMs = servicesRunEndMs + diskRead1Ms;
        var disk2EndMs = disk1EndMs + diskRead2Ms;
        const double wrapUpMs = 30;
        var wrapUpEndMs = disk2EndMs + wrapUpMs;
        var cpuBurstEndMs = wrapUpEndMs + cpuBurstMs;

        builder.Add(new ProcessStartEvent { TimestampMs = 0, ProcessId = wininitPid, ParentProcessId = 4, ImageFileName = "wininit.exe" });
        builder.Add(new ProcessStartEvent { TimestampMs = 5, ProcessId = servicesPid, ParentProcessId = wininitPid, ImageFileName = "services.exe" });
        builder.Add(new ProcessStartEvent { TimestampMs = 10, ProcessId = svcHostDiskPid, ParentProcessId = servicesPid, ImageFileName = "svchost.exe (DiskSvc)" });
        builder.Add(new ProcessStartEvent { TimestampMs = 10, ProcessId = svcHostCpuPid, ParentProcessId = servicesPid, ImageFileName = "svchost.exe (IndexingSvc)" });
        builder.Add(new ProcessStartEvent { TimestampMs = 15, ProcessId = explorerPid, ParentProcessId = wininitPid, ImageFileName = "explorer.exe" });

        // services.exe (601) runs briefly, then blocks on wininit (501) - a plain CPU segment for the chain to end on.
        builder.Add(new ContextSwitchEvent { TimestampMs = 10, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = wininitTid, NewProcessId = wininitPid, OldThreadWaitReason = "WrDispatchInt" });
        builder.Add(new ReadyThreadEvent { TimestampMs = wininitRunEndMs, AwakenedThreadId = servicesTid, AwakenedProcessId = servicesPid, ReadyingThreadId = wininitTid, ReadyingProcessId = wininitPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = wininitRunEndMs, ProcessorNumber = 0, OldThreadId = wininitTid, OldProcessId = wininitPid, NewThreadId = servicesTid, NewProcessId = servicesPid, OldThreadWaitReason = "Executive" });

        // services.exe wakes svcHostDiskTid, which issues two chained disk reads (image load + config file) - the dominant offender.
        builder.Add(new ReadyThreadEvent { TimestampMs = servicesRunEndMs, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = servicesTid, ReadyingProcessId = servicesPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = servicesRunEndMs, ProcessorNumber = 0, OldThreadId = servicesTid, OldProcessId = servicesPid, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // First disk read: reading the service DLL.
        builder.Add(new DiskIoEvent
        {
            TimestampMs = disk1EndMs, Kind = DiskIoKind.Read, IssuingProcessId = svcHostDiskPid, IssuingThreadId = svcHostDiskTid,
            DurationMs = diskRead1Ms, ByteOffset = 0x10000, TransferSizeBytes = 4096, FileName = @"C:\Windows\System32\diskservice.dll", DiskNumber = 0,
        });
        builder.Add(new ReadyThreadEvent { TimestampMs = disk1EndMs, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = disk1EndMs, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // Second disk read: reading its config.
        builder.Add(new DiskIoEvent
        {
            TimestampMs = disk2EndMs, Kind = DiskIoKind.Read, IssuingProcessId = svcHostDiskPid, IssuingThreadId = svcHostDiskTid,
            DurationMs = diskRead2Ms, ByteOffset = 0x20000, TransferSizeBytes = 8192, FileName = @"C:\ProgramData\DiskService\config.dat", DiskNumber = 0,
        });
        builder.Add(new ReadyThreadEvent { TimestampMs = disk2EndMs, AwakenedThreadId = svcHostDiskTid, AwakenedProcessId = svcHostDiskPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = disk2EndMs, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = svcHostDiskTid, NewProcessId = svcHostDiskPid, OldThreadWaitReason = "Executive" });

        // DiskSvc wakes the indexing service, which pegs the CPU before waking explorer's thread.
        builder.Add(new ReadyThreadEvent { TimestampMs = wrapUpEndMs, AwakenedThreadId = svcHostCpuTid, AwakenedProcessId = svcHostCpuPid, ReadyingThreadId = svcHostDiskTid, ReadyingProcessId = svcHostDiskPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = wrapUpEndMs, ProcessorNumber = 0, OldThreadId = svcHostDiskTid, OldProcessId = svcHostDiskPid, NewThreadId = svcHostCpuTid, NewProcessId = svcHostCpuPid, OldThreadWaitReason = "Executive" });
        for (var t = wrapUpEndMs; t < cpuBurstEndMs; t += 1.0)
        {
            builder.Add(new CpuSampleEvent { TimestampMs = t, ProcessId = svcHostCpuPid, ThreadId = svcHostCpuTid, ProcessorNumber = 0 });
        }

        builder.Add(new ReadyThreadEvent { TimestampMs = cpuBurstEndMs, AwakenedThreadId = explorerTid, AwakenedProcessId = explorerPid, ReadyingThreadId = svcHostCpuTid, ReadyingProcessId = svcHostCpuPid });
        builder.Add(new ContextSwitchEvent { TimestampMs = cpuBurstEndMs, ProcessorNumber = 0, OldThreadId = svcHostCpuTid, OldProcessId = svcHostCpuPid, NewThreadId = explorerTid, NewProcessId = explorerPid, OldThreadWaitReason = "Executive" });

        return builder.Build();
    }
}
