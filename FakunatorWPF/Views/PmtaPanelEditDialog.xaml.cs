using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core.Pmta;

namespace Fakunator.Views;

public partial class PmtaPanelEditDialog : Window
{
    public PmtaPanel? Result { get; private set; }
    private readonly PmtaPanel? _existing;

    public PmtaPanelEditDialog(PmtaPanel? existing)
    {
        InitializeComponent();
        _existing = existing;
        if (existing != null)
        {
            TxtHeader.Text = "Редактировать PMTA-панель";
            TbLabel.Text = existing.Label;
            TbHost.Text = existing.Host;
            TbPort.Text = existing.Port.ToString();
            CbScheme.SelectedIndex = existing.UseHttps ? 0 : 1;
            CbIgnoreCert.IsChecked = existing.IgnoreCertErrors;
            CbEnabled.IsChecked = existing.Enabled;
        }
        else
        {
            CbScheme.SelectedIndex = 0;
        }
    }

    private PmtaPanel? Build()
    {
        var host = TbHost.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(host))
        {
            TxtTest.Text = "⚠ Хост обязателен.";
            TxtTest.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            return null;
        }
        if (!int.TryParse(TbPort.Text, out var port) || port <= 0 || port > 65535)
        {
            TxtTest.Text = "⚠ Порт должен быть 1..65535.";
            TxtTest.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            return null;
        }
        return new PmtaPanel
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Label = TbLabel.Text?.Trim() ?? "",
            Host = host,
            Port = port,
            UseHttps = (CbScheme.SelectedItem as ComboBoxItem)?.Content?.ToString() == "https",
            IgnoreCertErrors = CbIgnoreCert.IsChecked == true,
            Enabled = CbEnabled.IsChecked == true,
        };
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var p = Build();
        if (p == null) return;
        TxtTest.Text = "Подключаюсь…";
        TxtTest.Foreground = (Brush)FindResource("Fg4Brush");
        try
        {
            using var client = new PmtaClient(p);
            var st = await client.GetStatusAsync();
            var info = st?.Data?.Mta?.Product;
            var traf = st?.Data?.Status?.Traffic?.LastMin?.Out?.Rcp ?? 0;
            TxtTest.Text = $"✅ OK · {info?.Name ?? "?"} {info?.Version ?? ""}\n" +
                           $"host: {st?.Data?.Mta?.FullHostName}  ·  сейчас {traf} rcp/мин";
            TxtTest.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        catch (Exception ex)
        {
            TxtTest.Text = "❌ Ошибка: " + ex.Message;
            TxtTest.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var p = Build();
        if (p == null) return;
        Result = p;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
