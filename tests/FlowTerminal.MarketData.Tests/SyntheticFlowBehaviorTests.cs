using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using Xunit;

namespace FlowTerminal.MarketData.Tests;

/// <summary>
/// Behavioural realism of the synthetic tape: event timing must be stochastic (no fixed
/// spacing), aggressive flow must cluster by side (no mechanical alternation, no
/// memoryless 50/50), and the book/trade interaction must never print impossible sizes.
/// </summary>
public class SyntheticFlowBehaviorTests
{
    private static readonly Contract Nq = new(RootSymbol.NQ, QuarterlyMonth.December, 2025);
    private static readonly DateTime Start = new(2024, 6, 3, 13, 30, 0, DateTimeKind.Utc);

    private static List<MarketEvent> Trades(int events, ulong seed = 7)
    {
        var gen = new SyntheticSessionGenerator(1, Nq, Start,
            new SyntheticOptions { Seed = seed, StartPrice = 20_000m });
        var trades = new List<MarketEvent>();
        foreach (var e in gen.Generate(events))
            if (e.Type == MarketEventType.Trade) trades.Add(e);
        return trades;
    }

    [Fact]
    public void Interarrival_Times_Are_Stochastic_Not_Fixed()
    {
        var trades = Trades(120_000);
        Assert.True(trades.Count > 500, "expected a live tape");

        var gaps = new HashSet<long>();
        int bursts = 0; // several trades inside the same or near-same millisecond
        for (int i = 1; i < trades.Count; i++)
        {
            long gapMs = (long)(trades[i].ExchangeTimestampUtc - trades[i - 1].ExchangeTimestampUtc).TotalMilliseconds;
            gaps.Add(gapMs);
            if (gapMs <= 1) bursts++;
        }

        Assert.True(gaps.Count > 20, $"interarrival times look fixed ({gaps.Count} distinct gaps)");
        Assert.True(bursts > 0, "clustered bursts should occur");
        Assert.True(bursts < trades.Count / 2, "the tape should not be one continuous burst");
    }

    [Fact]
    public void Aggressor_Side_Clusters_Without_Locking()
    {
        var trades = Trades(150_000);
        int same = 0, buys = 0;
        for (int i = 1; i < trades.Count; i++)
        {
            if (trades[i].Aggressor == trades[i - 1].Aggressor) same++;
            if (trades[i].Aggressor == AggressorSide.Buy) buys++;
        }

        double pSame = same / (double)(trades.Count - 1);
        double pBuy = buys / (double)(trades.Count - 1);

        // Momentum + regime bias ⇒ short-lived clustering: clearly above a memoryless
        // coin flip, clearly below a locked one-sided stream, and never alternation (≈0).
        Assert.InRange(pSame, 0.53, 0.92);
        // Over a whole session neither side dominates outright.
        Assert.InRange(pBuy, 0.30, 0.70);
    }

    [Fact]
    public void Prints_Never_Exceed_Displayed_Liquidity_At_Their_Level()
    {
        // Every execution is emitted per consumed level, so no single print can exceed
        // the engine's displayed-level cap — the "no teleporting through the book" bound.
        var gen = new SyntheticSessionGenerator(1, Nq, Start, new SyntheticOptions { Seed = 11, StartPrice = 20_000m });
        long maxLevel = SyntheticMarketConfiguration.ForRoot(RootSymbol.NQ).MaxLevelSize;
        foreach (var e in gen.Generate(200_000))
        {
            if (e.Type == MarketEventType.Trade)
            {
                Assert.True(e.Quantity > 0, "no zero/negative prints");
                Assert.True(e.Quantity <= maxLevel, $"print {e.Quantity} exceeds any possible displayed level");
            }
            else if (e.Type is MarketEventType.BidUpdate or MarketEventType.AskUpdate)
            {
                Assert.True(e.Quantity >= 0, "no negative depth");
            }
        }
    }
}
