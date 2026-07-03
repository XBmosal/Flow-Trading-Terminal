using FlowTerminal.Domain.Events;

namespace FlowTerminal.MarketData.Synthetic;

/// <summary>Receives the book mutations and executions the generators produce.</summary>
internal interface IBookEventSink
{
    /// <summary>A resting level's displayed size changed (0 ⇒ the level was removed).</summary>
    void OnDepthChanged(Side side, long price, long displayed);

    /// <summary>An aggressive order executed <paramref name="qty"/> at <paramref name="price"/>.</summary>
    void OnExecution(AggressorSide aggressor, long price, long qty);
}

/// <summary>
/// Generates aggressive trades <em>from the book</em>. Every clip is matched against
/// real resting liquidity: it lifts asks (buy) or hits bids (sell) from the touch
/// outward, consuming levels strictly in price order (no skipping), printing one
/// execution per price it trades through, and reducing/removing the levels it eats.
/// When a touch level is fully consumed the best price moves — that is the only way
/// (together with re-quoting) the synthetic price changes, so price is emergent, not
/// an external walk. Clip sizes are heavy-tailed; rare outsized clips sweep.
/// </summary>
public sealed class SyntheticTradeGenerator
{
    private readonly SyntheticMarketConfiguration _cfg;

    public SyntheticTradeGenerator(SyntheticMarketConfiguration cfg) => _cfg = cfg;

    /// <summary>Net executed quantity (buy positive) produced on the most recent step.</summary>
    public long LastNetFlow { get; private set; }

    // Short-lived aggressor momentum (EMA of recent clip directions). It makes flow
    // cluster — a few buys beget more buys — without ever locking one-sided.
    private double _momentum;

    internal void Generate(SyntheticOrderBook book, in RegimeProfile p, double buyBias,
        ref DeterministicRng rng, IBookEventSink sink, double phaseRate = 1.0)
    {
        LastNetFlow = 0;
        double lambda = _cfg.TradeRate * p.TradeMult * phaseRate;
        int clips = SampleClipCount(lambda, p.TradeMult, ref rng);
        if (clips == 0)
        {
            return;
        }

        double pBuy = Math.Clamp(
            0.5 + 0.40 * buyBias + 0.09 * _momentum + 0.06 * SynthDistributions.NextGaussian(ref rng),
            0.04, 0.96);

        for (int c = 0; c < clips; c++)
        {
            bool buy = rng.NextDouble() < pBuy;
            _momentum = Math.Clamp(0.90 * _momentum + 0.10 * (buy ? 1 : -1), -1, 1);
            long size = ClipSize(in p, ref rng);
            if (size <= 0)
            {
                continue;
            }

            if (buy)
            {
                Sweep(book, Side.Ask, AggressorSide.Buy, size, ref rng, sink, ascending: true);
            }
            else
            {
                Sweep(book, Side.Bid, AggressorSide.Sell, size, ref rng, sink, ascending: false);
            }
        }
    }

    /// <summary>
    /// Executes a single aggressive order of the given size against the book, used by
    /// the orchestrator and exercised directly in tests. A buy lifts asks, a sell hits
    /// bids, always from the touch outward in strict price order.
    /// </summary>
    internal void ExecuteOrder(SyntheticOrderBook book, AggressorSide aggressor, long size,
        ref DeterministicRng rng, IBookEventSink sink)
    {
        LastNetFlow = 0;
        if (aggressor == AggressorSide.Buy)
        {
            Sweep(book, Side.Ask, AggressorSide.Buy, size, ref rng, sink, ascending: true);
        }
        else
        {
            Sweep(book, Side.Bid, AggressorSide.Sell, size, ref rng, sink, ascending: false);
        }
    }

    private void Sweep(SyntheticOrderBook book, Side restingSide, AggressorSide aggressor,
        long size, ref DeterministicRng rng, IBookEventSink sink, bool ascending)
    {
        long remaining = size;
        int levels = 0;

        while (remaining > 0 && levels < _cfg.MaxSweepLevels)
        {
            long price = restingSide == Side.Ask ? book.BestAskTicks : book.BestBidTicks;
            if (price == SyntheticOrderBook.NoPrice)
            {
                break; // nothing left to trade against this step
            }

            var lvl = book.Find(restingSide, price);
            if (lvl is null)
            {
                break;
            }

            long fill = Math.Min(remaining, lvl.Displayed);
            if (fill <= 0)
            {
                break;
            }

            lvl.Displayed -= fill;
            lvl.ExecutedTotal += fill;
            remaining -= fill;
            levels++;
            LastNetFlow += aggressor == AggressorSide.Buy ? fill : -fill;

            sink.OnExecution(aggressor, price, fill);

            if (lvl.Displayed <= 0)
            {
                book.Remove(restingSide, price);
                sink.OnDepthChanged(restingSide, price, 0);
            }
            else
            {
                lvl.LastChangeUtc = lvl.CreatedUtc; // touched this step
                sink.OnDepthChanged(restingSide, price, lvl.Displayed);
                break; // partial fill of the touch — order is done, no need to walk further
            }

            // Continue only if the order is outsized enough to keep eating (sweep).
            if (remaining > 0 && !SynthDistributions.Chance(ref rng, 0.85))
            {
                break;
            }

            _ = ascending; // direction is encoded by best-price recompute in the book
        }
    }

    private int SampleClipCount(double lambda, double regimeTradeMult, ref DeterministicRng rng)
    {
        // Cheap, bounded approximation of a clustered arrival process: integer part
        // fires for sure, fractional part is a coin flip, and an occasional burst of
        // several clips lands in one step (more often in busy regimes). Bursts change
        // *when* trades arrive, never how big they are.
        int n = (int)lambda;
        if (rng.NextDouble() < lambda - n) n++;
        if (rng.NextDouble() < 0.02 * regimeTradeMult) n += rng.NextInt(2, 5);
        return Math.Min(n, 8);
    }

    /// <summary>
    /// Three-tier mixture clip size: heavy-tailed small body, uncommon large tier, and an
    /// exceptional extreme tail. Regime SweepMult scales only the tail-tier probabilities
    /// (never every clip's size), and a hard cap keeps the tail finite.
    /// </summary>
    private long ClipSize(in RegimeProfile p, ref DeterministicRng rng)
    {
        long size;
        if (_cfg.ExtremeTradeProbability > 0 &&
            SynthDistributions.Chance(ref rng, _cfg.ExtremeTradeProbability * p.SweepMult))
        {
            size = (long)Math.Round(SynthDistributions.NextLogNormal(ref rng, _cfg.ExtremeSizeMedian, _cfg.ExtremeSizeSigma));
        }
        else if (SynthDistributions.Chance(ref rng, _cfg.LargeTradeProbability * p.SweepMult))
        {
            size = _cfg.LargeSizeMedian > 0
                ? (long)Math.Round(SynthDistributions.NextLogNormal(ref rng, _cfg.LargeSizeMedian, _cfg.LargeSizeSigma))
                : (long)Math.Round(_cfg.TradeSizeMedian *
                    SynthDistributions.Lerp(ref rng, _cfg.LargeTradeMultMin, _cfg.LargeTradeMultMax));
        }
        else
        {
            size = SynthDistributions.Size(ref rng, _cfg.TradeSizeMedian, _cfg.TradeSizeSigma, 1.0, 1);
        }

        return Math.Clamp(size, 1, _cfg.MaxClipSize);
    }
}
