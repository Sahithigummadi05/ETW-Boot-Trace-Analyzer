using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Synthetic;
using Xunit;

namespace EtwBootTraceAnalyzer.Tests;

public class WaitChainCriticalPathAnalyzerTests
{
    // Thread ids from SyntheticBootTraceGenerator's internal boot narrative.
    private const int ExplorerThreadId = 901;
    private const int DiskServicePid = 700;
    private const int IndexingServicePid = 800;

    [Fact]
    public void BuildCriticalPath_WalksBackwardThroughDiskAndCpuHops_InChronologicalOrder()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var analyzer = new WaitChainCriticalPathAnalyzer();

        var path = analyzer.BuildCriticalPath(trace, ExplorerThreadId, targetTimeMs: 460);

        Assert.Equal(6, path.Count);
        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(path[i].StartMs >= path[i - 1].EndMs - 1e-9, "segments must be chronological and non-overlapping");
        }

        Assert.Equal(CriticalPathCause.DiskIo, path[2].Cause);
        Assert.Equal(220, path[2].DurationMs, precision: 3);
        Assert.Equal(DiskServicePid, path[2].ProcessId);

        Assert.Equal(CriticalPathCause.DiskIo, path[3].Cause);
        Assert.Equal(120, path[3].DurationMs, precision: 3);

        Assert.Equal(CriticalPathCause.CpuExecution, path[^1].Cause);
        Assert.Equal(IndexingServicePid, path[^1].ProcessId);
        Assert.Equal(60, path[^1].DurationMs, precision: 3);
    }

    [Fact]
    public void BuildCriticalPath_ProducesHumanReadableExplanationForTheDiskStall()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var analyzer = new WaitChainCriticalPathAnalyzer();

        var path = analyzer.BuildCriticalPath(trace, ExplorerThreadId, targetTimeMs: 460);
        var longestDiskSegment = path.Where(s => s.Cause == CriticalPathCause.DiskIo).MaxBy(s => s.DurationMs)!;

        Assert.Contains("svchost.exe (DiskSvc)", longestDiskSegment.Explanation);
        Assert.Contains("220 ms", longestDiskSegment.Explanation);
        Assert.Contains("diskservice.dll", longestDiskSegment.Explanation);
    }

    [Fact]
    public void BuildCriticalPath_StopsAndDoesNotThrow_WhenNoReadyEventExplainsTheThread()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var analyzer = new WaitChainCriticalPathAnalyzer();

        // A thread id that never appears as an AwakenedThreadId anywhere in the trace.
        var path = analyzer.BuildCriticalPath(trace, targetThreadId: 999_999, targetTimeMs: 100);

        Assert.Empty(path);
    }

    [Fact]
    public void OffenderRanking_RanksTheDiskBoundServiceAboveTheCpuBoundOne()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var analyzer = new WaitChainCriticalPathAnalyzer();
        var path = analyzer.BuildCriticalPath(trace, ExplorerThreadId, targetTimeMs: 460);

        var ranked = OffenderRanking.Rank(path);

        Assert.Equal(DiskServicePid, ranked[0].ProcessId);
        Assert.Equal(370, ranked[0].AttributedDelayMs, precision: 3); // 220 + 120 + 30ms wrap-up
        Assert.True(ranked[0].PercentOfCriticalPath > 80, "the dominant offender should account for most of the 450ms critical path");

        Assert.Contains(ranked, r => r.ProcessId == IndexingServicePid);
    }

    [Fact]
    public void BootAnalysisEngine_ProducesAFullReportFromTheSyntheticTrace()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var engine = new BootAnalysisEngine();

        var report = engine.Analyze(trace, ExplorerThreadId, milestoneTimeMs: 460);

        Assert.Equal(450, report.CriticalPathTotalMs, precision: 3);
        Assert.NotEmpty(report.RankedOffenders);
        Assert.Equal(DiskServicePid, report.RankedOffenders[0].ProcessId);
    }
}
