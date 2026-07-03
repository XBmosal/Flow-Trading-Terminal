using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Analytics.OrderFlow;
using FlowTerminal.Analytics.PriceAction;
using FlowTerminal.Analytics.Profiles;
using FlowTerminal.Analytics.Vwap;

namespace FlowTerminal.Charting.Overlays;

/// <summary>
/// Per-frame study overlay data computed by the feed and drawn onto the price chart
/// by <see cref="ChartOverlayRenderer"/>. All prices are integer ticks; series are
/// aligned to the visible bar list by index.
/// </summary>
public sealed record ChartOverlays(
    IReadOnlyList<ProfileLevel> Profile,
    long PocTicks,
    long VahTicks,
    long ValTicks,
    IReadOnlyList<double> VwapByBar,
    IReadOnlyList<FvgBox> Fvgs,
    long? OrbHighTicks,
    long? OrbLowTicks,
    IReadOnlyList<FootprintBar> Footprint,
    IReadOnlyList<TpoRow> Tpo,
    IReadOnlyList<BigTradeGroup> BigTrades)
{
    /// <summary>Confirmed delta-divergence signals (pivot-to-pivot; no lookahead).</summary>
    public IReadOnlyList<DivergenceSignal> Divergences { get; init; } = Array.Empty<DivergenceSignal>();

    /// <summary>The currently-developing divergence, if any (drawn dashed; may vanish).</summary>
    public DivergenceSignal? DevelopingDivergence { get; init; }

    /// <summary>Recent completed delta blocks (canonical trade-derived).</summary>
    public IReadOnlyList<DeltaBlock> DeltaBlocks { get; init; } = Array.Empty<DeltaBlock>();

    /// <summary>Anchored VWAP per-bar samples (ticks; NaN before the anchor has volume).</summary>
    public IReadOnlyList<AnchoredVwapPoint> AnchoredVwap { get; init; } = Array.Empty<AnchoredVwapPoint>();

    public static ChartOverlays Empty { get; } = new(
        Array.Empty<ProfileLevel>(), long.MinValue, long.MinValue, long.MinValue,
        Array.Empty<double>(), Array.Empty<FvgBox>(), null, null,
        Array.Empty<FootprintBar>(), Array.Empty<TpoRow>(), Array.Empty<BigTradeGroup>());
}

/// <summary>A fair-value gap anchored to the bar index where it formed.</summary>
public readonly record struct FvgBox(int BarIndex, GapDirection Direction, long TopTicks, long BottomTicks);

/// <summary>One TPO/Market-Profile row: a price and its lettered brackets (e.g. "ABD").</summary>
public readonly record struct TpoRow(long PriceTicks, string Letters);
