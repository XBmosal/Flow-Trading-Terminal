using FlowTerminal.Analytics.Bars;

namespace FlowTerminal.Analytics.Detectors;

public sealed record ExhaustionSettings
{
    /// <summary>Bars over which aggression must decline into the extreme.</summary>
    public int DeclineBars { get; init; } = 3;

    /// <summary>The extreme must be the highest high / lowest low of this lookback.</summary>
    public int ExtremeLookback { get; init; } = 12;

    /// <summary>Final bar's |delta| must be below this fraction of the run's first bar.</summary>
    public double DeclineRatio { get; init; } = 0.5;
}

/// <summary>
/// Exhaustion candidate on completed bars: price prints a local extreme while the
/// aggressive participation *into* that extreme declines (falling |delta| and volume
/// across the approach bars). Descriptive evidence of fading initiative — labelled a
/// candidate, never a confirmed reversal.
/// </summary>
public sealed class ExhaustionDetector : IDetector
{
    private readonly ExhaustionSettings _s;
    private readonly List<Bar> _bars = new();

    public ExhaustionDetector(ExhaustionSettings? settings = null) => _s = settings ?? new ExhaustionSettings();

    public string Name => "Exhaustion";
    public string Tooltip => "Local price extreme reached on declining aggressive participation. Candidate only; no reversal guarantee.";
    public bool Enabled { get; set; } = true;

    public Detection? OnBar(in Bar bar)
    {
        if (!Enabled) return null;
        _bars.Add(bar);
        if (_bars.Count > 400) _bars.RemoveAt(0);
        int n = _bars.Count;
        if (n < Math.Max(_s.ExtremeLookback, _s.DeclineBars + 1)) return null;

        var cur = _bars[^1];

        // Local extreme over the lookback.
        bool isHighest = true, isLowest = true;
        for (int i = n - _s.ExtremeLookback; i < n - 1; i++)
        {
            if (_bars[i].HighTicks >= cur.HighTicks) isHighest = false;
            if (_bars[i].LowTicks <= cur.LowTicks) isLowest = false;
        }

        if (!isHighest && !isLowest) return null;

        // Declining aggression across the approach: |delta| and volume shrink.
        var first = _bars[n - 1 - _s.DeclineBars];
        bool declining = true;
        for (int i = n - _s.DeclineBars; i < n; i++)
        {
            if (Math.Abs(_bars[i].Delta) > Math.Abs(_bars[i - 1].Delta) &&
                _bars[i].Volume > _bars[i - 1].Volume)
            {
                declining = false;
                break;
            }
        }

        if (!declining || Math.Abs(first.Delta) == 0) return null;
        if (Math.Abs(cur.Delta) > _s.DeclineRatio * Math.Abs(first.Delta)) return null;

        var bias = isHighest ? DetectionBias.Bearish : DetectionBias.Bullish;
        long price = isHighest ? cur.HighTicks : cur.LowTicks;
        return new Detection(Name, cur.EndUtc, price, bias, IsEstimated: false,
            $"Exhaustion candidate at {(isHighest ? "high" : "low")}: |delta| {Math.Abs(first.Delta)}→{Math.Abs(cur.Delta)}",
            new Dictionary<string, double>
            {
                ["firstDelta"] = first.Delta,
                ["lastDelta"] = cur.Delta,
                ["declineBars"] = _s.DeclineBars,
            });
    }
}

public sealed record TrappedTraderSettings
{
    /// <summary>Minimum |delta| of the aggressive bar that may trap its participants.</summary>
    public long MinTrapDelta { get; init; } = 100;

    /// <summary>Bars allowed for the failure + return to complete.</summary>
    public int ConfirmWithinBars { get; init; } = 5;
}

/// <summary>
/// Trapped-trader candidate on completed bars: a strongly one-sided aggressive bar near
/// a local extreme fails to continue and price closes back through that bar's range —
/// the late aggressors are (candidately) offside. The signal fires only when the return
/// bar completes; there is no lookahead. These are candidates about positioning
/// pressure, never proof of any individual trader's position.
/// </summary>
public sealed class TrappedTraderDetector : IDetector
{
    private readonly TrappedTraderSettings _s;
    private readonly List<Bar> _bars = new();
    private int _armedIndex = -1;
    private bool _armedBull; // true = aggressive BUYING armed (potential bull trap)

    public TrappedTraderDetector(TrappedTraderSettings? settings = null) => _s = settings ?? new TrappedTraderSettings();

    public string Name => "Trapped Traders";
    public string Tooltip => "Strong one-sided aggression near an extreme, then price returns through it. Candidate positioning pressure only.";
    public bool Enabled { get; set; } = true;

    public Detection? OnBar(in Bar bar)
    {
        if (!Enabled) return null;
        _bars.Add(bar);
        if (_bars.Count > 400) { _bars.RemoveAt(0); if (_armedIndex >= 0) _armedIndex--; }
        int n = _bars.Count;

        // Confirm an armed trap: price closes back through the aggressive bar's range.
        if (_armedIndex >= 0)
        {
            if (n - 1 - _armedIndex > _s.ConfirmWithinBars)
            {
                _armedIndex = -1; // expired unconfirmed
            }
            else
            {
                var armed = _bars[_armedIndex];
                if (_armedBull && bar.CloseTicks < armed.LowTicks)
                {
                    _armedIndex = -1;
                    return new Detection(Name, bar.EndUtc, armed.HighTicks, DetectionBias.Bearish, IsEstimated: false,
                        $"Trapped buyers candidate: +{armed.Delta} delta bar failed, close below its low",
                        new Dictionary<string, double> { ["trapDelta"] = armed.Delta, ["trapHigh"] = armed.HighTicks, ["trapLow"] = armed.LowTicks });
                }

                if (!_armedBull && bar.CloseTicks > armed.HighTicks)
                {
                    _armedIndex = -1;
                    return new Detection(Name, bar.EndUtc, armed.LowTicks, DetectionBias.Bullish, IsEstimated: false,
                        $"Trapped sellers candidate: {armed.Delta} delta bar failed, close above its high",
                        new Dictionary<string, double> { ["trapDelta"] = armed.Delta, ["trapHigh"] = armed.HighTicks, ["trapLow"] = armed.LowTicks });
                }
            }
        }

        // Arm on a strongly one-sided bar at a short-term extreme.
        if (Math.Abs(bar.Delta) >= _s.MinTrapDelta && n >= 4)
        {
            bool nearHigh = bar.HighTicks >= Math.Max(_bars[n - 2].HighTicks, _bars[n - 3].HighTicks);
            bool nearLow = bar.LowTicks <= Math.Min(_bars[n - 2].LowTicks, _bars[n - 3].LowTicks);
            if (bar.Delta > 0 && nearHigh) { _armedIndex = n - 1; _armedBull = true; }
            else if (bar.Delta < 0 && nearLow) { _armedIndex = n - 1; _armedBull = false; }
        }

        return null;
    }
}
