using FlowTerminal.Domain.Events;

namespace FlowTerminal.Analytics.OrderFlow;

public enum DeltaBlockMode
{
    /// <summary>Close a block after a fixed time bucket elapses.</summary>
    Time,

    /// <summary>Close a block once |delta| reaches the threshold.</summary>
    AbsoluteDelta,

    /// <summary>Close a block once total volume reaches the threshold.</summary>
    Volume,

    /// <summary>Close a block once the trade count reaches the threshold.</summary>
    TradeCount,
}

/// <summary>
/// One completed delta block: a contiguous slice of canonical trades summarized as a
/// price×time region. Every trade lands in exactly one block, so block totals reconcile
/// with canonical volume over the same range (tested). Derived from real trade delta —
/// never from candle direction.
/// </summary>
public sealed record DeltaBlock(
    int Index,
    DateTime StartUtc,
    DateTime EndUtc,
    long LowTicks,
    long HighTicks,
    long BuyVolume,
    long SellVolume,
    long UnknownVolume,
    int TradeCount,
    long MaxTrade,
    long PriceProgressTicks)
{
    public long TotalVolume => BuyVolume + SellVolume + UnknownVolume;
    public long Delta => BuyVolume - SellVolume;
    public double DeltaPercent { get { long c = BuyVolume + SellVolume; return c > 0 ? Delta / (double)c : 0; } }
    public double DeltaEfficiency => Delta != 0 ? Math.Abs(PriceProgressTicks) / (double)Math.Abs(Delta) : double.NaN;
}

public sealed record DeltaBlockSettings
{
    public DeltaBlockMode Mode { get; init; } = DeltaBlockMode.AbsoluteDelta;

    /// <summary>Threshold meaning depends on the mode: |delta| / volume / trade count.</summary>
    public long Threshold { get; init; } = 150;

    /// <summary>Bucket length for Time mode.</summary>
    public TimeSpan TimeBucket { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Blocks retained (ring; oldest evicted).</summary>
    public int MaxBlocks { get; init; } = 600;

    public DeltaBlockSettings Validate() => this with
    {
        Threshold = Math.Max(1, Threshold),
        MaxBlocks = Math.Clamp(MaxBlocks, 8, 100_000),
        TimeBucket = TimeBucket <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : TimeBucket,
    };
}

/// <summary>
/// Builds delta blocks incrementally from the canonical trade stream (O(1) per trade).
/// A block accumulates until its mode's completion rule fires, then it is sealed and a
/// new one starts with the next trade — no trade is dropped or double-counted.
/// Deterministic: the same trades and settings reproduce identical blocks.
/// </summary>
public sealed class DeltaBlockEngine
{
    private readonly DeltaBlockSettings _s;
    private readonly List<DeltaBlock> _blocks = new();
    private readonly BarFlowStats _open = new();
    private int _nextIndex;

    public DeltaBlockEngine(DeltaBlockSettings? settings = null) => _s = (settings ?? new DeltaBlockSettings()).Validate();

    public DeltaBlockSettings Settings => _s;

    /// <summary>Completed blocks, oldest first (bounded ring).</summary>
    public IReadOnlyList<DeltaBlock> Blocks => _blocks;

    /// <summary>Snapshot of the currently-forming block, or null when empty.</summary>
    public DeltaBlock? Open => _open.TradeCount == 0 ? null : Seal(_open, _nextIndex);

    public void OnTrade(in MarketEvent e)
    {
        if (e.Type != MarketEventType.Trade || e.Quantity <= 0) return;

        // Time mode seals BEFORE adding a trade that falls into the next bucket, so a
        // block never spans a bucket boundary.
        if (_s.Mode == DeltaBlockMode.Time && _open.TradeCount > 0 &&
            e.ExchangeTimestampUtc - _open.StartUtc >= _s.TimeBucket)
        {
            Complete();
        }

        _open.OnTrade(e);

        bool done = _s.Mode switch
        {
            DeltaBlockMode.AbsoluteDelta => Math.Abs(_open.Delta) >= _s.Threshold,
            DeltaBlockMode.Volume => _open.TotalVolume >= _s.Threshold,
            DeltaBlockMode.TradeCount => _open.TradeCount >= _s.Threshold,
            _ => false,
        };
        if (done) Complete();
    }

    private void Complete()
    {
        _blocks.Add(Seal(_open, _nextIndex++));
        if (_blocks.Count > _s.MaxBlocks) _blocks.RemoveAt(0);
        _open.Reset();
    }

    private static DeltaBlock Seal(BarFlowStats s, int index) => new(
        index, s.StartUtc, s.EndUtc, s.LowTicks, s.HighTicks,
        s.BuyVolume, s.SellVolume, s.UnknownVolume, s.TradeCount, s.MaxTrade, s.PriceProgressTicks);

    public void Reset()
    {
        _blocks.Clear();
        _open.Reset();
        _nextIndex = 0;
    }
}
