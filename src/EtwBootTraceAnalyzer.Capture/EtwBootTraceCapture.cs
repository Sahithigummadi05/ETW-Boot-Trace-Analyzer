using EtwBootTraceAnalyzer.Core.Model;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace EtwBootTraceAnalyzer.Capture;

/// <summary>
/// Entry points for turning an ETW kernel session - live, or a previously captured .etl file -
/// into a <see cref="BootTrace"/>. Both paths reuse the same event wiring
/// (<see cref="KernelEventBridge"/>); only how the events arrive differs.
/// </summary>
public static class EtwBootTraceCapture
{
    private const KernelTraceEventParser.Keywords BootRelevantKeywords =
        KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.ImageLoad
        | KernelTraceEventParser.Keywords.Thread
        | KernelTraceEventParser.Keywords.ContextSwitch
        | KernelTraceEventParser.Keywords.Dispatcher
        | KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.DiskIOInit
        | KernelTraceEventParser.Keywords.Profile
        | KernelTraceEventParser.Keywords.Interrupt
        | KernelTraceEventParser.Keywords.DeferedProcedureCalls;

    /// <summary>
    /// Starts a real-time kernel session, captures for <paramref name="duration"/>, and returns
    /// the resulting trace. Requires an elevated (Administrator) process - kernel ETW sessions
    /// are not available to standard users, and only one kernel session can be active system-wide.
    /// </summary>
    public static BootTrace CaptureLive(string sessionName, TimeSpan duration, double cpuSampleIntervalMs = 1.0)
    {
        if (TraceEventSession.IsElevated() != true)
        {
            throw new InvalidOperationException(
                "Capturing a kernel ETW session requires an elevated (Administrator) process.");
        }

        var builder = new BootTraceBuilder
        {
            SessionName = sessionName,
            BootStartUtc = DateTime.UtcNow,
            CpuSampleIntervalMs = cpuSampleIntervalMs,
        };

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
        session.CpuSampleIntervalMSec = (float)cpuSampleIntervalMs;
        session.EnableKernelProvider(BootRelevantKeywords);

        var kernel = new KernelTraceEventParser(session.Source);
        new KernelEventBridge(builder).Attach(kernel);

        // session.Source.Process() blocks pumping events until the session stops, so a timer
        // stops it once the requested capture window has elapsed.
        using var stopTimer = new Timer(_ => session.Stop(), null, duration, Timeout.InfiniteTimeSpan);
        session.Source.Process();

        return builder.Build();
    }

    /// <summary>
    /// Parses a previously captured .etl file (e.g. from `wpr -start GeneralProfile -filemode`
    /// or `xperf`) into a <see cref="BootTrace"/>. This is the path to use for analyzing a boot
    /// trace captured on a different machine, since it doesn't require a live kernel session.
    /// </summary>
    public static BootTrace LoadFromEtl(string etlFilePath, double cpuSampleIntervalMs = 1.0)
    {
        var builder = new BootTraceBuilder
        {
            SessionName = Path.GetFileNameWithoutExtension(etlFilePath),
            BootStartUtc = File.GetCreationTimeUtc(etlFilePath),
            CpuSampleIntervalMs = cpuSampleIntervalMs,
        };

        using var source = new ETWTraceEventSource(etlFilePath);
        var kernel = new KernelTraceEventParser(source);
        new KernelEventBridge(builder).Attach(kernel);
        source.Process();

        return builder.Build();
    }
}
