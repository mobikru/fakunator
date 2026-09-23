using System.Windows;
using System.Windows.Controls;
using Fakunator.Core;

namespace Fakunator.Views;

/// <summary>
/// Диалог добавления домена: имя + опциональный IP (для master-зоны у панелей
/// которые требуют его при создании; для DNSmanager обычно необязательно).
/// </summary>
public class AddDomainDialog : Window
{
    public (string Domain, string? Ip) Result { get; private set; } = ("", null);

    private readonly TextBox _tbDomain = new();
    private readonly TextBox _tbIp = new();

    public AddDomainDialog()
    {
        Title = Loc.T("domains.addDialog.title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(Label(Loc.T("domains.addDialog.lblDomain")));
        panel.Children.Add(Style(_tbDomain));
        panel.Children.Add(Hint(Loc.T("domains.addDialog.hintDomain")));
        panel.Children.Add(Spacer(12));

        panel.Children.Add(Label(Loc.T("domains.addDialog.lblIp")));
        panel.Children.Add(Style(_tbIp));
        panel.Children.Add(Hint(Loc.T("domains.addDialog.hintIp")));
        panel.Children.Add(Spacer(18));

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Loc.T("domains.addDialog.btnCancel"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 100 };
        cancel.SetResourceReference(StyleProperty, "GhostBtn");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = Loc.T("domains.addDialog.btnAdd"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryBtn");
        ok.Click += (_, _) =>
        {
            var d = _tbDomain.Text.Trim().ToLowerInvariant();
            var ip = _tbIp.Text.Trim();
            if (string.IsNullOrEmpty(d))
            {
                MessageBox.Show(Loc.T("domains.addDialog.err.emptyDomainBody"), Loc.T("domains.addDialog.err.emptyDomainTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = (d, string.IsNullOrEmpty(ip) ? null : ip);
            DialogResult = true;
            Close();
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);

        Content = panel;
    }

    private static TextBlock Label(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }
    private static TextBlock Hint(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 10, Margin = new Thickness(0, 4, 0, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        return tb;
    }
    private static UIElement Spacer(double h) => new Border { Height = h };
    private static Control Style(TextBox tb)
    {
        tb.Height = 32;
        tb.Padding = new Thickness(10, 0, 10, 0);
        tb.VerticalContentAlignment = VerticalAlignment.Center;
        tb.FontSize = 12;
        tb.BorderThickness = new Thickness(1);
        tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
        return tb;
    }
}
