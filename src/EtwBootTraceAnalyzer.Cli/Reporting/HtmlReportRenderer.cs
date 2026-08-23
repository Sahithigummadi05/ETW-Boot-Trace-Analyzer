using System.Net;
using System.Text;
using EtwBootTraceAnalyzer.Core.Analysis;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Cli.Reporting;

/// <summary>
/// Renders a <see cref="BootAnalysisReport"/> as a single self-contained HTML file - a visual
/// timeline of the critical path plus the same tables <see cref="ConsoleReportPrinter"/> prints,
/// styled instead of monospaced. No external CSS/JS: the file opens standalone in a browser and
/// is safe to share on its own.
/// </summary>
internal static class HtmlReportRenderer
{
    public static string Render(BootTrace trace, BootAnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>Boot trace report - {{Enc(trace.SessionName)}}</title>
            <style>
              :root {
                color-scheme: light dark;
                --bg: #0f1115; --panel: #171a21; --border: #2a2f3a; --text: #e6e8eb; --muted: #9aa3b2;
                --cpu: #4f8cff; --disk: #ff8a4c; --intr: #b366ff; --accent: #33c17a;
              }
              @media (prefers-color-scheme: light) {
                :root { --bg: #f6f7f9; --panel: #ffffff; --border: #e2e5ea; --text: #1a1d23; --muted: #5a6472; }
              }
              body { background: var(--bg); color: var(--text); font-family: -apple-system, Segoe UI, Roboto, sans-serif; margin: 0; padding: 2rem; }
              h1 { font-size: 1.4rem; margin: 0 0 0.25rem; }
              .subtitle { color: var(--muted); margin: 0 0 1.5rem; font-size: 0.9rem; }
              .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 1.25rem; margin-bottom: 1.5rem; }
              h2 { font-size: 1.05rem; margin: 0 0 1rem; }
              table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
              th, td { text-align: left; padding: 0.4rem 0.6rem; border-bottom: 1px solid var(--border); }
              th { color: var(--muted); font-weight: 600; }
              tr:last-child td { border-bottom: none; }
              .timeline { display: flex; height: 34px; border-radius: 6px; overflow: hidden; border: 1px solid var(--border); margin-bottom: 0.75rem; }
              .seg { height: 100%; min-width: 2px; }
              .seg.cpu { background: var(--cpu); } .seg.disk { background: var(--disk); } .seg.intr { background: var(--intr); }
              .legend { display: flex; gap: 1.25rem; font-size: 0.8rem; color: var(--muted); margin-bottom: 1rem; }
              .legend span { display: inline-flex; align-items: center; gap: 0.35rem; }
              .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
              .dot.cpu { background: var(--cpu); } .dot.disk { background: var(--disk); } .dot.intr { background: var(--intr); }
              .bar-track { background: var(--border); border-radius: 4px; height: 8px; overflow: hidden; margin-top: 0.25rem; }
              .bar-fill { background: var(--accent); height: 100%; }
              .mono { font-variant-numeric: tabular-nums; }
              .muted { color: var(--muted); }
            </style>
            </head>
            <body>
            <h1>Boot trace report</h1>
            <p class="subtitle">{{Enc(trace.SessionName)}} &middot; {{trace.TotalEventCount:N0}} events analyzed</p>
            """);

        AppendCriticalPath(sb, report);
        AppendOffenders(sb, report);
        AppendDiskIo(sb, report);
        AppendDpcIsr(sb, report);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendCriticalPath(StringBuilder sb, BootAnalysisReport report)
    {
        sb.Append($"""
            <div class="panel">
            <h2>Critical path &mdash; {report.CriticalPathTotalMs:F0} ms across {report.CriticalPath.Count} hop(s)</h2>
            <div class="legend">
              <span><span class="dot cpu"></span>CPU</span>
              <span><span class="dot disk"></span>Disk I/O</span>
              <span><span class="dot intr"></span>Interrupt</span>
            </div>
            <div class="timeline">
            """);

        var total = Math.Max(report.CriticalPathTotalMs, 0.001);
        foreach (var seg in report.CriticalPath)
        {
            var widthPct = 100.0 * seg.DurationMs / total;
            var cssClass = CauseClass(seg.Cause);
            sb.Append($"<div class=\"seg {cssClass}\" style=\"width:{widthPct:F2}%\" title=\"{Enc(seg.Explanation)}\"></div>");
        }
        sb.Append("</div>");

        sb.Append("<table><tr><th>Start</th><th>End</th><th>Duration</th><th>Cause</th><th>What happened</th></tr>");
        foreach (var seg in report.CriticalPath)
        {
            sb.Append($"""
                <tr>
                  <td class="mono">{seg.StartMs:F1} ms</td>
                  <td class="mono">{seg.EndMs:F1} ms</td>
                  <td class="mono">{seg.DurationMs:F0} ms</td>
                  <td>{seg.Cause}</td>
                  <td>{Enc(seg.Explanation)}</td>
                </tr>
                """);
        }
        sb.Append("</table></div>");
    }

    private static void AppendOffenders(StringBuilder sb, BootAnalysisReport report)
    {
        sb.Append("<div class=\"panel\"><h2>Top offenders</h2><table><tr><th>Process</th><th>Attributed delay</th><th>Share of critical path</th><th>Why</th></tr>");
        foreach (var offender in report.RankedOffenders.Take(10))
        {
            var reasons = string.Join("<br>", offender.TopReasons.Select(r => Enc(r)));
            sb.Append($"""
                <tr>
                  <td>{Enc(offender.ProcessName)} <span class="muted">(pid {offender.ProcessId})</span></td>
                  <td class="mono">{offender.AttributedDelayMs:F0} ms</td>
                  <td>
                    {offender.PercentOfCriticalPath:F1}%
                    <div class="bar-track"><div class="bar-fill" style="width:{offender.PercentOfCriticalPath:F1}%"></div></div>
                  </td>
                  <td style="font-size:0.82rem">{reasons}</td>
                </tr>
                """);
        }
        sb.Append("</table></div>");
    }

    private static void AppendDiskIo(StringBuilder sb, BootAnalysisReport report)
    {
        if (report.DiskIoSummaries.Count == 0)
        {
            return;
        }
        sb.Append("<div class=\"panel\"><h2>Disk I/O summary</h2><table><tr><th>Process</th><th>Total wait</th><th>Operations</th><th>Longest stall</th></tr>");
        foreach (var s in report.DiskIoSummaries.Take(10))
        {
            var longest = s.LongestStall is { } io ? $"{io.DurationMs:F0} ms ({Enc(io.FileName ?? "?")})" : "-";
            sb.Append($"""
                <tr>
                  <td>{Enc(s.ProcessName)} <span class="muted">(pid {s.ProcessId})</span></td>
                  <td class="mono">{s.TotalWaitMs:F0} ms</td>
                  <td class="mono">{s.OperationCount}</td>
                  <td>{longest}</td>
                </tr>
                """);
        }
        sb.Append("</table></div>");
    }

    private static void AppendDpcIsr(StringBuilder sb, BootAnalysisReport report)
    {
        if (report.DpcIsrMsByModule.Count == 0)
        {
            return;
        }
        sb.Append("<div class=\"panel\"><h2>DPC/ISR time by driver module</h2><table><tr><th>Module</th><th>Total time</th></tr>");
        foreach (var (module, ms) in report.DpcIsrMsByModule.OrderByDescending(kv => kv.Value).Take(10))
        {
            sb.Append($"<tr><td>{Enc(module)}</td><td class=\"mono\">{ms:F2} ms</td></tr>");
        }
        sb.Append("</table></div>");
    }

    private static string CauseClass(CriticalPathCause cause) => cause switch
    {
        CriticalPathCause.DiskIo => "disk",
        CriticalPathCause.Interrupt => "intr",
        _ => "cpu",
    };

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
