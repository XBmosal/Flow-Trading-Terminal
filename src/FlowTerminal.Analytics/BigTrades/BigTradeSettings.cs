using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.Analytics.BigTrades;

/// <summary>
/// Transparent threshold rules for deciding when a trade or group is "big". Every mode
/// is documented and exposes the threshold it used — there is no black-box score.
/// Modes marked (staged) are not yet implemented and fall back to RollingPercentile.
/// </summary>
public enum ThresholdMode
{
    /// <summary>Absolute contract count.</summary>
    Fixed,

    /// <summary>Exceeds a percentile of recent trade sizes (rolling window).</summary>
    RollingPercentile,

    /// <summary>Exceeds a percentile of all trades this session (resets per session).</summary>
    SessionPercentile,

    /// <summary>Rolling mean + k·standard-deviations.</summary>
    ZScore,

    /// <summary>A multiple of the rolling average trade size.</summary>
    RelativeToAverage,

    /// <summary>(staged) Visible-range percentile.</summary>
    VisibleRangePercentile,

    /// <summary>(staged) Relative to recent traded volume.</summary>
    RelativeToVolume,

    /// <summary>(staged) Regime-aware adaptive blend.</summary>
    Adaptive,
}

/// <summary>How aggressive prints are grouped into a single Big Trade.</summary>
public enum AggregationMode
{
    /// <summary>Every qualifying trade is its own bubble (raw mode).</summary>
    None,

    /// <summary>Same price and side within the time window.</summary>
    SamePrice,

    /// <summary>Adjacent prices (within a tick distance) and same side within the window.</summary>
    AdjacentPrice,

    /// <summary>Same-side run progressing across consecutive levels (sweep-friendly).</summary>
    Sweep,
}

/// <summary>
/// Complete, serialisable Big Trades configuration. One settings object drives the
/// shared detector; individual panels may apply their own <see cref="BigTradeVisualSettings"/>
/// on top, but the detection/aggregation here is the single source of truth.
/// </summary>
public sealed record BigTradeSettings
{
    // ── Detection ────────────────────────────────────────────────────────
    public bool ShowBuys { get; init; } = true;
    public bool ShowSells { get; init; } = true;
    public bool ShowUnknown { get; init; } = true;

    /// <summary>Trades smaller than this never participate (noise gate, contracts).</summary>
    public long MinTradeQuantity { get; init; } = 1;

    // ── Threshold ────────────────────────────────────────────────────────
    public ThresholdMode Mode { get; init; } = ThresholdMode.RollingPercentile;

    /// <summary>Fixed-mode absolute thresholds (buy/sell may differ).</summary>
    public long FixedBuyThreshold { get; init; } = 50;
    public long FixedSellThreshold { get; init; } = 50;

    public double Percentile { get; init; } = 0.98;
    public int RollingWindow { get; init; } = 500;
    public int SessionCapacity { get; init; } = 50_000;
    public int MinSamples { get; init; } = 30;
    public double ZScoreThreshold { get; init; } = 3.0;
    public double RelativeMultiplier { get; init; } = 6.0;

    /// <summary>An absolute floor a group total must always clear, regardless of mode.</summary>
    public long AbsoluteFloor { get; init; } = 10;

    // ── Aggregation ──────────────────────────────────────────────────────
    public AggregationMode Aggregation { get; init; } = AggregationMode.AdjacentPrice;
    public int TimeWindowMs { get; init; } = 250;
    public long MaxTickDistance { get; init; } = 4;
    public bool RequireSameSide { get; init; } = true;
    public int MaxGroupDurationMs { get; init; } = 1500;
    public int MaxRetainedChildren { get; init; } = 64;

    // ── Sweep ────────────────────────────────────────────────────────────
    public int SweepMinLevels { get; init; } = 3;
    public long SweepMinVolume { get; init; } = 30;

    /// <summary>Returns the fixed threshold for a given side.</summary>
    public long FixedThresholdFor(Domain.Events.AggressorSide side) =>
        side == Domain.Events.AggressorSide.Sell ? FixedSellThreshold : FixedBuyThreshold;

    public BigTradeSettings Validate() => this with
    {
        Percentile = Math.Clamp(Percentile, 0.5, 0.9999),
        RollingWindow = Math.Clamp(RollingWindow, 20, 100_000),
        SessionCapacity = Math.Clamp(SessionCapacity, 100, 1_000_000),
        MinSamples = Math.Clamp(MinSamples, 1, 100_000),
        ZScoreThreshold = Math.Clamp(ZScoreThreshold, 0.5, 12.0),
        RelativeMultiplier = Math.Clamp(RelativeMultiplier, 1.0, 100.0),
        TimeWindowMs = Math.Clamp(TimeWindowMs, 0, 60_000),
        MaxTickDistance = Math.Clamp(MaxTickDistance, 0, 1000),
        MaxGroupDurationMs = Math.Clamp(MaxGroupDurationMs, 50, 600_000),
        MaxRetainedChildren = Math.Clamp(MaxRetainedChildren, 0, 100_000),
        SweepMinLevels = Math.Clamp(SweepMinLevels, 2, 1000),
        FixedBuyThreshold = Math.Max(1, FixedBuyThreshold),
        FixedSellThreshold = Math.Max(1, FixedSellThreshold),
        AbsoluteFloor = Math.Max(1, AbsoluteFloor),
        MinTradeQuantity = Math.Max(1, MinTradeQuantity),
    };
}

/// <summary>A named, instrument-aware Big Trades preset. Built-ins are protected.</summary>
public sealed record BigTradePreset(string Name, string Description, BigTradeSettings Settings, bool IsBuiltIn = false);

/// <summary>The built-in Big Trades presets, with NQ/ES-tuned defaults. All editable.</summary>
public static class BigTradePresetRegistry
{
    private static BigTradePreset P(string name, string desc, BigTradeSettings s) => new(name, desc, s.Validate(), true);

    public static IReadOnlyList<BigTradePreset> BuiltIns { get; } = new[]
    {
        P("Balanced Bubbles", "Rolling-percentile threshold, moderate bubble count.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.98, Aggregation = AggregationMode.AdjacentPrice }),

        P("Large Only", "High percentile; only the biggest prints.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.995, AbsoluteFloor = 40, Aggregation = AggregationMode.AdjacentPrice }),

        P("Detailed Tape", "Lower threshold, short window; rich for replay study.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.90, TimeWindowMs = 120, Aggregation = AggregationMode.SamePrice }),

        P("Sweep Detector", "Groups multi-level aggressive runs and flags sweeps.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.95, Aggregation = AggregationMode.Sweep, SweepMinLevels = 3 }),

        P("Adaptive Session", "Session-percentile threshold; adapts as the session develops.",
            new BigTradeSettings { Mode = ThresholdMode.SessionPercentile, Percentile = 0.985, MinSamples = 50 }),

        // The instrument presets qualify *group totals*, which sum several prints — so the
        // floor is set at "meaningful aggressive action" size, not at single-print size.
        P("NQ Balanced", "NQ-tuned percentile defaults: occasional, meaningful bubbles.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.999, FixedBuyThreshold = 50, FixedSellThreshold = 50, AbsoluteFloor = 40, TimeWindowMs = 150, MaxTickDistance = 2 }),

        P("ES Balanced", "ES-tuned percentile defaults: occasional, meaningful bubbles.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.999, FixedBuyThreshold = 75, FixedSellThreshold = 75, AbsoluteFloor = 60, TimeWindowMs = 150, MaxTickDistance = 2 }),

        P("Replay Study", "Detailed grouping, more retained children for inspection.",
            new BigTradeSettings { Mode = ThresholdMode.RollingPercentile, Percentile = 0.95, Aggregation = AggregationMode.AdjacentPrice, MaxRetainedChildren = 256 }),
    };

    public static BigTradePreset Default => BuiltIns[0];

    public static BigTradePreset? ByName(string name)
    {
        foreach (var p in BuiltIns)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Instrument-appropriate starting preset.</summary>
    public static BigTradePreset ForInstrument(RootSymbol root) =>
        root == RootSymbol.ES ? ByName("ES Balanced")! : ByName("NQ Balanced")!;
}
