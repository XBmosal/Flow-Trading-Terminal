# Backend status

Legend: ✅ done · 🟡 partial · ⬜ pending. "Deterministic" = same inputs ⇒ identical outputs (tested where ✅).

| Subsystem | Implemented | Tested | Benchmarked | Replay-compatible | Deterministic | Production-ready | Known issues |
|---|---|---|---|---|---|---|---|
| Canonical event model + tick conversion | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Instrument/contract metadata (NQ/ES) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Capability model + honest labels | ✅ | ✅ | — | ✅ | — | ✅ | |
| Provider abstraction (mock/replay/historical) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Rithmic boundary (no fake SDK) | ✅ | ✅ | — | — | — | 🟡 | needs licensed SDK + conformance |
| Databento boundary (mapper tested) | ✅ | ✅ | — | — | ✅ | 🟡 | wire transport behind DATABENTO_LIVE |
| Pipeline (bounded, zero-drop, diagnostics) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Sequence validation / gap detection | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Order book (shared, valid-state enforced) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Unified aggressor classification (all consumers)** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | added this pass |
| Bar builders (time/tick/volume/range) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Footprint (reconciles to canonical) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| CVD | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Profiles / TPO / VWAP / AVWAP | ✅ | ✅ | 🟡 | ✅ | ✅ | ✅ | |
| Big Trades + sweeps (no double count) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Heatmap state (canonical depth) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| DOM backend (+ replay hash) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Order-flow engines (divergence/blocks/stats) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Detector suite | ✅ | ✅ | — | ✅ | ✅ | ✅ | heuristic candidates, labelled |
| Mock engine (calibrated, stress-separated) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| Recording (Parquet parts, atomic, repair) | ✅ | ✅ | — | ✅ | ✅ | ✅ | |
| **Recording manifest (version/hash/validate)** | ✅ | ✅ | — | ✅ | ✅ | ✅ | added this pass |
| Replay (deterministic, speed-independent) | ✅ | ✅ | — | ✅ | ✅ | ✅ | seek = restart+fast-forward |
| Settings / templates / workspaces | ✅ | ✅ | — | — | — | 🟡 | tolerant per-file versioning |
| Session/contract management | ✅ | ✅ | — | ✅ | ✅ | ✅ | Globex rollover resets session analytics; RTH/ETH on snapshot |
| Diagnostics + structured logging | ✅ | ✅ | — | ✅ | — | ✅ | |
| Threading (single-writer, snapshot copies) | ✅ | ✅ | — | ✅ | — | ✅ | |
| Benchmarks (BenchmarkDotNet) | ✅ | — | ✅ | — | — | ✅ | order book, pipeline, analytics suite |
| Error handling (no silent pipeline failures) | ✅ | ✅ | — | ✅ | — | ✅ | render faults isolated per panel |

## This pass (backend hardening)
1. **Unified classification** — the shared classifier's result is now enriched onto the
   event once, so CVD/footprint/profiles/tape/bars/detectors/Big Trades all read one
   truth (previously only Big Trades classified; other engines read the raw field —
   a split-brain on any feed without native aggressor flags). Proven by an
   inference-only-stream reconciliation test.
2. **Recording manifest** — schema/app version, instrument, tick size, counts, time
   range, order-sensitive hash; `Validate()` detects missing/tampered events; corrupt
   manifests load as null, never crash.
3. **Benchmarks extended** — analytics hot paths added (bars/footprint/CVD/profile/
   Big Trades/DOM) to the existing order-book/pipeline benchmarks. Indicative dry-run
   over 10k events: CVD 1.7 ms · profile 2.9 ms · footprint 3.4 ms · bars 3.7 ms ·
   DOM 9.8 ms · Big Trades 19.5 ms (≈2 µs/event) — ample headroom for real-time.

## Guarantees
- No execution surfaces anywhere; read-only preserved.
- Backend builds and tests without launching WPF.
- No fabricated SDK behavior (Rithmic/Databento transports honestly gated).
