# Rithmic integration plan

Status: **boundary complete, transport intentionally absent.** The app builds with no
proprietary dependency; nothing fabricates SDK behavior. This document is the map for
the day the licensed R|API+ SDK is available.

## Adapter boundary (already in place)

- `RithmicMarketDataProvider : IMarketDataProvider` — same contract as mock/replay, so
  downstream code needs zero changes. All operations throw
  `RithmicSdkUnavailableException` until the SDK region is wired.
- `RITHMIC_SDK` build symbol isolates every SDK-touching line; the normal build never
  references proprietary code (`RithmicAvailability.IsCompiledIn` reports it honestly).
- `RithmicCredentials` (username/password/system/gateway/environment) — password is
  session-only, redacted from all string output, never persisted;
  `RithmicCredentialStore` persists only non-secret fields.
- `RithmicSession` — validation → connect → honest outcome
  (Connected / InvalidCredentials / Failed / SdkUnavailable), already driven by the
  footer login UI.
- Connection-state model: `ConnectionState` + `ConnectionStateChanged` event.

## Required SDK integration points (inside the `RITHMIC_SDK` region)

1. **Engine/params init** — environment, system, gateway selection per SDK docs.
2. **Login/auth** — from `RithmicCredentials`; surface SDK error text (never the password).
3. **Connection lifecycle** — state transitions raised through `ConnectionStateChanged`;
   heartbeat/reconnect policy; disconnect on dispose.
4. **Reference data** — contract discovery for NQ/ES (`RithmicReferenceDataProvider`).
5. **Market-data subscription** — trades + MBP depth per `SubscriptionOptions`.
6. **Trade stream → canonical** — map price to integer ticks via `PriceConverter`,
   aggressor to Buy/Sell/Unknown with `AggressorSupplied` only when the feed states it,
   exchange sequence/timestamps with presence flags. (Pattern proven by the tested
   `DatabentoMapper` — mirror it.)
7. **Depth stream → canonical** — Bid/AskUpdate absolute-size events; book resets on
   snapshot boundaries; sequence-gap events for the pipeline's validator.
8. **Capability detection** — advertise only confirmed capabilities (never claim MBO
   or historical depth unconfirmed).
9. **Recording** — the canonical stream flows into the existing Parquet recorder +
   manifest untouched.

## Sequencing & error handling

Exchange sequences feed the existing `SequenceValidator` (gap detection). Provider
errors map to the app's honest states: recover (transient), warn (estimated data),
pause source + invalidate book (sequence break), fail fast (auth/config). No silent
fallback to mock while "connected".

## Compliance

The adapter is market-data only. No order, account, position or P&L API is wired, ever —
Flow Terminal remains a read-only terminal.

## Intentionally not implemented

- Any code that pretends to call R|API+ without the SDK present.
- Simulated "connected" states.
- Conformance-dependent behavior (Rithmic requires vendor conformance before an app may
  connect; that process is external to this repo).

## Prerequisites checklist (external)

- [ ] Licensed R|API+ .NET SDK obtained (see `lib/rithmic/README.md`).
- [ ] Rithmic conformance/certification for this app.
- [ ] User credentials entitled for API/market-data access (prop-firm logins often gate
      this separately from R|Trader access).
