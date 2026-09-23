using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace Fakunator.Core;

/// <summary>
/// XAML: Text="{core:Tr cleanup.idle.title}". Индексатор-биндинг к Loc.Instance —
/// переоценивается сам при смене языка (см. Loc.PropertyChanged("Item[]")).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TrExtension() { }
    public TrExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
