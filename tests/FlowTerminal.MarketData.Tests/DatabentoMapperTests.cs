using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Databento;
using FlowTerminal.OrderBook;
using Xunit;

namespace FlowTerminal.MarketData.Tests;

public class DatabentoMapperTests
{
    private static readonly Contract Nq = new(RootSymbol.NQ, QuarterlyMonth.December, 2025);

    private static DatabentoMapper Mapper() => new(1, Nq);

    private static long Fixed(decimal price) => (long)(price * 1_000_000_000m);

    // ── Field conversions ────────────────────────────────────────────────────

    [Fact]
    public void Timestamp_Nanoseconds_Convert_To_Utc()
    {
        ulong ns = 1_704_067_200_000_000_000UL; // 2024-01-01T00:00:00Z
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), DatabentoMapper.ToUtc(ns));
    }

    [Fact]
    public void Fixed_Point_Price_Converts_To_Integer_Ticks()
    {
        // 20000.00 points ÷ 0.25 tick = 80000 ticks.
        Assert.Equal(80_000, Mapper().ToTicks(Fixed(20_000.00m)));
        Assert.Equal(80_002, Mapper().ToTicks(Fixed(20_000.50m)));
    }

    [Theory]
    [InlineData('B', AggressorSide.Buy)]
    [InlineData('A', AggressorSide.Sell)]
    [InlineData('N', AggressorSide.Unknown)]
    [InlineData(' ', AggressorSide.Unknown)]
    public void Side_Char_Maps_To_Aggressor(char side, AggressorSide expected)
        => Assert.Equal(expected, DatabentoMapper.MapAggressor(side));

    // ── Trade mapping ────────────────────────────────────────────────────────

    [Fact]
    public void Buy_Trade_Is_Native_Aggressor_With_Ticks_And_Sequence()
    {
        var e = Mapper().MapTrade(new DatabentoTrade(1_704_067_200_000_000_000UL, Fixed(20_010.25m), 7, 'B', 42));
        Assert.Equal(MarketEventType.Trade, e.Type);
        Assert.Equal(AggressorSide.Buy, e.Aggressor);
        Assert.True(e.HasFlag(MarketEventFlags.AggressorSupplied)); // native, not estimated
        Assert.Equal(80_041, e.PriceTicks);   // 20010.25 / 0.25
        Assert.Equal(7, e.Quantity);
        Assert.Equal(42, e.ExchangeSequence);
        Assert.Equal(SourceProvider.Databento, e.Source);
    }

    [Fact]
    public void Unknown_Side_Trade_Is_Not_Flagged_Native_And_Stays_Unknown()
    {
        var e = Mapper().MapTrade(new DatabentoTrade(0, Fixed(20_000m), 3, 'N', 1));
        Assert.Equal(AggressorSide.Unknown, e.Aggressor);
        Assert.False(e.HasFlag(MarketEventFlags.AggressorSupplied)); // never a fake native side
    }

    // ── Depth (mbp-10) mapping ───────────────────────────────────────────────

    private static DatabentoMbp10 Book(char action, params (decimal Bid, uint BidSz, decimal Ask, uint AskSz)[] levels)
    {
        var lv = levels.Select(l => new DatabentoLevel(
            l.Bid == 0 ? DatabentoMbp10.UndefPrice : (long)(l.Bid * 1_000_000_000m), l.Ask == 0 ? DatabentoMbp10.UndefPrice : (long)(l.Ask * 1_000_000_000m),
            l.BidSz, l.AskSz)).ToList();
        return new DatabentoMbp10(0, action, 'N', 0, 0, 100, lv);
    }

    [Fact]
    public void Mbp10_Emits_Level_Updates_And_Reconstructs_A_Valid_Book()
    {
        var m = Mapper();
        var book = new MarketByPriceOrderBook();

        // Initial snapshot: 3 levels each side.
        foreach (var e in m.MapDepth(Book('A',
            (19_999.75m, 10, 20_000.25m, 8),
            (19_999.50m, 20, 20_000.50m, 15),
            (19_999.25m, 30, 20_000.75m, 25))))
            book.Apply(e);

        Assert.Equal(m.ToTicks(Fixed(19_999.75m)), book.BestBidTicks);
        Assert.Equal(m.ToTicks(Fixed(20_000.25m)), book.BestAskTicks);
        Assert.Equal(10, book.SizeAt(Side.Bid, book.BestBidTicks));
        Assert.Equal(8, book.SizeAt(Side.Ask, book.BestAskTicks));
        Assert.True(book.IsValid);
    }

    [Fact]
    public void Mbp10_Only_Emits_Deltas_For_Changed_Levels()
    {
        var m = Mapper();
        m.MapDepth(Book('A', (19_999.75m, 10, 20_000.25m, 8)));

        // Same bid, resized ask, plus a new deeper bid level.
        var events = m.MapDepth(Book('M',
            (19_999.75m, 10, 20_000.25m, 12),   // bid unchanged, ask 8→12
            (19_999.50m, 20, 20_000.50m, 5)))    // new levels
            .ToList();

        // Bid@19999.75 (unchanged) must NOT re-emit; the rest do.
        Assert.DoesNotContain(events, e => e.Type == MarketEventType.BidUpdate
            && e.PriceTicks == m.ToTicks(Fixed(19_999.75m)));
        Assert.Contains(events, e => e.Type == MarketEventType.AskUpdate
            && e.PriceTicks == m.ToTicks(Fixed(20_000.25m)) && e.Quantity == 12);
        Assert.Contains(events, e => e.Type == MarketEventType.BidUpdate
            && e.PriceTicks == m.ToTicks(Fixed(19_999.50m)) && e.Quantity == 20);
    }

    [Fact]
    public void Level_Dropping_Out_Of_Top10_Emits_A_Zero_Size_Update()
    {
        var m = Mapper();
        m.MapDepth(Book('A', (19_999.75m, 10, 20_000.25m, 8), (19_999.50m, 20, 20_000.50m, 5)));
        // Next snapshot no longer contains the 19_999.50 bid.
        var events = m.MapDepth(Book('M', (19_999.75m, 10, 20_000.25m, 8))).ToList();

        Assert.Contains(events, e => e.Type == MarketEventType.BidUpdate
            && e.PriceTicks == m.ToTicks(Fixed(19_999.50m)) && e.Quantity == 0);
    }

    [Fact]
    public void Embedded_Trade_In_Mbp10_Is_Emitted_Before_Level_Deltas()
    {
        var m = Mapper();
        var rec = new DatabentoMbp10(0, 'T', 'A', Fixed(20_000.25m), 4, 100,
            new[] { new DatabentoLevel(Fixed(19_999.75m), Fixed(20_000.25m), 10, 8) });
        var events = m.MapDepth(rec).ToList();

        Assert.Equal(MarketEventType.Trade, events[0].Type);
        Assert.Equal(AggressorSide.Sell, events[0].Aggressor); // 'A' = sell aggressor
        Assert.Equal(4, events[0].Quantity);
        Assert.Contains(events, e => e.Type is MarketEventType.BidUpdate or MarketEventType.AskUpdate);
    }

    [Fact]
    public void Book_Clear_Zeroes_Known_Levels()
    {
        var m = Mapper();
        m.MapDepth(Book('A', (19_999.75m, 10, 20_000.25m, 8)));
        var events = m.MapDepth(Book('R')).ToList(); // clear, empty levels

        Assert.Contains(events, e => e.Type == MarketEventType.BidUpdate && e.Quantity == 0);
        Assert.Contains(events, e => e.Type == MarketEventType.AskUpdate && e.Quantity == 0);
    }
}
