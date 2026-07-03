using FlowTerminal.Analytics.Delta;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Analytics.Profiles;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using Xunit;

namespace FlowTerminal.UiTests;

/// <summary>
/// Volume normalization across components (one canonical stream, one truth): the volume
/// profile, CVD, and footprint fed the same synthetic trades must reconcile exactly with
/// the canonical buy/sell totals — no path duplicates or drops volume.
/// </summary>
public class SyntheticReconciliationTests
{
    [Fact]
    public void Profile_Cvd_And_Footprint_Reconcile_With_Canonical_Trades()
    {
        var contract = new Contract(RootSymbol.NQ, QuarterlyMonth.December, 2025);
        var start = new DateTime(2024, 6, 3, 13, 30, 0, DateTimeKind.Utc);
        var gen = new SyntheticSessionGenerator(1, contract, start, new SyntheticOptions { Seed = 7, StartPrice = 20_000m });

        var profile = new VolumeProfile();
        var cvd = new CvdCalculator();
        var footprint = new Footprint();
        long canonicalBuy = 0, canonicalSell = 0, trades = 0;

        foreach (var e in gen.Generate(150_000))
        {
            if (e.Type != MarketEventType.Trade) continue;
            trades++;
            if (e.Aggressor == AggressorSide.Buy) canonicalBuy += e.Quantity; else canonicalSell += e.Quantity;
            profile.AddTrade(e);
            cvd.Add(e);
            footprint.AddTrade(e);
        }

        Assert.True(trades > 1000, "expected a live tape");

        // Profile: per-price buy/sell sums equal the canonical totals.
        long profBuy = 0, profSell = 0;
        foreach (var lvl in profile.Levels()) { profBuy += lvl.BuyVolume; profSell += lvl.SellVolume; }
        Assert.Equal(canonicalBuy, profBuy);
        Assert.Equal(canonicalSell, profSell);

        // CVD: cumulative delta equals buys − sells.
        Assert.Equal(canonicalBuy - canonicalSell, cvd.CumulativeDelta);

        // Footprint: ask/bid (buy/sell) totals equal the same canonical totals — and its
        // per-level sums agree with its own totals (no duplication anywhere in the path).
        Assert.Equal(canonicalBuy, footprint.BuyVolume);
        Assert.Equal(canonicalSell, footprint.SellVolume);
        long fpBuy = 0, fpSell = 0;
        foreach (var lvl in footprint.Levels()) { fpBuy += lvl.BuyVolume; fpSell += lvl.SellVolume; }
        Assert.Equal(canonicalBuy, fpBuy);
        Assert.Equal(canonicalSell, fpSell);
    }
}
