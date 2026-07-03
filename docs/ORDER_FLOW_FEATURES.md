# Order-flow analysis suite

All features derive from the one canonical `MarketEvent` trade stream. Canonical delta =
**aggressive-buy volume − aggressive-sell volume**; unknown-side volume is tracked
separately and never forced into a side. Delta is never derived from candle color or
close-vs-open. Aggressor classification (shared `AggressorClassifier`): native provider
side → bid/ask comparison → tick rule → inherit → **Unknown**, with per-trade quality
(Native / estimated variants / Unknown / InvalidBook). Estimated values are labelled.
Prices are integer ticks internally.

## Shared statistics (`Analytics/OrderFlow/OrderFlowStats.cs`)

- `BarFlowStats` — O(1)-per-trade bar accumulation: buy/sell/unknown volume, delta,
  delta %, trade counts, max trade, price progress, **delta efficiency**
  (= |price progress ticks| / |delta|, NaN when delta 0) and **volume efficiency**
  (= |progress| / total volume). Low efficiency with high |delta| is an
  *effort-versus-result anomaly* (absorption candidate) — never auto-labelled confirmed
  absorption.
- `RollingFlowWindow` — bounded time-window stats with **real-elapsed-time** velocities
  (delta/sec, volume/sec, trades/sec); replay speed cannot change them.

## Delta Divergence Pro (`DDIV`)

Pivot engine: a swing high/low must strictly dominate `PivotLeft` bars back and
`PivotRight` bars forward; it is confirmed **only when the right side has completed**, so
a signal comparing pivots first exists at `secondPivotBar + PivotRight` — the
**no-lookahead guarantee** (tested). Types: regular bullish (price LL, delta HL), regular
bearish (HH, LH), hidden bullish (HL, LL), hidden bearish (LH, HH). The delta compared is
the pivot bar's canonical delta. A **developing** signal (current unconfirmed extreme vs
the last confirmed pivot) renders dashed and may vanish; it never rewrites history.

**Strength score (documented, transparent):**
`score = 0.45·min(1,|Δdelta|/max(|d1|,|d2|,1)) + 0.35·min(1,|Δprice|/20 ticks) + 0.20·min(1,separation/maxSeparation)`;
Weak < 0.4 ≤ Moderate < 0.7 ≤ Strong. Each signal record carries both pivots' time,
price, delta, the score, and state — the full evidence for inspection.

Filters: min/max pivot separation, min price difference, min delta difference, hidden
on/off. Bounded memory (bars + signals). Deterministic.

## Delta Blocks (`DBLK`)

Contiguous slices of canonical trades sealed by a mode rule — **every trade lands in
exactly one block**, so block totals reconcile with canonical volume (tested):

- **Time** — fixed buckets (blocks never span a boundary).
- **AbsoluteDelta** (default, threshold 150) — seals when |delta| reaches the threshold.
- **Volume** / **TradeCount** — seals at the volume / count threshold.

Each block: time range, price range, buy/sell/unknown volume, delta, delta %, trade
count, max trade, price progress, delta efficiency. Rendered as translucent
green (+delta) / light-purple (−delta) regions behind the candles, intensity normalized
to the visible max |delta| (one extreme block cannot flatten the rest to invisibility
thanks to an alpha floor); net-delta labels appear only when the block is large enough
(LOD; rendering only, never calculations).

## Anchored VWAP (`AVWAP`)

`AnchoredVWAP = Σ(price·volume) / Σ(volume)` from the anchor forward, trade-price
weighted from canonical trades. Bands are **volume-weighted population standard
deviation**: `variance = Σ(v·p²)/Σv − vwap²` (see `VwapCalculator`), drawn at ±1σ/±2σ
(engine default multipliers 1/2/3). Trades before the anchor are ignored; a no-volume
anchor reports NaN (no fake value). `AnchoredVwapSet` manages up to 12 simultaneous
instances with per-instance anchors (manual timestamp, session open, swing, large trade,
sweep, divergence, custom), band settings, and per-bar sampled series; the shell
currently wires the **session-open** instance (anchored at the first trade of the
session, reset on contract/session change). Multi-anchor chart interaction (click-to-
anchor, drag, rename) is staged — the engine and persistence surface are ready.

## Exhaustion Candidates (`EXH`)

A local extreme (highest high / lowest low of the lookback) reached while aggression
*into* the extreme declines: |delta| and volume non-increasing across the approach bars
and the final |delta| below `DeclineRatio` × the run's first |delta|. Reported through
the standard detections panel as a **candidate** (bearish at highs, bullish at lows).

## Trapped-Trader Candidates (`TRAP`)

A strongly one-sided bar (|delta| ≥ threshold) printing at a short-term extreme *arms* a
candidate; it fires only if, within `ConfirmWithinBars`, price **closes back through**
that bar's far side (buyers: close < the bar's low; sellers: close > its high). Fires on
the completing return bar — no lookahead (tested). Labelled candidates about positioning
pressure, never proof of any trader's position. Expires silently if unconfirmed.

## Absorption evidence levels

Absorption remains layered by honesty of evidence: **Level 1 (trade-only)** — the
existing `AbsorptionDetector` (large aggressive volume, tight price band, short window).
**Level 2 (footprint-supported)** — stacked-imbalance context from the footprint
aggregator. **Level 3 (depth-supported)** — replenishment via `ReplenishmentDetector` /
`PullStackTracker` (estimated under MBP). **Level 4 (MBO)** — unavailable until native
MBO data exists; never claimed. Trade-only candidates are never presented as
exchange-confirmed iceberg activity.

## Existing components this suite builds on (not duplicated)

CVD, per-price volume/delta profiles (buy/sell/unknown), footprint diagonal/stacked
imbalances, Big Trades (thresholds/aggregation/sweeps — §16's sweep grouping), pulling/
stacking/replenishment (DOM), iceberg/stop-run/regime detectors, deterministic replay,
and the calibrated mock engine.

## Known limitations / staged

Confluence engine, alerts, multi-anchor AVWAP interaction, per-indicator settings panels,
delta cluster/absorption/sweep *block* variants, dedicated divergence inspector UI, and
BenchmarkDotNet microbenchmarks (throughput/soak perf tests exist) are staged. All
staged items have their data models in place.
