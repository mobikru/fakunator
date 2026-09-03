using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core.DomainsManager;

namespace Fakunator.Views;

/// <summary>
/// Модальный диалог добавления аккаунта панели ISPmanager.
/// Возвращает IspAccount через свойство Result если юзер нажал OK.
/// </summary>
public class AddIspAccountDialog : Window
{
    public IspAccount? Result { get; private set; }

    private readonly TextBox _tbHost = new();
    private readonly TextBox _tbUser = new();
    private readonly PasswordBox _tbPass = new();
    private readonly TextBox _tbName = new();

    public AddIspAccountDialog()
    {
        Title = "Добавить аккаунт панели";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var panel = new StackPanel { Margin = new Thickness(20) };

        // ── Shortcut: подключиться через WebView2 (FirstVDS и подобные) ───
        var quickBtn = new Button
        {
            Content = "⚡  Подключить FirstVDS через браузер",
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12.5,
        };
        quickBtn.SetResourceReference(StyleProperty, "AccentBtn");
        quickBtn.Click += (_, _) =>
        {
            var dlg = new ConnectFirstVdsDialog();
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                Result = dlg.Result;
                DialogResult = true;
                Close();
            }
        };
        panel.Children.Add(quickBtn);

        var hint = new TextBlock
        {
            Text = "— или введи данные вручную (обычный ISPmanager с логином+паролем) —",
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 14),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(hint);

        panel.Children.Add(MakeLabel("Хост панели"));
        _tbHost.Text = "";
        panel.Children.Add(StyleInput(_tbHost));
        panel.Children.Add(MakeHint("panel.zomro.com или panel.host.ru:1501"));
        panel.Children.Add(MakeSpacer(12));

        panel.Children.Add(MakeLabel("Логин"));
        panel.Children.Add(StyleInput(_tbUser));
        panel.Children.Add(MakeSpacer(12));

        panel.Children.Add(MakeLabel("Пароль"));
        panel.Children.Add(StylePassword(_tbPass));
        panel.Children.Add(MakeSpacer(12));

        panel.Children.Add(MakeLabel("Заметка (опционально)"));
        panel.Children.Add(StyleInput(_tbName));
        panel.Children.Add(MakeHint("например: «main zomro» — для отображения в списке"));
        panel.Children.Add(MakeSpacer(18));

        // Buttons row
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var btnCancel = new Button { Content = "Отмена", Padding = new Thickness(16, 8, 16, 8), MinWidth = 100 };
        btnCancel.SetResourceReference(StyleProperty, "GhostBtn");
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button { Content = "Проверить и сохранить", Padding = new Thickness(16, 8, 16, 8), MinWidth = 200, Margin = new Thickness(8, 0, 0, 0) };
        btnOk.SetResourceReference(StyleProperty, "PrimaryBtn");
        btnOk.Click += (_, _) =>
        {
            var host = _tbHost.Text.Trim();
            var user = _tbUser.Text.Trim();
            var pass = _tbPass.Password;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("Заполните хост, логин и пароль.",
                    "Не все поля", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new IspAccount
            {
                Host = host,
                Username = user,
                Password = pass,
                DisplayName = string.IsNullOrWhiteSpace(_tbName.Text) ? user : _tbName.Text.Trim(),
                Provider = "ispmanager",
            };
            DialogResult = true;
            Close();
        };
        btnRow.Children.Add(btnCancel);
        btnRow.Children.Add(btnOk);
        panel.Children.Add(btnRow);

        Content = panel;
    }

    private static TextBlock MakeLabel(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return t;
    }
    private static TextBlock MakeHint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        return t;
    }
    private static UIElement MakeSpacer(double h) => new Border { Height = h };
    private static Control StyleInput(TextBox tb)
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
    private static Control StylePassword(PasswordBox pb)
    {
        pb.Height = 32;
        pb.Padding = new Thickness(10, 0, 10, 0);
        pb.VerticalContentAlignment = VerticalAlignment.Center;
        pb.FontSize = 12;
        pb.BorderThickness = new Thickness(1);
        pb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        pb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        pb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
        return pb;
    }
}
