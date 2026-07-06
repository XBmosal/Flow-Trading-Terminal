# Databento adapter

An alternative, self-serve market-data source for Flow Terminal. Databento is a pure
market-data vendor (no broker account, no conformance/certification, no proprietary SDK to
license), so — unlike the Rithmic path — an ordinary API key is all a user needs, and the
integration is plain code in this repo rather than a licensed binary.

Flow Terminal is provider-agnostic: everything downstream consumes the canonical
`MarketEvent` stream through `IMarketDataProvider`. So a new feed is essentially **one
mapping file**; every panel, indicator, and the replay system work unchanged.

## What's implemented (and tested)

- **`DatabentoMapper`** — the whole instrument-specific surface, pure and deterministic
  (no network/SDK), exhaustively unit-tested:
  - **Prices**: DBN Int64 fixed-point at 1e-9 → integer ticks via the shared
    `PriceConverter` (through `decimal`, never binary float).
  - **Timestamps**: UInt64 nanoseconds since the UNIX epoch → UTC `DateTime`.
  - **`trades` schema** → canonical `Trade` events. Databento's trade `side` is the
    **aggressor**: `'B'` → aggressive buy, `'A'` → aggressive sell, `'N'` → Unknown. A
    definite side is flagged `AggressorSupplied` (native, not estimated); `'N'` stays
    Unknown and is never forced into a side.
  - **`mbp-10` schema** → `BidUpdate`/`AskUpdate` deltas: only levels that changed since
    the previous top-10 snapshot emit an update, a level that drops out of the top 10
    emits a size-0 update, a `'R'` clear zeroes the known book, and an embedded `'T'`/`'F'`
    trade is emitted before the level deltas. Verified end-to-end by reconstructing a
    valid `MarketByPriceOrderBook` from the emitted events.
- **`DatabentoCredentials` / `DatabentoSession`** — API key + dataset (e.g. `GLBX.MDP3`)
  + symbol. The API key is session-only and **never logged or persisted** (`ToString`
  redacts it, tested). The session validates and reports honest connect states.
- **`DatabentoMarketDataProvider`** — implements `IMarketDataProvider` with honest
  capabilities: Trades, TopOfBook, MarketByPriceDepth, ExchangeTimestamps,
  SequenceNumbers, **native AggressorSideFlags**, HistoricalTrades/Depth/Bars. It does
  **not** claim MBO (that needs the `mbo` schema + a queue model).

## What remains (the only pending piece)

The **live DBN wire transport** — connecting to the Databento gateway, authenticating with
the API key, subscribing, and decoding DBN records. It is isolated behind the
`DATABENTO_LIVE` build symbol (like Rithmic's `RITHMIC_SDK` region) so the normal build
needs no networking client. Until it is compiled in, `DatabentoSession.ConnectAsync`
reports `TransportUnavailable` and the app stays on mock/replay — it never fakes a
connection. Databento's live API delivers DBN over TCP (or JSON over their gateway); a
future pass wires a decoder that feeds `DatabentoMapper`. Everything the decoder produces
already has a tested home.

## Aggressor-side caveat

The `'B'`=buy / `'A'`=sell aggressor mapping follows Databento's documented convention. It
should be confirmed against a real fixture from the target dataset before production use —
if a dataset encodes `side` as the resting side instead, only `MapAggressor` changes.

## Cost / effort vs Rithmic (informational)

Databento is self-serve and usage-priced; CME exchange fees are the floor regardless of
vendor. The integration effort is far lower than Rithmic (no SDK licensing, no
conformance), which is why it's the recommended path for a read-only analytics terminal
that doesn't need broker execution or prop-firm credentials.
