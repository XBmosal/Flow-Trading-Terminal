# Order-flow suite status

Legend: ✅ done · 🟡 partial / estimated-capable · ⬜ staged.

| Feature | Implemented | Live | Replay | Mock | Capability | Tested | UI | Limitation |
|---|---|---|---|---|---|---|---|---|
| Canonical delta + unknown volume | ✅ | ✅ | ✅ | ✅ | Native/Estimated labelled | ✅ | ✅ | |
| Shared bar/window statistics | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | |
| Delta velocity / efficiency (real time) | ✅ | ✅ | ✅ | ✅ | — | ✅ | 🟡 | no dedicated pane yet |
| Delta Divergence (regular + hidden) | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | one comparison pivot back |
| Developing vs confirmed divergence | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | dashed vs solid |
| No-lookahead guarantee | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | asserted in tests |
| Divergence strength score (documented) | ✅ | ✅ | ✅ | ✅ | — | ✅ | 🟡 | inspector UI staged |
| Delta Blocks: time/delta/volume/count | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | cluster/absorption/sweep modes staged |
| Block volume reconciles with canonical | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | |
| Anchored VWAP + weighted σ bands | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | session anchor wired; manual click staged |
| Multiple AVWAP instances (engine) | ✅ | 🟡 | ✅ | ✅ | — | ✅ | ⬜ | shell wires session instance |
| Absorption Level 1 (trade-only) | ✅ | ✅ | ✅ | ✅ | labelled candidate | ✅ | ✅ | pre-existing detector |
| Absorption Levels 2–3 | 🟡 | ✅ | ✅ | ✅ | Estimated (MBP) | 🟡 | 🟡 | via footprint/replenishment context |
| Absorption Level 4 (MBO) | ⬜ | — | — | — | Unavailable | — | — | needs native MBO |
| Exhaustion candidates | ✅ | ✅ | ✅ | ✅ | candidate | ✅ | ✅ | detections panel |
| Trapped-trader candidates | ✅ | ✅ | ✅ | ✅ | candidate | ✅ | ✅ | detections panel |
| Imbalances (diagonal/stacked) | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | pre-existing footprint suite |
| Profiles (volume/delta by price) | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | pre-existing |
| Sweep / aggressive clusters | ✅ | ✅ | ✅ | ✅ | — | ✅ | ✅ | Big Trades engine |
| Pulling/stacking/replenishment | ✅ | ✅ | ✅ | ✅ | Estimated (MBP) | ✅ | ✅ | pre-existing DOM/detectors |
| Confluence engine | ⬜ | — | — | — | — | — | — | staged |
| Alerts | ⬜ | — | — | — | — | — | — | staged |
| BenchmarkDotNet benchmarks | ⬜ | — | — | — | — | — | — | perf tests exist (throughput/soak) |

## Guarantees
- Read-only: no execution surfaces added anywhere in the suite.
- Green/light-purple identity preserved; amber = developing/estimated; no red except errors.
- All engines incremental, bounded, deterministic; snapshots copied at the UI boundary.
