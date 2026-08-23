using System.Text.Json;
using System.Text.Json.Serialization;
using EtwBootTraceAnalyzer.Core.Events;
using EtwBootTraceAnalyzer.Core.Model;

namespace EtwBootTraceAnalyzer.Core.Ingestion;

/// <summary>
/// Plain-old-data mirror of <see cref="BootTrace"/> for JSON import/export. Exists so a trace
/// captured on Windows (EtwBootTraceAnalyzer.Capture) can be handed to this pipeline on any OS,
/// and so the analysis engine has a stable on-disk format independent of the SQLite schema.
/// </summary>
public sealed class PortableTraceFile
{
    public string SessionName { get; set; } = "";
    public DateTime BootStartUtc { get; set; }
    public double CpuSampleIntervalMs { get; set; } = 1.0;
    public List<ProcessStartEvent> ProcessStarts { get; set; } = [];
    public List<ProcessStopEvent> ProcessStops { get; set; } = [];
    public List<CpuSampleEvent> CpuSamples { get; set; } = [];
    public List<ContextSwitchEvent> ContextSwitches { get; set; } = [];
    public List<ReadyThreadEvent> ReadyThreadEvents { get; set; } = [];
    public List<DiskIoEvent> DiskIoEvents { get; set; } = [];
    public List<DpcIsrEvent> DpcIsrEvents { get; set; } = [];
}

public static class JsonTraceIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Export(BootTrace trace, string path)
    {
        var dto = new PortableTraceFile
        {
            SessionName = trace.SessionName,
            BootStartUtc = trace.BootStartUtc,
            CpuSampleIntervalMs = trace.CpuSampleIntervalMs,
            ProcessStarts = trace.ProcessStarts.ToList(),
            ProcessStops = trace.ProcessStops.ToList(),
            CpuSamples = trace.CpuSamples.ToList(),
            ContextSwitches = trace.ContextSwitches.ToList(),
            ReadyThreadEvents = trace.ReadyThreadEvents.ToList(),
            DiskIoEvents = trace.DiskIoEvents.ToList(),
            DpcIsrEvents = trace.DpcIsrEvents.ToList(),
        };
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, dto, Options);
    }

    public static BootTrace Import(string path)
    {
        using var stream = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize<PortableTraceFile>(stream, Options)
                  ?? throw new InvalidDataException($"'{path}' did not contain a valid boot trace export.");

        var builder = new BootTraceBuilder
        {
            SessionName = dto.SessionName,
            BootStartUtc = dto.BootStartUtc,
            CpuSampleIntervalMs = dto.CpuSampleIntervalMs,
        };
        foreach (var e in dto.ProcessStarts) builder.Add(e);
        foreach (var e in dto.ProcessStops) builder.Add(e);
        foreach (var e in dto.CpuSamples) builder.Add(e);
        foreach (var e in dto.ContextSwitches) builder.Add(e);
        foreach (var e in dto.ReadyThreadEvents) builder.Add(e);
        foreach (var e in dto.DiskIoEvents) builder.Add(e);
        foreach (var e in dto.DpcIsrEvents) builder.Add(e);
        return builder.Build();
    }
}
