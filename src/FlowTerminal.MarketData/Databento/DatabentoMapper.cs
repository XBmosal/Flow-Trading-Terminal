using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.MarketData.Databento;

/// <summary>
/// Maps decoded Databento DBN records into Flow Terminal's canonical
/// <see cref="MarketEvent"/> stream. This is the entire instrument-specific integration
/// surface: everything downstream (book, footprints, CVD, DOM, Big Trades, order-flow
/// suite, replay) is already source-agnostic, so a correct mapper here is all that a new
/// feed needs. Pure and deterministic — no network, no SDK — so it is exhaustively unit
/// tested with fixtures.
///
/// Semantics (Databento documented conventions):
///   • Prices are Int64 fixed-point at 1e-9; converted to integer ticks via the shared
///     <see cref="PriceConverter"/> (through decimal — never binary floating point).
///   • Timestamps are UInt64 nanoseconds since the UNIX epoch, UTC.
///   • A trade's <c>side</c> is the AGGRESSOR side: 'B' → aggressive buy, 'A' → aggressive
///     sell, 'N'/other → Unknown. When a definite side is supplied the event is flagged
///     <see cref="MarketEventFlags.AggressorSupplied"/> (native, not estimated); 'N' stays
///     Unknown and is never forced into a side.
///
/// The aggressor-side convention is Databento's own; a real fixture from the target
/// dataset should confirm it before production use (documented in ORDER_FLOW / MOCK docs).
/// </summary>
public sealed class DatabentoMapper
{
    private readonly int _instrumentId;
    private readonly InstrumentSpec _spec;
    private readonly string _contractSymbol;
    private readonly string _exchange;

    // Last-known top-of-book snapshots (price ticks → size) for mbp-10 delta emission.
    private readonly Dictionary<long, long> _bids = new();
    private readonly Dictionary<long, long> _asks = new();
    private readonly List<MarketEvent> _scratch = new();

    private const MarketEventFlags Depth =
        MarketEventFlags.HasExchangeSequence | MarketEventFlags.HasExchangeTimestamp;
    private const MarketEventFlags TradeNative = Depth | MarketEventFlags.AggressorSupplied;

    public DatabentoMapper(int instrumentId, Contract contract)
    {
        _instrumentId = instrumentId;
        _spec = contract.Spec;
        _contractSymbol = contract.Symbol;
        _exchange = contract.Spec.Exchange;
    }

    /// <summary>Nanoseconds-since-epoch → UTC <see cref="DateTime"/> (1 tick = 100 ns).</summary>
    public static DateTime ToUtc(ulong tsEventNs) => DateTime.UnixEpoch.AddTicks((long)(tsEventNs / 100));

    /// <summary>1e-9 fixed-point → integer ticks for this instrument (through decimal).</summary>
    public long ToTicks(long priceFixed) => PriceConverter.ToTicks(_spec, priceFixed / 1_000_000_000m);

    /// <summary>Databento aggressor <c>side</c> char → canonical aggressor side.</summary>
    public static AggressorSide MapAggressor(char side) => side switch
    {
        'B' or 'b' => AggressorSide.Buy,
        'A' or 'a' => AggressorSide.Sell,
        _ => AggressorSide.Unknown,
    };

    /// <summary>Maps a <c>trades</c>-schema record to a canonical trade event.</summary>
    public MarketEvent MapTrade(in DatabentoTrade t)
    {
        var side = MapAggressor(t.Side);
        var ts = ToUtc(t.TsEventNs);
        return MarketEvent.Trade(
            _instrumentId, _spec.Root, _contractSymbol, _exchange, ts, ts,
            ToTicks(t.PriceFixed), t.Size, side,
            exchangeSequence: (long)t.Sequence, tradeId: (long)t.Sequence,
            flags: side == AggressorSide.Unknown ? Depth : TradeNative,
            source: SourceProvider.Databento);
    }

    /// <summary>
    /// Maps an <c>mbp-10</c> record to canonical events: any embedded trade first, then
    /// BidUpdate/AskUpdate deltas for every top-10 level that changed since the previous
    /// snapshot (including a size-0 update for a level that dropped out of the top 10).
    /// Returns a reused buffer — copy it if you need to retain it past the next call.
    /// </summary>
    public IReadOnlyList<MarketEvent> MapDepth(in DatabentoMbp10 m)
    {
        _scratch.Clear();
        var ts = ToUtc(m.TsEventNs);
        long seq = (long)m.Sequence;

        // Book clear: drop all known levels (emit size-0 for each), then rebuild below.
        if (m.Action is 'R' or 'r')
        {
            EmitClear(Side.Bid, _bids, ts, ref seq);
            EmitClear(Side.Ask, _asks, ts, ref seq);
        }

        if (m.Action is 'T' or 't' or 'F' or 'f')
        {
            var side = MapAggressor(m.Side);
            _scratch.Add(MarketEvent.Trade(
                _instrumentId, _spec.Root, _contractSymbol, _exchange, ts, ts,
                ToTicks(m.PriceFixed), m.Size, side,
                exchangeSequence: seq, tradeId: seq,
                flags: side == AggressorSide.Unknown ? Depth : TradeNative,
                source: SourceProvider.Databento));
            seq++;
        }

        DiffSide(Side.Bid, _bids, m.Levels, ts, ref seq);
        DiffSide(Side.Ask, _asks, m.Levels, ts, ref seq);
        return _scratch;
    }

    public void Reset()
    {
        _bids.Clear();
        _asks.Clear();
        _scratch.Clear();
    }

    private void DiffSide(Side side, Dictionary<long, long> prev, IReadOnlyList<DatabentoLevel> levels, DateTime ts, ref long seq)
    {
        // Build the new top-10 map for this side.
        var now = new Dictionary<long, long>(levels.Count);
        foreach (var lvl in levels)
        {
            long priceFixed = side == Side.Bid ? lvl.BidPriceFixed : lvl.AskPriceFixed;
            uint size = side == Side.Bid ? lvl.BidSize : lvl.AskSize;
            if (priceFixed == DatabentoMbp10.UndefPrice || size == 0) continue;
            now[ToTicks(priceFixed)] = size;
        }

        // Changed / new levels → update event.
        foreach (var (price, size) in now)
        {
            if (!prev.TryGetValue(price, out var old) || old != size)
            {
                Emit(side, price, size, ts, ref seq);
            }
        }

        // Levels that fell out of the top 10 → size-0 update.
        foreach (var (price, _) in prev)
        {
            if (!now.ContainsKey(price)) Emit(side, price, 0, ts, ref seq);
        }

        prev.Clear();
        foreach (var kv in now) prev[kv.Key] = kv.Value;
    }

    private void EmitClear(Side side, Dictionary<long, long> book, DateTime ts, ref long seq)
    {
        foreach (var (price, _) in book) Emit(side, price, 0, ts, ref seq);
        book.Clear();
    }

    private void Emit(Side side, long priceTicks, long size, DateTime ts, ref long seq)
    {
        var type = side == Side.Bid ? MarketEventType.BidUpdate : MarketEventType.AskUpdate;
        _scratch.Add(MarketEvent.Quote(
            _instrumentId, _spec.Root, _contractSymbol, _exchange, type, ts, ts,
            side, priceTicks, size, seq++, Depth, SourceProvider.Databento));
    }
}
