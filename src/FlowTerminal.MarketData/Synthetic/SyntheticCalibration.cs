using System.Globalization;
using System.Text;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.MarketData.Synthetic;

/// <summary>
/// Developer-only calibration probe for the synthetic market engine. It runs the
/// generator for a simulated span and measures what actually comes out — trade counts,
/// size percentiles, size-bucket histogram, threshold exceedances, sweep behaviour,
/// spread, price range, and regime occupancy — so unrealistic output is visible as
/// numbers, not vibes. Used by calibration tests (which assert ranges) and available
/// for ad-hoc tuning runs. It only consumes the canonical stream; it never alters it.
/// </summary>
public sealed class SyntheticCalibrationReport
{
    // Size buckets per the calibration spec.
    private static readonly long[] BucketEdges = { 1, 5, 10, 25, 50, 100, 149, 249, 499 };
    private static readonly string[] BucketNames =
        { "1", "2–5", "6–10", "11–25", "26–50", "51–100", "101–149", "150–249", "250–499", "500+" };

    public required string Instrument { get; init; }
    public required double SimulatedMinutes { get; init; }
    public required long Trades { get; init; }
    public required long Volume { get; init; }
    public required long DepthEvents { get; init; }
    public required double MeanSize { get; init; }
    public required long MedianSize { get; init; }
    public required long P90 { get; init; }
    public required long P95 { get; init; }
    public required long P99 { get; init; }
    public required long P999 { get; init; }
    public required long MaxTrade { get; init; }
    public required long[] BucketCounts { get; init; }
    public required long Above25 { get; init; }
    public required long Above50 { get; init; }
    public required long Above100 { get; init; }
    public required long Above150 { get; init; }
    public required long Above250 { get; init; }
    public required double TradesPerMinute { get; init; }
    public required double VolumePerMinute { get; init; }
    public required double Above150PerMinute { get; init; }
    public required double AvgSpreadTicks { get; init; }
    public required long PriceRangeTicks { get; init; }
    public required long DuplicateTradeIds { get; init; }

    public double FractionAbove(long threshold) => Trades == 0 ? 0 : threshold switch
    {
        25 => Above25 / (double)Trades,
        50 => Above50 / (double)Trades,
        100 => Above100 / (double)Trades,
        150 => Above150 / (double)Trades,
        250 => Above250 / (double)Trades,
        _ => throw new ArgumentOutOfRangeException(nameof(threshold)),
    };

    /// <summary>Runs the generator for a simulated wall-clock span and measures the output.</summary>
    public static SyntheticCalibrationReport Run(Contract contract, SyntheticOptions options, TimeSpan simulatedSpan)
    {
        var start = new DateTime(2024, 6, 3, 13, 30, 0, DateTimeKind.Utc);
        var gen = new SyntheticSessionGenerator(1, contract, start, options);
        var end = start + simulatedSpan;

        var sizes = new List<long>(1 << 16);
        long volume = 0, depthEvents = 0, dupIds = 0;
        long above25 = 0, above50 = 0, above100 = 0, above150 = 0, above250 = 0;
        var buckets = new long[BucketNames.Length];
        var seenIds = new HashSet<long>();

        long bestBid = long.MinValue, bestAsk = long.MinValue;
        double spreadSum = 0;
        long spreadSamples = 0;
        long minPrice = long.MaxValue, maxPrice = long.MinValue;

        while (true)
        {
            var e = gen.Next();
            if (e.ExchangeTimestampUtc >= end) break;

            if (e.Type is MarketEventType.BidUpdate or MarketEventType.AskUpdate)
            {
                depthEvents++;
                // Track the touch from the engine's own book (cheap + exact).
                bestBid = gen.Engine.Book.BestBidTicks;
                bestAsk = gen.Engine.Book.BestAskTicks;
                if (bestBid != SyntheticOrderBook.NoPrice && bestAsk != SyntheticOrderBook.NoPrice && bestAsk > bestBid)
                {
                    spreadSum += bestAsk - bestBid;
                    spreadSamples++;
                }
            }
            else if (e.Type == MarketEventType.Trade)
            {
                long q = e.Quantity;
                sizes.Add(q);
                volume += q;
                if (!seenIds.Add(e.TradeId)) dupIds++;
                if (q >= 25) above25++;
                if (q >= 50) above50++;
                if (q >= 100) above100++;
                if (q >= 150) above150++;
                if (q >= 250) above250++;
                buckets[BucketFor(q)]++;
                minPrice = Math.Min(minPrice, e.PriceTicks);
                maxPrice = Math.Max(maxPrice, e.PriceTicks);
            }
        }

        double minutes = simulatedSpan.TotalMinutes;
        sizes.Sort();
        return new SyntheticCalibrationReport
        {
            Instrument = contract.Root.ToString(),
            SimulatedMinutes = minutes,
            Trades = sizes.Count,
            Volume = volume,
            DepthEvents = depthEvents,
            MeanSize = sizes.Count > 0 ? volume / (double)sizes.Count : 0,
            MedianSize = Pct(sizes, 0.50),
            P90 = Pct(sizes, 0.90),
            P95 = Pct(sizes, 0.95),
            P99 = Pct(sizes, 0.99),
            P999 = Pct(sizes, 0.999),
            MaxTrade = sizes.Count > 0 ? sizes[^1] : 0,
            BucketCounts = buckets,
            Above25 = above25,
            Above50 = above50,
            Above100 = above100,
            Above150 = above150,
            Above250 = above250,
            TradesPerMinute = sizes.Count / Math.Max(1e-9, minutes),
            VolumePerMinute = volume / Math.Max(1e-9, minutes),
            Above150PerMinute = above150 / Math.Max(1e-9, minutes),
            AvgSpreadTicks = spreadSamples > 0 ? spreadSum / spreadSamples : 0,
            PriceRangeTicks = maxPrice >= minPrice ? maxPrice - minPrice : 0,
            DuplicateTradeIds = dupIds,
        };
    }

    private static int BucketFor(long q)
    {
        for (int i = 0; i < BucketEdges.Length; i++)
            if (q <= BucketEdges[i]) return i;
        return BucketEdges.Length;
    }

    private static long Pct(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    public string Format()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"── Synthetic calibration · {Instrument} · {SimulatedMinutes:0} sim-min ──");
        sb.AppendLine($"trades {Trades:N0} ({TradesPerMinute:N0}/min) · volume {Volume:N0} ({VolumePerMinute:N0}/min) · depth events {DepthEvents:N0}");
        sb.AppendLine($"size: mean {MeanSize.ToString("0.00", ci)} · median {MedianSize} · p90 {P90} · p95 {P95} · p99 {P99} · p99.9 {P999} · max {MaxTrade}");
        sb.AppendLine($"≥25 {Above25:N0} ({FractionAbove(25):P2}) · ≥50 {Above50:N0} ({FractionAbove(50):P2}) · ≥100 {Above100:N0} ({FractionAbove(100):P3}) · ≥150 {Above150:N0} ({FractionAbove(150):P3}, {Above150PerMinute.ToString("0.00", ci)}/min) · ≥250 {Above250:N0} ({FractionAbove(250):P4})");
        sb.Append("buckets: ");
        for (int i = 0; i < BucketNames.Length; i++)
            sb.Append($"{BucketNames[i]}={BucketCounts[i]:N0}  ");
        sb.AppendLine();
        sb.AppendLine($"spread avg {AvgSpreadTicks.ToString("0.00", ci)} ticks · traded price range {PriceRangeTicks} ticks · duplicate trade ids {DuplicateTradeIds}");
        return sb.ToString();
    }
}
