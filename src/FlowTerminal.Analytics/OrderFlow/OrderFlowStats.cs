using FlowTerminal.Domain.Events;

namespace FlowTerminal.Analytics.OrderFlow;

/// <summary>
/// Shared incremental per-bar order-flow statistics. One instance accumulates a bar's
/// trades in O(1) per trade; indicators read the struct-like snapshot instead of each
/// re-deriving the same sums. Canonical delta = aggressive buys − aggressive sells;
/// unknown-side volume is tracked separately and never forced into a side.
/// </summary>
public sealed class BarFlowStats
{
    public long BuyVolume { get; private set; }
    public long SellVolume { get; private set; }
    public long UnknownVolume { get; private set; }
    public int TradeCount { get; private set; }
    public int BuyTradeCount { get; private set; }
    public int SellTradeCount { get; private set; }
    public long MaxTrade { get; private set; }
    public long HighTicks { get; private set; } = long.MinValue;
    public long LowTicks { get; private set; } = long.MaxValue;
    public long FirstPriceTicks { get; private set; }
    public long LastPriceTicks { get; private set; }
    public DateTime StartUtc { get; private set; }
    public DateTime EndUtc { get; private set; }

    public long TotalVolume => BuyVolume + SellVolume + UnknownVolume;
    public long Delta => BuyVolume - SellVolume;

    /// <summary>Delta as a fraction of classified (buy+sell) volume; 0 when none.</summary>
    public double DeltaPercent
    {
        get { long c = BuyVolume + SellVolume; return c > 0 ? Delta / (double)c : 0; }
    }

    public double AverageTradeSize => TradeCount > 0 ? TotalVolume / (double)TradeCount : 0;

    /// <summary>Signed price progress of the bar in ticks (last − first).</summary>
    public long PriceProgressTicks => TradeCount > 0 ? LastPriceTicks - FirstPriceTicks : 0;

    /// <summary>
    /// Delta efficiency = |price progress| / |delta| (ticks per contract of net
    /// aggression). Low values with high |delta| are effort-versus-result anomalies
    /// (absorption candidates). NaN when delta is zero — callers must handle it.
    /// </summary>
    public double DeltaEfficiency => Delta != 0 ? Math.Abs(PriceProgressTicks) / (double)Math.Abs(Delta) : double.NaN;

    /// <summary>Volume efficiency = |price progress| / total volume. NaN when no volume.</summary>
    public double VolumeEfficiency => TotalVolume > 0 ? Math.Abs(PriceProgressTicks) / (double)TotalVolume : double.NaN;

    public void OnTrade(in MarketEvent e)
    {
        if (e.Type != MarketEventType.Trade || e.Quantity <= 0) return;
        if (TradeCount == 0)
        {
            StartUtc = e.ExchangeTimestampUtc;
            FirstPriceTicks = e.PriceTicks;
        }

        EndUtc = e.ExchangeTimestampUtc;
        LastPriceTicks = e.PriceTicks;
        HighTicks = Math.Max(HighTicks, e.PriceTicks);
        LowTicks = Math.Min(LowTicks, e.PriceTicks);
        TradeCount++;
        MaxTrade = Math.Max(MaxTrade, e.Quantity);

        switch (e.Aggressor)
        {
            case AggressorSide.Buy: BuyVolume += e.Quantity; BuyTradeCount++; break;
            case AggressorSide.Sell: SellVolume += e.Quantity; SellTradeCount++; break;
            default: UnknownVolume += e.Quantity; break;
        }
    }

    public void Reset()
    {
        BuyVolume = SellVolume = UnknownVolume = 0;
        TradeCount = BuyTradeCount = SellTradeCount = 0;
        MaxTrade = 0;
        HighTicks = long.MinValue;
        LowTicks = long.MaxValue;
        FirstPriceTicks = LastPriceTicks = 0;
        StartUtc = EndUtc = default;
    }
}

/// <summary>
/// Rolling time-window order-flow statistics over the canonical trade stream (bounded
/// ring; O(1) amortized per trade). Velocities use REAL elapsed time from event
/// timestamps — never an assumed fixed interval — so replay speed cannot change them.
/// </summary>
public sealed class RollingFlowWindow
{
    private readonly record struct Entry(DateTime Ts, long PriceTicks, long Qty, AggressorSide Side);

    private readonly TimeSpan _window;
    private readonly Queue<Entry> _entries = new();
    private long _buy, _sell, _unknown;

    public RollingFlowWindow(TimeSpan window) => _window = window;

    public long BuyVolume => _buy;
    public long SellVolume => _sell;
    public long UnknownVolume => _unknown;
    public long Delta => _buy - _sell;
    public long TotalVolume => _buy + _sell + _unknown;
    public int TradeCount => _entries.Count;

    /// <summary>Signed price progress across the window in ticks (0 with <2 trades).</summary>
    public long PriceProgressTicks { get; private set; }

    /// <summary>Actual seconds spanned by the retained entries (≥ a small epsilon).</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>Delta per second over the real elapsed span.</summary>
    public double DeltaVelocity => Delta / Math.Max(0.001, ElapsedSeconds);

    /// <summary>Total volume per second over the real elapsed span.</summary>
    public double VolumeVelocity => TotalVolume / Math.Max(0.001, ElapsedSeconds);

    /// <summary>Trades per second over the real elapsed span.</summary>
    public double TradeVelocity => TradeCount / Math.Max(0.001, ElapsedSeconds);

    private Entry _last;

    public void OnTrade(in MarketEvent e)
    {
        if (e.Type != MarketEventType.Trade || e.Quantity <= 0) return;
        _last = new Entry(e.ExchangeTimestampUtc, e.PriceTicks, e.Quantity, e.Aggressor);
        _entries.Enqueue(_last);
        Add(e.Aggressor, e.Quantity);
        Trim(e.ExchangeTimestampUtc);
        Recompute();
    }

    private void Trim(DateTime now)
    {
        while (_entries.Count > 0 && now - _entries.Peek().Ts > _window)
        {
            var old = _entries.Dequeue();
            Add(old.Side, -old.Qty);
        }
    }

    private void Add(AggressorSide side, long qty)
    {
        switch (side)
        {
            case AggressorSide.Buy: _buy += qty; break;
            case AggressorSide.Sell: _sell += qty; break;
            default: _unknown += qty; break;
        }
    }

    private void Recompute()
    {
        if (_entries.Count < 2) { PriceProgressTicks = 0; ElapsedSeconds = 0.001; return; }
        var first = _entries.Peek();
        PriceProgressTicks = _last.PriceTicks - first.PriceTicks;
        ElapsedSeconds = Math.Max(0.001, (_last.Ts - first.Ts).TotalSeconds);
    }

    public void Reset()
    {
        _entries.Clear();
        _buy = _sell = _unknown = 0;
        PriceProgressTicks = 0;
        ElapsedSeconds = 0;
    }
}
