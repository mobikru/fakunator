using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Fakunator.Controls;

public class ToggleSwitch : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            nameof(IsOn),
            typeof(bool),
            typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsOnChanged));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public event EventHandler? Toggled;

    private double _knobOffset; // 0 = off (left), 1 = on (right)

    public ToggleSwitch()
    {
        Width = 34;
        Height = 20;
        Cursor = Cursors.Hand;
        _knobOffset = 0;
        App.ThemeChanged += OnAppThemeChanged;
        Unloaded += (_, _) => App.ThemeChanged -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleSwitch ts)
        {
            ts.AnimateKnob((bool)e.NewValue);
            ts.Toggled?.Invoke(ts, EventArgs.Empty);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        IsOn = !IsOn;
        e.Handled = true;
    }

    private void AnimateKnob(bool on)
    {
        var target = on ? 1.0 : 0.0;
        var anim = new DoubleAnimation
        {
            From = _knobOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        anim.Completed += (_, _) =>
        {
            _knobOffset = target;
            InvalidateVisual();
        };

        // Use a composition clock for smooth animation
        var clock = anim.CreateClock();
        clock.CurrentTimeInvalidated += (_, _) =>
        {
            if (clock.CurrentProgress.HasValue)
            {
                var from = on ? 0.0 : 1.0;
                _knobOffset = from + (target - from) * clock.CurrentProgress.Value;
                InvalidateVisual();
            }
        };

        ApplyAnimationClock(IsOnProperty, null); // clear any old clock
        clock.Controller?.Begin();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Resolve theme colors
        var bg4Brush = Application.Current.TryFindResource("Bg4Brush") as SolidColorBrush;
        var accentBrush = Application.Current.TryFindResource("AccentBrush") as SolidColorBrush;
        var accent2Brush = Application.Current.TryFindResource("Accent2Brush") as SolidColorBrush;

        var offColor = bg4Brush?.Color ?? Color.FromRgb(0x23, 0x23, 0x29);
        var accentColor = accentBrush?.Color ?? Color.FromRgb(0x5e, 0x6a, 0xd2);
        var accent2Color = accent2Brush?.Color ?? Color.FromRgb(0x81, 0x8c, 0xf8);

        // Interpolate track color between off and on
        var t = _knobOffset;
        var trackColor = Color.FromArgb(
            255,
            (byte)(offColor.R + (accentColor.R - offColor.R) * t),
            (byte)(offColor.G + (accentColor.G - offColor.G) * t),
            (byte)(offColor.B + (accentColor.B - offColor.B) * t));

        Brush trackBrush;
        if (t > 0.5)
        {
            trackBrush = new LinearGradientBrush(accentColor, accent2Color, 0);
        }
        else
        {
            trackBrush = new SolidColorBrush(trackColor);
        }

        // Draw rounded track
        var trackRadius = h / 2.0;
        var trackRect = new Rect(0, 0, w, h);
        var trackGeometry = new RectangleGeometry(trackRect, trackRadius, trackRadius);
        dc.DrawGeometry(trackBrush, null, trackGeometry);

        // Draw white knob
        var knobDiameter = 16.0;
        var knobRadius = knobDiameter / 2.0;
        var margin = (h - knobDiameter) / 2.0;
        var minX = margin + knobRadius;
        var maxX = w - margin - knobRadius;
        var cx = minX + (maxX - minX) * _knobOffset;
        var cy = h / 2.0;

        dc.DrawEllipse(Brushes.White, null, new Point(cx, cy), knobRadius, knobRadius);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(34, 20);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return new Size(34, 20);
    }
}
