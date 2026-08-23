using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

/// <summary>
/// Reconstructs the boot's critical path by walking Thread/ReadyThread wake-up edges backward
/// from a milestone (e.g. the thread that started the shell). This is the same root-cause
/// technique WPA's "Ready Thread"/wait-chain analysis uses: at every point a thread only
/// resumed because some other thread (or an interrupt) woke it, so following that edge
/// backward turns "the trace was busy" into "here is specifically what it was waiting on, and
/// for how long."
/// </summary>
public sealed class WaitChainCriticalPathAnalyzer
{
    /// <summary>
    /// Matching window for correlating a Disk I/O completion (or interrupt) to the ReadyThread
    /// event it triggered. In a real trace the two fire within the same DPC, effectively
    /// simultaneously; a couple of ETW ticks of slack absorbs clock-source rounding between
    /// providers.
    /// </summary>
    private const double CorrelationWindowMs = 2.0;

    /// <summary>Windows reserves pid 0 for the Idle process; a readying thread there means an interrupt fired on an otherwise-idle core.</summary>
    private const int IdleProcessId = 0;

    public IReadOnlyList<CriticalPathSegment> BuildCriticalPath(
        BootTrace trace,
        int targetThreadId,
        double targetTimeMs,
        int maxSegments = 500)
    {
        var index = new TraceIndex(trace);
        var segments = new List<CriticalPathSegment>();
        var visited = new HashSet<(int ThreadId, double TimeMs)>();

        var currentThreadId = targetThreadId;
        var currentTimeMs = targetTimeMs;

        for (var i = 0; i < maxSegments; i++)
        {
            if (!visited.Add((currentThreadId, currentTimeMs)))
            {
                break; // cycle guard: two threads readying each other in a loop
            }

            var readyEvent = index.LatestReadyAtOrBefore(currentThreadId, currentTimeMs);
            if (readyEvent is null)
            {
                break; // no earlier wake-up recorded; we've walked back to the start of what's explainable
            }

            var diskCompletion = index.NearestDiskCompletion(readyEvent.ReadyingThreadId, readyEvent.TimestampMs, CorrelationWindowMs);
            if (diskCompletion is not null)
            {
                var processName = trace.ProcessName(diskCompletion.IssuingProcessId);
                var fileSuffix = diskCompletion.FileName is { Length: > 0 } f ? $" ({f})" : "";
                segments.Add(new CriticalPathSegment
                {
                    StartMs = diskCompletion.IssueTimeMs,
                    EndMs = diskCompletion.TimestampMs,
                    ProcessId = diskCompletion.IssuingProcessId,
                    ProcessName = processName,
                    Cause = CriticalPathCause.DiskIo,
                    Explanation =
                        $"{processName} (pid {diskCompletion.IssuingProcessId}) blocked on a " +
                        $"{diskCompletion.Kind.ToString().ToLowerInvariant()} for {diskCompletion.DurationMs:F0} ms{fileSuffix}",
                });

                currentThreadId = diskCompletion.IssuingThreadId;
                currentTimeMs = diskCompletion.IssueTimeMs;
                continue;
            }

            if (readyEvent.ReadyingProcessId == IdleProcessId)
            {
                var interrupt = index.NearestInterrupt(readyEvent.TimestampMs, CorrelationWindowMs);
                if (interrupt is not null)
                {
                    segments.Add(new CriticalPathSegment
                    {
                        StartMs = interrupt.TimestampMs - interrupt.DurationMs,
                        EndMs = interrupt.TimestampMs,
                        ProcessId = IdleProcessId,
                        ProcessName = interrupt.RoutineModule,
                        Cause = CriticalPathCause.Interrupt,
                        Explanation =
                            $"{interrupt.RoutineModule} held CPU {interrupt.ProcessorNumber} for " +
                            $"{interrupt.DurationMs:F2} ms servicing a {interrupt.Kind.ToString().ToUpperInvariant()}",
                    });
                    // Interrupts are exogenous (hardware-driven) - there's no earlier "why" to chase.
                    break;
                }
            }

            var scheduledIn = index.LatestScheduleInAtOrBefore(readyEvent.ReadyingThreadId, readyEvent.TimestampMs);
            var segmentStartMs = scheduledIn?.TimestampMs ?? Math.Max(0, readyEvent.TimestampMs - trace.CpuSampleIntervalMs);
            var cpuProcessName = trace.ProcessName(readyEvent.ReadyingProcessId);

            segments.Add(new CriticalPathSegment
            {
                StartMs = segmentStartMs,
                EndMs = readyEvent.TimestampMs,
                ProcessId = readyEvent.ReadyingProcessId,
                ProcessName = cpuProcessName,
                Cause = CriticalPathCause.CpuExecution,
                Explanation =
                    $"{cpuProcessName} (pid {readyEvent.ReadyingProcessId}) ran on CPU for " +
                    $"{readyEvent.TimestampMs - segmentStartMs:F0} ms before waking the next stage",
            });

            if (scheduledIn is null)
            {
                break; // can't establish what scheduled this thread in; stop here
            }

            currentThreadId = readyEvent.ReadyingThreadId;
            currentTimeMs = scheduledIn.TimestampMs;
        }

        segments.Reverse(); // walked backward in time; report it chronologically
        return segments;
    }

    /// <summary>
    /// Groups each event stream by the thread id we'll be looking it up by, sorted by
    /// timestamp, so each hop of the backward walk is a binary search instead of a scan over
    /// the full (potentially multi-million-row) trace.
    /// </summary>
    private sealed class TraceIndex
    {
        private readonly Dictionary<int, double[]> _readyTimestampsByAwakenedThread;
        private readonly Dictionary<int, ReadyThreadEvent[]> _readyEventsByAwakenedThread;

        private readonly Dictionary<int, double[]> _switchInTimestampsByNewThread;
        private readonly Dictionary<int, ContextSwitchEvent[]> _switchInEventsByNewThread;

        private readonly Dictionary<int, double[]> _diskTimestampsByIssuingThread;
        private readonly Dictionary<int, DiskIoEvent[]> _diskEventsByIssuingThread;

        private readonly double[] _interruptTimestamps;
        private readonly DpcIsrEvent[] _interruptEvents;

        public TraceIndex(BootTrace trace)
        {
            (_readyTimestampsByAwakenedThread, _readyEventsByAwakenedThread) =
                GroupSorted(trace.ReadyThreadEvents, e => e.AwakenedThreadId);

            (_switchInTimestampsByNewThread, _switchInEventsByNewThread) =
                GroupSorted(trace.ContextSwitches, e => e.NewThreadId);

            (_diskTimestampsByIssuingThread, _diskEventsByIssuingThread) =
                GroupSorted(trace.DiskIoEvents, e => e.IssuingThreadId);

            var interrupts = trace.DpcIsrEvents.OrderBy(e => e.TimestampMs).ToArray();
            _interruptTimestamps = interrupts.Select(e => e.TimestampMs).ToArray();
            _interruptEvents = interrupts;
        }

        public ReadyThreadEvent? LatestReadyAtOrBefore(int threadId, double timeMs) =>
            LatestAtOrBefore(_readyTimestampsByAwakenedThread, _readyEventsByAwakenedThread, threadId, timeMs);

        public ContextSwitchEvent? LatestScheduleInAtOrBefore(int threadId, double timeMs) =>
            LatestAtOrBefore(_switchInTimestampsByNewThread, _switchInEventsByNewThread, threadId, timeMs);

        public DiskIoEvent? NearestDiskCompletion(int issuingThreadId, double timeMs, double windowMs) =>
            Nearest(_diskTimestampsByIssuingThread, _diskEventsByIssuingThread, issuingThreadId, timeMs, windowMs);

        public DpcIsrEvent? NearestInterrupt(double timeMs, double windowMs) =>
            NearestInArray(_interruptTimestamps, _interruptEvents, timeMs, windowMs);

        private static T? LatestAtOrBefore<T>(
            Dictionary<int, double[]> timestampsByKey,
            Dictionary<int, T[]> eventsByKey,
            int key,
            double timeMs)
            where T : class
        {
            if (!timestampsByKey.TryGetValue(key, out var timestamps))
            {
                return null;
            }
            var idx = LastIndexAtOrBefore(timestamps, timeMs);
            return idx < 0 ? null : eventsByKey[key][idx];
        }

        private static T? Nearest<T>(
            Dictionary<int, double[]> timestampsByKey,
            Dictionary<int, T[]> eventsByKey,
            int key,
            double timeMs,
            double windowMs)
            where T : class
        {
            if (!timestampsByKey.TryGetValue(key, out var timestamps))
            {
                return null;
            }
            return NearestInArray(timestamps, eventsByKey[key], timeMs, windowMs);
        }

        private static T? NearestInArray<T>(double[] timestamps, T[] events, double timeMs, double windowMs)
            where T : class
        {
            if (timestamps.Length == 0)
            {
                return null;
            }

            var afterOrAt = LastIndexAtOrBefore(timestamps, timeMs) + 1; // first index with timestamp > timeMs (candidate "after")
            T? best = null;
            var bestDelta = double.MaxValue;

            // The nearest match is either the closest timestamp <= timeMs or the closest one > timeMs;
            // both live immediately around the split point found by binary search.
            for (var idx = afterOrAt - 1; idx <= afterOrAt && idx >= 0 && idx < timestamps.Length; idx++)
            {
                if (idx < 0)
                {
                    continue;
                }
                var delta = Math.Abs(timestamps[idx] - timeMs);
                if (delta <= windowMs && delta < bestDelta)
                {
                    best = events[idx];
                    bestDelta = delta;
                }
            }
            return best;
        }

        /// <summary>Index of the rightmost entry with timestamp &lt;= value, or -1 if none.</summary>
        private static int LastIndexAtOrBefore(double[] sortedTimestamps, double value)
        {
            var lo = 0;
            var hi = sortedTimestamps.Length - 1;
            var result = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (sortedTimestamps[mid] <= value)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return result;
        }

        private static (Dictionary<int, double[]> Timestamps, Dictionary<int, TEvent[]> Events) GroupSorted<TEvent>(
            IReadOnlyList<TEvent> events,
            Func<TEvent, int> keySelector)
            where TEvent : BootEvent
        {
            var grouped = events
                .GroupBy(keySelector)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.TimestampMs).ToArray());
            var timestamps = grouped.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.TimestampMs).ToArray());
            return (timestamps, grouped);
        }
    }
}
