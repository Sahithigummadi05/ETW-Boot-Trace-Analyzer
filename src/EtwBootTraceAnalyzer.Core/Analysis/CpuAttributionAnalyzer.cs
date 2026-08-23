using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

/// <summary>Approximates per-process CPU busy time from sampled-profile events (samples * interval).</summary>
public static class CpuAttributionAnalyzer
{
    public static IReadOnlyDictionary<int, double> ComputeCpuBusyMsByProcess(BootTrace trace)
    {
        var totals = new Dictionary<int, double>();
        foreach (var sample in trace.CpuSamples)
        {
            totals[sample.ProcessId] = totals.GetValueOrDefault(sample.ProcessId) + trace.CpuSampleIntervalMs;
        }
        return totals;
    }
}
