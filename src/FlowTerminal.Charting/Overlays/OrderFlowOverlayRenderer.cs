using FlowTerminal.Analytics.OrderFlow;
using FlowTerminal.Analytics.Vwap;
using SkiaSharp;

namespace FlowTerminal.Charting.Overlays;

/// <summary>
/// Draws the order-flow suite's chart overlays on the candle plot: delta blocks
/// (translucent green/purple regions by net delta, labels only when they fit — LOD),
/// delta-divergence lines (green bullish / light-purple bearish; dashed = developing,
/// solid = confirmed), and anchored VWAP with ±1σ/±2σ volume-weighted bands. Pure Skia
/// against caller-supplied coordinate maps, so it is headlessly testable and never
/// alters calculations — rendering detail only. Palette identity is preserved.
/// </summary>
public sealed class OrderFlowOverlayRenderer
{
    private readonly ChartPalette _palette;

    public OrderFlowOverlayRenderer(ChartPalette? palette = null) => _palette = palette ?? ChartPalette.Default;

    // ── Delta blocks ─────────────────────────────────────────────────────────

    public void RenderDeltaBlocks(
        SKCanvas canvas, ChartViewport vp, IReadOnlyList<DeltaBlock> blocks, Func<DateTime, float> x)
    {
        if (blocks.Count == 0) return;
        long maxAbs = 1;
        foreach (var b in blocks) maxAbs = Math.Max(maxAbs, Math.Abs(b.Delta));

        using var label = new SKPaint
        {
            IsAntialias = true, TextAlign = SKTextAlign.Center, TextSize = 10f,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
        };

        foreach (var b in blocks)
        {
            if (b.HighTicks < vp.MinPriceTicks || b.LowTicks >= vp.MaxPriceTicks) continue;
            float x0 = x(b.StartUtc), x1 = Math.Max(x(b.EndUtc), x0 + 2f);
            if (x1 < vp.PlotLeft || x0 > vp.PlotRight) continue;

            float top = vp.PriceToY(b.HighTicks) - 1;
            float bot = vp.PriceToY(b.LowTicks) + 1;
            var color = b.Delta >= 0 ? _palette.BidLiquidity : _palette.AskLiquidity;
            // Percentile-free normalization vs the visible max |delta|, floor for visibility.
            byte a = (byte)Math.Clamp(26 + 60 * Math.Abs(b.Delta) / (double)maxAbs, 26, 86);

            using var fill = new SKPaint { Color = color.WithAlpha(a).ToSkColor(), IsAntialias = true };
            using var edge = new SKPaint { Color = color.WithAlpha((byte)(a + 60)).ToSkColor(), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            var rect = new SKRect(x0, top, x1, bot);
            canvas.DrawRoundRect(rect, 2, 2, fill);
            canvas.DrawRoundRect(rect, 2, 2, edge);

            // LOD: net-delta label only when the block is comfortably large enough.
            if (rect.Width > 34 && rect.Height > 14)
            {
                label.Color = _palette.Text.ToSkColor();
                string txt = (b.Delta >= 0 ? "+" : "") + b.Delta.ToString("N0");
                canvas.DrawText(txt, rect.MidX, rect.MidY + 3.5f, label);
            }
        }
    }

    // ── Delta divergence ─────────────────────────────────────────────────────

    public void RenderDivergences(
        SKCanvas canvas, ChartViewport vp, IReadOnlyList<DivergenceSignal> confirmed,
        DivergenceSignal? developing, Func<DateTime, float> x)
    {
        using var label = new SKPaint
        {
            IsAntialias = true, TextAlign = SKTextAlign.Center, TextSize = 10f,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
        };

        foreach (var s in confirmed) Draw(canvas, vp, s, x, label, dashed: false);
        if (developing is { } dev) Draw(canvas, vp, dev, x, label, dashed: true);
    }

    private void Draw(SKCanvas canvas, ChartViewport vp, DivergenceSignal s, Func<DateTime, float> x, SKPaint label, bool dashed)
    {
        if (s.SecondPivotPriceTicks < vp.MinPriceTicks || s.SecondPivotPriceTicks >= vp.MaxPriceTicks) return;
        float x1 = x(s.FirstPivotTimeUtc), y1 = vp.PriceToY(s.FirstPivotPriceTicks);
        float x2 = x(s.SecondPivotTimeUtc), y2 = vp.PriceToY(s.SecondPivotPriceTicks);
        if (x2 < vp.PlotLeft || x1 > vp.PlotRight) return;

        var color = s.IsBullish ? _palette.BidLiquidity : _palette.AskLiquidity;
        using var line = new SKPaint
        {
            Color = color.WithAlpha(dashed ? (byte)150 : (byte)220).ToSkColor(),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            IsAntialias = true,
            PathEffect = dashed ? SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) : null,
        };
        canvas.DrawLine(x1, y1, x2, y2, line);
        line.PathEffect?.Dispose();

        using var dot = new SKPaint { Color = color.ToSkColor(), IsAntialias = true };
        canvas.DrawCircle(x1, y1, 2.6f, dot);
        canvas.DrawCircle(x2, y2, 2.6f, dot);

        label.Color = color.ToSkColor();
        string txt = s.Type switch
        {
            DivergenceType.RegularBullish => "Bull Div",
            DivergenceType.RegularBearish => "Bear Div",
            DivergenceType.HiddenBullish => "Hidden Bull",
            _ => "Hidden Bear",
        };
        if (dashed) txt += " (dev)";
        float ly = s.IsBullish ? y2 + 14f : y2 - 6f;
        canvas.DrawText(txt, x2, ly, label);
    }

    // ── Anchored VWAP ────────────────────────────────────────────────────────

    public void RenderAnchoredVwap(
        SKCanvas canvas, ChartViewport vp, IReadOnlyList<AnchoredVwapPoint> series, double[] bandMultipliers)
    {
        if (series.Count == 0) return;
        var accent = _palette.SelectedObject; // existing cyan accent — distinct from daily VWAP

        using var band = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        foreach (double k in bandMultipliers)
        {
            band.Color = accent.WithAlpha((byte)Math.Max(30, 90 - 25 * k)).ToSkColor();
            DrawSeries(canvas, vp, series, p => p.VwapTicks + k * p.StdDevTicks, band);
            DrawSeries(canvas, vp, series, p => p.VwapTicks - k * p.StdDevTicks, band);
        }

        using var main = new SKPaint { Color = accent.WithAlpha(230).ToSkColor(), Style = SKPaintStyle.Stroke, StrokeWidth = 1.7f, IsAntialias = true };
        DrawSeries(canvas, vp, series, p => p.VwapTicks, main);
    }

    private static void DrawSeries(
        SKCanvas canvas, ChartViewport vp, IReadOnlyList<AnchoredVwapPoint> series,
        Func<AnchoredVwapPoint, double> value, SKPaint paint)
    {
        using var path = new SKPath();
        bool started = false;
        int first = Math.Max(0, vp.FirstBarIndex);
        int last = Math.Min(series.Count - 1, vp.FirstBarIndex + vp.VisibleBarCount - 1);
        for (int i = first; i <= last; i++)
        {
            double v = value(series[i]);
            if (double.IsNaN(v)) { started = false; continue; }
            float px = vp.BarCenterX(i);
            float py = vp.PriceToY((long)Math.Round(v));
            if (!started) { path.MoveTo(px, py); started = true; }
            else path.LineTo(px, py);
        }
        canvas.DrawPath(path, paint);
    }
}
