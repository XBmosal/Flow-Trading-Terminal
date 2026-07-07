using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using FlowTerminal.Storage.Parquet;
using Xunit;

namespace FlowTerminal.Storage.Tests;

public sealed class RecordingManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly RecordingLayout _layout;
    private static readonly Contract Nq = new(RootSymbol.NQ, QuarterlyMonth.December, 2025);
    private static readonly DateOnly Date = new(2024, 6, 3);
    private static readonly DateTime Start = new(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc);

    public RecordingManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ft-manifest-" + Guid.NewGuid().ToString("N"));
        _layout = new RecordingLayout(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MarketEvent[] Generate(int count) =>
        new SyntheticSessionGenerator(1, Nq, Start, new SyntheticOptions { Seed = 5 }).Generate(count).ToArray();

    [Fact]
    public async Task Recorder_Writes_A_Manifest_That_Validates_The_Stream()
    {
        var events = Generate(1500);
        await using (var recorder = new ParquetMarketDataRecorder(_layout, RootSymbol.NQ, Nq.Symbol, Date, batchSize: 500))
        {
            recorder.SetTickSize(Nq.Spec.TickSize);
            foreach (var e in events) recorder.Record(e);
        }

        var dir = _layout.SessionDirectory(RootSymbol.NQ, Nq.Symbol, Date);
        var manifest = RecordingManifest.Load(dir);
        Assert.NotNull(manifest);
        Assert.Equal(RecordingManifest.CurrentSchemaVersion, manifest!.SchemaVersion);
        Assert.Equal("NQ", manifest.Root);
        Assert.Equal(0.25m, manifest.TickSize);
        Assert.Equal(events.Length, manifest.EventCount);
        Assert.Equal(events[0].ExchangeTimestampUtc, manifest.FirstEventUtc);
        Assert.Equal(events[^1].ExchangeTimestampUtc, manifest.LastEventUtc);

        // The recorded stream verifies clean.
        Assert.Null(manifest.Validate(events));
    }

    [Fact]
    public async Task Manifest_Detects_Missing_And_Tampered_Events()
    {
        var events = Generate(800);
        await using (var recorder = new ParquetMarketDataRecorder(_layout, RootSymbol.NQ, Nq.Symbol, Date))
        {
            recorder.SetTickSize(Nq.Spec.TickSize);
            foreach (var e in events) recorder.Record(e);
        }

        var manifest = RecordingManifest.Load(_layout.SessionDirectory(RootSymbol.NQ, Nq.Symbol, Date))!;

        // Missing tail → count mismatch.
        Assert.Contains("count mismatch", manifest.Validate(events.Take(700))!);

        // Same count but altered content → hash mismatch.
        var tampered = (MarketEvent[])events.Clone();
        tampered[100] = MarketEvent.Trade(1, RootSymbol.NQ, Nq.Symbol, "CME",
            tampered[100].ExchangeTimestampUtc, tampered[100].ExchangeTimestampUtc,
            tampered[100].PriceTicks + 1, tampered[100].Quantity, AggressorSide.Buy,
            tampered[100].ExchangeSequence);
        Assert.Contains("hash mismatch", manifest.Validate(tampered)!);
    }

    [Fact]
    public void Missing_Or_Corrupt_Manifest_Loads_As_Null_Not_A_Crash()
    {
        var dir = Path.Combine(_dir, "empty");
        Assert.Null(RecordingManifest.Load(dir));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, RecordingManifest.FileName), "{ not json ]");
        Assert.Null(RecordingManifest.Load(dir));
    }
}
