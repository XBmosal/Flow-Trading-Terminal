# Mock market status

Legend: ✅ done · 🟡 partial · ⬜ not planned/next.

| Feature | Implemented | Tested | Calibrated | Deterministic | Perf verified | Notes |
|---|---|---|---|---|---|---|
| Instrument-specific NQ/ES profiles | ✅ | ✅ | ✅ | ✅ | — | one config record, no scattered constants |
| Three-tier mixture trade sizing (no uniform) | ✅ | ✅ | ✅ | ✅ | — | body/large/extreme, hard cap |
| 150+ rare · 250+ exceptional (normal NQ) | ✅ | ✅ | ✅ | ✅ | — | CI-asserted envelope |
| Trade↔book interaction (per-level prints) | ✅ | ✅ | — | ✅ | — | no teleporting, no negative depth |
| Sweeps consume real levels in order | ✅ | ✅ | — | ✅ | — | groups recover parent size |
| Regime engine (8 persistent regimes) | ✅ | ✅ | 🟡 | ✅ | — | hand-weighted transitions |
| Session phases (per-UTC-hour) | ✅ | — | 🟡 | ✅ | — | hour-granularity multipliers |
| Stochastic timing + bursts | ✅ | ✅ | — | ✅ | — | no fixed spacing |
| Side momentum clustering (no alternation) | ✅ | ✅ | ✅ | ✅ | — | p(same-side) ≈ 0.53–0.92 band |
| Spread realism (≈1 tick, never crossed) | ✅ | ✅ | ✅ | ✅ | — | avg 1.05–1.1 measured |
| Stress separation (EventRate/Depth/LargeTrade/Corruption) | ✅ | ✅ | ✅ | ✅ | — | one aspect each; None is default |
| EventRate stress preserves size distribution | ✅ | ✅ | ✅ | ✅ | — | asserted |
| LargeTrade stress explicit + labeled | ✅ | ✅ | ✅ | ✅ | — | never silently active |
| Duplicate-event audit | ✅ | ✅ | — | — | — | 0 duplicate trade ids; detector fed once |
| Big Trade bubble rate (occasional, meaningful) | ✅ | ✅ | ✅ | ✅ | — | ≈6/min NQ; 150+ groups <0.2/min |
| Volume reconciliation (profile/CVD/footprint) | ✅ | ✅ | — | ✅ | — | exact equality asserted |
| Calibration report tool | ✅ | ✅ | — | ✅ | — | `SyntheticCalibrationReport` |
| Determinism (same seed ⇒ identical session) | ✅ | ✅ | — | ✅ | — | |
| Long-session soak / throughput | ✅ | ✅ | — | — | ✅ | pre-existing perf suite passes retuned |
| Rendering-stress dimension | ⬜ | — | — | — | — | UI-side history/density settings, not engine |
| Fitted (data-driven) calibration | ⬜ | — | — | — | — | targets are qualitative, not exchange-fitted |

## Root cause of the old flood (measured, not guessed)
1. **Trade rate ~4,110 prints/min NQ** (~68/sec) — several times a believable tape. At
   that rate the Big Trades percentile threshold qualified a large absolute number of
   groups every minute, and the 250 ms aggregation window chained almost continuously,
   inflating *group totals* into 150+ territory.
2. **ES tail genuinely hot**: legacy uniform 6–34× multiplier ⇒ 5.8 prints ≥150/min.
3. **No duplicates**: 0 duplicate trade ids; each trade reaches the detector exactly once
   (bubble flood was rate + grouping, not re-emission).

## Guarantees
- UI untouched: only data behavior, presets, diagnostics and tests changed.
- Every event still canonical + `Synthetic`-flagged; nothing fabricated per panel.
