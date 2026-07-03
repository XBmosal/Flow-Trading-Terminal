namespace FlowTerminal.Charting.Studies;

public enum StudyCategory
{
    VolumeAndProfile,
    OrderFlowAndTape,
    TechnicalAndPriceAction,
    TechnicalIndicators,
}

public enum StudyStatus
{
    /// <summary>Computed and visible in the shell now.</summary>
    Active,

    /// <summary>Engine implemented and unit-tested; chart overlay wiring is pending.</summary>
    EngineReady,

    /// <summary>Not yet implemented.</summary>
    Planned,
}

/// <summary>
/// One study in the catalog. <see cref="DetectorKey"/> links a study to a runtime
/// detector toggle when applicable.
/// </summary>
public sealed record StudyDefinition(
    string Name,
    string ShortCode,
    StudyCategory Category,
    StudyStatus Status,
    string Description,
    string? DetectorKey = null,
    bool DefaultOn = false);

/// <summary>
/// The catalog of available studies (à la a "Studies" tab). Each entry honestly
/// reflects whether it is Active, EngineReady (computed + tested, overlay pending),
/// or Planned — nothing here is a fake placeholder.
/// </summary>
public static class StudyCatalog
{
    public static IReadOnlyList<StudyDefinition> All { get; } = new[]
    {
        // ── Volume & Profile ─────────────────────────────────────────────
        new StudyDefinition("Volume Profile (VBP)", "VBP", StudyCategory.VolumeAndProfile, StudyStatus.Active,
            "Total volume traded at each price over a session; locates high/low volume nodes.", DefaultOn: true),
        new StudyDefinition("Volume (per bar)", "VOL", StudyCategory.VolumeAndProfile, StudyStatus.Active,
            "Per-bar traded volume as a histogram along the bottom of the chart, colored by bar direction."),
        new StudyDefinition("Bid/Ask Cluster Profile", "BAC", StudyCategory.VolumeAndProfile, StudyStatus.EngineReady,
            "Splits horizontal volume into buying (ask) and selling (bid) segments at each price."),
        new StudyDefinition("Delta Profile", "DP", StudyCategory.VolumeAndProfile, StudyStatus.EngineReady,
            "Net aggressive buyers minus sellers at each price increment."),
        new StudyDefinition("TPO / Market Profile", "TPO", StudyCategory.VolumeAndProfile, StudyStatus.EngineReady,
            "Time spent at price as lettered brackets; RTH/ETH split by session window."),
        new StudyDefinition("Volume Profile on Swing", "VPS", StudyCategory.VolumeAndProfile, StudyStatus.Planned,
            "Profiles anchored across micro-structural market swings (swing detector pending)."),
        new StudyDefinition("VWAP (daily/weekly/monthly/anchored)", "VWAP", StudyCategory.VolumeAndProfile, StudyStatus.EngineReady,
            "Volume-weighted average price with std-dev bands; periodic and manually anchored.", DefaultOn: true),

        // ── Order Flow & Tape ────────────────────────────────────────────
        new StudyDefinition("Footprint / Cluster Chart", "FP", StudyCategory.OrderFlowAndTape, StudyStatus.EngineReady,
            "Per-candle bid/ask execution volumes at each price."),
        new StudyDefinition("Volume Imbalance Tracker", "IMB", StudyCategory.OrderFlowAndTape, StudyStatus.EngineReady,
            "Stacked diagonal aggressive buy/sell imbalances across the spread."),
        new StudyDefinition("Cumulative Volume Delta (CVD)", "CVD", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Net continuous accumulation/distribution of aggressive orders; isolates divergence.", DefaultOn: true),
        new StudyDefinition("Large / Block Trade Detector", "LT", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Big Trades: emphasises unusually large aggressive executions as price/time bubbles (observed aggression, not trader intent).", DetectorKey: "Large Trade", DefaultOn: true),
        new StudyDefinition("Iceberg & Absorption Detector", "ICE", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Hidden refilling orders (estimated) and heavy passive absorption.", DetectorKey: "Iceberg", DefaultOn: true),
        new StudyDefinition("Speed of Tape (SOT)", "SOT", StudyCategory.OrderFlowAndTape, StudyStatus.EngineReady,
            "Transactions/contracts per second; quantifies HFT/algo aggression."),
        new StudyDefinition("Stop Run / Liquidity Hunt", "SR", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Sweeps through swing levels with volume bursts that run stops.", DetectorKey: "Stop-Run", DefaultOn: true),
        new StudyDefinition("Delta Divergence Pro", "DDIV", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Pivot-to-pivot delta divergence (regular + hidden) with confirmed/developing states drawn on the chart. No lookahead."),
        new StudyDefinition("Delta Blocks", "DBLK", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Trade-derived delta blocks: contiguous flow sealed at a delta threshold, shaded green/purple by net aggression."),
        new StudyDefinition("Anchored VWAP (Session)", "AVWAP", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Session-anchored VWAP with volume-weighted ±1σ/±2σ bands (documented weighted-variance formula)."),
        new StudyDefinition("Exhaustion Candidates", "EXH", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Local extreme reached on declining aggressive participation. Candidate only.", DetectorKey: "Exhaustion", DefaultOn: true),
        new StudyDefinition("Trapped-Trader Candidates", "TRAP", StudyCategory.OrderFlowAndTape, StudyStatus.Active,
            "Strong one-sided aggression near an extreme that price rejects. Candidate positioning pressure only.", DetectorKey: "Trapped Traders", DefaultOn: true),

        // ── Technical Analysis & Price Action ────────────────────────────
        new StudyDefinition("Fair Value Gap (FVG)", "FVG", StudyCategory.TechnicalAndPriceAction, StudyStatus.EngineReady,
            "3-bar structural inefficiencies / liquidity voids."),
        new StudyDefinition("Opening Range Breakout (ORB)", "ORB", StudyCategory.TechnicalAndPriceAction, StudyStatus.EngineReady,
            "Maps the opening-range high/low and flags the first break."),
        new StudyDefinition("Average Daily Range (ADR)", "ADR", StudyCategory.TechnicalAndPriceAction, StudyStatus.EngineReady,
            "Trailing daily-range average projected as session extension targets."),
        new StudyDefinition("Multi-Timeframe Candle Overlay", "MTF", StudyCategory.TechnicalAndPriceAction, StudyStatus.Planned,
            "Higher-period candles drawn onto a lower-period chart (overlay pending)."),
        new StudyDefinition("Chaikin Accumulation/Distribution", "AD", StudyCategory.TechnicalAndPriceAction, StudyStatus.EngineReady,
            "Volume-proxy A/D line assessing institutional pressure."),
        new StudyDefinition("Gap Detector", "GAP", StudyCategory.TechnicalAndPriceAction, StudyStatus.EngineReady,
            "Highlights weekend/overnight opening price gaps."),

        // ── Technical Indicators (standard library; overlays + oscillator panes) ──
        new StudyDefinition("Moving Average (EMA 20)", "MA", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Exponential moving average of close drawn on the price chart."),
        new StudyDefinition("Bollinger Bands (20, 2)", "BB", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "SMA envelope at ±2 population standard deviations."),
        new StudyDefinition("Donchian Channel (20)", "DC", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Highest-high / lowest-low breakout channel over 20 bars."),
        new StudyDefinition("Keltner Channel (20, 10, 2)", "KC", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "EMA midline with ATR-scaled bands."),
        new StudyDefinition("RSI (14)", "RSI", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Wilder's Relative Strength Index in its own pane (0–100)."),
        new StudyDefinition("MACD (12, 26, 9)", "MACD", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Moving-average convergence/divergence with signal and histogram."),
        new StudyDefinition("ADX (14)", "ADX", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Wilder's trend-strength index with +DI/−DI (green/purple)."),
        new StudyDefinition("ATR (14)", "ATR", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Average true range (volatility, in ticks)."),
        new StudyDefinition("Stochastic (14, 3, 3)", "STOCH", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Close position within the recent range; %K and %D (0–100)."),
        new StudyDefinition("CCI (20)", "CCI", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Commodity Channel Index oscillator."),
        new StudyDefinition("Rate of Change (9)", "ROC", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Percentage price change over a fixed lookback."),
        new StudyDefinition("Momentum (10)", "MOM", StudyCategory.TechnicalIndicators, StudyStatus.Active,
            "Absolute price change over a fixed lookback."),
    };

    public static IEnumerable<StudyDefinition> ByCategory(StudyCategory category) =>
        All.Where(s => s.Category == category);

    public static string CategoryTitle(StudyCategory c) => c switch
    {
        StudyCategory.VolumeAndProfile => "Volume & Profile",
        StudyCategory.OrderFlowAndTape => "Order Flow & Tape",
        StudyCategory.TechnicalAndPriceAction => "Technical & Price Action",
        StudyCategory.TechnicalIndicators => "Technical Indicators",
        _ => c.ToString(),
    };

    public static string StatusLabel(StudyStatus s) => s switch
    {
        StudyStatus.Active => "ACTIVE",
        StudyStatus.EngineReady => "READY",
        StudyStatus.Planned => "PLANNED",
        _ => s.ToString(),
    };
}
