# Mock market engine

The synthetic feed generates **canonical MarketEvents** (BidUpdate/AskUpdate/Trade) from a
persistent, incrementally-mutated limit-order book. Nothing is drawn that didn't happen in
the data: price moves only because aggressive executions deplete the touch, sweeps consume
real levels in order, and every panel (footprint, CVD, DOM, heatmap, tape, Big Trades)
reconciles against the same stream. Every event is flagged `Synthetic`.

## Instrument profiles

All tuning lives in `SyntheticMarketConfiguration` (one place, no scattered constants),
with separate calibrated NQ and ES profiles (`ForRoot`). Prices are integer ticks.

## Trade-size model (three-tier mixture)

Clip sizes are drawn from a mixture — never uniform, never one multiplier on everything:

| Tier | NQ | ES | Role |
|---|---|---|---|
| Body (log-normal) | median 2.6, σ 0.88 | median 4.2, σ 0.82 | dominant majority, 1–5 lots |
| Large (log-normal) | p≈0.4%, median 42, σ 0.55 | p≈0.4%, median 80, σ 0.5 | uncommon sweep candidates |
| Extreme (log-normal) | p≈0.035%, median 170, σ 0.45 | p≈0.03%, median 320, σ 0.4 | exceptional 150+/250+ tail |

Regime `SweepMult` scales only the two **tail probabilities** (modestly); a hard
`MaxClipSize` keeps the tail finite. A large clip executes through the book and prints per
consumed level, so a parent-like action appears as several variable child prints that the
Big Trades engine groups into **one** bubble — fragmentation is emergent, not scripted.

Measured (30 sim-min, seed 7, normal mode): NQ ≈ 1.2k trades/min, median 3, p99 ≈ 21,
81% of prints 1–5 lots, 150+ prints ≈ 0/min (150+ **groups** occasional), 250+
exceptional. ES prints chunkier (median 4–5) with its own tail. Big Trade bubbles with the
NQ preset: ≈ 6/min, 150+ groups < 0.2/min.

## Event timing & side selection

Steps advance a jittered simulated clock; trade arrivals are a regime-scaled thinned
process with occasional multi-clip bursts (bursts change *when* trades arrive, never their
size). Aggressor side comes from regime bias + mean-reversion toward a slow anchor + a
short-lived momentum EMA of recent clips — flow clusters by side without locking or
mechanical alternation.

## Regimes & session phases

`SyntheticRegimeEngine` moves the session through Quiet / Balanced / TrendingUp /
TrendingDown / Volatile / LiquidityVacuum / Absorption / FastMarket with variable
durations and weighted transitions. Each regime scales rate, depth, cancel/replenish,
spread-widen, sweep propensity and step pace — FastMarket raises *rate*, not size. On top,
a per-UTC-hour `SessionPhaseRate` table shapes a CME-like day (overnight quiet, opening
burst, midday lull, close pickup). Both are deterministic.

## Depth, spread, price

Resting levels are heavy-tailed log-normal with a distance-shaped profile, persistent
per-level identity, occasional walls (~4.5%), churn/pull/replenish lifecycles and bounded
stacking. Spread is normally one tick (measured avg ≈ 1.05–1.1), widening transiently in
vacuum/volatile regimes; the book is never crossed. Price is emergent: only touch
depletion and re-quoting move it.

## Stress modes (`SyntheticOptions.Stress`)

Each mode exaggerates exactly ONE aspect; none combine silently; the default is `None`:

- **EventRate** — ~3× steps/sec; size distribution unchanged (verified by test).
- **Depth** — ~3× churn / 2× refill+replenish; book stays valid; sizes unchanged.
- **LargeTrade** — explicit tail inflation for Big Trade testing only.
- **Corruption** — injects a sequence gap to exercise recovery (tests only).

Rendering stress (history length, visible density) is a UI-side setting, not an engine
distortion.

## Determinism & calibration

The stream is a pure function of the seed: identical seed + options ⇒ identical events,
trades, sweeps, prices, groups. `SyntheticCalibrationReport.Run(...)` produces the
developer calibration report (percentiles, size buckets, threshold exceedances, rates,
spread, duplicates); `SyntheticCalibrationTests` pins the realism envelope in CI so a
regression back to a flooded tape fails tests, not the eye check.

## Known limitations

- Session phases are hour-granularity multipliers, not a full auction model.
- Trade rate (~1.2k prints/min NQ) sits at the busy end of realistic; it keeps demo
  charts alive.
- No hidden/iceberg liquidity model in the mock book (icebergs are detected, not
  simulated); no implied spread legs.
- The regime engine's transition table is hand-weighted, not fitted to exchange data.
