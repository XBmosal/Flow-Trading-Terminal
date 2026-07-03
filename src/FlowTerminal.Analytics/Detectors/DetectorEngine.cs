using FlowTerminal.Analytics.Bars;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;

namespace FlowTerminal.Analytics.Detectors;

/// <summary>
/// Runs the full detector suite over a canonical stream and keeps a bounded ring of
/// recent detections for display. Each detector is independently toggleable. The
/// same engine runs in live and replay, so detections are reproducible.
/// </summary>
public sealed class DetectorEngine
{
    private readonly Detection[] _ring;
    private int _head;
    private int _count;
    private long _total;

    public DetectorEngine(RootSymbol root, int ringCapacity = 256)
    {
        _ring = new Detection[ringCapacity];
        Large = LargeTradeDetector.For(root);
    }

    public LargeTradeDetector Large { get; }
    public SweepDetector Sweep { get; } = new();
    public AbsorptionDetector Absorption { get; } = new();
    public ReplenishmentDetector Replenishment { get; } = new();
    public IcebergDetector Iceberg { get; } = new();
    public StopRunDetector StopRun { get; } = new();
    public MarketRegimeDetector Regime { get; } = new();
    public DeltaDivergenceDetector Divergence { get; } = new();
    public ExhaustionDetector Exhaustion { get; } = new();
    public TrappedTraderDetector Trapped { get; } = new();

    public long TotalDetections => _total;

    public IEnumerable<IDetector> Detectors => new IDetector[]
    {
        Large, Sweep, Absorption, Replenishment, Iceberg, StopRun, Regime, Divergence,
        Exhaustion, Trapped,
    };

    /// <summary>Processes one canonical event, recording any resulting detections.</summary>
    public void OnEvent(in MarketEvent e)
    {
        Record(Iceberg.OnEvent(e));
        Record(Regime.OnEvent(e));

        if (e.Type is MarketEventType.BidUpdate or MarketEventType.AskUpdate)
        {
            Record(Replenishment.OnDepth(e));
        }
        else if (e.Type == MarketEventType.Trade)
        {
            Record(Large.OnTrade(e));
            Record(Sweep.OnTrade(e));
            Record(Absorption.OnTrade(e));
            Record(StopRun.OnTrade(e));
        }
    }

    /// <summary>Feeds a completed bar to the bar-based detectors.</summary>
    public void OnBar(in Bar bar)
    {
        Record(Divergence.OnBar(bar));
        Record(Exhaustion.OnBar(bar));
        Record(Trapped.OnBar(bar));
    }

    public IReadOnlyList<Detection> Recent(int max)
    {
        int n = Math.Min(max, _count);
        var list = new List<Detection>(n);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - 1 - i + _ring.Length) % _ring.Length;
            list.Add(_ring[idx]);
        }

        return list;
    }

    private void Record(Detection? d)
    {
        if (d is null) return;
        _ring[_head] = d;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        _total++;
    }
}
