using FlowTerminal.Domain.Events;

namespace FlowTerminal.Analytics.BigTrades;

/// <summary>
/// Applies the shared classifier's result back onto a canonical trade event so that
/// EVERY downstream consumer (CVD, footprint, profiles, bars, tape, detectors, Big
/// Trades, heatmap dots) sees one classification truth instead of each interpreting the
/// raw aggressor field independently.
///
/// Rules (honesty-preserving):
///   • A feed-supplied side is never altered — the event passes through untouched.
///   • A raw Unknown that the classifier resolved is rewritten with the classified side
///     plus <see cref="MarketEventFlags.AggressorInferred"/>, so the side is usable but
///     permanently labelled Estimated.
///   • A raw Unknown the classifier could not resolve stays Unknown — never forced.
/// </summary>
public static class AggressorEnrichment
{
    public static MarketEvent Enrich(in MarketEvent e, ClassifiedSide classified)
    {
        if (e.Type != MarketEventType.Trade) return e;
        if (e.Aggressor != AggressorSide.Unknown) return e;          // native side wins, unchanged
        if (classified.Side == AggressorSide.Unknown) return e;      // honestly unknown stays unknown

        return MarketEvent.Trade(
            e.InstrumentId, e.Root, e.ContractSymbol, e.Exchange,
            e.ExchangeTimestampUtc, e.ReceiveTimestampUtc,
            e.PriceTicks, e.Quantity, classified.Side,
            e.ExchangeSequence, e.TradeId,
            e.Flags | MarketEventFlags.AggressorInferred,
            e.Source);
    }
}
