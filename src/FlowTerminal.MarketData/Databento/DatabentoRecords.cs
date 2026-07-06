namespace FlowTerminal.MarketData.Databento;

/// <summary>
/// The fields of a Databento DBN <c>trades</c> record (<c>TradeMsg</c>) that Flow
/// Terminal consumes, already decoded from the wire. A DBN decoder / the live client
/// populates these; the mapper turns them into canonical events. Kept as a plain value
/// type so the mapping logic is pure and fully testable without any network or SDK.
///
/// DBN conventions reflected here:
///   • <see cref="TsEventNs"/> — UInt64 nanoseconds since the UNIX epoch (UTC).
///   • <see cref="PriceFixed"/> — Int64 fixed-point at 1e-9 (real price = value / 1_000_000_000).
///   • <see cref="Side"/> — 'B' buy-aggressor, 'A' sell-aggressor, 'N' none/unknown.
/// </summary>
public readonly record struct DatabentoTrade(
    ulong TsEventNs,
    long PriceFixed,
    uint Size,
    char Side,
    ulong Sequence);

/// <summary>One depth level from a Databento <c>mbp-10</c> record.</summary>
public readonly record struct DatabentoLevel(long BidPriceFixed, long AskPriceFixed, uint BidSize, uint AskSize);

/// <summary>
/// The fields of a Databento DBN <c>mbp-10</c> record (<c>Mbp10Msg</c>) Flow Terminal
/// consumes: the resulting top-10 book after the event, plus the event's own trade fields
/// when <see cref="Action"/> is 'T'. Prices are 1e-9 fixed-point; an empty level uses the
/// DBN undefined sentinel (<see cref="UndefPrice"/>) or a zero size.
/// </summary>
public readonly record struct DatabentoMbp10(
    ulong TsEventNs,
    char Action,        // 'A' add · 'C' cancel · 'M' modify · 'R' clear · 'T' trade · 'F' fill
    char Side,
    long PriceFixed,    // the event price (for 'T'/'F')
    uint Size,
    ulong Sequence,
    IReadOnlyList<DatabentoLevel> Levels)
{
    /// <summary>DBN "undefined price" sentinel (INT64_MAX) — marks an empty book slot.</summary>
    public const long UndefPrice = long.MaxValue;
}
