using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Fakunator.Controls;

public class ParticleBackground : FrameworkElement
{
    private const int ParticleCount = 70;
    private const double ConnectionDistance = 165.0;
    private const double MouseAttractionRadius = 220.0;
    private const double MouseAttractionStrength = 0.0003;
    private const double BaseSpeed = 0.15;
    private const double MinRadius = 1.5;
    private const double MaxRadius = 3.5;

    private readonly Particle[] _particles;
    private readonly Random _rng = new();
    private Point _mousePos = new(-1000, -1000);
    private bool _mouseInside;
    private bool _rendering;

    public ParticleBackground()
    {
        ClipToBounds = true;
        _particles = new Particle[ParticleCount];

        for (int i = 0; i < ParticleCount; i++)
        {
            _particles[i] = new Particle
            {
                X = _rng.NextDouble() * 1200,
                Y = _rng.NextDouble() * 900,
                Vx = (_rng.NextDouble() - 0.5) * BaseSpeed * 2,
                Vy = (_rng.NextDouble() - 0.5) * BaseSpeed * 2,
                Radius = MinRadius + _rng.NextDouble() * (MaxRadius - MinRadius),
                Alpha = 0.15 + _rng.NextDouble() * 0.45
            };
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Scatter particles across actual size
        var w = ActualWidth > 0 ? ActualWidth : 1200;
        var h = ActualHeight > 0 ? ActualHeight : 900;
        for (int i = 0; i < ParticleCount; i++)
        {
            _particles[i].X = _rng.NextDouble() * w;
            _particles[i].Y = _rng.NextDouble() * h;
        }

        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_rendering)
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.GetPosition(this);
        _mouseInside = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        // Allow hit testing so we receive mouse events
        return new PointHitTestResult(this, hitTestParameters.HitPoint);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;

        UpdatePhysics();
        InvalidateVisual();
    }

    private void UpdatePhysics()
    {
        var w = ActualWidth;
        var h = ActualHeight;

        for (int i = 0; i < ParticleCount; i++)
        {
            ref var p = ref _particles[i];

            // Mouse attraction
            if (_mouseInside)
            {
                var dx = _mousePos.X - p.X;
                var dy = _mousePos.Y - p.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < MouseAttractionRadius && dist > 1)
                {
                    var force = MouseAttractionStrength * (1.0 - dist / MouseAttractionRadius);
                    p.Vx += dx / dist * force;
                    p.Vy += dy / dist * force;
                }
            }

            // Clamp velocity
            var speed = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            if (speed > BaseSpeed * 3)
            {
                p.Vx = p.Vx / speed * BaseSpeed * 3;
                p.Vy = p.Vy / speed * BaseSpeed * 3;
            }

            p.X += p.Vx;
            p.Y += p.Vy;

            // Edge wrap
            if (p.X < -10) p.X = w + 10;
            else if (p.X > w + 10) p.X = -10;
            if (p.Y < -10) p.Y = h + 10;
            else if (p.Y > h + 10) p.Y = -10;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // Resolve accent color from theme
        var accentBrush = Application.Current.TryFindResource("AccentBrush") as SolidColorBrush;
        var accent2Brush = Application.Current.TryFindResource("Accent2Brush") as SolidColorBrush;
        var accentColor = accentBrush?.Color ?? Color.FromRgb(0x5e, 0x6a, 0xd2);
        var accent2Color = accent2Brush?.Color ?? Color.FromRgb(0x81, 0x8c, 0xf8);

        // Draw connection lines using StreamGeometry for performance
        for (int i = 0; i < ParticleCount; i++)
        {
            for (int j = i + 1; j < ParticleCount; j++)
            {
                var dx = _particles[i].X - _particles[j].X;
                var dy = _particles[i].Y - _particles[j].Y;
                var distSq = dx * dx + dy * dy;
                var maxDistSq = ConnectionDistance * ConnectionDistance;

                if (distSq < maxDistSq)
                {
                    var dist = Math.Sqrt(distSq);
                    var alpha = (byte)(40 * (1.0 - dist / ConnectionDistance));
                    var lineColor = Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B);
                    var pen = new Pen(new SolidColorBrush(lineColor), 0.8);
                    pen.Freeze();

                    dc.DrawLine(pen,
                        new Point(_particles[i].X, _particles[i].Y),
                        new Point(_particles[j].X, _particles[j].Y));
                }
            }
        }

        // Draw particles
        for (int i = 0; i < ParticleCount; i++)
        {
            ref var p = ref _particles[i];
            var color = (i % 2 == 0) ? accentColor : accent2Color;
            var dotAlpha = (byte)(255 * p.Alpha);
            var dotColor = Color.FromArgb(dotAlpha, color.R, color.G, color.B);
            var brush = new SolidColorBrush(dotColor);
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Radius, p.Radius);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill available space
        return new Size(
            double.IsPositiveInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsPositiveInfinity(availableSize.Height) ? 600 : availableSize.Height);
    }

    private struct Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Radius;
        public double Alpha;
    }
}
