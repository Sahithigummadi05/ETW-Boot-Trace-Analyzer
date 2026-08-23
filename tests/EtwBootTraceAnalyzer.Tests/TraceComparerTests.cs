using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Synthetic;
using Xunit;

namespace EtwBootTraceAnalyzer.Tests;

public class TraceComparerTests
{
    private const int ExplorerThreadId = 901;

    [Fact]
    public void Compare_ReportsTheExactImprovement_WhenTheOffendingReadsAreSpedUp()
    {
        var engine = new BootAnalysisEngine();

        // "Before": the original fixture (220ms + 120ms disk reads).
        var before = SyntheticBootTraceGenerator.Generate();
        var beforeReport = engine.Analyze(before, ExplorerThreadId, milestoneTimeMs: 460);

        // "After": DiskSvc's config file has been cached, so both reads drop from 220/120ms to 20ms each.
        // The generator derives every later timestamp from these, so explorer's thread is now
        // readied at 10+10+20+20+30+60 = 160ms instead of 460ms.
        var after = SyntheticBootTraceGenerator.Generate(diskRead1Ms: 20, diskRead2Ms: 20);
        var afterReport = engine.Analyze(after, ExplorerThreadId, milestoneTimeMs: 160);

        var comparison = TraceComparer.Compare(beforeReport, afterReport);

        Assert.Equal(450, comparison.BeforeCriticalPathMs, precision: 3);
        Assert.Equal(150, comparison.AfterCriticalPathMs, precision: 3);
        Assert.Equal(300, comparison.ImprovementMs, precision: 3);
        Assert.Equal(300.0 / 450 * 100, comparison.ImprovementPercent, precision: 3);

        var diskSvcDelta = Assert.Single(comparison.OffenderDeltas, d => d.ProcessName == "svchost.exe (DiskSvc)");
        Assert.Equal(370, diskSvcDelta.BeforeMs, precision: 3); // 220 + 120 + 30ms wrap-up
        Assert.Equal(70, diskSvcDelta.AfterMs, precision: 3); // 20 + 20 + 30ms wrap-up
        Assert.Equal(-300, diskSvcDelta.DeltaMs, precision: 3);
    }

    [Fact]
    public void Compare_OfIdenticalTraces_ShowsNoChange()
    {
        var engine = new BootAnalysisEngine();
        var trace = SyntheticBootTraceGenerator.Generate();
        var report = engine.Analyze(trace, ExplorerThreadId, milestoneTimeMs: 460);

        var comparison = TraceComparer.Compare(report, report);

        Assert.Equal(0, comparison.ImprovementMs, precision: 6);
        Assert.Equal(0, comparison.ImprovementPercent, precision: 6);
        Assert.All(comparison.OffenderDeltas, d => Assert.Equal(0, d.DeltaMs, precision: 6));
    }
}
