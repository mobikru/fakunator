using System.Windows;
using System.Windows.Media;

namespace Fakunator.Core;

/// <summary>Attached-свойство «иконка» для единого класса кнопок (ActionBtn*).
/// Отдельно от Tag/Content, чтобы не конфликтовать с их обычным использованием
/// (Tag часто несёт command-параметр, Content — текст кнопки).</summary>
public static class IconButtonHelper
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(IconButtonHelper),
            new FrameworkPropertyMetadata(null));

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    // Явные цвета hover/pressed для единого класса кнопок-действий (ActionBtn*) —
    // конкретные оттенки для каждого варианта (primary/neutral/danger), а не общий
    // прозрачный оверлей поверх любого фона (тот выглядел грязно на белых кнопках).
    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached("HoverBackground", typeof(Brush), typeof(IconButtonHelper));
    public static void SetHoverBackground(DependencyObject d, Brush? value) => d.SetValue(HoverBackgroundProperty, value);
    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);

    public static readonly DependencyProperty HoverBorderProperty =
        DependencyProperty.RegisterAttached("HoverBorder", typeof(Brush), typeof(IconButtonHelper));
    public static void SetHoverBorder(DependencyObject d, Brush? value) => d.SetValue(HoverBorderProperty, value);
    public static Brush? GetHoverBorder(DependencyObject d) => (Brush?)d.GetValue(HoverBorderProperty);

    public static readonly DependencyProperty PressedBackgroundProperty =
        DependencyProperty.RegisterAttached("PressedBackground", typeof(Brush), typeof(IconButtonHelper));
    public static void SetPressedBackground(DependencyObject d, Brush? value) => d.SetValue(PressedBackgroundProperty, value);
    public static Brush? GetPressedBackground(DependencyObject d) => (Brush?)d.GetValue(PressedBackgroundProperty);

    public static readonly DependencyProperty PressedBorderProperty =
        DependencyProperty.RegisterAttached("PressedBorder", typeof(Brush), typeof(IconButtonHelper));
    public static void SetPressedBorder(DependencyObject d, Brush? value) => d.SetValue(PressedBorderProperty, value);
    public static Brush? GetPressedBorder(DependencyObject d) => (Brush?)d.GetValue(PressedBorderProperty);
}
