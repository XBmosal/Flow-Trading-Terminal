using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.MarketData.Synthetic;

/// <summary>
/// Independent stress dimensions for the synthetic engine. Each mode exaggerates ONE
/// aspect on purpose; they never combine silently and none is the default — normal mock
/// realism is <see cref="None"/>.
/// </summary>
public enum SyntheticStressMode
{
    /// <summary>Calibrated realism (the default; the only mode used for demos/screenshots).</summary>
    None,

    /// <summary>Higher event/trade throughput. The trade-size distribution is unchanged.</summary>
    EventRate,

    /// <summary>Much heavier add/cancel/replenish churn. Book stays valid; sizes unchanged.</summary>
    Depth,

    /// <summary>Explicit tail-inflation mode for Big Trade testing only — never normal output.</summary>
    LargeTrade,

    /// <summary>Injects a deliberate sequence gap to exercise recovery paths (tests only).</summary>
    Corruption,
}

public sealed record SyntheticOptions
{
    /// <summary>Seed driving the deterministic stream. Same seed → identical events.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Starting mid price in exchange points.</summary>
    public decimal StartPrice { get; init; } = 20_000m;

    /// <summary>Average milliseconds between simulation steps (drives event pacing).</summary>
    public int MeanInterEventMs { get; init; } = 5;

    /// <summary>
    /// Optional override of the large-clip (sweep-candidate) probability. Null (default)
    /// uses the instrument profile's calibrated value; setting it is a test/tuning knob.
    /// </summary>
    public double? LargeTradeProbability { get; init; }

    /// <summary>Which single aspect (if any) to stress. Default = calibrated realism.</summary>
    public SyntheticStressMode Stress { get; init; } = SyntheticStressMode.None;

    /// <summary>Inject a deliberate sequence gap after this many events (0 = never). For tests.</summary>
    public int InjectGapAfter { get; init; }
}

/// <summary>
/// Public, replay-stable façade over the stateful synthetic market engine
/// (<see cref="SyntheticEventGenerator"/>). It preserves the original pull interface
/// (<see cref="Next"/> / <see cref="Generate"/>) so the mock provider, the historical
/// provider and warm-up replay drive it unchanged, while every event now comes from a
/// genuine, incrementally-mutated limit-order book rather than a memoryless touch
/// quote. Output is a pure function of the seed and is flagged
/// <see cref="MarketEventFlags.Synthetic"/> on every event; it must never be presented
/// as real exchange data.
/// </summary>
public sealed class SyntheticSessionGenerator
{
    private readonly SyntheticEventGenerator _engine;

    public SyntheticSessionGenerator(int instrumentId, Contract contract, DateTime startUtc, SyntheticOptions options)
        => _engine = new SyntheticEventGenerator(instrumentId, contract, startUtc, options);

    /// <summary>The underlying stateful engine (book + regime + diagnostics) for tests/diagnostics.</summary>
    public SyntheticEventGenerator Engine => _engine;

    /// <summary>Produces the next canonical event. Deterministic given construction parameters.</summary>
    public MarketEvent Next() => _engine.Next();

    /// <summary>Lazily produces up to <paramref name="count"/> canonical events (deterministic).</summary>
    public IEnumerable<MarketEvent> Generate(int count) => _engine.Generate(count);
}
