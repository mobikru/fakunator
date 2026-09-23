using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fakunator.Controls;

/// <summary>
/// Круглый флаг из пака circle-flags (HatScripts, MIT) — векторный, поэтому любые
/// два флага всегда пиксель-в-пиксель одного размера и без размытия при масштабе.
/// Данные путей скопированы из исходных SVG как есть (SVG path mini-language
/// для M/L/H/V/Z и их relative-вариантов совпадает с WPF Path mini-language).
/// </summary>
public partial class FlagIcon : UserControl
{
    public static readonly DependencyProperty CountryProperty = DependencyProperty.Register(
        nameof(Country), typeof(string), typeof(FlagIcon),
        new PropertyMetadata("ru", OnCountryChanged));

    public string Country
    {
        get => (string)GetValue(CountryProperty);
        set => SetValue(CountryProperty, value);
    }

    public FlagIcon()
    {
        InitializeComponent();
        Build(Country);
    }

    private static void OnCountryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlagIcon icon) icon.Build((string)e.NewValue);
    }

    private void Build(string country)
    {
        RootCanvas.Children.Clear();
        var paths = country?.ToLowerInvariant() switch
        {
            "gb" => GbPaths,
            _ => RuPaths,
        };
        foreach (var (data, fill) in paths)
        {
            RootCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            });
        }
    }

    private static readonly (string Data, string Fill)[] RuPaths =
    [
        ("M512 170v172l-256 32L0 342V170l256-32z", "#0052b4"),
        ("M512 0v170H0V0Z", "#eeeeee"),
        ("M512 342v170H0V342Z", "#d80027"),
    ];

    private static readonly (string Data, string Fill)[] GbPaths =
    [
        ("m0 0 8 22-8 23v23l32 54-32 54v32l32 48-32 48v32l32 54-32 54v68l22-8 23 8h23l54-32 54 32h32l48-32 48 32h32l54-32 54 32h68l-8-22 8-23v-23l-32-54 32-54v-32l-32-48 32-48v-32l-32-54 32-54V0l-22 8-23-8h-23l-54 32-54-32h-32l-48 32-48-32h-32l-54 32L68 0H0z", "#eeeeee"),
        ("M336 0v108L444 0Zm176 68L404 176h108zM0 176h108L0 68ZM68 0l108 108V0Zm108 512V404L68 512ZM0 444l108-108H0Zm512-108H404l108 108Zm-68 176L336 404v108z", "#0052b4"),
        ("M0 0v45l131 131h45L0 0zm208 0v208H0v96h208v208h96V304h208v-96H304V0h-96zm259 0L336 131v45L512 0h-45zM176 336 0 512h45l131-131v-45zm160 0 176 176v-45L381 336h-45z", "#d80027"),
    ];
}
