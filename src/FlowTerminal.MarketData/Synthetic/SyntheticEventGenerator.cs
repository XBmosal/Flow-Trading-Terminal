using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.MarketData.Synthetic;

/// <summary>
/// The stateful, event-driven synthetic market engine. It maintains a persistent
/// limit-order book (<see cref="SyntheticOrderBook"/>) and mutates it incrementally
/// each simulation step: liquidity is added/pulled/replenished, a regime engine
/// shapes the flow, and aggressive trades execute <em>against the book</em> — which
/// is the only thing that moves price. Every mutation is published as a canonical
/// <see cref="MarketEvent"/> (BidUpdate/AskUpdate/Trade), so the order book the UI
/// reconstructs and the heatmap it renders are genuine histories of this book, never
/// invented visuals.
///
/// The engine is a pure function of its seed: identical seeds yield identical event
/// streams. It exposes a pull interface (<see cref="Next"/>) so the existing mock
/// provider and warm-up replay drive it unchanged.
/// </summary>
public sealed class SyntheticEventGenerator : IBookEventSink
{
    private readonly int _instrumentId;
    private readonly RootSymbol _root;
    private readonly string _symbol;
    private readonly string _exchange;
    private readonly SyntheticOptions _options;
    private readonly SyntheticMarketConfiguration _cfg;

    private readonly SyntheticOrderBook _book = new();
    private readonly SyntheticRegimeEngine _regime;
    private readonly SyntheticTradeGenerator _trades;
    private readonly SyntheticDepthRecorder _recorder = new();
    private readonly Queue<MarketEvent> _pending = new();

    private readonly List<long> _scan = new(64);

    private DeterministicRng _rng;
    private DateTime _clock;
    private long _sequence;
    private long _emitted;
    private bool _gapInjected;
    private bool _initialized;
    private int _step;
    private double _anchor;
    private readonly long _initBid;
    private readonly long _initAsk;

    private const MarketEventFlags DepthFlags =
        MarketEventFlags.Synthetic | MarketEventFlags.HasExchangeSequence | MarketEventFlags.HasExchangeTimestamp;
    private const MarketEventFlags TradeFlags = DepthFlags | MarketEventFlags.AggressorSupplied;

    public SyntheticEventGenerator(int instrumentId, Contract contract, DateTime startUtc, SyntheticOptions options)
    {
        if (startUtc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("startUtc must be UTC.", nameof(startUtc));
        }

        _instrumentId = instrumentId;
        _root = contract.Root;
        _symbol = contract.Symbol;
        _exchange = contract.Spec.Exchange;
        _options = options;
        var cfg = SyntheticMarketConfiguration.ForRoot(contract.Root) with
        {
            BaseStepMs = Math.Max(2, options.MeanInterEventMs * 2),
        };
        // Explicit tuning override; null keeps the instrument profile's calibrated tail.
        if (options.LargeTradeProbability is { } largeP)
        {
            cfg = cfg with { LargeTradeProbability = largeP };
        }

        _cfg = ApplyStress(cfg, options.Stress);
        _rng = new DeterministicRng(options.Seed);
        _clock = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        _regime = new SyntheticRegimeEngine(ref _rng);
        _trades = new SyntheticTradeGenerator(_cfg);

        long mid = PriceConverter.ToTicks(contract.Spec, options.StartPrice);
        _initBid = mid;
        _initAsk = mid + 1;
        _anchor = mid + 0.5;
    }

    /// <summary>
    /// Maps a stress mode onto the configuration. Each mode changes exactly one aspect:
    /// EventRate = more steps per second (size distribution untouched); Depth = heavier
    /// add/cancel/replenish churn; LargeTrade = explicit tail inflation (Big Trade testing
    /// only); Corruption = a deliberate sequence gap. Normal mode returns the profile as-is.
    /// </summary>
    private static SyntheticMarketConfiguration ApplyStress(SyntheticMarketConfiguration cfg, SyntheticStressMode stress) => stress switch
    {
        SyntheticStressMode.EventRate => cfg with
        {
            BaseStepMs = Math.Max(1, cfg.BaseStepMs / 3),   // ~3× steps/sec → ~3× events
        },
        SyntheticStressMode.Depth => cfg with
        {
            LevelChurnRate = Math.Min(0.9, cfg.LevelChurnRate * 3),
            RefillRate = Math.Min(0.95, cfg.RefillRate * 2),
            ReplenishRate = Math.Min(0.95, cfg.ReplenishRate * 2),
        },
        SyntheticStressMode.LargeTrade => cfg with
        {
            LargeTradeProbability = Math.Min(0.25, cfg.LargeTradeProbability * 12),
            ExtremeTradeProbability = Math.Min(0.05, cfg.ExtremeTradeProbability * 12),
        },
        _ => cfg,
    };

    /// <summary>The live synthetic book (observational; used by tests and diagnostics).</summary>
    public SyntheticOrderBook Book => _book;

    public SyntheticRegime CurrentRegime => _regime.Current;

    public SyntheticDiagnostics Diagnostics => _recorder.Latest;

    /// <summary>Produces the next canonical event. Deterministic for a fixed seed.</summary>
    public MarketEvent Next()
    {
        int guard = 0;
        while (_pending.Count == 0)
        {
            Advance();
            if (++guard > 50_000)
            {
                throw new InvalidOperationException("Synthetic engine produced no events; aborting to avoid a spin.");
            }
        }

        return _pending.Dequeue();
    }

    public IEnumerable<MarketEvent> Generate(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return Next();
        }
    }

    // ── Simulation step ─────────────────────────────────────────────────────

    private void Advance()
    {
        _step++;
        _regime.Step(ref _rng);
        var p = _regime.Profile();

        int dt = Math.Max(1, (int)Math.Round(_cfg.BaseStepMs * p.StepScale *
            (1 + (_rng.NextDouble() * 2 - 1) * _cfg.StepJitter)));
        _clock = _clock.AddMilliseconds(dt);

        if (!_initialized)
        {
            InitLadder(p);
            _initialized = true;
            _recorder.MaybeSample(_step, _regime.Current, _book);
            return;
        }

        _anchor += _cfg.AnchorDriftTicks * SynthDistributions.NextGaussian(ref _rng);
        double mid = (_book.BestBidTicks + _book.BestAskTicks) / 2.0;
        double bias = Math.Clamp(p.BuyBias - _cfg.MeanReversion * (mid - _anchor), -1, 1);

        Churn(p);
        Replenish(p);
        EnsureDepth(p);
        // Session phase: time-of-day activity multiplier (overnight quiet → opening burst
        // → midday lull → close). Deterministic — it reads only the simulated clock.
        double phaseRate = _cfg.SessionPhaseRate is { Length: 24 } phases ? phases[_clock.Hour] : 1.0;
        _trades.Generate(_book, in p, bias, ref _rng, this, phaseRate);
        Recenter(p, bias);
        EnsureValid(p);

        _recorder.MaybeSample(_step, _regime.Current, _book);
    }

    private void InitLadder(RegimeProfile p)
    {
        for (int d = 0; d < _cfg.MaintainedLevels; d++)
        {
            PostLevel(Side.Bid, _initBid - d, d, p);
            PostLevel(Side.Ask, _initAsk + d, d, p);
        }
    }

    private void EnsureDepth(RegimeProfile p)
    {
        FillSide(Side.Bid, p);
        FillSide(Side.Ask, p);
    }

    private void FillSide(Side side, RegimeProfile p)
    {
        long best = side == Side.Bid ? _book.BestBidTicks : _book.BestAskTicks;
        if (best == SyntheticOrderBook.NoPrice)
        {
            return;
        }

        int made = 0;
        for (int d = 0; d < _cfg.MaintainedLevels && made < 4; d++)
        {
            long price = side == Side.Bid ? best - d : best + d;
            if (price <= 0)
            {
                continue;
            }

            if (_book.Find(side, price) is null && SynthDistributions.Chance(ref _rng, _cfg.RefillRate)
                && PostLevel(side, price, d, p))
            {
                made++;
            }
        }
    }

    private void Churn(RegimeProfile p)
    {
        ChurnSide(Side.Bid, p);
        ChurnSide(Side.Ask, p);
    }

    private void ChurnSide(Side side, RegimeProfile p)
    {
        long best = side == Side.Bid ? _book.BestBidTicks : _book.BestAskTicks;
        if (best == SyntheticOrderBook.NoPrice)
        {
            return;
        }

        _scan.Clear();
        foreach (var k in (side == Side.Bid ? _book.Bids : _book.Asks).Keys) _scan.Add(k);

        foreach (var price in _scan)
        {
            var lvl = _book.Find(side, price);
            if (lvl is null)
            {
                continue;
            }

            lvl.AgeSteps++;
            long d = side == Side.Bid ? best - price : price - best;

            // Liquidity far from the touch (outside the maintained window) gets pulled.
            if (d >= _cfg.MaintainedLevels)
            {
                if (SynthDistributions.Chance(ref _rng, 0.5 * p.CancelMult))
                {
                    lvl.CanceledTotal += lvl.Displayed;
                    _book.Remove(side, price);
                    OnDepthChanged(side, price, 0);
                }

                continue;
            }

            double churn = _cfg.LevelChurnRate * p.CancelMult * (1 - lvl.Persistence);
            if (SynthDistributions.Chance(ref _rng, churn))
            {
                if (!lvl.IsWall && SynthDistributions.Chance(ref _rng, 0.45))
                {
                    lvl.CanceledTotal += lvl.Displayed;
                    _book.Remove(side, price);
                    OnDepthChanged(side, price, 0);
                }
                else
                {
                    long nw = Math.Max(_cfg.MinLevelSize,
                        (long)(lvl.Displayed * SynthDistributions.Lerp(ref _rng, 0.4, 0.85)));
                    if (nw != lvl.Displayed)
                    {
                        lvl.CanceledTotal += lvl.Displayed - nw;
                        lvl.Displayed = nw;
                        lvl.LastChangeUtc = _clock;
                        OnDepthChanged(side, price, nw);
                    }
                }
            }
            else if (SynthDistributions.Chance(ref _rng, 0.04))
            {
                // Stacking: extra size is added to a resting level. The increment is a
                // fraction of the configured median (a fixed base), never of the level's
                // own growing size, so stacking cannot compound geometrically. Capped.
                long add = Math.Max(1, (long)(_cfg.LevelSizeMedian * SynthDistributions.Lerp(ref _rng, 0.1, 0.5)));
                long nw = Math.Min(_cfg.MaxLevelSize, lvl.Displayed + add);
                if (nw != lvl.Displayed)
                {
                    lvl.AddedTotal += nw - lvl.Displayed;
                    lvl.Displayed = nw;
                    lvl.LastChangeUtc = _clock;
                    OnDepthChanged(side, price, nw);
                }
            }
        }
    }

    private void Replenish(RegimeProfile p)
    {
        ReplenishSide(Side.Bid, p);
        ReplenishSide(Side.Ask, p);
    }

    private void ReplenishSide(Side side, RegimeProfile p)
    {
        _scan.Clear();
        foreach (var k in (side == Side.Bid ? _book.Bids : _book.Asks).Keys) _scan.Add(k);

        foreach (var price in _scan)
        {
            var lvl = _book.Find(side, price);
            if (lvl is null || lvl.Displayed >= lvl.TargetSize || lvl.ReplenishCount >= _cfg.MaxReplenish)
            {
                continue;
            }

            if (SynthDistributions.Chance(ref _rng, _cfg.ReplenishRate * p.ReplenishMult * lvl.Persistence))
            {
                long add = Math.Max(1,
                    (long)((lvl.TargetSize - lvl.Displayed) * SynthDistributions.Lerp(ref _rng, 0.4, 0.95)));
                long nw = Math.Min(lvl.TargetSize, lvl.Displayed + add);
                if (nw != lvl.Displayed)
                {
                    lvl.AddedTotal += nw - lvl.Displayed;
                    lvl.Displayed = nw;
                    lvl.ReplenishCount++;
                    lvl.LastChangeUtc = _clock;
                    OnDepthChanged(side, price, nw);
                }
            }
        }
    }

    private void Recenter(RegimeProfile p, double bias)
    {
        long bestBid = _book.BestBidTicks;
        long bestAsk = _book.BestAskTicks;
        if (bestBid == SyntheticOrderBook.NoPrice || bestAsk == SyntheticOrderBook.NoPrice)
        {
            return;
        }

        long spread = bestAsk - bestBid;
        int target = TargetSpread(p);
        if (spread <= target)
        {
            return;
        }

        double reTightP = _regime.Current switch
        {
            SyntheticRegime.LiquidityVacuum => 0.35,
            SyntheticRegime.Volatile => 0.6,
            SyntheticRegime.FastMarket => 0.7,
            _ => 0.88,
        };
        if (!SynthDistributions.Chance(ref _rng, reTightP))
        {
            return;
        }

        long flow = _trades.LastNetFlow;
        double dir = flow > 0 ? 1 : flow < 0 ? -1 : (bias >= 0 ? 1 : -1);
        int maxMove = _regime.Current == SyntheticRegime.FastMarket ? 3 : 1;
        int move = Math.Max(1, Math.Min(maxMove, (int)(spread - target)));

        if (dir > 0)
        {
            long np = Math.Min(bestAsk - 1, bestBid + move);
            if (np > bestBid) PostLevel(Side.Bid, np, 0, p);
        }
        else
        {
            long np = Math.Max(bestBid + 1, bestAsk - move);
            if (np < bestAsk) PostLevel(Side.Ask, np, 0, p);
        }
    }

    private int TargetSpread(RegimeProfile p) => _regime.Current switch
    {
        SyntheticRegime.LiquidityVacuum => SynthDistributions.Chance(ref _rng, 0.5) ? 3 : 2,
        SyntheticRegime.Volatile => SynthDistributions.Chance(ref _rng, 0.4) ? 2 : 1,
        SyntheticRegime.FastMarket => SynthDistributions.Chance(ref _rng, 0.5) ? 2 : 1,
        _ => 1,
    };

    private void EnsureValid(RegimeProfile p)
    {
        if (_book.BestBidTicks == SyntheticOrderBook.NoPrice)
        {
            long anchor = _book.BestAskTicks != SyntheticOrderBook.NoPrice
                ? _book.BestAskTicks - 1
                : (long)Math.Round(_anchor);
            PostLevel(Side.Bid, anchor, 0, p);
        }

        if (_book.BestAskTicks == SyntheticOrderBook.NoPrice)
        {
            long anchor = _book.BestBidTicks != SyntheticOrderBook.NoPrice
                ? _book.BestBidTicks + 1
                : (long)Math.Round(_anchor) + 1;
            PostLevel(Side.Ask, anchor, 0, p);
        }
    }

    // ── Level creation ──────────────────────────────────────────────────────

    private bool PostLevel(Side side, long price, int distanceIndex, RegimeProfile p)
    {
        double w = DistanceWeightAt(distanceIndex);
        double baseTarget = _cfg.LevelSizeMedian * w * p.DepthMult;
        double noise = SynthDistributions.NoiseFactor(ref _rng);
        long size = SynthDistributions.Size(ref _rng, baseTarget, _cfg.LevelSizeSigma, noise, _cfg.MinLevelSize);

        bool wall = false;
        if (SynthDistributions.Chance(ref _rng, _cfg.WallProbability))
        {
            double mult = SynthDistributions.Lerp(ref _rng, _cfg.WallMultMin, _cfg.WallMultMax);
            size = Math.Max(size, (long)Math.Round(size * mult));
            wall = true;
        }

        size = Math.Clamp(size, _cfg.MinLevelSize, _cfg.MaxLevelSize);

        var lvl = new SyntheticLevel
        {
            PriceTicks = price,
            Displayed = size,
            TargetSize = size,
            AddedTotal = size,
            Noise = noise,
            IsWall = wall,
            Persistence = Math.Clamp(
                _cfg.BasePersistence + (wall ? 0.25 : 0) + 0.08 * SynthDistributions.NextGaussian(ref _rng),
                0.05, 0.97),
            CreatedUtc = _clock,
            LastChangeUtc = _clock,
        };

        if (!_book.Put(side, lvl))
        {
            return false;
        }

        OnDepthChanged(side, price, size);
        return true;
    }

    private double DistanceWeightAt(int idx)
    {
        var w = _cfg.DistanceWeight;
        if (idx < w.Length)
        {
            return w[idx];
        }

        return w[^1] * Math.Pow(_cfg.FarDecay, idx - w.Length + 1);
    }

    // ── Event emission (IBookEventSink) ─────────────────────────────────────

    private long NextSeq()
    {
        _sequence++;
        // Corruption stress implies a gap even when the caller didn't set one explicitly.
        int gapAfter = _options.InjectGapAfter > 0 ? _options.InjectGapAfter
            : _options.Stress == SyntheticStressMode.Corruption ? 5_000 : 0;
        if (gapAfter > 0 && !_gapInjected && _emitted == gapAfter)
        {
            _sequence += 5; // deliberate sequence gap (test scenarios only)
            _gapInjected = true;
        }

        _emitted++;
        return _sequence;
    }

    void IBookEventSink.OnDepthChanged(Side side, long price, long displayed) => OnDepthChanged(side, price, displayed);

    private void OnDepthChanged(Side side, long price, long displayed)
    {
        long seq = NextSeq();
        var type = side == Side.Bid ? MarketEventType.BidUpdate : MarketEventType.AskUpdate;
        _pending.Enqueue(MarketEvent.Quote(
            _instrumentId, _root, _symbol, _exchange, type, _clock, _clock,
            side, price, Math.Max(0, displayed), seq, DepthFlags));
        _recorder.NoteEvent();
    }

    void IBookEventSink.OnExecution(AggressorSide aggressor, long price, long qty)
    {
        long seq = NextSeq();
        _pending.Enqueue(MarketEvent.Trade(
            _instrumentId, _root, _symbol, _exchange, _clock, _clock, price, qty,
            aggressor, exchangeSequence: seq, tradeId: seq, flags: TradeFlags));
        _recorder.NoteExecution(qty);
        _recorder.NoteEvent();
    }
}
