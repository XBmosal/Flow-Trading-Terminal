# Backend architecture

## System overview

Flow Terminal's backend is a source-agnostic, single-writer market-data system. One
canonical event model drives everything; the UI only ever samples immutable/copied
snapshots. The backend builds and tests headlessly (WPF is confined to
`FlowTerminal.App`).

```
Provider (Mock | Parquet replay | Synthetic historical | Rithmic* | Databento*)
  └─ IMarketDataProvider → IAsyncEnumerable<MarketEvent>
       └─ InstrumentPipeline (bounded channel, single writer, zero drops, diagnostics)
            └─ LiveFeedService.Ingest (one lock, one classification truth)
                 ├─ MarketByPriceOrderBook (shared: DOM, heatmap, liquidity, classify)
                 ├─ AggressorClassifier → AggressorEnrichment (one classified event for ALL consumers)
                 ├─ Bar aggregators (time/tick/volume/range)
                 ├─ VolumeProfile · Footprint · CVD · TPO · VWAP/AVWAP
                 ├─ BigTradeDetector (thresholds, groups, sweeps)
                 ├─ Order-flow engines (divergence, delta blocks, flow stats)
                 ├─ Detectors (absorption, exhaustion, traps, iceberg, stop-run…)
                 └─ ChartSnapshot (copied lists) → UI timer samples at ~30 FPS
```

`* Rithmic`: honest stub behind `RITHMIC_SDK` (no fabricated SDK calls).
`* Databento`: mapper complete/tested; wire transport behind `DATABENTO_LIVE`.

## Canonical events

`MarketEvent` (readonly struct): instrument, contract, exchange, type
(Trade/BidUpdate/AskUpdate/snapshot/lifecycle/control), UTC exchange+receive
timestamps, exchange sequence, integer-tick price, quantity, aggressor
(Buy/Sell/**Unknown** — never forced), trade id, flags
(`AggressorSupplied`/`AggressorInferred`/`Synthetic`/`Snapshot`/sequence+timestamp
presence), source provider. Prices convert through the centralized `PriceConverter`
(decimal, banker's rounding); NQ/ES metadata (tick 0.25, point values, exchange) live in
`InstrumentRegistry`/`InstrumentSpec` only.

## Capability model

`ProviderCapabilities`/`DataCapabilities` flags (trades, ToB, MBP, MBO, aggressor flags,
historical, timestamps, sequences) declared per provider and surfaced to diagnostics.
Derived values are labelled: **Native** (feed-supplied), **Estimated**
(`AggressorInferred`, pull/stack under MBP), **Unavailable** (MBO analytics without MBO
data). Synthetic events always carry the `Synthetic` flag and the UI shows the
SIMULATED banner.

## One classification truth

`AggressorClassifier` (native → bid/ask touch → tick rule → inherit → Unknown, with a
quality label incl. invalid-book pause) runs **once per trade** in the ingest path; the
result is applied back onto the event via `AggressorEnrichment` (side +
`AggressorInferred`). CVD, footprint, profiles, tape, bars, detectors, Big Trades and
heatmap dots all consume the same enriched event — tested by feeding an inference-only
stream and asserting every engine agrees with the classifier's own counters.

## Order book

`MarketByPriceOrderBook` is the single book truth (DOM, heatmap, liquidity analytics,
classification context). It enforces bid<ask, non-negative depth, exposes validity +
invalid-reason, top-of-book, sizes by level; `PullStackTracker` derives pulling/
stacking/replenishment (Estimated under MBP); `ReadOnlyDom` derives the ladder
(cumulative touch-outward depth, walls, distance) — with a deterministic replay hash.

## Bars, analytics, reconciliation

Four deterministic bar kinds (time/tick/volume/range) built from one `BarBuilder`
(OHLC, buy/sell/unknown volume, delta, trade count). Footprint/profile/CVD totals are
asserted equal to canonical buy/sell totals; Big Trade groups and sweep children never
double-count (tested). All engines are incremental, bounded, and deterministic.

## Replay, recording, storage

Parquet part-file recording (batched, atomic temp→rename publish, crash-safe), DuckDB
projections, repair tooling, and a session **manifest** (`manifest.json`: schema+app
version, instrument, tick size, date, source, event count, time range, order-sensitive
FNV hash) with `Validate()` so incomplete or tampered recordings are detected instead of
silently replayed. Replay uses the same pipeline and analytics as live; speed only paces
wall-clock delivery (calculation-independence tested); determinism is asserted by
replay-hash tests across the DOM, Big Trades, mock engine, and record/replay round trips.

## Threading model

Single-writer ingestion (pipeline thread) into one lock-guarded state; the UI samples
`ChartSnapshot`s on a dispatcher timer — mutable engine lists are **copied** at the
snapshot boundary. No UI-thread market-data processing; bounded channels; cancellation
tokens; render faults isolated per panel via `RenderSafety` so a bad frame never takes
the app down.

## Diagnostics

`PipelineDiagnostics` (received/processed/dropped/queue depth), `SyntheticDiagnostics`
(regime, spread, distribution realism), `BigTradeDiagnostics` (sides, quality counts,
thresholds), book validity + reason, detector totals — all surfaced on the snapshot and
the app's diagnostics readout. Structured Serilog logging; crash log + honest error
dialog on unhandled faults.

## Known limitations

- Session/contract handling covers trading-date calculation, per-instrument presets and
  session resets; a full RTH/ETH session-template engine is not yet centralized.
- Settings versioning is per-file and tolerant (unknown fields ignored, corrupt files
  fall back to defaults) rather than a single schema-versioned store.
- Rithmic/Databento live transports intentionally absent (see integration plan).
- MBO-dependent analytics remain gated Unavailable.
