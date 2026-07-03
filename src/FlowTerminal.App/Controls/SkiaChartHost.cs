using System.Globalization;
using System.Windows;
using System.Windows.Input;
using FlowTerminal.Analytics.Bars;
using FlowTerminal.Analytics.BigTrades;
using FlowTerminal.Analytics.Footprints;
using FlowTerminal.Charting;
using FlowTerminal.Charting.BigTrades;
using FlowTerminal.Charting.Drawings;
using FlowTerminal.Charting.Overlays;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace FlowTerminal.App.Controls;

/// <summary>
/// A single SkiaSharp-backed chart control that renders either the candle view (with
/// study overlays) or the footprint/cluster view. The entire chart is drawn on one
/// canvas — there is NO WPF control per candle. The latest bar/overlay snapshot is
/// supplied from the (off-thread) feed and the control repaints on a coalesced timer.
///
/// Both views share the same interaction model (all view transforms — no data is
/// modified, no trading controls):
///   • Drag in the plot area      → scroll back/forward through history.
///   • Drag the right price axis   → zoom price (drag up = taller candles/cells).
///   • Drag the bottom time axis   → zoom time (drag right = fewer, wider columns).
/// The candle and footprint views keep independent pan/zoom so each stays sensible.
/// </summary>
public sealed class SkiaChartHost : SKElement
{
    private const float RightAxisWidth = 64f;
    private const float BottomAxisHeight = 22f;
    private const float TopPadding = 8f;

    private readonly CandlestickRenderer _renderer = new();
    private readonly BarSeriesRenderer _barSeries = new();
    private readonly ChartOverlayRenderer _overlays = new();
    private readonly FootprintRenderer _footprint = new();
    private readonly VolumeStripRenderer _volumeStrip = new();
    private readonly PriceMarkerRenderer _priceMarker = new();
    private readonly DrawingRenderer _drawingRenderer = new();
    private readonly IndicatorSeriesEngine _indicatorEngine = new();
    private readonly IndicatorRenderer _indicatorRenderer = new();
    private readonly BigTradeRenderer _bigTradeRenderer = new();
    private readonly OrderFlowOverlayRenderer _orderFlow = new();
    private IndicatorRenderData _indicatorData = IndicatorRenderData.Empty;
    private List<ChartDrawing> _drawings = new();
    private readonly Stack<List<ChartDrawing>> _undo = new();
    private readonly Stack<List<ChartDrawing>> _redo = new();
    private ChartDrawing? _pending;
    private bool _drawing;
    private ChartViewport? _lastViewport;
    private IReadOnlyList<Bar> _bars = Array.Empty<Bar>();
    private ChartOverlays _overlayData = ChartOverlays.Empty;
    private StudyState? _studies;
    private ChartType _chartType = ChartType.Candles;

    /// <summary>The active drawing tool. <see cref="DrawingTool.Select"/> pans/zooms.</summary>
    public DrawingTool ActiveTool { get; set; } = DrawingTool.Select;

    /// <summary>Footprint display configuration (mode, imbalance, POC…). Drives the footprint renderer.</summary>
    public FootprintSettings FootprintSettings { get; set; } = FootprintSettings.Default;

    /// <summary>Removes every drawing from the chart.</summary>
    public void ClearDrawings()
    {
        if (_drawings.Count == 0) return;
        PushUndo();
        _drawings = new List<ChartDrawing>();
        _pending = null;
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(new List<ChartDrawing>(_drawings));
        _drawings = _undo.Pop();
        _pending = null;
        InvalidateVisual();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(new List<ChartDrawing>(_drawings));
        _drawings = _redo.Pop();
        _pending = null;
        InvalidateVisual();
    }

    private void PushUndo()
    {
        _undo.Push(new List<ChartDrawing>(_drawings));
        _redo.Clear();
    }

    /// <summary>Tick size for axis price labels (NQ/ES = 0.25). Set from the instrument spec.</summary>
    public decimal TickSize { get; set; } = 0.25m;

    /// <summary>Price-series presentation (candles / bars / line). Footprint is a study.</summary>
    public ChartType ChartType
    {
        get => _chartType;
        set { _chartType = value; InvalidateVisual(); }
    }

    /// <summary>Independent pan/zoom for one view (candles or footprint).</summary>
    private sealed class ViewState
    {
        public int Visible;
        public int Pan;
        public double PriceZoom = 1.0;
    }

    private readonly ViewState _candleView = new() { Visible = 60 };
    private readonly ViewState _footprintView = new() { Visible = 18 };

    private enum DragMode { None, Pan, PriceZoom, TimeZoom }

    private DragMode _drag = DragMode.None;
    private Point _dragStart;
    private float _lastBarSlotWidth = 1f;
    private int _dragStartPan;
    private int _dragStartVisible;
    private double _dragStartZoom;

    public SkiaChartHost()
    {
        ClipToBounds = true;
    }

    public void Attach(StudyState studies) => _studies = studies;

    /// <summary>Scrolls back to the live (most recent) bar without changing zoom.</summary>
    public void ReturnToLive()
    {
        _candleView.Pan = 0;
        _footprintView.Pan = 0;
        InvalidateVisual();
    }

    /// <summary>Resets pan, time zoom and price zoom to defaults (return-to-live + default scale).</summary>
    public void ResetView()
    {
        _candleView.Pan = 0; _candleView.Visible = 60; _candleView.PriceZoom = 1.0;
        _footprintView.Pan = 0; _footprintView.Visible = 18; _footprintView.PriceZoom = 1.0;
        InvalidateVisual();
    }

    public void UpdateFrame(IReadOnlyList<Bar> bars, ChartOverlays overlays)
    {
        _bars = bars;
        _overlayData = overlays;
        // Technical indicators are recomputed once per frame from the visible bar list
        // (a pure function of bars + enabled set), never on the market-data thread.
        _indicatorData = _studies is { Enabled.Count: > 0 } s
            ? _indicatorEngine.Compute(bars, s.Enabled)
            : IndicatorRenderData.Empty;
        InvalidateVisual();
    }

    private bool FootprintMode =>
        _studies is not null && _studies.IsEnabled("FP") && _overlayData.Footprint.Count > 0;

    private ViewState View => FootprintMode ? _footprintView : _candleView;

    private int Total => FootprintMode ? _overlayData.Footprint.Count : _bars.Count;

    // ── Mouse interaction ───────────────────────────────────────────────────

    private DragMode RegionAt(Point p) =>
        p.X >= ActualWidth - RightAxisWidth ? DragMode.PriceZoom
        : p.Y >= ActualHeight - BottomAxisHeight ? DragMode.TimeZoom
        : DragMode.Pan;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        var region = RegionAt(_dragStart);

        // A drawing tool takes over clicks inside the plot; the axes always zoom.
        if (region == DragMode.Pan && ActiveTool != DrawingTool.Select && !FootprintMode
            && _lastViewport is not null && _bars.Count > 0)
        {
            if (ActiveTool == DrawingTool.Erase)
            {
                EraseAt(_dragStart);
            }
            else
            {
                var anchor = PixelToAnchor(_dragStart);
                _pending = new ChartDrawing(ActiveTool, anchor, anchor);
                _drawing = true;
            }

            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _drag = region;
        var view = View;
        _dragStartPan = view.Pan;
        _dragStartVisible = view.Visible;
        _dragStartZoom = view.PriceZoom;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_drawing && _pending is not null && _lastViewport is not null)
        {
            _pending.B = PixelToAnchor(e.GetPosition(this));
            InvalidateVisual();
            return;
        }

        if (_drag == DragMode.None)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var view = View;
        switch (_drag)
        {
            case DragMode.Pan:
            {
                double dx = pos.X - _dragStart.X;
                int barsDelta = (int)Math.Round(dx / Math.Max(1f, _lastBarSlotWidth));
                int visible = Math.Min(view.Visible, Total);
                int maxPan = Math.Max(0, Total - visible);
                view.Pan = Math.Clamp(_dragStartPan + barsDelta, 0, maxPan);
                break;
            }

            case DragMode.PriceZoom:
            {
                // Drag up → zoom in (taller candles/cells); drag down → zoom out.
                double dy = _dragStart.Y - pos.Y;
                view.PriceZoom = Math.Clamp(_dragStartZoom * Math.Exp(dy * 0.005), 0.2, 12.0);
                break;
            }

            case DragMode.TimeZoom:
            {
                // Drag right → fewer, wider columns; drag left → more.
                double dx = pos.X - _dragStart.X;
                int target = (int)Math.Round(_dragStartVisible * Math.Exp(-dx * 0.005));
                view.Visible = Math.Clamp(target, 5, 1000);
                break;
            }
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_drawing)
        {
            _drawing = false;
            ReleaseMouseCapture();
            if (_pending is not null)
            {
                PushUndo();
                _drawings.Add(_pending);
                _pending = null;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    // ── Drawing data↔pixel mapping ──────────────────────────────────────────

    private AnchorPoint PixelToAnchor(Point p)
    {
        var vp = _lastViewport!;
        double slot = (p.X - vp.PlotLeft) / Math.Max(1e-3, vp.BarSlotWidth);
        int idx = Math.Clamp(vp.FirstBarIndex + (int)Math.Round(slot - 0.5), 0, _bars.Count - 1);
        return new AnchorPoint(_bars[idx].StartUtc, vp.YToPrice((float)p.Y));
    }

    private float TimeToX(ChartViewport vp, DateTime t) => vp.BarCenterX(NearestBarByTime(t));

    // ── Big Trades overlay (bar-indexed placement) ──────────────────────────

    private void RenderBigTrades(SKCanvas canvas, ChartViewport vp)
    {
        // Only the groups whose bar is on screen — bounds the work and normalizes bubble
        // sizing to the visible window.
        var visible = new List<BigTradeGroup>();
        foreach (var g in _overlayData.BigTrades)
            if (vp.IsBarVisible(ContainingBarIndex(g.EndUtc))) visible.Add(g);
        if (visible.Count == 0) return;

        _bigTradeRenderer.RenderMapped(canvas,
            new SKRect(vp.PlotLeft, vp.PlotTop, vp.PlotRight, vp.PlotBottom),
            visible, t => BigTradeX(vp, t), vp.PriceToY, vp.MinPriceTicks, vp.MaxPriceTicks);
    }

    /// <summary>X for a trade time: the centre of the bar it falls in, nudged by its position within that bar.</summary>
    private float BigTradeX(ChartViewport vp, DateTime t)
    {
        if (_bars.Count == 0) return vp.PlotLeft;
        int i = ContainingBarIndex(t);
        DateTime start = _bars[i].StartUtc;
        DateTime end = i + 1 < _bars.Count ? _bars[i + 1].StartUtc
            : start + (i > 0 ? _bars[i].StartUtc - _bars[i - 1].StartUtc : TimeSpan.FromMinutes(1));
        double frac = end > start ? Math.Clamp((t - start).TotalSeconds / (end - start).TotalSeconds, 0, 1) : 0.5;
        return vp.BarCenterX(i) + (float)(frac - 0.5) * vp.BarSlotWidth;
    }

    /// <summary>Index of the bar whose start time is the latest at or before <paramref name="t"/>.</summary>
    private int ContainingBarIndex(DateTime t)
    {
        if (_bars.Count == 0) return 0;
        if (t <= _bars[0].StartUtc) return 0;
        if (t >= _bars[^1].StartUtc) return _bars.Count - 1;
        int lo = 0, hi = _bars.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_bars[mid].StartUtc <= t) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private int NearestBarByTime(DateTime t)
    {
        if (_bars.Count == 0) return 0;
        if (t <= _bars[0].StartUtc) return 0;
        if (t >= _bars[^1].StartUtc) return _bars.Count - 1;

        int lo = 0, hi = _bars.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var bt = _bars[mid].StartUtc;
            if (bt < t) lo = mid + 1;
            else if (bt > t) hi = mid - 1;
            else return mid;
        }

        int a = Math.Clamp(lo, 0, _bars.Count - 1);
        int b = Math.Clamp(lo - 1, 0, _bars.Count - 1);
        return Math.Abs((_bars[a].StartUtc - t).Ticks) < Math.Abs((_bars[b].StartUtc - t).Ticks) ? a : b;
    }

    private void EraseAt(Point p)
    {
        var vp = _lastViewport!;
        float best = 8f;
        int hit = -1;
        for (int i = 0; i < _drawings.Count; i++)
        {
            float d = DistanceTo(_drawings[i], vp, (float)p.X, (float)p.Y);
            if (d < best) { best = d; hit = i; }
        }

        if (hit >= 0)
        {
            PushUndo();
            _drawings.RemoveAt(hit);
        }
    }

    private float DistanceTo(ChartDrawing d, ChartViewport vp, float px, float py)
    {
        float xa = TimeToX(vp, d.A.Time), ya = vp.PriceToY(d.A.PriceTicks);
        float xb = TimeToX(vp, d.B.Time), yb = vp.PriceToY(d.B.PriceTicks);
        if (d.Tool == DrawingTool.HorizontalLine) return Math.Abs(py - ya);
        if (d.Tool == DrawingTool.Ray) { xb = vp.PlotRight; if (TimeToX(vp, d.B.Time) != xa) yb = ya + (yb - ya) * (xb - xa) / (TimeToX(vp, d.B.Time) - xa); }
        return DistToSegment(px, py, xa, ya, xb, yb);
    }

    private static float DistToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        float t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        float cx = ax + t * dx, cy = ay + t * dy;
        return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        // Isolate any render fault to this panel: a bad frame paints a "view paused"
        // card instead of taking down the whole terminal via the WPF render loop.
        var canvas = e.Surface.Canvas;
        var bounds = new SKRect(0, 0, e.Info.Width, e.Info.Height);
        if (!RenderSafety.Guard(canvas, bounds, () => PaintChart(e), _renderer.Palette, "Chart view paused"))
        {
            RenderGuard.LogThrottled("chart");
        }
    }

    private void PaintChart(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        float w = e.Info.Width;
        float h = e.Info.Height;
        canvas.Clear(_renderer.Palette.Background.ToSkColor());
        if (w <= 0 || h <= 0)
        {
            return;
        }

        bool footprint = FootprintMode;
        var view = footprint ? _footprintView : _candleView;
        int total = Total;
        if (total == 0)
        {
            return;
        }

        int visible = Math.Clamp(Math.Min(view.Visible, total), 1, total);
        int maxPan = Math.Max(0, total - visible);
        if (view.Pan > maxPan) view.Pan = maxPan;
        int first = total - visible - view.Pan;

        // Auto-fit the price range to the visible columns/bars, then apply the vertical
        // zoom about the mid price.
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = first; i < first + visible; i++)
        {
            long low = footprint ? _overlayData.Footprint[i].LowTicks : _bars[i].LowTicks;
            long high = footprint ? _overlayData.Footprint[i].HighTicks : _bars[i].HighTicks;
            lo = Math.Min(lo, low);
            hi = Math.Max(hi, high);
        }

        long pad = Math.Max(1, (hi - lo) / 20);
        double center = (lo + hi) / 2.0;
        double half = ((hi - lo) / 2.0 + pad) / view.PriceZoom;
        long min = (long)Math.Floor(center - half);
        long max = (long)Math.Ceiling(center + half);
        if (max <= min) max = min + 1;

        // Reserve a band on the right for the volume profile (candle view only) so the
        // histogram never overlaps the candles.
        float totalRight = w - RightAxisWidth;
        bool profileOn = !footprint && _studies is not null &&
            (_studies.IsEnabled("VBP") || _studies.IsEnabled("BAC") || _studies.IsEnabled("DP"));
        float profileBand = profileOn ? Math.Min(totalRight * 0.18f, 200f) : 0f;

        // Reserve a strip below the candles for any active oscillator panes (candle
        // view only). The candle plot shrinks by exactly this amount so panes never
        // overlap the candles; the time axis stays at the very bottom.
        float oscReserved = footprint ? 0f
            : Math.Min(_indicatorRenderer.ReservedPaneHeight(_indicatorData), Math.Max(0f, (h - BottomAxisHeight - TopPadding) * 0.55f));

        var viewport = new ChartViewport(
            w, h, min, max, first, visible,
            leftPadding: 0f, rightAxisWidth: RightAxisWidth + profileBand,
            topPadding: TopPadding, bottomPadding: BottomAxisHeight + oscReserved);
        _lastBarSlotWidth = viewport.BarSlotWidth;

        var plot = new SKRect(viewport.PlotLeft, viewport.PlotTop, totalRight, viewport.PlotBottom);
        canvas.Save();
        canvas.ClipRect(plot);
        if (footprint)
        {
            _footprint.Render(canvas, viewport, _overlayData.Footprint, FootprintSettings, TickSize);
        }
        else
        {
            // Delta blocks sit behind the candles so price stays readable.
            if (_studies?.IsEnabled("DBLK") == true && _overlayData.DeltaBlocks.Count > 0)
            {
                _orderFlow.RenderDeltaBlocks(canvas, viewport, _overlayData.DeltaBlocks, t => BigTradeX(viewport, t));
            }

            switch (_chartType)
            {
                case ChartType.Bars: _barSeries.RenderBars(canvas, viewport, _bars); break;
                case ChartType.Line: _barSeries.RenderLine(canvas, viewport, _bars); break;
                default: _renderer.Render(canvas, viewport, _bars); break;
            }

            if (_studies is { Enabled.Count: > 0 } studies)
            {
                _overlays.Render(canvas, viewport, _overlayData, studies.Enabled, profileBand);
                if (studies.IsEnabled("VOL")) _volumeStrip.Render(canvas, viewport, _bars);
            }

            // Technical-indicator price overlays (MA line, Bollinger/Donchian/Keltner).
            if (!_indicatorData.IsEmpty)
            {
                _indicatorRenderer.RenderOverlays(canvas, viewport, _indicatorData);
            }

            // Big Trades: aggressive-execution bubbles at their price/time, on top of the
            // candles. Gated by the Large/Block Trade study. Each group is placed in the bar
            // it executed in (offset within the bar by its intra-bar time) so bubbles in the
            // same bar spread out instead of stacking.
            if (_studies?.IsEnabled("LT") == true && _overlayData.BigTrades.Count > 0)
            {
                RenderBigTrades(canvas, viewport);
            }

            // Order-flow suite overlays (time-mapped; rendering detail only).
            if (_studies?.IsEnabled("AVWAP") == true && _overlayData.AnchoredVwap.Count > 0)
            {
                _orderFlow.RenderAnchoredVwap(canvas, viewport, _overlayData.AnchoredVwap, new[] { 1.0, 2.0 });
            }

            if (_studies?.IsEnabled("DDIV") == true)
            {
                _orderFlow.RenderDivergences(canvas, viewport, _overlayData.Divergences,
                    _overlayData.DevelopingDivergence, t => TimeToX(viewport, t));
            }
        }

        canvas.Restore();

        // Oscillator panes in the reserved strip between the candles and the time axis.
        if (!footprint && oscReserved > 0 && _indicatorData.Panes.Count > 0)
        {
            float paneTop = viewport.PlotBottom;
            float paneH = oscReserved / _indicatorData.Panes.Count;
            for (int i = 0; i < _indicatorData.Panes.Count; i++)
            {
                var rect = new SKRect(viewport.PlotLeft, paneTop + i * paneH, viewport.PlotRight, paneTop + (i + 1) * paneH);
                _indicatorRenderer.RenderPane(canvas, viewport, rect, _indicatorData.Panes[i]);
            }
        }

        DrawPriceAxis(canvas, viewport, totalRight);
        DrawTimeAxis(canvas, viewport, first, visible, footprint);

        // Current-price marker (latest close) in the right gutter, on top of the axis.
        if (!footprint && _bars.Count > 0)
        {
            var last = _bars[^1];
            _priceMarker.Render(canvas, viewport, last.CloseTicks, last.CloseTicks >= last.OpenTicks, TickSize, totalRight);
        }

        // User drawings (candle/bars/line view only), clipped to the plot.
        if (!footprint && (_drawings.Count > 0 || _pending is not null))
        {
            canvas.Save();
            canvas.ClipRect(plot);
            float TimeMap(DateTime t) => TimeToX(viewport, t);
            foreach (var d in _drawings) _drawingRenderer.Render(canvas, viewport, d, TimeMap, TickSize);
            if (_pending is not null) _drawingRenderer.Render(canvas, viewport, _pending, TimeMap, TickSize, preview: true);
            canvas.Restore();
        }

        if (view.Pan > 0)
        {
            DrawHistoryBadge(canvas, viewport.PlotRight);
        }

        _lastViewport = viewport; // cache for drawing mouse-mapping
    }

    private void DrawPriceAxis(SKCanvas canvas, ChartViewport vp, float axisX)
    {
        using var grid = new SKPaint { Color = _renderer.Palette.GridLine.WithAlpha(110).ToSkColor(), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var text = TextPaint(10f);
        const int lines = 6;
        long span = vp.MaxPriceTicks - vp.MinPriceTicks;
        for (int i = 0; i <= lines; i++)
        {
            long priceTicks = vp.MinPriceTicks + span * i / lines;
            float y = vp.PriceToY(priceTicks);
            canvas.DrawLine(vp.PlotLeft, y, axisX, y, grid);
            decimal price = priceTicks * TickSize;
            canvas.DrawText(price.ToString("N2", CultureInfo.InvariantCulture), axisX + 6, y + 3.5f, text);
        }
    }

    private void DrawTimeAxis(SKCanvas canvas, ChartViewport vp, int first, int visible, bool footprint)
    {
        using var grid = new SKPaint { Color = _renderer.Palette.GridLine.WithAlpha(110).ToSkColor(), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var text = TextPaint(10f);
        float axisTop = vp.PlotBottom;
        canvas.DrawLine(vp.PlotLeft, axisTop, vp.PlotRight, axisTop, grid);

        int step = Math.Max(1, (int)Math.Ceiling(visible / Math.Max(1f, vp.PlotWidth / 80f)));
        for (int i = first; i < first + visible; i++)
        {
            if ((i - first) % step != 0) continue;
            float x = vp.BarCenterX(i);
            canvas.DrawLine(x, axisTop, x, axisTop + 4, grid);
            DateTime t = footprint ? _overlayData.Footprint[i].StartUtc : _bars[i].StartUtc;
            string label = t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            float tw = text.MeasureText(label);
            canvas.DrawText(label, x - tw / 2f, axisTop + 16f, text);
        }
    }

    private void DrawHistoryBadge(SKCanvas canvas, float plotRight)
    {
        var pal = _renderer.Palette;
        using var bg = new SKPaint { Color = pal.Background.WithAlpha(0xCC).ToSkColor(), IsAntialias = true };
        using var fg = new SKPaint { Color = pal.BearishCandle.ToSkColor(), IsAntialias = true, TextSize = 12 };
        const string text = "◂ history — drag right edge to resume live";
        float tw = fg.MeasureText(text);
        var rect = new SKRect(plotRight - tw - 24, 8, plotRight - 8, 28);
        canvas.DrawRoundRect(rect, 4, 4, bg);
        canvas.DrawText(text, rect.Left + 8, rect.Bottom - 6, fg);
    }

    private SKPaint TextPaint(float size) => new()
    {
        Color = _renderer.Palette.MutedText.ToSkColor(),
        IsAntialias = true,
        Typeface = SKTypeface.Default,
        TextSize = size,
    };
}
