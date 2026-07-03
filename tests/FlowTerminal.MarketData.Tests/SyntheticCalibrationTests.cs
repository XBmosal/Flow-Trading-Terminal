using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Synthetic;
using Xunit;
using Xunit.Abstractions;

namespace FlowTerminal.MarketData.Tests;

/// <summary>
/// Measured calibration of the synthetic market. The dump test writes a full
/// distribution report (for tuning); the assertion tests pin the realism envelope so a
/// regression back to "dozens of 150-lot prints per candle" fails CI, not the eye test.
/// </summary>
public class SyntheticCalibrationTests
{
    private static readonly Contract Nq = new(RootSymbol.NQ, QuarterlyMonth.December, 2025);
    private static readonly Contract Es = new(RootSymbol.ES, QuarterlyMonth.December, 2025);

    private readonly ITestOutputHelper _output;
    public SyntheticCalibrationTests(ITestOutputHelper output) => _output = output;

    private static SyntheticCalibrationReport Run(Contract c, ulong seed = 7, int minutes = 30,
        SyntheticStressMode stress = SyntheticStressMode.None)
    {
        decimal start = c.Root == RootSymbol.ES ? 5_000m : 20_000m;
        return SyntheticCalibrationReport.Run(c,
            new SyntheticOptions { Seed = seed, StartPrice = start, Stress = stress },
            TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void Dump_Calibration_Reports()
    {
        foreach (var (contract, label) in new[] { (Nq, "NQ"), (Es, "ES") })
        {
            foreach (var stress in new[]
            {
                SyntheticStressMode.None, SyntheticStressMode.EventRate, SyntheticStressMode.LargeTrade,
            })
            {
                var r = Run(contract, minutes: 30, stress: stress);
                _output.WriteLine($"[{label} · {stress}]");
                _output.WriteLine(r.Format());
            }
        }

        var dir = Environment.GetEnvironmentVariable("FT_CALIB_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            var lines = new List<string>();
            foreach (var (contract, label) in new[] { (Nq, "NQ"), (Es, "ES") })
            foreach (var stress in new[] { SyntheticStressMode.None, SyntheticStressMode.EventRate, SyntheticStressMode.LargeTrade })
            {
                lines.Add($"[{label} · {stress}]");
                lines.Add(Run(contract, minutes: 30, stress: stress).Format());
            }
            File.WriteAllLines(Path.Combine(dir, "calibration.txt"), lines);
        }
    }

    // ── Normal-mode realism envelope (NQ) ────────────────────────────────────

    [Fact]
    public void Nq_Normal_Mode_Small_Trades_Dominate()
    {
        var r = Run(Nq);
        Assert.True(r.Trades > 1000, $"expected a live tape, got {r.Trades} trades");
        Assert.InRange(r.MedianSize, 1, 5);

        // 1–5 contract prints must be the dominant majority.
        long small = r.BucketCounts[0] + r.BucketCounts[1];
        Assert.True(small / (double)r.Trades > 0.55,
            $"1–5 lot prints should dominate; got {small / (double)r.Trades:P1}");
    }

    [Fact]
    public void Nq_Normal_Mode_150_Plus_Is_Rare_And_250_Plus_Exceptional()
    {
        var r = Run(Nq);
        Assert.True(r.FractionAbove(150) < 0.001,
            $"150+ prints must be <0.1% of trades; got {r.FractionAbove(150):P3}");
        Assert.True(r.Above150PerMinute < 1.0,
            $"150+ prints must average <1/min in normal NQ mode; got {r.Above150PerMinute:0.00}/min");
        Assert.True(r.Above250 <= 3,
            $"250+ prints must be exceptional in 30 min; got {r.Above250}");
        // Tail must not dominate the tape: mean stays near the body.
        Assert.True(r.MeanSize < 4 * r.MedianSize + 6,
            $"mean {r.MeanSize:0.0} is tail-dominated vs median {r.MedianSize}");
    }

    [Fact]
    public void Nq_Trade_Rate_Is_Sane_And_Spread_Is_Tight()
    {
        var r = Run(Nq);
        Assert.InRange(r.TradesPerMinute, 100, 2000);
        Assert.InRange(r.AvgSpreadTicks, 0.9, 1.6);   // mostly one tick
        Assert.Equal(0, r.DuplicateTradeIds);          // duplicate-event audit
    }

    // ── ES has its own calibrated distribution ───────────────────────────────

    [Fact]
    public void Es_Distribution_Differs_From_Nq_And_Stays_Heavy_Tailed()
    {
        var nq = Run(Nq);
        var es = Run(Es);
        Assert.True(es.MedianSize > nq.MedianSize || es.MeanSize > nq.MeanSize,
            "ES should print larger typical sizes than NQ");
        Assert.InRange(es.MedianSize, 2, 9);
        Assert.True(es.FractionAbove(250) < 0.002, $"ES 250+ must stay rare; got {es.FractionAbove(250):P3}");
    }

    // ── Stress separation ────────────────────────────────────────────────────

    [Fact]
    public void EventRate_Stress_Raises_Rate_Without_Inflating_Sizes()
    {
        var normal = Run(Nq);
        var stress = Run(Nq, stress: SyntheticStressMode.EventRate);

        Assert.True(stress.TradesPerMinute > normal.TradesPerMinute * 1.7,
            $"event-rate stress should raise throughput (got {normal.TradesPerMinute:0}/min → {stress.TradesPerMinute:0}/min)");
        // The size distribution must NOT inflate: median equal-ish, tail fraction bounded.
        Assert.InRange(stress.MedianSize, 1, normal.MedianSize + 2);
        Assert.True(stress.FractionAbove(100) < normal.FractionAbove(100) * 3 + 0.001,
            "event-rate stress must not multiply large-trade share");
    }

    [Fact]
    public void LargeTrade_Stress_Is_The_Explicit_Tail_Mode()
    {
        var normal = Run(Nq);
        var big = Run(Nq, stress: SyntheticStressMode.LargeTrade);
        Assert.True(big.FractionAbove(100) > normal.FractionAbove(100) * 3,
            "LargeTrade stress should visibly raise the tail");
        Assert.True(big.Above150PerMinute > normal.Above150PerMinute,
            "LargeTrade stress should produce more 150+ prints than normal mode");
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void Same_Seed_Reproduces_Identical_Statistics()
    {
        var a = Run(Nq, seed: 42, minutes: 8);
        var b = Run(Nq, seed: 42, minutes: 8);
        Assert.Equal(a.Trades, b.Trades);
        Assert.Equal(a.Volume, b.Volume);
        Assert.Equal(a.MaxTrade, b.MaxTrade);
        Assert.Equal(a.Above150, b.Above150);

        var c = Run(Nq, seed: 43, minutes: 8);
        Assert.True(a.Volume != c.Volume || a.Trades != c.Trades, "different seeds should differ");
    }
}
