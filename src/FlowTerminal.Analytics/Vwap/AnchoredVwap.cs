using FlowTerminal.Domain.Events;

namespace FlowTerminal.Analytics.Vwap;

public enum VwapAnchorType
{
    Manual,          // user-picked timestamp (chart click / settings)
    SessionOpen,
    SwingHigh,
    SwingLow,
    LargeTrade,
    Sweep,
    Divergence,
    Custom,
}

/// <summary>
/// One anchored-VWAP instance: identity + anchor metadata + the incremental
/// accumulator. VWAP = Σ(price·volume)/Σ(volume) from the anchor forward, with
/// volume-weighted population std-dev bands (variance = Σ(v·p²)/Σv − vwap², see
/// <see cref="VwapCalculator"/>). Trades strictly before the anchor are ignored; an
/// anchor with no volume yet reports no value rather than a fake one.
/// </summary>
public sealed class AnchoredVwapInstance
{
    private readonly VwapCalculator _calc = new();

    public AnchoredVwapInstance(ulong id, string name, VwapAnchorType type, DateTime anchorUtc)
    {
        Id = id;
        Name = name;
        Type = type;
        AnchorUtc = anchorUtc;
    }

    public ulong Id { get; }
    public string Name { get; set; }
    public VwapAnchorType Type { get; }
    public DateTime AnchorUtc { get; private set; }
    public bool Visible { get; set; } = true;
    public bool ShowBands { get; set; } = true;

    /// <summary>Band multipliers (volume-weighted σ). Defaults 1/2/3 per the spec.</summary>
    public double[] BandMultipliers { get; set; } = { 1.0, 2.0, 3.0 };

    public bool HasData => _calc.HasData;
    public double TotalVolume => _calc.TotalVolume;

    public VwapValue Value => _calc.Value();

    public void OnTrade(in MarketEvent e)
    {
        if (e.Type != MarketEventType.Trade || e.Quantity <= 0) return;
        if (e.ExchangeTimestampUtc < AnchorUtc) return;
        _calc.AddAt(e.PriceTicks, e.Quantity);
    }

    /// <summary>Moves the anchor; the accumulation restarts (caller replays history if available).</summary>
    public void MoveAnchor(DateTime newAnchorUtc)
    {
        AnchorUtc = newAnchorUtc;
        _calc.Reset();
    }
}

/// <summary>A per-bar sample of an anchored VWAP for rendering (ticks; NaN before data).</summary>
public readonly record struct AnchoredVwapPoint(double VwapTicks, double StdDevTicks);

/// <summary>
/// Manages multiple simultaneous anchored-VWAP instances over one canonical trade
/// stream, and records a per-bar sample series for each so the chart can draw the line
/// and bands without recomputation. Deterministic; instances are bounded.
/// </summary>
public sealed class AnchoredVwapSet
{
    public const int MaxInstances = 12;

    private readonly List<AnchoredVwapInstance> _instances = new();
    private readonly Dictionary<ulong, List<AnchoredVwapPoint>> _series = new();
    private ulong _nextId = 1;

    public IReadOnlyList<AnchoredVwapInstance> Instances => _instances;

    /// <summary>Per-bar sampled series for an instance (aligned to the bars fed via <see cref="OnBarClose"/>).</summary>
    public IReadOnlyList<AnchoredVwapPoint> Series(ulong id) =>
        _series.TryGetValue(id, out var s) ? s : Array.Empty<AnchoredVwapPoint>();

    public AnchoredVwapInstance? Add(string name, VwapAnchorType type, DateTime anchorUtc)
    {
        if (_instances.Count >= MaxInstances) return null;
        var inst = new AnchoredVwapInstance(_nextId++, name, type, anchorUtc);
        _instances.Add(inst);
        _series[inst.Id] = new List<AnchoredVwapPoint>();
        return inst;
    }

    public bool Remove(ulong id)
    {
        int i = _instances.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        _instances.RemoveAt(i);
        _series.Remove(id);
        return true;
    }

    public void OnTrade(in MarketEvent e)
    {
        foreach (var inst in _instances) inst.OnTrade(e);
    }

    /// <summary>
    /// Samples every instance at a bar close, padding earlier bars with NaN so all
    /// series stay index-aligned with the chart's bar list.
    /// </summary>
    public void OnBarClose(int barIndex)
    {
        foreach (var inst in _instances)
        {
            var list = _series[inst.Id];
            while (list.Count < barIndex) list.Add(new AnchoredVwapPoint(double.NaN, 0));
            var v = inst.HasData ? inst.Value : new VwapValue(double.NaN, 0);
            list.Add(new AnchoredVwapPoint(v.VwapTicks, v.StdDevTicks));
        }
    }

    public void Reset()
    {
        _instances.Clear();
        _series.Clear();
        _nextId = 1;
    }
}
