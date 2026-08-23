using EtwBootTraceAnalyzer.Core.Ingestion;
using EtwBootTraceAnalyzer.Core.Storage;
using EtwBootTraceAnalyzer.Core.Synthetic;
using Xunit;

namespace EtwBootTraceAnalyzer.Tests;

public class StorageRoundTripTests
{
    [Fact]
    public void SqliteTraceStore_SaveThenLoad_ReturnsAnEquivalentTrace()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var dbPath = Path.Combine(Path.GetTempPath(), $"etw-boot-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteTraceStore(dbPath))
            {
                store.Save(trace);
            }

            using var reload = new SqliteTraceStore(dbPath);
            var loaded = reload.Load(trace.SessionName);

            Assert.Equal(trace.ProcessStarts.Count, loaded.ProcessStarts.Count);
            Assert.Equal(trace.DiskIoEvents.Count, loaded.DiskIoEvents.Count);
            Assert.Equal(trace.ReadyThreadEvents.Count, loaded.ReadyThreadEvents.Count);
            Assert.Equal(trace.ContextSwitches.Count, loaded.ContextSwitches.Count);
            Assert.Equal(trace.CpuSamples.Count, loaded.CpuSamples.Count);
            Assert.Equal(trace.TotalEventCount, loaded.TotalEventCount);

            var originalDiskIo = trace.DiskIoEvents.OrderBy(e => e.TimestampMs).First();
            var loadedDiskIo = loaded.DiskIoEvents.OrderBy(e => e.TimestampMs).First();
            Assert.Equal(originalDiskIo.FileName, loadedDiskIo.FileName);
            Assert.Equal(originalDiskIo.DurationMs, loadedDiskIo.DurationMs, precision: 6);
            Assert.Equal(originalDiskIo.Kind, loadedDiskIo.Kind);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void JsonTraceIo_ExportThenImport_ReturnsAnEquivalentTrace()
    {
        var trace = SyntheticBootTraceGenerator.Generate();
        var jsonPath = Path.Combine(Path.GetTempPath(), $"etw-boot-test-{Guid.NewGuid():N}.json");
        try
        {
            JsonTraceIo.Export(trace, jsonPath);
            var loaded = JsonTraceIo.Import(jsonPath);

            Assert.Equal(trace.SessionName, loaded.SessionName);
            Assert.Equal(trace.TotalEventCount, loaded.TotalEventCount);
            Assert.Equal(trace.ProcessStarts[0].ImageFileName, loaded.ProcessStarts[0].ImageFileName);
            Assert.Equal(trace.DpcIsrEvents.Count, loaded.DpcIsrEvents.Count);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }
}
