using FlowTerminal.Analytics.Bars;
using FlowTerminal.Analytics.OrderFlow;
using FlowTerminal.Analytics.Vwap;
using FlowTerminal.Charting;
using FlowTerminal.Charting.Overlays;
using SkiaSharp;
using Xunit;

namespace FlowTerminal.UiTests;

/// <summary>Headless render check for the order-flow suite overlays (blocks, divergence,
/// anchored VWAP): identity colors present, no red, and a visual preview dump.</summary>
public class OrderFlowOverlayRenderTests
{
    private static readonly DateTime T = new(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc);

    private static Bar MkBar(int i, long high, long low, long close) => new(
        BarKind.Time, T.AddMinutes(i), T.AddMinutes(i + 1),
        (high + low) / 2, high, low, close, 100, 60, 40, 10);

    [Fact]
    public void Overlays_Render_On_Identity_Colors_Without_Red()
    {
        const int w = 760, h = 460;
        var bars = new List<Bar>();
        long[] closes = { 990, 996, 1001, 994, 988, 992, 999, 1006, 1002, 1008, 1014, 1010 };
        long prev = 985;
        for (int i = 0; i < closes.Length; i++)
        {
            bars.Add(MkBar(i, Math.Max(prev, closes[i]) + 5, Math.Min(prev, closes[i]) - 5, closes[i]));
            prev = closes[i];
        }

        var vp = new ChartViewport(w, h, 960, 1040, 0, bars.Count);
        float X(DateTime t)
        {
            int idx = (int)Math.Clamp((t - T).TotalMinutes, 0, bars.Count - 1);
            return vp.BarCenterX(idx);
        }

        var blocks = new List<DeltaBlock>
        {
            new(0, T.AddMinutes(1), T.AddMinutes(3), 985, 1005, 220, 60, 0, 30, 40, 10),   // +delta green
            new(1, T.AddMinutes(5), T.AddMinutes(7), 982, 998, 40, 260, 0, 28, 55, -12),   // −delta purple
        };
        var signals = new List<DivergenceSignal>
        {
            new(1, DivergenceType.RegularBullish, DivergenceState.Confirmed, 4, 9, 11,
                T.AddMinutes(4), T.AddMinutes(9), 983, 997, -120, -30, 0.62, DivergenceStrength.Moderate),
        };
        var developing = new DivergenceSignal(0, DivergenceType.RegularBearish, DivergenceState.Developing, 7, 11, 11,
            T.AddMinutes(7), T.AddMinutes(11), 1011, 1019, 90, 25, 0.5, DivergenceStrength.Moderate);

        var avwap = new List<AnchoredVwapPoint>();
        for (int i = 0; i < bars.Count; i++) avwap.Add(new AnchoredVwapPoint(995 + i, 6));

        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(ChartPalette.Default.Background.ToSkColor());
        new CandlestickRenderer().Render(canvas, vp, bars);
        var r = new OrderFlowOverlayRenderer();
        r.RenderDeltaBlocks(canvas, vp, blocks, X);
        r.RenderAnchoredVwap(canvas, vp, avwap, new[] { 1.0, 2.0 });
        r.RenderDivergences(canvas, vp, signals, developing, X);

        bool green = false, purple = false, cyan = false, red = false;
        for (int x = 0; x < w; x += 2)
        for (int y = 0; y < h; y += 2)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Green > p.Red + 25 && p.Green > p.Blue + 10 && p.Green > 90) green = true;
            if (p.Blue > p.Green + 15 && p.Red > p.Green && p.Blue > 110) purple = true;
            if (p.Blue > 140 && p.Green > 120 && p.Red < 90) cyan = true;      // AVWAP accent
            if (p.Red > 180 && p.Green < 90 && p.Blue < 90) red = true;
        }

        Assert.True(green, "expected green (bullish divergence / +delta block)");
        Assert.True(purple, "expected light purple (bearish / −delta block)");
        Assert.True(cyan, "expected the anchored-VWAP accent line");
        Assert.False(red, "order-flow overlays must not use red");

        var dir = Environment.GetEnvironmentVariable("FT_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, "orderflow.png"), data.ToArray());
        }
    }
}
