using FlowTerminal.Analytics.Bars;
using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Analytics.Delta;
using FlowTerminal.Analytics.Detectors;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Analytics.OrderFlow;
using FlowTerminal.Analytics.PriceAction;
using FlowTerminal.Analytics.Profiles;
using FlowTerminal.Analytics.Vwap;
using FlowTerminal.Charting;
using FlowTerminal.Charting.Dom;
using FlowTerminal.Charting.Heatmap;
using FlowTerminal.Charting.Overlays;
using FlowTerminal.Charting.Tape;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.Domain.Sessions;
using FlowTerminal.MarketData.Abstractions;
using FlowTerminal.MarketData.Pipeline;
using FlowTerminal.MarketData.Synthetic;
using FlowTerminal.OrderBook;
using SkiaSharp;

namespace FlowTerminal.App;

public sealed record ChartSnapshot(
    IReadOnlyList<Bar> Bars,
    IReadOnlyList<DomRow> Dom,
    long Cvd,
    IReadOnlyList<TapeRow> Tape,
    bool BookValid,
    string? BookInvalidReason,
    DiagnosticsSnapshot Diagnostics,
    IReadOnlyList<Detection> Detections,
    long TotalDetections,
    ChartOverlays Overlays,
    long BestBidTicks,
    long BestAskTicks,
    IReadOnlyList<CvdBar> CvdSeries,
    IReadOnlyList<BigTradeGroup> BigTrades,
    BigTradeDiagnostics BigTradeDiagnostics)
{
    /// <summary>The CME trading date the feed is currently in (Globex 17:00 CT rollover).</summary>
    public DateOnly TradingDate { get; init; }

    /// <summary>True while the current event time is inside Regular Trading Hours (08:30–15:00 CT).</summary>
    public bool IsRth { get; init; }
}

/// <summary>
/// Drives a mock (or, when wired, live) feed through the canonical pipeline and the
/// Phase-3 analytics, exposing coalesced snapshots for the UI. Ingestion and
/// analytics run on the pipeline's single-writer thread; the UI samples the latest
/// snapshot on its own timer, so rendering is decoupled from event processing and
/// canonical events are never dropped to keep up with the screen.
/// </summary>
public sealed class LiveFeedService : IAsyncDisposable
{
    private const int MaxChartBars = 500;

    private readonly object _lock = new();
    private MarketByPriceOrderBook _book = new();
    private VolumeProfile _profile = new();
    private CvdCalculator _cvd = new();
    private TimeAndSalesModel _tape = new();
    private IBarAggregator _bars = BarAggregator.Time(TimeSpan.FromMinutes(1));
    private readonly List<Bar> _completed = new();
    private readonly List<double> _vwapByBar = new();
    private MultiVwap _multiVwap = new();
    private FairValueGapDetector _fvg = new();
    private readonly List<FvgBox> _fvgs = new();
    private readonly TradingCalendar _calendar = new();
    private readonly List<Footprint> _barFootprints = new();
    private readonly List<FootprintBar> _footprintColumns = new(); // 1:1 with _completed
    private FootprintSettings _footprintSettings = FootprintSettings.Default;

    /// <summary>Current footprint configuration (mode, imbalance ratios, thresholds…).</summary>
    public FootprintSettings FootprintSettings => _footprintSettings;

    /// <summary>Applies new footprint settings; the next snapshot rebuilds bars under them.</summary>
    public void SetFootprintSettings(FootprintSettings settings) => _footprintSettings = settings.Validate();
    private readonly List<CvdBar> _cvdBars = new();                   // CVD OHLC per completed bar
    private long _cvdOpen, _cvdHigh, _cvdLow, _cvdClose;
    private bool _cvdHasDev;
    private Footprint _currentFootprint = new();
    private TpoProfile? _tpo;
    private OpeningRangeBreakout? _orb;
    private int _barsEvicted;
    private long _lastWarmPriceTicks;

    // Warm-start sizing: aim for ~50 bars of recent synthetic history at the current
    // timeframe, but cap the replay span so even high timeframes stay responsive.
    private const int TargetWarmBars = 100; // enough history that the chart can be panned
    private const int MinWarmMinutes = 20;
    private const int MaxWarmMinutes = 480; // ~8h cap keeps even high-TF switches ~2s

    private LiquidityHeatmap _heatmap = new(TimeSpan.FromMilliseconds(250));
    private PullStackTracker _pullStack = new();

    // ── Order-flow suite engines (shared canonical stream; deterministic) ────
    private DeltaDivergenceEngine _divergence = new();
    private DeltaBlockEngine _deltaBlocks = new();
    private AnchoredVwapInstance? _avwapSession;               // session-open anchored VWAP
    private readonly List<AnchoredVwapPoint> _avwapByBar = new(); // 1:1 with _completed
    private readonly HeatmapRenderer _heatmapRenderer = new();
    private readonly BookmapRenderer _bookmapRenderer = new();
    private readonly List<TradeDot> _tradeDots = new(); // recent executions for heatmap bubbles
    private long _lastTradeTicks;
    private DateTime _lastEventUtc; // most recent processed event time (for age-based queries)

    // Session awareness (Globex trading-date + RTH/ETH), advanced per event.
    private readonly SessionTracker _session = new();
    private SessionState _sessionState;

    /// <summary>
    /// Resets session-scoped analytics on a trading-date rollover: the session volume
    /// profile, session CVD, TPO, opening range, Big Trades session percentile,
    /// pull/stack accumulation, and the session-anchored VWAP (re-anchors at the next
    /// trade). Bars, book, heatmap history, divergence signals and delta blocks persist —
    /// they are chart history, not session state.
    /// </summary>
    private void OnTradingDateRollover()
    {
        _profile = new VolumeProfile();
        _cvd = new CvdCalculator();
        _cvdHasDev = false;
        _tpo = null;
        _orb = null;
        _bigTrades.ResetSession();
        _pullStack = new PullStackTracker();
        _avwapSession = null; // re-anchors at the first trade of the new session
    }
    private long _heatmapMinSize; // contrast filter: hide resting levels below this size

    /// <summary>Sets the heatmap contrast filter — resting levels smaller than this are hidden.</summary>
    public void SetHeatmapMinSize(long minSize) => _heatmapMinSize = Math.Max(0, minSize);
    private readonly DetectorEngine _detectors = new(RootSymbol.NQ);

    // Shared Big Trades engine: classifies aggressor side, aggregates groups, flags sweeps.
    // The same groups drive the heatmap bubbles and are exposed on the snapshot so other
    // panels reconcile against one source of truth.
    private BigTradeDetector _bigTrades = BigTradeDetector.For(RootSymbol.NQ);

    /// <summary>Whether qualifying Big Trade groups are emphasised on the heatmap.</summary>
    public bool ShowBigTrades { get; set; } = true;

    private readonly PipelineDiagnostics _diagnostics = new();

    private InstrumentPipeline? _pipeline;
    private IMarketDataProvider? _provider;
    private CancellationTokenSource? _cts;
    private Contract? _contract;
    private TimeSpan _interval = TimeSpan.FromMinutes(1);
    private decimal _startPrice = 20_000m;
    private int _switching;

    /// <summary>The bar timeframe the feed is currently aggregating.</summary>
    public TimeSpan Timeframe => _interval;

    /// <summary>The contract the feed is currently streaming.</summary>
    public Contract? Contract => _contract;

    /// <summary>
    /// Realism diagnostics for the in-flight synthetic session (regime, spread, level
    /// distribution, executed/trade totals), or null on a non-mock feed. For the
    /// debug readout only — observational, never affects rendering or the stream.
    /// </summary>
    public SyntheticDiagnostics? SyntheticDiagnostics => (_provider as MockMarketDataProvider)?.SyntheticDiagnostics;

    public Task StartAsync(Contract contract)
    {
        _contract = contract;
        _startPrice = DefaultStartPrice(contract.Root);
        ApplyFootprintPreset(contract.Root);
        return StartFeedAsync();
    }

    /// <summary>
    /// Switches the streamed contract (e.g. NQ ↔ ES, or a different expiry). The feed
    /// is stopped, per-session analytics reset, history re-warmed for the new contract
    /// and the live stream resumed. View/observation only — no orders are involved.
    /// </summary>
    public async Task ChangeContractAsync(Contract contract)
    {
        if (_contract is not null && contract.FullSymbol == _contract.FullSymbol)
        {
            return;
        }

        if (Interlocked.Exchange(ref _switching, 1) == 1)
        {
            return;
        }

        try
        {
            await StopFeedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _contract = contract;
                _startPrice = DefaultStartPrice(contract.Root);
                ApplyFootprintPreset(contract.Root);
                ResetSessionState();
            }

            await StartFeedAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    private static decimal DefaultStartPrice(RootSymbol root) => root == RootSymbol.ES ? 5_000m : 20_000m;

    /// <summary>Per-instrument footprint preset, preserving the user's current display mode.</summary>
    private void ApplyFootprintPreset(RootSymbol root) =>
        _footprintSettings = (root == RootSymbol.ES ? FootprintSettings.Es : FootprintSettings.Nq)
            with { Mode = _footprintSettings.Mode };

    // ── Playback transport (pause / speed / restart) ────────────────────────

    private bool _paused;
    private double _speed = 1.0;

    /// <summary>Whether the paced stream is currently paused.</summary>
    public bool IsPaused => _paused;

    /// <summary>Current playback speed multiplier.</summary>
    public double Speed => _speed;

    /// <summary>Pauses or resumes the live (simulated) stream.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        (_provider as MockMarketDataProvider)?.SetPaused(paused);
    }

    /// <summary>Sets the playback speed (e.g. 2.0 = twice real time).</summary>
    public void SetSpeed(double multiplier)
    {
        _speed = multiplier;
        (_provider as MockMarketDataProvider)?.SetSpeed(multiplier);
    }

    /// <summary>Restarts the session from a fresh warm-up at the current contract/timeframe.</summary>
    public async Task RestartAsync()
    {
        if (_contract is null || Interlocked.Exchange(ref _switching, 1) == 1)
        {
            return;
        }

        try
        {
            await StopFeedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                ResetSessionState();
            }

            await StartFeedAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    /// <summary>
    /// Switches the bar timeframe. The current feed is stopped, the per-session
    /// analytics are reset, history is re-warmed at the new interval and the live
    /// stream resumes — detector toggles and diagnostics are preserved. This is a
    /// view/aggregation change only; no orders or trading state are involved.
    /// </summary>
    public async Task ChangeTimeframeAsync(TimeSpan interval)
    {
        if (interval == _interval || _contract is null)
        {
            return;
        }

        // Ignore overlapping switches (rapid clicks) until the in-flight one settles.
        if (Interlocked.Exchange(ref _switching, 1) == 1)
        {
            return;
        }

        try
        {
            await StopFeedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _interval = interval;
                ResetSessionState();
            }

            await StartFeedAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    private async Task StartFeedAsync()
    {
        _cts = new CancellationTokenSource();

        // 1) Warm-start with ~recent synthetic history (off the UI thread) so the
        //    chart, profile, VWAP, footprint and TPO are populated the moment the
        //    window opens, instead of slowly building one live bar at a time.
        decimal startPrice = _startPrice;
        await Task.Run(() => WarmUp(_contract!, startPrice), _cts.Token).ConfigureAwait(false);

        // 2) Go live, continuing from the warmed-up price so there is no seam.
        decimal liveStart = _lastWarmPriceTicks > 0
            ? PriceConverter.ToPrice(_contract!.Spec, _lastWarmPriceTicks)
            : startPrice;

        var mock = new MockMarketDataProvider(
            1, _contract!, new SyntheticOptions { Seed = 7, StartPrice = liveStart }, realTimePacing: true);
        mock.SetPaused(_paused);   // preserve transport state across restarts/switches
        mock.SetSpeed(_speed);
        _provider = mock;
        _pipeline = new InstrumentPipeline(1, ProcessAsync, _diagnostics);
        _pipeline.Start();

        await _provider.ConnectAsync(_cts.Token);
        await _provider.SubscribeAsync(_contract!, SubscriptionOptions.Default, _cts.Token);
        _ = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task StopFeedAsync()
    {
        _cts?.Cancel();
        if (_provider is not null)
        {
            await _provider.DisconnectAsync().ConfigureAwait(false);
            await _provider.DisposeAsync().ConfigureAwait(false);
            _provider = null;
        }

        if (_pipeline is not null)
        {
            await _pipeline.DisposeAsync().ConfigureAwait(false);
            _pipeline = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Clears the per-session market-data and analytics aggregates so history can be
    /// rebuilt at a new timeframe. The detector engine and diagnostics are kept so the
    /// user's study toggles and the running counters survive a timeframe change.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void ResetSessionState()
    {
        _book = new MarketByPriceOrderBook();
        _profile = new VolumeProfile();
        _cvd = new CvdCalculator();
        _tape = new TimeAndSalesModel();
        _bars = BarAggregator.Time(_interval);
        _completed.Clear();
        _vwapByBar.Clear();
        _multiVwap = new MultiVwap();
        _fvg = new FairValueGapDetector();
        _fvgs.Clear();
        _barFootprints.Clear();
        _footprintColumns.Clear();
        _cvdBars.Clear();
        _cvdOpen = _cvdHigh = _cvdLow = _cvdClose = 0;
        _cvdHasDev = false;
        _currentFootprint = new Footprint();
        _tpo = null;
        _orb = null;
        _barsEvicted = 0;
        _lastWarmPriceTicks = 0;
        _heatmap = new LiquidityHeatmap(TimeSpan.FromMilliseconds(250));
        _pullStack = new PullStackTracker();
        _bigTrades = BigTradeDetector.For(_contract?.Root ?? RootSymbol.NQ);
        _divergence = new DeltaDivergenceEngine();
        _deltaBlocks = new DeltaBlockEngine();
        _avwapSession = null;
        _avwapByBar.Clear();
        _tradeDots.Clear();
        _lastTradeTicks = 0;
        _lastEventUtc = default;
        _session.Reset();
        _sessionState = default;
    }

    /// <summary>
    /// Synchronously replays a window of synthetic history through the same analytics
    /// the live feed uses, populating the chart before the live stream begins. The
    /// span scales with the timeframe so the chart shows a useful number of bars.
    /// </summary>
    private void WarmUp(Contract contract, decimal startPrice)
    {
        var now = DateTime.UtcNow;
        int warmMinutes = Math.Clamp(
            (int)(_interval.TotalMinutes * TargetWarmBars), MinWarmMinutes, MaxWarmMinutes);
        var warmStart = now - TimeSpan.FromMinutes(warmMinutes);
        var gen = new SyntheticSessionGenerator(1, contract, warmStart,
            new SyntheticOptions { Seed = 7, StartPrice = startPrice });

        // The stateful engine emits several canonical events per simulation step, so a
        // full 8h warm window needs a higher iteration ceiling than the old one-event
        // generator. The loop still exits the moment it reaches "now".
        for (int guard = 0; guard < 24_000_000; guard++)
        {
            var e = gen.Next();
            if (e.ExchangeTimestampUtc >= now)
            {
                break;
            }

            // Warm-up only builds what the historical chart needs (bars, profile,
            // VWAP, footprint, TPO). The liquidity heatmap and the detector engine are
            // intentionally skipped here: the heatmap's per-event forward-fill and the
            // detectors processing every print are the slow path, and neither produces
            // anything visible on the *historical* candle view. They start live with
            // the real stream. This keeps warm-up well under a second instead of ~40s.
            Ingest(e, warmUp: true);
            if (e.Type == MarketEventType.Trade)
            {
                _lastWarmPriceTicks = e.PriceTicks;
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _provider!.StreamAsync(ct))
            {
                await _pipeline!.EnqueueAsync(ev, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private ValueTask ProcessAsync(MarketEvent e, CancellationToken ct)
    {
        Ingest(e, warmUp: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>Projects a bar and its footprint into a fully-derived render-ready footprint bar.</summary>
    private FootprintBar BuildFootprintBar(Bar bar, Footprint fp, bool isClosed) =>
        FootprintAggregator.Build(
            fp, bar.OpenTicks, bar.HighTicks, bar.LowTicks, bar.CloseTicks,
            bar.StartUtc, bar.EndUtc, isClosed, _footprintSettings);

    /// <summary>
    /// Processes one canonical event into the analytics. When <paramref name="warmUp"/>
    /// is true the heavy, history-replay-only-irrelevant work (the liquidity heatmap's
    /// per-event forward-fill and the detector engine) is skipped so the historical
    /// chart can be built in a fraction of a second; the live stream passes false so
    /// every engine runs in real time.
    /// </summary>
    private void Ingest(MarketEvent e, bool warmUp)
    {
        lock (_lock)
        {
            _lastEventUtc = e.ExchangeTimestampUtc;

            // Session awareness: detect the Globex trading-date rollover (17:00 CT) and
            // reset session-scoped analytics at the correct boundary. Chart history,
            // the live book, and rolling analytics are deliberately NOT cleared.
            _sessionState = _session.OnEvent(e.ExchangeTimestampUtc);
            if (_sessionState.RolledOver)
            {
                OnTradingDateRollover();
            }

            _book.Apply(e);

            // ── One classification truth ─────────────────────────────────────
            // Every trade is classified exactly once (native side preferred; honest
            // inference otherwise) by the shared engine inside BigTradeDetector, and the
            // classified side is applied back onto the event. EVERY consumer below —
            // CVD, footprint, profiles, tape, bars, detectors, heatmap dots — then reads
            // the same aggressor, flagged AggressorInferred when it was estimated. This
            // also warms the Big Trades size distribution on historical trades.
            if (e.Type == MarketEventType.Trade && e.Quantity > 0)
            {
                var classified = _bigTrades.OnTrade(e, _book.BestBidTicks, _book.BestAskTicks, _book.IsValid);
                e = AggressorEnrichment.Enrich(e, classified);
            }

            if (!warmUp)
            {
                _heatmap.OnClock(e.ExchangeTimestampUtc);
                _detectors.OnEvent(e);
                if (e.Type is MarketEventType.BidUpdate or MarketEventType.AskUpdate)
                {
                    _heatmap.OnDepth(e);
                    _pullStack.OnDepth(e);
                }
            }

            if (e.Type == MarketEventType.Trade)
            {
                _profile.AddTrade(e);
                _cvd.Add(e);
                long cvdNow = _cvd.CumulativeDelta;
                if (!_cvdHasDev)
                {
                    _cvdOpen = _cvdHigh = _cvdLow = cvdNow;
                    _cvdHasDev = true;
                }

                _cvdHigh = Math.Max(_cvdHigh, cvdNow);
                _cvdLow = Math.Min(_cvdLow, cvdNow);
                _cvdClose = cvdNow;
                _tape.Add(e);

                // Heatmap trade bubbles are live-only (warm history has no dot trail).
                if (!warmUp)
                {
                    _lastTradeTicks = e.PriceTicks;
                    _tradeDots.Add(new TradeDot(e.ExchangeTimestampUtc, e.PriceTicks, e.Quantity, e.Aggressor));
                    if (_tradeDots.Count > 8000) _tradeDots.RemoveRange(0, _tradeDots.Count - 8000);
                }
                _multiVwap.AddTrade(e.PriceTicks, e.Quantity, _calendar.TradingDate(e.ExchangeTimestampUtc));

                // Order-flow suite: warm history is included so the chart's back-history
                // carries signals/blocks too. The session AVWAP anchors at the first trade.
                _avwapSession ??= new AnchoredVwapInstance(1, "Session", VwapAnchorType.SessionOpen, e.ExchangeTimestampUtc);
                _avwapSession.OnTrade(e);
                _deltaBlocks.OnTrade(e);

                _currentFootprint.AddTrade(e);
                _tpo ??= new TpoProfile(e.ExchangeTimestampUtc);
                _tpo.AddTrade(e.PriceTicks, e.ExchangeTimestampUtc);

                // Opening-range breakout: 5-minute range from the first trade seen.
                _orb ??= new OpeningRangeBreakout(e.ExchangeTimestampUtc.AddMinutes(5));
                _orb.OnTrade(e.PriceTicks, e.ExchangeTimestampUtc);

                if (_bars.AddTrade(e) is { } completed)
                {
                    if (!warmUp) _detectors.OnBar(completed);
                    _divergence.OnBar(completed);
                    _completed.Add(completed);
                    _vwapByBar.Add(_multiVwap.Daily.VwapTicks);
                    var av = _avwapSession is { HasData: true } a ? a.Value : new VwapValue(double.NaN, 0);
                    _avwapByBar.Add(new AnchoredVwapPoint(av.VwapTicks, av.StdDevTicks));
                    var doneFootprint = _currentFootprint;
                    _barFootprints.Add(doneFootprint);
                    _footprintColumns.Add(BuildFootprintBar(completed, doneFootprint, isClosed: true));
                    _currentFootprint = new Footprint();

                    // Close out this bar's CVD candle and re-open the next at the same level.
                    _cvdBars.Add(new CvdBar(_cvdOpen, _cvdHigh, _cvdLow, _cvdClose));
                    _cvdOpen = _cvdHigh = _cvdLow = _cvdClose;

                    int absoluteIndex = _barsEvicted + _completed.Count - 1;
                    if (_fvg.OnBar(completed) is { } gap)
                    {
                        _fvgs.Add(new FvgBox(absoluteIndex, gap.Direction, gap.TopTicks, gap.BottomTicks));
                        if (_fvgs.Count > 64) _fvgs.RemoveRange(0, _fvgs.Count - 64);
                    }

                    if (_completed.Count > MaxChartBars)
                    {
                        int remove = _completed.Count - MaxChartBars;
                        _completed.RemoveRange(0, remove);
                        _vwapByBar.RemoveRange(0, remove);
                        _avwapByBar.RemoveRange(0, remove);
                        _barFootprints.RemoveRange(0, remove);
                        _footprintColumns.RemoveRange(0, remove);
                        if (_cvdBars.Count >= remove) _cvdBars.RemoveRange(0, remove);
                        _barsEvicted += remove;
                    }
                }
            }
        }
    }

    /// <summary>Latest coalesced snapshot for rendering. Cheap; safe to call at frame rate.</summary>
    public ChartSnapshot Snapshot()
    {
        lock (_lock)
        {
            var bars = new List<Bar>(_completed.Count + 1);
            bars.AddRange(_completed);

            // VWAP series aligned to the bar list (developing bar gets the live value).
            var vwap = new List<double>(_vwapByBar.Count + 1);
            vwap.AddRange(_vwapByBar);
            if (_bars.HasDeveloping)
            {
                bars.Add(_bars.Developing);
                vwap.Add(_multiVwap.Daily.VwapTicks);
            }

            // FVG boxes rebased to the current (post-eviction) bar indexing.
            var fvgs = new List<FvgBox>(_fvgs.Count);
            foreach (var f in _fvgs)
            {
                int rel = f.BarIndex - _barsEvicted;
                if (rel >= 0 && rel < bars.Count)
                {
                    fvgs.Add(f with { BarIndex = rel });
                }
            }

            // Footprint columns for the whole buffer (aligned 1:1 with completed bars),
            // plus the developing bar, so the footprint view can pan/zoom like candles.
            // The completed columns are built once on bar close, so this is a cheap copy.
            var footprint = new List<FootprintBar>(_footprintColumns.Count + 1);
            footprint.AddRange(_footprintColumns);
            if (_bars.HasDeveloping)
            {
                footprint.Add(BuildFootprintBar(_bars.Developing, _currentFootprint, isClosed: false));
            }

            // TPO rows (lettered brackets per price) over the session.
            var tpoRows = new List<TpoRow>();
            if (_tpo is not null)
            {
                foreach (var lvl in _profile.Levels())
                {
                    string letters = _tpo.LettersAt(lvl.PriceTicks);
                    if (letters.Length > 0) tpoRows.Add(new TpoRow(lvl.PriceTicks, letters));
                }
            }

            var va = _profile.ComputeValueArea();
            var bigTrades = _bigTrades.Snapshot(_lastEventUtc == default ? DateTime.UtcNow : _lastEventUtc);
            var overlays = new ChartOverlays(
                _profile.Levels(), _profile.PocTicks(), va.VahTicks, va.ValTicks,
                vwap, fvgs,
                _orb is { IsEstablished: true } orb ? orb.HighTicks : null,
                _orb is { IsEstablished: true } orb2 ? orb2.LowTicks : null,
                footprint, tpoRows, bigTrades)
            {
                // Copies: the engines mutate on the pipeline thread while the UI samples.
                Divergences = new List<DivergenceSignal>(_divergence.Signals),
                DevelopingDivergence = _divergence.Developing(),
                DeltaBlocks = new List<DeltaBlock>(_deltaBlocks.Blocks),
                AnchoredVwap = new List<AnchoredVwapPoint>(_avwapByBar),
            };

            // CVD history (completed bars + the developing bar so the line is live).
            var cvdSeries = new List<CvdBar>(_cvdBars.Count + 1);
            cvdSeries.AddRange(_cvdBars);
            if (_cvdHasDev) cvdSeries.Add(new CvdBar(_cvdOpen, _cvdHigh, _cvdLow, _cvdClose));

            long wallFloor = (_contract?.Root ?? RootSymbol.NQ) == RootSymbol.ES ? 300 : 150;
            var dom = ReadOnlyDom.Build(_book, _profile, 12, _pullStack, wallFloor);
            return new ChartSnapshot(
                bars, dom, _cvd.CumulativeDelta, _tape.Latest(50),
                _book.IsValid, _book.InvalidReason, _diagnostics.Snapshot(),
                _detectors.Recent(8), _detectors.TotalDetections, overlays,
                _book.BestBidTicks, _book.BestAskTicks, cvdSeries,
                bigTrades, _bigTrades.DiagnosticsSnapshot())
            {
                TradingDate = _sessionState.TradingDate,
                IsRth = _sessionState.IsRth,
            };
        }
    }

    /// <summary>
    /// Renders the liquidity heatmap under the feed lock (no copy of session history).
    /// Called from the UI control's paint; the model keeps history tiled so this does
    /// not rebuild the whole session.
    /// </summary>
    /// <summary>Enables/disables a detector by its display name (from the Studies panel).</summary>
    public void SetDetectorEnabled(string detectorName, bool enabled)
    {
        foreach (var d in _detectors.Detectors)
        {
            if (d.Name == detectorName)
            {
                d.Enabled = enabled;
            }
        }
    }

    public void RenderHeatmap(SKCanvas canvas, SKRect bounds)
    {
        lock (_lock)
        {
            long bid = _book.BestBidTicks;
            long ask = _book.BestAskTicks;
            long mid = bid != BookSide.NoPrice && ask != BookSide.NoPrice ? (bid + ask) / 2 : 80_000;
            const long window = 60; // ticks above/below mid
            _heatmapRenderer.Render(canvas, bounds, _heatmap, mid - window, mid + window, HeatmapScale.Percentile);
        }
    }

    /// <summary>Renders the full Bookmap-style liquidity view (heatmap + bubbles + axes).</summary>
    public void RenderBookmap(SKCanvas canvas, SKRect bounds, BookmapView view)
    {
        lock (_lock)
        {
            long bid = _book.BestBidTicks;
            long ask = _book.BestAskTicks;
            long mid = bid != BookSide.NoPrice && ask != BookSide.NoPrice ? (bid + ask) / 2
                : _lastTradeTicks > 0 ? _lastTradeTicks : 80_000;
            var (lo, hi) = HeatmapWindow(mid);
            decimal tick = _contract?.Spec.TickSize ?? 0.25m;
            long bidSize = bid != BookSide.NoPrice ? _book.SizeAt(Side.Bid, bid) : 0;
            long askSize = ask != BookSide.NoPrice ? _book.SizeAt(Side.Ask, ask) : 0;
            var bigTrades = ShowBigTrades
                ? _bigTrades.Snapshot(_lastEventUtc == default ? DateTime.UtcNow : _lastEventUtc)
                : null;
            _bookmapRenderer.Render(canvas, bounds, _heatmap, _tradeDots, bid, ask, _lastTradeTicks,
                lo, hi, tick, _heatmapMinSize, view, bidSize, askSize, bigTrades: bigTrades);
        }
    }

    /// <summary>Frames the price window to the recent active liquidity (with padding).</summary>
    private (long Lo, long Hi) HeatmapWindow(long mid)
    {
        long lo = long.MaxValue, hi = long.MinValue;
        var cols = _heatmap.Columns;
        for (int i = Math.Max(0, cols.Count - 80); i < cols.Count; i++)
        {
            foreach (var k in cols[i].Bid.Keys) { lo = Math.Min(lo, k); hi = Math.Max(hi, k); }
            foreach (var k in cols[i].Ask.Keys) { lo = Math.Min(lo, k); hi = Math.Max(hi, k); }
        }

        if (lo > hi) { lo = mid - 80; hi = mid + 80; }
        long pad = Math.Max(4, (hi - lo) / 12);
        return (lo - pad, hi + pad);
    }

    public async ValueTask DisposeAsync()
    {
        await StopFeedAsync().ConfigureAwait(false);
    }
}
