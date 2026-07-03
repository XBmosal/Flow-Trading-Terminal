using FlowTerminal.Analytics.Bars;

namespace FlowTerminal.Analytics.OrderFlow;

public enum DivergenceType
{
    RegularBullish,   // price lower low, delta higher low
    RegularBearish,   // price higher high, delta lower high
    HiddenBullish,    // price higher low, delta lower low
    HiddenBearish,    // price lower high, delta higher high
}

public enum DivergenceState { Developing, Confirmed, Invalidated }

public enum DivergenceStrength { Weak, Moderate, Strong }

/// <summary>
/// One detected delta divergence between two same-kind swing pivots. All fields are the
/// evidence the signal was built from, so an inspector can show exactly why it exists.
/// Descriptive only — a divergence is not a directional guarantee.
/// </summary>
public sealed record DivergenceSignal(
    ulong Id,
    DivergenceType Type,
    DivergenceState State,
    int FirstPivotBar,
    int SecondPivotBar,
    int ConfirmedAtBar,          // the bar index at which the signal legally exists (no lookahead)
    DateTime FirstPivotTimeUtc,
    DateTime SecondPivotTimeUtc,
    long FirstPivotPriceTicks,
    long SecondPivotPriceTicks,
    long FirstDelta,
    long SecondDelta,
    double Score,                // 0..1, formula documented in ORDER_FLOW_FEATURES.md
    DivergenceStrength Strength)
{
    public bool IsBullish => Type is DivergenceType.RegularBullish or DivergenceType.HiddenBullish;
}

public sealed record DeltaDivergenceSettings2
{
    /// <summary>Bars to the left that a pivot must dominate.</summary>
    public int PivotLeft { get; init; } = 3;

    /// <summary>Bars to the right that must complete before the pivot is confirmed (no lookahead).</summary>
    public int PivotRight { get; init; } = 3;

    /// <summary>Minimum / maximum bar separation between the compared pivots.</summary>
    public int MinSeparationBars { get; init; } = 3;
    public int MaxSeparationBars { get; init; } = 60;

    /// <summary>Minimum price difference (ticks) between the two pivots.</summary>
    public long MinPriceDiffTicks { get; init; } = 2;

    /// <summary>Minimum delta difference between the two pivots.</summary>
    public long MinDeltaDiff { get; init; } = 1;

    /// <summary>Include hidden (continuation) divergences.</summary>
    public bool DetectHidden { get; init; } = true;

    /// <summary>Bound on retained bars/pivots (memory stays flat over long sessions).</summary>
    public int MaxRetainedBars { get; init; } = 2_000;
}

/// <summary>
/// Pivot-based delta-divergence engine on completed bars.
///
/// A swing high (low) at bar i is confirmed only once <c>PivotRight</c> further bars
/// have completed and bar i's high (low) strictly dominates <c>PivotLeft</c> bars back
/// and <c>PivotRight</c> bars forward. A signal comparing pivots therefore first exists
/// at bar <c>i + PivotRight</c> — <b>never earlier</b> — which is the no-lookahead
/// guarantee replay relies on. The delta compared at a pivot is the pivot bar's delta
/// (aggressive buys − sells of that bar, from canonical trades).
///
/// Types: regular bullish (price LL, delta HL), regular bearish (price HH, delta LH),
/// hidden bullish (price HL, delta LL), hidden bearish (price LH, delta HH).
///
/// A <b>developing</b> signal is additionally reported when the current still-open
/// extreme would form a divergence against the last confirmed pivot; it is visually
/// distinct and may vanish — it never rewrites history.
///
/// Strength score (documented, transparent):
///   score = 0.45·min(1, |Δdelta| / deltaScale)
///         + 0.35·min(1, |Δprice| / priceScale)
///         + 0.20·min(1, separationBars / MaxSeparationBars)
/// where deltaScale = max(|d1|,|d2|,1) and priceScale = 20 ticks.
/// Weak &lt; 0.4 ≤ Moderate &lt; 0.7 ≤ Strong.
/// </summary>
public sealed class DeltaDivergenceEngine
{
    private readonly record struct Pivot(int Bar, long PriceTicks, long Delta, DateTime TimeUtc, bool IsHigh);

    private readonly DeltaDivergenceSettings2 _s;
    private readonly List<Bar> _bars = new();
    private readonly List<Pivot> _highs = new();
    private readonly List<Pivot> _lows = new();
    private readonly List<DivergenceSignal> _signals = new();
    private ulong _nextId = 1;
    private int _evicted; // bars dropped from the front; indexes reported are absolute

    public DeltaDivergenceEngine(DeltaDivergenceSettings2? settings = null) => _s = settings ?? new DeltaDivergenceSettings2();

    /// <summary>All confirmed signals so far, in confirmation order.</summary>
    public IReadOnlyList<DivergenceSignal> Signals => _signals;

    public void Reset()
    {
        _bars.Clear();
        _highs.Clear();
        _lows.Clear();
        _signals.Clear();
        _evicted = 0;
        _nextId = 1;
    }

    /// <summary>
    /// Feeds the next completed bar; returns any signal confirmed at this bar.
    /// Deterministic: the same bar sequence yields the same signals and ids.
    /// </summary>
    public DivergenceSignal? OnBar(in Bar bar)
    {
        _bars.Add(bar);
        Bound();

        // The bar that may now be *confirmed* as a pivot is PivotRight bars back.
        int rel = _bars.Count - 1 - _s.PivotRight;
        if (rel < _s.PivotLeft) return null;

        DivergenceSignal? emitted = null;
        if (IsPivot(rel, high: true))
        {
            var p = MakePivot(rel, high: true);
            emitted = Compare(_highs, p, high: true) ?? emitted;
            _highs.Add(p);
            if (_highs.Count > 64) _highs.RemoveAt(0);
        }

        if (IsPivot(rel, high: false))
        {
            var p = MakePivot(rel, high: false);
            emitted = Compare(_lows, p, high: false) ?? emitted;
            _lows.Add(p);
            if (_lows.Count > 64) _lows.RemoveAt(0);
        }

        if (emitted is not null)
        {
            _signals.Add(emitted);
            if (_signals.Count > 256) _signals.RemoveAt(0); // bounded for long sessions
        }

        return emitted;
    }

    /// <summary>
    /// The developing (unconfirmed) divergence the currently-forming extreme would
    /// create against the last confirmed pivot, or null. Purely advisory — it may
    /// disappear and is never added to <see cref="Signals"/>.
    /// </summary>
    public DivergenceSignal? Developing()
    {
        if (_bars.Count == 0) return null;
        int lastRel = _bars.Count - 1;
        var bar = _bars[lastRel];
        var asHigh = new Pivot(Abs(lastRel), bar.HighTicks, bar.Delta, bar.EndUtc, IsHigh: true);
        var asLow = new Pivot(Abs(lastRel), bar.LowTicks, bar.Delta, bar.EndUtc, IsHigh: false);
        return Build(_highs, asHigh, high: true, DivergenceState.Developing)
            ?? Build(_lows, asLow, high: false, DivergenceState.Developing);
    }

    private int Abs(int rel) => rel + _evicted;

    private bool IsPivot(int rel, bool high)
    {
        var b = _bars[rel];
        long v = high ? b.HighTicks : b.LowTicks;
        for (int i = rel - _s.PivotLeft; i <= rel + _s.PivotRight; i++)
        {
            if (i == rel) continue;
            if (i < 0 || i >= _bars.Count) return false;
            long o = high ? _bars[i].HighTicks : _bars[i].LowTicks;
            if (high ? o >= v : o <= v) return false; // strict dominance
        }

        return true;
    }

    private Pivot MakePivot(int rel, bool high)
    {
        var b = _bars[rel];
        return new Pivot(Abs(rel), high ? b.HighTicks : b.LowTicks, b.Delta, b.EndUtc, high);
    }

    private DivergenceSignal? Compare(List<Pivot> prior, Pivot cur, bool high)
        => prior.Count == 0 ? null : Build(prior, cur, high, DivergenceState.Confirmed);

    private DivergenceSignal? Build(List<Pivot> prior, Pivot cur, bool high, DivergenceState state)
    {
        if (prior.Count == 0) return null;
        var prev = prior[^1];
        int sep = cur.Bar - prev.Bar;
        if (sep < _s.MinSeparationBars || sep > _s.MaxSeparationBars) return null;
        if (Math.Abs(cur.PriceTicks - prev.PriceTicks) < _s.MinPriceDiffTicks) return null;
        if (Math.Abs(cur.Delta - prev.Delta) < _s.MinDeltaDiff) return null;

        DivergenceType? type = null;
        if (high)
        {
            if (cur.PriceTicks > prev.PriceTicks && cur.Delta < prev.Delta) type = DivergenceType.RegularBearish;
            else if (_s.DetectHidden && cur.PriceTicks < prev.PriceTicks && cur.Delta > prev.Delta) type = DivergenceType.HiddenBearish;
        }
        else
        {
            if (cur.PriceTicks < prev.PriceTicks && cur.Delta > prev.Delta) type = DivergenceType.RegularBullish;
            else if (_s.DetectHidden && cur.PriceTicks > prev.PriceTicks && cur.Delta < prev.Delta) type = DivergenceType.HiddenBullish;
        }

        if (type is null) return null;

        // Transparent score (see class doc / ORDER_FLOW_FEATURES.md).
        double deltaScale = Math.Max(1, Math.Max(Math.Abs(prev.Delta), Math.Abs(cur.Delta)));
        double score =
            0.45 * Math.Min(1.0, Math.Abs(cur.Delta - prev.Delta) / deltaScale) +
            0.35 * Math.Min(1.0, Math.Abs(cur.PriceTicks - prev.PriceTicks) / 20.0) +
            0.20 * Math.Min(1.0, sep / (double)_s.MaxSeparationBars);
        var strength = score < 0.4 ? DivergenceStrength.Weak
            : score < 0.7 ? DivergenceStrength.Moderate : DivergenceStrength.Strong;

        // The signal legally exists only once the second pivot's right side completed.
        int confirmedAt = state == DivergenceState.Confirmed ? cur.Bar + _s.PivotRight : cur.Bar;

        return new DivergenceSignal(
            state == DivergenceState.Confirmed ? _nextId++ : 0,
            type.Value, state,
            prev.Bar, cur.Bar, confirmedAt,
            prev.TimeUtc, cur.TimeUtc,
            prev.PriceTicks, cur.PriceTicks,
            prev.Delta, cur.Delta,
            Math.Round(score, 4), strength);
    }

    private void Bound()
    {
        if (_bars.Count <= _s.MaxRetainedBars) return;
        int drop = _bars.Count - _s.MaxRetainedBars;
        _bars.RemoveRange(0, drop);
        _evicted += drop;
    }
}
