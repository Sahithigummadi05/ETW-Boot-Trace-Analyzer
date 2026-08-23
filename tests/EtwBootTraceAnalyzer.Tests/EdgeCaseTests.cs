using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;
using EtwBootTraceAnalyzer.Core.Synthetic;
using Xunit;

namespace EtwBootTraceAnalyzer.Tests;

public class EdgeCaseTests
{
    [Fact]
    public void BuildCriticalPath_Terminates_WhenTwoThreadsReadyEachOtherInACycle()
    {
        // Thread 1 is "readied by" thread 2 at t=10, and thread 2 is "readied by" thread 1 at
        // t=10 as well - a malformed/cyclic trace that a naive backward walk could loop on
        // forever. The cycle guard (visited (threadId, timeMs) pairs) must catch this.
        var builder = new BootTraceBuilder { SessionName = "cycle", BootStartUtc = DateTime.UtcNow };
        builder.Add(new ReadyThreadEvent { TimestampMs = 10, AwakenedThreadId = 1, AwakenedProcessId = 100, ReadyingThreadId = 2, ReadyingProcessId = 200 });
        builder.Add(new ReadyThreadEvent { TimestampMs = 10, AwakenedThreadId = 2, AwakenedProcessId = 200, ReadyingThreadId = 1, ReadyingProcessId = 100 });
        builder.Add(new ContextSwitchEvent { TimestampMs = 10, ProcessorNumber = 0, OldThreadId = 1, OldProcessId = 100, NewThreadId = 2, NewProcessId = 200, OldThreadWaitReason = "Executive" });
        builder.Add(new ContextSwitchEvent { TimestampMs = 10, ProcessorNumber = 0, OldThreadId = 2, OldProcessId = 200, NewThreadId = 1, NewProcessId = 100, OldThreadWaitReason = "Executive" });
        var trace = builder.Build();

        var analyzer = new WaitChainCriticalPathAnalyzer();
        var path = analyzer.BuildCriticalPath(trace, targetThreadId: 1, targetTimeMs: 10, maxSegments: 500);

        // The point of the test is that this returns at all (within the xunit default timeout)
        // rather than spinning forever; the exact segment count is secondary.
        Assert.True(path.Count < 500);
    }

    [Fact]
    public void MilestoneSelector_LastReadiedThread_ReturnsNull_ForATraceWithNoReadyEvents()
    {
        var builder = new BootTraceBuilder { SessionName = "empty", BootStartUtc = DateTime.UtcNow };
        var trace = builder.Build();

        Assert.Null(MilestoneSelector.LastReadiedThread(trace));
    }

    [Fact]
    public void MilestoneSelector_FirstReadyForProcess_ReturnsNull_WhenNoProcessMatches()
    {
        var trace = SyntheticBootTraceGenerator.Generate();

        Assert.Null(MilestoneSelector.FirstReadyForProcess(trace, "not-a-real-process.exe"));
    }

    [Fact]
    public void MilestoneSelector_FirstReadyForProcess_FindsExplorer()
    {
        var trace = SyntheticBootTraceGenerator.Generate();

        var milestone = MilestoneSelector.FirstReadyForProcess(trace, "explorer");

        Assert.NotNull(milestone);
        Assert.Equal(901, milestone!.AwakenedThreadId);
        Assert.Equal(460, milestone.TimestampMs, precision: 3);
    }

    [Fact]
    public void LargeScaleTraceGenerator_ProducesAnAnalyzableTraceNearTheRequestedSize()
    {
        var result = LargeScaleTraceGenerator.Generate(targetEventCount: 20_000, chainHops: 10, seed: 7);

        // "Near" because the background-noise budget is divided evenly across a fixed thread
        // count and rounds down, so the actual total can undershoot the target slightly.
        Assert.InRange(result.Trace.TotalEventCount, 15_000, 21_000);

        var report = new BootAnalysisEngine().Analyze(result.Trace, result.MilestoneThreadId, result.MilestoneTimeMs);
        Assert.NotEmpty(report.CriticalPath);
        Assert.NotEmpty(report.RankedOffenders);
    }
}
