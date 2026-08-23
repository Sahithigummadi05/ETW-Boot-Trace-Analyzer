using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

/// <summary>Orchestrates every analyzer over a <see cref="BootTrace"/> into one report.</summary>
public sealed class BootAnalysisEngine
{
    private readonly WaitChainCriticalPathAnalyzer _criticalPathAnalyzer = new();

    /// <param name="milestoneThreadId">
    /// The thread whose "became ready" moment marks boot-complete (e.g. the thread that starts
    /// explorer.exe, or a shell-ready marker). The critical path is built backward from here.
    /// </param>
    /// <param name="milestoneTimeMs">Timestamp of the milestone, in the same relative-ms clock as the trace.</param>
    public BootAnalysisReport Analyze(BootTrace trace, int milestoneThreadId, double milestoneTimeMs)
    {
        var criticalPath = _criticalPathAnalyzer.BuildCriticalPath(trace, milestoneThreadId, milestoneTimeMs);
        var rankedOffenders = OffenderRanking.Rank(criticalPath);

        return new BootAnalysisReport
        {
            CriticalPath = criticalPath,
            RankedOffenders = rankedOffenders,
            CpuBusyMsByProcess = CpuAttributionAnalyzer.ComputeCpuBusyMsByProcess(trace),
            DiskIoSummaries = DiskIoAnalyzer.Summarize(trace),
            DpcIsrMsByModule = DpcIsrAnalyzer.TotalDurationMsByModule(trace),
            CriticalPathTotalMs = criticalPath.Count > 0 ? criticalPath[^1].EndMs - criticalPath[0].StartMs : 0,
        };
    }
}
