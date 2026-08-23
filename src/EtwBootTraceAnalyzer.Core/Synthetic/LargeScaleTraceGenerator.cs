using System.Diagnostics;
using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Synthetic;

/// <summary>
/// Generates a trace shaped like a real boot at realistic scale: a genuine, deterministic
/// wait-chain (the actual critical path an analysis should recover) buried inside millions of
/// unrelated background events (other processes' CPU samples, context switches, disk I/O,
/// interrupts) that the wait-chain walk has to index past rather than get confused by.
///
/// This exists to put a real, measured number behind "processes ~2M events/session" instead of
/// leaving it as an unverified claim - see `etwboot benchmark`.
/// </summary>
public static class LargeScaleTraceGenerator
{
    public sealed record Result(BootTrace Trace, int MilestoneThreadId, double MilestoneTimeMs, TimeSpan GenerationTime);

    public static Result Generate(long targetEventCount, int chainHops = 25, int? seed = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var rng = new Random(seed ?? 42);
        var builder = new BootTraceBuilder
        {
            SessionName = $"benchmark-{targetEventCount}",
            BootStartUtc = DateTime.UtcNow,
            CpuSampleIntervalMs = 1.0,
        };

        long emitted = 0;
        void Track() => emitted++;

        // 1. The genuine critical path: a deterministic chain of processes, each readying the
        // next, alternating disk-bound and CPU-bound hops - the same shape as
        // SyntheticBootTraceGenerator, just longer, so the analyzer has real work to walk
        // through the noise below.
        var prevTid = -1;
        var prevPid = -1;
        var t = 0.0;
        var milestoneTid = -1;
        var nextId = 100;

        for (var i = 0; i < chainHops; i++)
        {
            var pid = nextId;
            var tid = nextId + 1;
            nextId += 100;

            builder.Add(new ProcessStartEvent { TimestampMs = t, ProcessId = pid, ParentProcessId = prevPid < 0 ? 4 : prevPid, ImageFileName = $"chainsvc{i}.exe" });
            Track();

            if (prevTid >= 0)
            {
                builder.Add(new ReadyThreadEvent { TimestampMs = t, AwakenedThreadId = tid, AwakenedProcessId = pid, ReadyingThreadId = prevTid, ReadyingProcessId = prevPid });
                Track();
            }
            builder.Add(new ContextSwitchEvent { TimestampMs = t, ProcessorNumber = 0, OldThreadId = Math.Max(prevTid, 0), OldProcessId = Math.Max(prevPid, 0), NewThreadId = tid, NewProcessId = pid, OldThreadWaitReason = "Executive" });
            Track();

            var hopMs = 5 + rng.Next(1, 50);
            if (i % 3 == 0)
            {
                // Disk-bound hop: issue a read, then the completion readies the same thread back in.
                var end = t + hopMs;
                builder.Add(new DiskIoEvent { TimestampMs = end, Kind = DiskIoKind.Read, IssuingProcessId = pid, IssuingThreadId = tid, DurationMs = hopMs, ByteOffset = 0, TransferSizeBytes = 4096, FileName = $@"C:\chain\file{i}.dll", DiskNumber = 0 });
                Track();
                builder.Add(new ReadyThreadEvent { TimestampMs = end, AwakenedThreadId = tid, AwakenedProcessId = pid, ReadyingThreadId = tid, ReadyingProcessId = pid });
                Track();
                builder.Add(new ContextSwitchEvent { TimestampMs = end, ProcessorNumber = 0, OldThreadId = 0, OldProcessId = 0, NewThreadId = tid, NewProcessId = pid, OldThreadWaitReason = "Executive" });
                Track();
                t = end;
            }
            else
            {
                // CPU-bound hop: just burns wall-clock time before waking the next hop.
                t += hopMs;
            }

            prevTid = tid;
            prevPid = pid;
        }
        milestoneTid = prevTid;
        var milestoneTimeMs = t;

        // 2. Background noise: independent processes/threads doing CPU work, unrelated to the
        // chain above, spread across the same time window. This is most of the event budget -
        // in a real boot trace, the vast majority of events are not on the critical path either.
        var remaining = Math.Max(0, targetEventCount - emitted);
        const int backgroundThreadCount = 1000;
        var perThreadBudget = Math.Max(1, (int)(remaining / backgroundThreadCount));

        for (var bt = 0; bt < backgroundThreadCount && emitted < targetEventCount; bt++)
        {
            var pid = 100_000 + bt;
            var tid = 200_000 + bt;
            builder.Add(new ProcessStartEvent { TimestampMs = 0, ProcessId = pid, ParentProcessId = 4, ImageFileName = $"background{bt}.exe" });
            Track();

            var bgT = rng.NextDouble() * Math.Max(milestoneTimeMs, 1.0);
            var processorNumber = bt % 8;
            for (var k = 0; k < perThreadBudget; k++)
            {
                bgT += rng.NextDouble() * 0.5;
                builder.Add(new CpuSampleEvent { TimestampMs = bgT, ProcessId = pid, ThreadId = tid, ProcessorNumber = processorNumber });
                Track();
            }
        }

        var trace = builder.Build();
        stopwatch.Stop();
        return new Result(trace, milestoneTid, milestoneTimeMs, stopwatch.Elapsed);
    }
}
