using BenchmarkDotNet.Attributes;
using FlowTerminal.Analytics.Bars;
using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Analytics.Delta;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Analytics.Profiles;
using FlowTerminal.Charting.Dom;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using FlowTerminal.OrderBook;

namespace FlowTerminal.Benchmarks;

/// <summary>
/// Hot-path throughput for the analytics engines, driven by one pre-generated
/// deterministic synthetic session (10k canonical events) so runs are comparable.
/// Measures per-batch cost of: bar building, footprint aggregation, CVD, volume
/// profile, Big Trade detection+aggregation, and DOM ladder construction.
/// </summary>
[MemoryDiagnoser]
public class AnalyticsBenchmark
{
    private FlowTerminal.Domain.Events.MarketEvent[] _events = Array.Empty<FlowTerminal.Domain.Events.MarketEvent>();
    private MarketByPriceOrderBook _book = new();

    [GlobalSetup]
    public void Setup()
    {
        var contract = new Contract(RootSymbol.NQ, QuarterlyMonth.December, 2025);
        var start = new DateTime(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc);
        _events = new SyntheticSessionGenerator(1, contract, start,
            new SyntheticOptions { Seed = 7, StartPrice = 20_000m }).Generate(10_000).ToArray();

        _book = new MarketByPriceOrderBook();
        foreach (var e in _events) _book.Apply(e);
    }

    [Benchmark]
    public int BarBuilding_TimeBars()
    {
        var bars = BarAggregator.Time(TimeSpan.FromMinutes(1));
        int completed = 0;
        foreach (var e in _events)
            if (bars.AddTrade(e) is not null) completed++;
        return completed;
    }

    [Benchmark]
    public long Footprint_Aggregation()
    {
        var fp = new Footprint();
        foreach (var e in _events) fp.AddTrade(e);
        return fp.TotalVolume;
    }

    [Benchmark]
    public long Cvd_Update()
    {
        var cvd = new CvdCalculator();
        foreach (var e in _events) cvd.Add(e);
        return cvd.CumulativeDelta;
    }

    [Benchmark]
    public long VolumeProfile_Update()
    {
        var profile = new VolumeProfile();
        foreach (var e in _events) profile.AddTrade(e);
        return profile.TotalVolume;
    }

    [Benchmark]
    public long BigTrades_Classify_And_Aggregate()
    {
        var detector = BigTradeDetector.For(RootSymbol.NQ);
        var book = new MarketByPriceOrderBook();
        foreach (var e in _events)
        {
            book.Apply(e);
            if (e.Type == FlowTerminal.Domain.Events.MarketEventType.Trade)
                detector.OnTrade(e, book.BestBidTicks, book.BestAskTicks, book.IsValid);
        }
        detector.Flush();
        return detector.DiagnosticsSnapshot().TradesProcessed;
    }

    [Benchmark]
    public int Dom_Ladder_Build()
    {
        var profile = new VolumeProfile();
        foreach (var e in _events) profile.AddTrade(e);
        return ReadOnlyDom.Build(_book, profile, 12).Count;
    }
}
