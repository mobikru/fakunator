using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Fakunator.Controls;

public class SparklineChart : FrameworkElement
{
    private const int MaxPoints = 60;
    private readonly Queue<double> _points = new();

    public SparklineChart()
    {
        MinHeight = 100;
        ClipToBounds = true;
        App.ThemeChanged += OnAppThemeChanged;
        Unloaded += (_, _) => App.ThemeChanged -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public void AddPoint(double value)
    {
        _points.Enqueue(value);
        while (_points.Count > MaxPoints)
            _points.Dequeue();
        InvalidateVisual();
    }

    public void Clear()
    {
        _points.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Resolve theme brushes
        var accentBrush = Application.Current.TryFindResource("AccentBrush") as SolidColorBrush;
        var bg3Brush = Application.Current.TryFindResource("Bg3Brush") as SolidColorBrush;
        var fg4Brush = Application.Current.TryFindResource("Fg4Brush") as SolidColorBrush;

        var accentColor = accentBrush?.Color ?? Color.FromRgb(0x5e, 0x6a, 0xd2);
        var gridColor = fg4Brush?.Color ?? Color.FromRgb(0x52, 0x52, 0x5b);

        // Margins inside the chart area — top needs room for glow dot (radius 6)
        const double padTop = 14;
        const double padBottom = 6;
        double chartH = h - padTop - padBottom;
        if (chartH < 10) return;

        // Draw subtle grid lines at 25%, 50%, 75%
        var gridLineColor = Color.FromArgb(30, gridColor.R, gridColor.G, gridColor.B);
        var gridPen = new Pen(new SolidColorBrush(gridLineColor), 0.5);
        gridPen.Freeze();

        foreach (var frac in new[] { 0.25, 0.50, 0.75 })
        {
            double y = padTop + chartH * (1.0 - frac);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (_points.Count < 2) return;

        // Find max for Y-axis scaling
        double maxVal = 0;
        foreach (var v in _points)
        {
            if (v > maxVal) maxVal = v;
        }
        if (maxVal <= 0) maxVal = 1;

        // Build points array
        var pts = new List<double>(_points);
        int count = pts.Count;
        double stepX = w / (MaxPoints - 1);
        // Offset so the latest point is at the right edge
        double startX = w - (count - 1) * stepX;

        // Build the line geometry
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            double x0 = startX;
            double y0 = padTop + chartH * (1.0 - pts[0] / maxVal);
            ctx.BeginFigure(new Point(x0, y0), false, false);

            for (int i = 1; i < count; i++)
            {
                double x = startX + i * stepX;
                double y = padTop + chartH * (1.0 - pts[i] / maxVal);
                ctx.LineTo(new Point(x, y), true, true);
            }
        }
        lineGeo.Freeze();

        // Build the filled area geometry (same path, closed to bottom)
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            double x0 = startX;
            double y0 = padTop + chartH * (1.0 - pts[0] / maxVal);
            ctx.BeginFigure(new Point(x0, padTop + chartH), true, true);
            ctx.LineTo(new Point(x0, y0), true, false);

            for (int i = 1; i < count; i++)
            {
                double x = startX + i * stepX;
                double y = padTop + chartH * (1.0 - pts[i] / maxVal);
                ctx.LineTo(new Point(x, y), true, true);
            }

            // Close to bottom-right
            double lastX = startX + (count - 1) * stepX;
            ctx.LineTo(new Point(lastX, padTop + chartH), true, false);
        }
        fillGeo.Freeze();

        // Draw filled area (accent at 10% opacity)
        var fillColor = Color.FromArgb(26, accentColor.R, accentColor.G, accentColor.B);
        var fillBrush = new SolidColorBrush(fillColor);
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fillGeo);

        // Draw line on top (accent, 1.5px)
        var lineBrush = new SolidColorBrush(accentColor);
        lineBrush.Freeze();
        var linePen = new Pen(lineBrush, 1.5);
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, lineGeo);

        // Draw last-point dot with glow
        double lastPtX = startX + (count - 1) * stepX;
        double lastPtY = padTop + chartH * (1.0 - pts[count - 1] / maxVal);
        var center = new Point(lastPtX, lastPtY);

        // Glow (larger, semi-transparent)
        var glowColor = Color.FromArgb(50, accentColor.R, accentColor.G, accentColor.B);
        var glowBrush = new SolidColorBrush(glowColor);
        glowBrush.Freeze();
        dc.DrawEllipse(glowBrush, null, center, 6, 6);

        // Dot
        var dotBrush = new SolidColorBrush(accentColor);
        dotBrush.Freeze();
        dc.DrawEllipse(dotBrush, null, center, 3, 3);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsPositiveInfinity(availableSize.Width) ? 300 : availableSize.Width;
        double h = double.IsPositiveInfinity(availableSize.Height) ? 100 : availableSize.Height;
        return new Size(w, Math.Max(h, MinHeight));
    }
}
