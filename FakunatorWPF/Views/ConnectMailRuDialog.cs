using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Fakunator.Core;
using Fakunator.Core.Postmaster;

namespace Fakunator.Views;

/// <summary>
/// Диалог подключения аккаунта mail.ru к Postmaster.
///
/// Просим две вещи:
///  1. Логин + пароль mail.ru — для веб-сессии (добавление доменов + верификация:
///     публичный OAuth API этого не делает, всё только через веб-формы с CSRF).
///  2. refresh_token (Manual OAuth) — для API-статистики (reg-list, troubles, stats).
///     Опционально: если пропустить, всё равно работает через веб-сессию.
///
/// Юзер явно согласился хранить пароль mail.ru в config.json — см. memory.
/// </summary>
public class ConnectMailRuDialog : Window
{
    public PostmasterAccount? Result { get; private set; }

    private const string OAuthUrl =
        "https://o2.mail.ru/login" +
        "?client_id=postmaster_api_client" +
        "&response_type=code" +
        "&state=fakunator" +
        "&redirect_uri=https%3A%2F%2Fpostmaster.mail.ru%2Fext-api%2Foauth%2F";

    private readonly TextBox _tbUser = new();
    private readonly PasswordBox _pbPass = new();
    private readonly TextBox _tbToken = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 70,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _statusLbl = new()
    {
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly Button _btnOk;

    public ConnectMailRuDialog()
    {
        Title = Loc.T("domains.connectMailRuDialog.title");
        Width = 580;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var panel = new StackPanel { Margin = new Thickness(20) };

        var title = new TextBlock
        {
            Text = Loc.T("domains.connectMailRuDialog.heading"),
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        panel.Children.Add(title);

        var hint = new TextBlock
        {
            Text = Loc.T("domains.connectMailRuDialog.hint"),
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(hint);

        // ── Шаг 1: логин + пароль ─────────────────────────────────
        panel.Children.Add(Section(Loc.T("domains.connectMailRuDialog.section1")));
        panel.Children.Add(Label(Loc.T("domains.connectMailRuDialog.lblEmail")));
        panel.Children.Add(StyleInput(_tbUser));
        panel.Children.Add(Spacer(8));
        panel.Children.Add(Label(Loc.T("domains.connectMailRuDialog.lblPassword")));
        panel.Children.Add(StylePasswordInput(_pbPass));
        panel.Children.Add(Spacer(14));

        // ── Шаг 2: refresh_token (опционально) ────────────────────
        panel.Children.Add(Section(Loc.T("domains.connectMailRuDialog.section2")));
        var apiHint = new TextBlock
        {
            Text = Loc.T("domains.connectMailRuDialog.apiHint"),
            FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        apiHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(apiHint);

        var btnOpen = new Button
        {
            Content = Loc.T("domains.connectMailRuDialog.btnOpenBrowser"),
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 8),
        };
        btnOpen.SetResourceReference(StyleProperty, "GhostBtn");
        btnOpen.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(OAuthUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("domains.connectMailRuDialog.errOpenBrowserTitle")); }
        };
        panel.Children.Add(btnOpen);

        panel.Children.Add(Label(Loc.T("domains.connectMailRuDialog.lblToken")));
        panel.Children.Add(StyleBigInput(_tbToken));

        _statusLbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        panel.Children.Add(_statusLbl);
        panel.Children.Add(Spacer(14));

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Loc.T("domains.connectMailRuDialog.btnCancel"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 100 };
        cancel.SetResourceReference(StyleProperty, "GhostBtn");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _btnOk = new Button { Content = Loc.T("domains.connectMailRuDialog.btnSave"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 200, Margin = new Thickness(8, 0, 0, 0) };
        _btnOk.SetResourceReference(StyleProperty, "PrimaryBtn");
        _btnOk.Click += async (_, _) => await SaveAsync();
        row.Children.Add(cancel);
        row.Children.Add(_btnOk);
        panel.Children.Add(row);

        Content = panel;
    }

    private async Task SaveAsync()
    {
        var user = _tbUser.Text.Trim();
        var pass = _pbPass.Password;
        var rawToken = _tbToken.Text.Trim();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            _statusLbl.Text = Loc.T("domains.connectMailRuDialog.errEmptyFields");
            return;
        }

        _btnOk.IsEnabled = false;
        _statusLbl.Text = Loc.T("domains.connectMailRuDialog.checkingLogin");
        try
        {
            using var session = new MailRuWebSession(user);
            await session.LoginAsync(pass);
        }
        catch (Exception ex)
        {
            _statusLbl.Text = string.Format(Loc.T("domains.connectMailRuDialog.loginFailed"), ex.Message);
            _btnOk.IsEnabled = true;
            return;
        }

        var refresh = "";
        if (!string.IsNullOrEmpty(rawToken))
        {
            refresh = ExtractRefreshToken(rawToken);
            if (!string.IsNullOrEmpty(refresh))
            {
                _statusLbl.Text = Loc.T("domains.connectMailRuDialog.checkingToken");
                try
                {
                    using var oauth = new MailRuOAuthClient();
                    var tokens = await oauth.RefreshAccessAsync(refresh);
                    if (!string.IsNullOrEmpty(tokens.RefreshToken))
                        refresh = tokens.RefreshToken;
                }
                catch (Exception ex)
                {
                    _statusLbl.Text = string.Format(Loc.T("domains.connectMailRuDialog.tokenInvalid"), ex.Message);
                    // не блокируем — юзер может пересохранить с рабочим токеном позже
                    refresh = "";
                }
            }
        }

        Result = new PostmasterAccount
        {
            Username = user,
            Password = pass,
            RefreshToken = refresh,
            DisplayName = user,
        };
        DialogResult = true;
        Close();
    }

    private static string ExtractRefreshToken(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
                    return rt.GetString() ?? "";
            }
            catch { }
        }
        return raw.Trim('"', '\'', ' ', '\r', '\n');
    }

    private static TextBlock Section(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        return tb;
    }
    private static TextBlock Label(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }
    private static UIElement Spacer(double h) => new Border { Height = h };
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
    private static Control StylePasswordInput(PasswordBox pb)
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
    private static Control StyleBigInput(TextBox tb)
    {
        tb.Padding = new Thickness(10, 8, 10, 8);
        tb.FontSize = 11;
        tb.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        tb.BorderThickness = new Thickness(1);
        tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
        return tb;
    }
}
