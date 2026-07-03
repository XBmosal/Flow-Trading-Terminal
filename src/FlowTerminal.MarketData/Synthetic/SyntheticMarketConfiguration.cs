using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.MarketData.Synthetic;

/// <summary>
/// Centralised, instrument-specific parameters for the synthetic order-book engine.
/// Every "magic number" that shapes the synthetic market lives here (one place,
/// separate NQ/ES defaults) so the generators read like policy, not arithmetic.
///
/// All sizes are in contracts; all prices are in integer ticks; all rates are
/// per simulation step unless noted. Nothing here touches rendering or colour —
/// this only governs how realistic the generated <em>data</em> is.
/// </summary>
public sealed record SyntheticMarketConfiguration
{
    // ── Time stepping ───────────────────────────────────────────────────────
    /// <summary>Baseline wall-clock advance per simulation step (ms), before regime scaling.</summary>
    public int BaseStepMs { get; init; } = 11;

    /// <summary>Random jitter applied to the step interval (± fraction).</summary>
    public double StepJitter { get; init; } = 0.6;

    // ── Book shape ──────────────────────────────────────────────────────────
    /// <summary>How many price levels each side the engine actively maintains around the touch.</summary>
    public int MaintainedLevels { get; init; } = 22;

    /// <summary>Median resting size at a typical level (contracts). Heavy-tailed around this.</summary>
    public double LevelSizeMedian { get; init; } = 26;

    /// <summary>Log-normal sigma for resting size — larger ⇒ heavier tail / more size variance.</summary>
    public double LevelSizeSigma { get; init; } = 0.74;

    /// <summary>Smallest size a displayed level is allowed to show.</summary>
    public long MinLevelSize { get; init; } = 1;

    /// <summary>Hard ceiling on any displayed level (keeps walls finite, prevents runaway growth).</summary>
    public long MaxLevelSize { get; init; } = 1_600;

    /// <summary>
    /// Distance weighting: relative target size at distance d (ticks) from the touch.
    /// Books are typically thin right at the touch, build up a few levels back, then
    /// taper. Index 0 = touch. Beyond the array the last value is reused with decay.
    /// </summary>
    public double[] DistanceWeight { get; init; } =
        { 0.55, 0.8, 1.0, 1.15, 1.2, 1.15, 1.05, 0.95, 0.85, 0.78, 0.7, 0.62 };

    /// <summary>Far-tail decay per level applied beyond <see cref="DistanceWeight"/>.</summary>
    public double FarDecay { get; init; } = 0.93;

    // ── Walls (occasional, not constant) ────────────────────────────────────
    /// <summary>Probability a freshly created level is an outsized "wall".</summary>
    public double WallProbability { get; init; } = 0.045;

    /// <summary>Wall size multiplier range over the normal target size.</summary>
    public double WallMultMin { get; init; } = 4.0;
    public double WallMultMax { get; init; } = 13.0;

    // ── Lifecycle (stacking / pulling / replenishment) ──────────────────────
    /// <summary>Per-step probability that an ordinary resting level is partially pulled or re-sized.</summary>
    public double LevelChurnRate { get; init; } = 0.10;

    /// <summary>Per-step probability a thinned/executed level replenishes back toward target.</summary>
    public double ReplenishRate { get; init; } = 0.22;

    /// <summary>Maximum times a single level will replenish before it is allowed to die.</summary>
    public int MaxReplenish { get; init; } = 6;

    /// <summary>Per-step probability a brand-new gap level is filled (controls how fast depth rebuilds).</summary>
    public double RefillRate { get; init; } = 0.35;

    /// <summary>Baseline persistence (stickiness) of a level: higher ⇒ rests longer before pulling.</summary>
    public double BasePersistence { get; init; } = 0.55;

    // ── Trades / executions ─────────────────────────────────────────────────
    // Clip sizes come from a three-tier mixture, calibrated per instrument:
    //   body    — log-normal, most prints 1–5 contracts, occasional 10–40;
    //   large   — uncommon (≈0.4% of clips), tens of contracts, sweep candidates;
    //   extreme — exceptional tail (≈0.03%), the 150+ events that should be *rare*.
    // Regime SweepMult scales the two tail tiers modestly; nothing multiplies every
    // trade's size, so stress/fast modes raise rate without inflating the tape.

    /// <summary>Baseline per-step probability that any aggressive trading occurs.</summary>
    public double TradeRate { get; init; } = 0.10;

    /// <summary>Median aggressive clip size (contracts) of the body tier.</summary>
    public double TradeSizeMedian { get; init; } = 2.6;

    /// <summary>Log-normal sigma of the body tier.</summary>
    public double TradeSizeSigma { get; init; } = 0.88;

    /// <summary>Probability a clip comes from the "large" size tier (sweep candidate).</summary>
    public double LargeTradeProbability { get; init; } = 0.004;

    /// <summary>Large-trade size multiplier range (legacy uniform tier; used when LargeSizeMedian = 0).</summary>
    public double LargeTradeMultMin { get; init; } = 6.0;
    public double LargeTradeMultMax { get; init; } = 34.0;

    /// <summary>Median / shape of the large tier (contracts, log-normal). 0 median ⇒ use legacy mult range.</summary>
    public double LargeSizeMedian { get; init; } = 42;
    public double LargeSizeSigma { get; init; } = 0.55;

    /// <summary>Probability a clip is an extreme tail event (150+ territory). Exceptional by default.</summary>
    public double ExtremeTradeProbability { get; init; } = 0.00035;
    public double ExtremeSizeMedian { get; init; } = 170;
    public double ExtremeSizeSigma { get; init; } = 0.45;

    /// <summary>Hard ceiling on a single aggressive clip (keeps the tail finite).</summary>
    public long MaxClipSize { get; init; } = 1_000;

    /// <summary>
    /// Per-UTC-hour activity multiplier (index = hour 0–23) applied to the aggressive
    /// trade rate, shaping a CME-like session: overnight quiet, opening burst, midday
    /// lull, afternoon pickup, closing activity. All 1.0 ⇒ phases disabled.
    /// </summary>
    public double[] SessionPhaseRate { get; init; } =
        { 0.45, 0.4, 0.4, 0.4, 0.45, 0.5, 0.5, 0.55, 0.6, 0.65, 0.7, 0.8,
          0.9, 1.25, 1.35, 1.0, 0.85, 0.8, 0.95, 1.05, 1.3, 0.6, 0.5, 0.45 };

    /// <summary>Maximum number of price levels a single aggressive order may sweep.</summary>
    public int MaxSweepLevels { get; init; } = 9;

    // ── Price anchoring (keeps an 8h session bounded without forcing the book) ──
    /// <summary>Gentle pull of order-flow imbalance back toward a slow anchor (per tick of deviation).</summary>
    public double MeanReversion { get; init; } = 0.0009;

    /// <summary>Slow drift of the fair-value anchor itself (ticks per step, random sign).</summary>
    public double AnchorDriftTicks { get; init; } = 0.02;

    public static SyntheticMarketConfiguration ForRoot(RootSymbol root) => root switch
    {
        // ES: deeper resting size, chunkier prints, its own calibrated tail — larger
        // typical sizes than NQ but 150+ still uncommon and 250+ exceptional.
        RootSymbol.ES => new SyntheticMarketConfiguration
        {
            LevelSizeMedian = 58,
            LevelSizeSigma = 0.68,
            TradeRate = 0.13,
            TradeSizeMedian = 4.2,
            TradeSizeSigma = 0.82,
            LargeTradeProbability = 0.004,
            LargeSizeMedian = 80,
            LargeSizeSigma = 0.5,
            ExtremeTradeProbability = 0.0003,
            ExtremeSizeMedian = 320,
            ExtremeSizeSigma = 0.4,
            MaxClipSize = 1_500,
            MaintainedLevels = 24,
            MaxLevelSize = 3_200,
        },
        // NQ: thinner books, smaller prints, spikier travel (record defaults are NQ-tuned).
        _ => new SyntheticMarketConfiguration(),
    };
}
