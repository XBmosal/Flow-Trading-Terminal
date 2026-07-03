using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using FlowTerminal.OrderBook;
using Xunit;
using Xunit.Abstractions;

namespace FlowTerminal.UiTests;

/// <summary>
/// End-to-end Big Trade *rate* calibration: drives the retuned synthetic NQ feed through
/// the real order book and the shared BigTradeDetector (the app's NQ preset) and asserts
/// the bubble rate is "occasional and meaningful" — not the dozens-per-candle flood the
/// old firehose produced. This is the regression fence for the mock-realism rework.
/// </summary>
public class BigTradeRateTests
{
    private static readonly Contract Nq = new(RootSymbol.NQ, QuarterlyMonth.December, 2025);
    private static readonly DateTime Start = new(2024, 6, 3, 13, 30, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;
    public BigTradeRateTests(ITestOutputHelper output) => _output = output;

    private static (IReadOnlyList<BigTradeGroup> Groups, double Minutes, long Trades) Drive(
        int minutes, SyntheticStressMode stress = SyntheticStressMode.None)
    {
        var gen = new SyntheticSessionGenerator(1, Nq, Start,
            new SyntheticOptions { Seed = 7, StartPrice = 20_000m, Stress = stress });
        var book = new MarketByPriceOrderBook();
        var detector = BigTradeDetector.For(RootSymbol.NQ);
        var end = Start.AddMinutes(minutes);
        long trades = 0;
        DateTime last = Start;

        while (true)
        {
            var e = gen.Next();
            if (e.ExchangeTimestampUtc >= end) break;
            last = e.ExchangeTimestampUtc;
            book.Apply(e);
            if (e.Type == MarketEventType.Trade)
            {
                trades++;
                detector.OnTrade(e, book.BestBidTicks, book.BestAskTicks, book.IsValid);
            }
        }

        detector.Flush();
        return (detector.Snapshot(last), (last - Start).TotalMinutes, trades);
    }

    [Fact]
    public void Normal_Nq_Bubble_Rate_Is_Occasional_Not_A_Flood()
    {
        var (groups, minutes, trades) = Drive(minutes: 20);
        double perMinute = groups.Count / Math.Max(1e-9, minutes);
        int over150 = groups.Count(g => g.TotalQuantity >= 150);
        int over250 = groups.Count(g => g.TotalQuantity >= 250);

        _output.WriteLine($"trades {trades:N0} · bubbles {groups.Count} ({perMinute:0.00}/min) · " +
                          $"150+ groups {over150} · 250+ groups {over250} · " +
                          $"largest group {(groups.Count > 0 ? groups.Max(g => g.TotalQuantity) : 0)}");

        Assert.True(groups.Count > 0, "a live session should produce some qualifying Big Trades");
        Assert.True(perMinute < 8, $"normal NQ must not flood: got {perMinute:0.0} bubbles/min");
        // 150+ *groups* are the meaningful events: occasional, never dozens per candle.
        Assert.True(over150 / Math.Max(1e-9, minutes) < 1.5,
            $"150+ groups must be occasional; got {over150} in {minutes:0.0} min");
        Assert.True(over250 <= 3, $"250+ groups must be rare; got {over250}");
    }

    [Fact]
    public void EventRate_Stress_Does_Not_Explode_The_Large_Group_Rate()
    {
        var (normal, nMin, _) = Drive(minutes: 12);
        var (stress, sMin, _) = Drive(minutes: 12, stress: SyntheticStressMode.EventRate);

        double n150 = normal.Count(g => g.TotalQuantity >= 150) / Math.Max(1e-9, nMin);
        double s150 = stress.Count(g => g.TotalQuantity >= 150) / Math.Max(1e-9, sMin);
        _output.WriteLine($"150+ groups/min: normal {n150:0.00} · event-rate stress {s150:0.00}");

        // More events, sure — but the large-group rate must stay the same order of
        // magnitude, because sizes aren't inflated by throughput.
        Assert.True(s150 < Math.Max(1.0, n150 * 6),
            $"event-rate stress must not manufacture 150+ groups (normal {n150:0.00}/min → stress {s150:0.00}/min)");
    }
}
