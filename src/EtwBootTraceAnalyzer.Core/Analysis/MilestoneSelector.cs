using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Analysis;

/// <summary>Picks the thread/timestamp to build a critical path backward from.</summary>
public static class MilestoneSelector
{
    /// <summary>
    /// Default heuristic when the caller doesn't know which thread marks "boot complete": the
    /// last ReadyThread event in the trace is the last time anything was handed the CPU, so it's
    /// a reasonable stand-in for "the moment the system finished doing boot work."
    /// </summary>
    public static ReadyThreadEvent? LastReadiedThread(BootTrace trace) =>
        trace.ReadyThreadEvents.Count == 0 ? null : trace.ReadyThreadEvents.MaxBy(e => e.TimestampMs);

    /// <summary>
    /// Finds the ReadyThread event that first wakes a thread belonging to a process whose image
    /// name contains <paramref name="processNameFragment"/> (case-insensitive) - e.g. pass
    /// "explorer.exe" to build the path back from the shell starting.
    /// </summary>
    public static ReadyThreadEvent? FirstReadyForProcess(BootTrace trace, string processNameFragment)
    {
        var matchingPids = trace.ProcessStarts
            .Where(p => p.ImageFileName.Contains(processNameFragment, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ProcessId)
            .ToHashSet();

        if (matchingPids.Count == 0)
        {
            return null;
        }

        return trace.ReadyThreadEvents
            .Where(e => matchingPids.Contains(e.AwakenedProcessId))
            .MinBy(e => e.TimestampMs);
    }
}
