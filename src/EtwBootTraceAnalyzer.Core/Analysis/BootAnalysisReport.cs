namespace EtwBootTraceAnalyzer.Core.Analysis;

public sealed record BootAnalysisReport
{
    public required IReadOnlyList<CriticalPathSegment> CriticalPath { get; init; }
    public required IReadOnlyList<RankedOffender> RankedOffenders { get; init; }
    public required IReadOnlyDictionary<int, double> CpuBusyMsByProcess { get; init; }
    public required IReadOnlyList<DiskIoSummary> DiskIoSummaries { get; init; }
    public required IReadOnlyDictionary<string, double> DpcIsrMsByModule { get; init; }
    public required double CriticalPathTotalMs { get; init; }
}
