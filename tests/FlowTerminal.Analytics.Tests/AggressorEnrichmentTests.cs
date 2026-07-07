using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Analytics.Delta;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Analytics.Profiles;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using Xunit;

namespace FlowTerminal.Analytics.Tests;

/// <summary>
/// The "one classification truth" contract: when a feed supplies no native aggressor
/// side, the shared classifier's result is enriched back onto the event, so CVD, the
/// volume profile, the footprint and the Big Trades engine all agree — instead of Big
/// Trades inferring sides while everything else treats the same trades as Unknown.
/// </summary>
public class AggressorEnrichmentTests
{
    private static readonly DateTime T = new(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc);

    private static MarketEvent RawTrade(double sec, long price, long qty) =>
        MarketEvent.Trade(1, RootSymbol.NQ, "NQZ5", "CME", T.AddSeconds(sec), T.AddSeconds(sec),
            price, qty, AggressorSide.Unknown); // deliberately NO native side, NO flags

    [Fact]
    public void Enriched_Event_Carries_Classified_Side_And_Inferred_Flag()
    {
        var detector = new BigTradeDetector();
        var raw = RawTrade(0, 101, 5);                       // at the ask → inferred buy
        var classified = detector.OnTrade(raw, bestBidTicks: 99, bestAskTicks: 101, bookValid: true);
        var enriched = AggressorEnrichment.Enrich(raw, classified);

        Assert.Equal(AggressorSide.Buy, enriched.Aggressor);
        Assert.True(enriched.HasFlag(MarketEventFlags.AggressorInferred)); // labelled Estimated
        Assert.False(enriched.HasFlag(MarketEventFlags.AggressorSupplied));
        Assert.Equal(raw.PriceTicks, enriched.PriceTicks);
        Assert.Equal(raw.ExchangeSequence, enriched.ExchangeSequence);
    }

    [Fact]
    public void Native_Side_And_Unresolvable_Unknown_Pass_Through_Unchanged()
    {
        var detector = new BigTradeDetector();

        var native = MarketEvent.Trade(1, RootSymbol.NQ, "NQZ5", "CME", T, T, 100, 5,
            AggressorSide.Sell, flags: MarketEventFlags.AggressorSupplied);
        var c1 = detector.OnTrade(native, 99, 101, true);
        Assert.Equal(native, AggressorEnrichment.Enrich(native, c1)); // untouched

        // Inside the spread, no prior trade, book valid → honestly unknown, stays unknown.
        var detector2 = new BigTradeDetector();
        var unknowable = RawTrade(1, 100, 5);
        var c2 = detector2.OnTrade(unknowable, 98, 102, true);
        var still = AggressorEnrichment.Enrich(unknowable, c2);
        Assert.Equal(AggressorSide.Unknown, still.Aggressor);
        Assert.False(still.HasFlag(MarketEventFlags.AggressorInferred));
    }

    [Fact]
    public void Cvd_Profile_Footprint_And_BigTrades_Agree_On_An_Inference_Only_Stream()
    {
        var detector = new BigTradeDetector();
        var cvd = new CvdCalculator();
        var profile = new VolumeProfile();
        var footprint = new Footprint();

        // 60 trades alternating at the ask (buys) and at the bid (sells), sizes varying;
        // NONE carry a native side — everything must come from the shared classifier.
        long expectedBuy = 0, expectedSell = 0;
        for (int i = 0; i < 60; i++)
        {
            bool atAsk = i % 3 != 0;                          // 40 buys, 20 sells
            long qty = 1 + i % 7;
            var raw = RawTrade(i * 0.1, atAsk ? 101 : 99, qty);
            if (atAsk) expectedBuy += qty; else expectedSell += qty;

            var classified = detector.OnTrade(raw, bestBidTicks: 99, bestAskTicks: 101, bookValid: true);
            var e = AggressorEnrichment.Enrich(raw, classified);

            cvd.Add(e);
            profile.AddTrade(e);
            footprint.AddTrade(e);
        }

        // Every consumer sees the same classified totals…
        Assert.Equal(expectedBuy - expectedSell, cvd.CumulativeDelta);
        Assert.Equal(expectedBuy, profile.TotalBuyVolume);
        Assert.Equal(expectedSell, profile.TotalSellVolume);
        Assert.Equal(expectedBuy, footprint.BuyVolume);
        Assert.Equal(expectedSell, footprint.SellVolume);

        // …and they reconcile with the classifier's own diagnostics (no split brain).
        var diag = detector.DiagnosticsSnapshot();
        Assert.Equal(40, diag.BuyTrades);
        Assert.Equal(20, diag.SellTrades);
        Assert.Equal(0, diag.UnknownTrades);
        Assert.Equal(60, diag.EstimatedClassifications); // all inferred, all labelled
        Assert.Equal(0, diag.NativeClassifications);
    }
}
