using FlowTerminal.Analytics.Bars;
using FlowTerminal.Analytics.Detectors;
using FlowTerminal.Analytics.OrderFlow;
using FlowTerminal.Analytics.Vwap;
using FlowTerminal.Domain.Events;
using Xunit;

namespace FlowTerminal.Analytics.Tests;

public class OrderFlowSuiteTests
{
    private static readonly DateTime T = new(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc);

    private static Bar MkBar(int i, long high, long low, long delta, long volume = 100, long close = 0) => new(
        BarKind.Time, T.AddMinutes(i), T.AddMinutes(i + 1),
        OpenTicks: (high + low) / 2, HighTicks: high, LowTicks: low,
        CloseTicks: close != 0 ? close : (high + low) / 2,
        Volume: volume,
        BuyVolume: (volume + delta) / 2, SellVolume: (volume - delta) / 2, TradeCount: 10);

    // ── Canonical stats ──────────────────────────────────────────────────────

    [Fact]
    public void BarFlowStats_Tracks_Sides_And_Unknown_Honestly()
    {
        var s = new BarFlowStats();
        s.OnTrade(TestTrades.At(0, 100, 30, AggressorSide.Buy));
        s.OnTrade(TestTrades.At(1, 101, 10, AggressorSide.Sell));
        s.OnTrade(TestTrades.At(2, 102, 7, AggressorSide.Unknown));

        Assert.Equal(30, s.BuyVolume);
        Assert.Equal(10, s.SellVolume);
        Assert.Equal(7, s.UnknownVolume);      // never forced into a side
        Assert.Equal(47, s.TotalVolume);
        Assert.Equal(20, s.Delta);             // buys − sells, unknown excluded
        Assert.Equal(2, s.PriceProgressTicks);
        Assert.Equal(0.1, s.DeltaEfficiency, 3); // 2 ticks / |20|
    }

    [Fact]
    public void RollingWindow_Velocity_Uses_Real_Elapsed_Time()
    {
        var w = new RollingFlowWindow(TimeSpan.FromSeconds(30));
        w.OnTrade(TestTrades.At(0, 100, 40, AggressorSide.Buy));
        w.OnTrade(TestTrades.At(10, 105, 10, AggressorSide.Sell));

        Assert.Equal(30, w.Delta);
        Assert.Equal(10.0, w.ElapsedSeconds, 1);
        Assert.Equal(3.0, w.DeltaVelocity, 1);   // 30 delta over 10 real seconds
        Assert.Equal(5, w.PriceProgressTicks);

        // Old entries roll out of the window (t=0 is 35s old at t=35; t=10 stays).
        w.OnTrade(TestTrades.At(35, 106, 5, AggressorSide.Buy));
        Assert.Equal(2, w.TradeCount);
        Assert.Equal(-5, w.Delta); // buy 5 − sell 10
    }

    // ── Delta divergence ─────────────────────────────────────────────────────

    private static DeltaDivergenceEngine Engine() => new(new DeltaDivergenceSettings2
    { PivotLeft = 2, PivotRight = 2, MinSeparationBars = 3, MinPriceDiffTicks = 1, MinDeltaDiff = 1 });

    /// <summary>Feeds bars with two price lows (i=5, i=13) shaped by <paramref name="lowPrices"/>/<paramref name="lowDeltas"/>.</summary>
    private static List<DivergenceSignal> RunLows(long low1, long d1, long low2, long d2)
    {
        var e = Engine();
        var signals = new List<DivergenceSignal>();
        for (int i = 0; i < 20; i++)
        {
            long lo = 100, delta = 0;
            if (i == 5) { lo = low1; delta = d1; }
            if (i == 13) { lo = low2; delta = d2; }
            var s = e.OnBar(MkBar(i, high: 120, low: lo, delta: delta));
            if (s is not null) signals.Add(s);
        }
        return signals;
    }

    [Fact]
    public void Regular_Bullish_Divergence_Detected_On_Lower_Low_With_Higher_Delta()
    {
        var signals = RunLows(low1: 90, d1: -80, low2: 85, d2: -20);
        var s = Assert.Single(signals);
        Assert.Equal(DivergenceType.RegularBullish, s.Type);
        Assert.Equal(DivergenceState.Confirmed, s.State);
        Assert.True(s.IsBullish);
        Assert.Equal(90, s.FirstPivotPriceTicks);
        Assert.Equal(85, s.SecondPivotPriceTicks);
    }

    [Fact]
    public void Hidden_Bullish_Divergence_Detected_On_Higher_Low_With_Lower_Delta()
    {
        var signals = RunLows(low1: 85, d1: -20, low2: 90, d2: -80);
        var s = Assert.Single(signals);
        Assert.Equal(DivergenceType.HiddenBullish, s.Type);
    }

    [Fact]
    public void Regular_And_Hidden_Bearish_Detected_On_Highs()
    {
        DivergenceSignal? Run(long h1, long d1, long h2, long d2)
        {
            var e = Engine();
            DivergenceSignal? got = null;
            for (int i = 0; i < 20; i++)
            {
                long hi = 100, delta = 0;
                if (i == 5) { hi = h1; delta = d1; }
                if (i == 13) { hi = h2; delta = d2; }
                got = e.OnBar(MkBar(i, high: hi, low: 80, delta: delta)) ?? got;
            }
            return got;
        }

        Assert.Equal(DivergenceType.RegularBearish, Run(110, 80, 115, 20)!.Type);  // HH, weaker delta
        Assert.Equal(DivergenceType.HiddenBearish, Run(115, 20, 110, 80)!.Type);   // LH, stronger delta
    }

    [Fact]
    public void Divergence_Has_No_Lookahead()
    {
        // The second pivot forms at bar 13 with PivotRight=2 → the signal may first
        // exist when bar 15 completes, never before.
        var e = Engine();
        for (int i = 0; i <= 14; i++)
        {
            long lo = 100, delta = 0;
            if (i == 5) { lo = 90; delta = -80; }
            if (i == 13) { lo = 85; delta = -20; }
            var s = e.OnBar(MkBar(i, 120, lo, delta));
            Assert.Null(s); // nothing may fire through bar 14
        }

        var confirmed = e.OnBar(MkBar(15, 120, 100, 0));
        Assert.NotNull(confirmed);
        Assert.Equal(15, confirmed!.ConfirmedAtBar);
        Assert.Equal(13, confirmed.SecondPivotBar);
    }

    [Fact]
    public void Divergence_Is_Deterministic_And_Scored()
    {
        var a = RunLows(90, -80, 85, -20);
        var b = RunLows(90, -80, 85, -20);
        Assert.Equal(a[0], b[0]);                    // identical record incl. id + score
        Assert.InRange(a[0].Score, 0.0, 1.0);
        Assert.True(Enum.IsDefined(a[0].Strength));
    }

    // ── Delta blocks ─────────────────────────────────────────────────────────

    [Fact]
    public void Delta_Blocks_Reconcile_With_Canonical_Volume()
    {
        var eng = new DeltaBlockEngine(new DeltaBlockSettings { Mode = DeltaBlockMode.Volume, Threshold = 50 });
        long canonical = 0;
        for (int i = 0; i < 100; i++)
        {
            var side = i % 3 == 0 ? AggressorSide.Sell : i % 7 == 0 ? AggressorSide.Unknown : AggressorSide.Buy;
            long qty = 5 + i % 9;
            canonical += qty;
            eng.OnTrade(TestTrades.At(i, 100 + i % 5, qty, side));
        }

        long inBlocks = eng.Blocks.Sum(b => b.TotalVolume) + (eng.Open?.TotalVolume ?? 0);
        Assert.Equal(canonical, inBlocks);           // every trade in exactly one block
        Assert.All(eng.Blocks, b => Assert.True(b.TotalVolume >= 50));
    }

    [Fact]
    public void Threshold_Delta_Blocks_Seal_On_Absolute_Delta()
    {
        var eng = new DeltaBlockEngine(new DeltaBlockSettings { Mode = DeltaBlockMode.AbsoluteDelta, Threshold = 60 });
        for (int i = 0; i < 10; i++) eng.OnTrade(TestTrades.At(i, 100, 10, AggressorSide.Buy));

        Assert.Single(eng.Blocks);
        Assert.Equal(60, eng.Blocks[0].Delta);
        Assert.Equal(40, eng.Open!.Delta);           // remainder is forming
    }

    [Fact]
    public void Time_Delta_Blocks_Never_Span_The_Bucket()
    {
        var eng = new DeltaBlockEngine(new DeltaBlockSettings { Mode = DeltaBlockMode.Time, TimeBucket = TimeSpan.FromSeconds(10) });
        eng.OnTrade(TestTrades.At(0, 100, 5, AggressorSide.Buy));
        eng.OnTrade(TestTrades.At(5, 100, 5, AggressorSide.Buy));
        eng.OnTrade(TestTrades.At(11, 100, 5, AggressorSide.Sell)); // next bucket

        Assert.Single(eng.Blocks);
        Assert.Equal(10, eng.Blocks[0].TotalVolume);
        Assert.Equal(5, eng.Open!.TotalVolume);
    }

    // ── Anchored VWAP ────────────────────────────────────────────────────────

    [Fact]
    public void Anchored_Vwap_Matches_Hand_Calculation_With_Bands()
    {
        var set = new AnchoredVwapSet();
        var inst = set.Add("test", VwapAnchorType.Manual, T)!;
        set.OnTrade(TestTrades.At(0, 100, 10, AggressorSide.Buy));
        set.OnTrade(TestTrades.At(1, 104, 30, AggressorSide.Sell));

        // vwap = (100·10 + 104·30)/40 = 103; var = (100²·10+104²·30)/40 − 103² = 3 → σ=√3
        var v = inst.Value;
        Assert.Equal(103.0, v.VwapTicks, 6);
        Assert.Equal(Math.Sqrt(3.0), v.StdDevTicks, 6);
        Assert.Equal(103.0 + 2 * Math.Sqrt(3.0), v.UpperBand(2.0), 6);
    }

    [Fact]
    public void Anchored_Vwap_Ignores_Trades_Before_Anchor_And_Handles_No_Volume()
    {
        var set = new AnchoredVwapSet();
        var inst = set.Add("later", VwapAnchorType.Manual, T.AddSeconds(100))!;
        set.OnTrade(TestTrades.At(0, 100, 10, AggressorSide.Buy)); // before anchor
        Assert.False(inst.HasData);
        Assert.True(double.IsNaN(inst.Value.VwapTicks));           // honest no-volume state

        set.OnTrade(TestTrades.At(150, 200, 5, AggressorSide.Buy));
        Assert.Equal(200.0, inst.Value.VwapTicks, 6);
    }

    [Fact]
    public void Multiple_Anchors_Run_Independently_And_Series_Align()
    {
        var set = new AnchoredVwapSet();
        var a = set.Add("a", VwapAnchorType.SessionOpen, T)!;
        set.OnTrade(TestTrades.At(0, 100, 10, AggressorSide.Buy));
        set.OnBarClose(0);
        var b = set.Add("b", VwapAnchorType.Manual, T.AddSeconds(30))!;
        set.OnTrade(TestTrades.At(60, 110, 10, AggressorSide.Buy));
        set.OnBarClose(1);

        Assert.Equal(105.0, a.Value.VwapTicks, 6);
        Assert.Equal(110.0, b.Value.VwapTicks, 6);
        Assert.Equal(2, set.Series(a.Id).Count);
        Assert.Equal(2, set.Series(b.Id).Count);        // padded with NaN for bar 0
        Assert.True(double.IsNaN(set.Series(b.Id)[0].VwapTicks));
    }

    // ── Exhaustion + trapped traders ─────────────────────────────────────────

    [Fact]
    public void Exhaustion_Candidate_At_High_On_Declining_Aggression()
    {
        var d = new ExhaustionDetector(new ExhaustionSettings { DeclineBars = 3, ExtremeLookback = 8, DeclineRatio = 0.5 });
        Detection? got = null;
        for (int i = 0; i < 12; i++) got = d.OnBar(MkBar(i, 100 + i, 90, delta: 0, volume: 100)) ?? got;
        // Approach: rising highs with shrinking delta + volume into the extreme.
        got = d.OnBar(MkBar(12, 113, 90, delta: 80, volume: 100)) ?? got;
        got = d.OnBar(MkBar(13, 114, 90, delta: 40, volume: 80)) ?? got;
        got = d.OnBar(MkBar(14, 115, 90, delta: 20, volume: 60)) ?? got;
        got = d.OnBar(MkBar(15, 116, 90, delta: 10, volume: 40)) ?? got;

        Assert.NotNull(got);
        Assert.Equal(DetectionBias.Bearish, got!.Bias);
        Assert.Contains("Exhaustion", got.DetectorName);
    }

    [Fact]
    public void Trapped_Buyers_Fire_Only_After_Return_Bar_Completes()
    {
        var d = new TrappedTraderDetector(new TrappedTraderSettings { MinTrapDelta = 50, ConfirmWithinBars = 4 });
        Assert.Null(d.OnBar(MkBar(0, 100, 95, 0)));
        Assert.Null(d.OnBar(MkBar(1, 101, 96, 0)));
        Assert.Null(d.OnBar(MkBar(2, 102, 97, 0)));
        // Strong buy bar at a local high arms the trap — no signal yet (no lookahead).
        Assert.Null(d.OnBar(MkBar(3, 110, 104, delta: 90, volume: 120)));
        // Price closes back below the aggressive bar's low → trapped-buyers candidate.
        var s = d.OnBar(MkBar(4, 106, 100, 0, close: 101));
        Assert.NotNull(s);
        Assert.Equal(DetectionBias.Bearish, s!.Bias);
        Assert.Contains("Trapped buyers", s.Description);
    }
}
