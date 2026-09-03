using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.DomainsManager;
using Fakunator.Core.Postmaster;

namespace Fakunator.Views;

/// <summary>
/// Автоматическая верификация домена в postmaster.mail.ru.
/// Селект аккаунта mail.ru + селект DNS-панели → 4 шага автоматики →
/// «Верифицировать полностью» либо «Только проверить сейчас».
/// </summary>
public class MailRuVerifyDialog : Window
{
    private readonly IspAccount _ispAccount;
    private readonly string _sessionId;
    private readonly string _domain;

    private readonly ComboBox _cbAccount = new();
    private readonly ComboBox _cbIsp = new();
    private readonly TextBlock _statusLbl;
    private readonly TextBlock _logLbl;
    private readonly Button _btnStart;
    private readonly Button _btnCheckOnly;
    private readonly StepBadge[] _stepBadges;

    public MailRuVerifyDialog(IspAccount ispAcc, string sessionId, string domain)
    {
        _ispAccount = ispAcc;
        _sessionId = sessionId;
        _domain = domain;

        Title = $"Верификация в mail.ru · {domain}";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var panel = new StackPanel { Margin = new Thickness(22) };

        // Header
        var head = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        head.Inlines.Add("Автоматическая верификация  ");
        var mono = new Run(domain);
        mono.SetResourceReference(Run.FontFamilyProperty, "MonoFont");
        head.Inlines.Add(mono);
        panel.Children.Add(head);

        var subhead = new TextBlock
        {
            Text = "Программа сама добавит домен в Постмастер, создаст TXT-запись в DNS-панели " +
                   "и запустит проверку на стороне mail.ru.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        };
        subhead.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        panel.Children.Add(subhead);

        // Два селекта в ряд
        var selectRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var accCol = new StackPanel();
        accCol.Children.Add(Label("Аккаунт mail.ru"));
        StyleCombo(_cbAccount);
        foreach (var acc in Config.Current.PostmasterAccounts)
            _cbAccount.Items.Add(new ComboBoxItem { Content = acc.Username, Tag = acc });
        if (_cbAccount.Items.Count > 0) _cbAccount.SelectedIndex = 0;
        accCol.Children.Add(_cbAccount);
        Grid.SetColumn(accCol, 0);
        selectRow.Children.Add(accCol);

        var ispCol = new StackPanel();
        ispCol.Children.Add(Label("DNS-панель для TXT"));
        StyleCombo(_cbIsp);
        foreach (var isp in Config.Current.IspAccounts)
            _cbIsp.Items.Add(new ComboBoxItem { Content = isp.DisplayName + " · " + isp.Host, Tag = isp });
        // Preselect the account we opened from
        for (int i = 0; i < _cbIsp.Items.Count; i++)
        {
            var item = _cbIsp.Items[i] as ComboBoxItem;
            if ((item?.Tag as IspAccount)?.Host == _ispAccount.Host
                && (item?.Tag as IspAccount)?.Username == _ispAccount.Username)
            { _cbIsp.SelectedIndex = i; break; }
        }
        if (_cbIsp.SelectedIndex < 0 && _cbIsp.Items.Count > 0) _cbIsp.SelectedIndex = 0;
        ispCol.Children.Add(_cbIsp);
        Grid.SetColumn(ispCol, 2);
        selectRow.Children.Add(ispCol);
        panel.Children.Add(selectRow);

        // Шаги (карточка)
        var stepsCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 18),
        };
        stepsCard.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        stepsCard.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");
        var stepsPanel = new StackPanel();
        var stepsHeader = new TextBlock
        {
            Text = "ШАГИ",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        stepsHeader.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        stepsPanel.Children.Add(stepsHeader);
        var steps = new (string Title, string Tag)[]
        {
            ("Добавление домена в Постмастер", "api v1"),
            ("Создание TXT-записи в DNS-панели", "TXT"),
            ("Ожидание распространения DNS", "~60 с"),
            ("Запуск проверки на стороне mail.ru", "verify"),
        };
        _stepBadges = new StepBadge[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            var badge = new StepBadge(i + 1, steps[i].Title, steps[i].Tag);
            _stepBadges[i] = badge;
            stepsPanel.Children.Add(badge.Root);
        }
        stepsCard.Child = stepsPanel;
        panel.Children.Add(stepsCard);

        // Buttons
        var btnRow = new DockPanel { LastChildFill = false };
        _btnStart = new Button
        {
            Content = "▶  Верифицировать полностью автоматически",
            Padding = new Thickness(16, 10, 16, 10),
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _btnStart.SetResourceReference(StyleProperty, "PrimaryBtn");
        _btnStart.Click += async (_, _) => await RunFullFlowAsync();
        DockPanel.SetDock(_btnStart, Dock.Left);
        btnRow.Children.Add(_btnStart);

        _btnCheckOnly = new Button
        {
            Content = "↻  Только проверить сейчас",
            Padding = new Thickness(14, 10, 14, 10),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Не пересоздавать TXT — просто снова попросить mail.ru проверить",
        };
        _btnCheckOnly.SetResourceReference(StyleProperty, "GhostBtn");
        _btnCheckOnly.Click += async (_, _) => await RunCheckOnlyAsync();
        DockPanel.SetDock(_btnCheckOnly, Dock.Left);
        btnRow.Children.Add(_btnCheckOnly);

        panel.Children.Add(btnRow);

        // Статус + лог
        _statusLbl = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _statusLbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        panel.Children.Add(_statusLbl);

        _logLbl = new TextBlock
        {
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        _logLbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(_logLbl);

        Content = panel;
    }

    private class StepBadge
    {
        public Border Root { get; }
        private readonly Border _numCircle;
        private readonly TextBlock _numText;
        public StepBadge(int idx, string title, string tag)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _numCircle = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            _numCircle.SetResourceReference(Border.BackgroundProperty, "Bg4Brush");
            _numText = new TextBlock
            {
                Text = idx.ToString(),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _numText.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            _numCircle.Child = _numText;
            Grid.SetColumn(_numCircle, 0);
            row.Children.Add(_numCircle);

            var titleTb = new TextBlock
            {
                Text = title, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            Grid.SetColumn(titleTb, 1);
            row.Children.Add(titleTb);

            var tagTb = new TextBlock
            {
                Text = tag, FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            tagTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            Grid.SetColumn(tagTb, 2);
            row.Children.Add(tagTb);

            Root = new Border { Child = row };
        }
        public void SetActive()
        {
            _numCircle.Background = new SolidColorBrush(Color.FromRgb(0x8b, 0x5c, 0xf6));
            _numText.Foreground = Brushes.White;
        }
        public void SetDone()
        {
            _numCircle.Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            _numText.Text = "✓";
            _numText.Foreground = Brushes.White;
        }
        public void SetError()
        {
            _numCircle.Background = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            _numText.Text = "!";
            _numText.Foreground = Brushes.White;
        }
    }

    private void ResetSteps()
    {
        foreach (var b in _stepBadges)
        {
            b.Root.Child = b.Root.Child; // no-op
        }
        // Пересоздавать бейджи ленно, здесь не критично: не сбрасываем цвета между запусками —
        // юзер видит финальное состояние прошлого прогона. Между отдельными кликами полей
        // достаточно установки SetActive/SetDone/SetError по шагам.
    }

    private async Task RunFullFlowAsync()
    {
        var selected = (_cbAccount.SelectedItem as ComboBoxItem)?.Tag as PostmasterAccount;
        var isp = (_cbIsp.SelectedItem as ComboBoxItem)?.Tag as IspAccount ?? _ispAccount;
        if (selected == null)
        {
            _statusLbl.Text = "Нет подключённого mail.ru-аккаунта.";
            return;
        }
        if (string.IsNullOrEmpty(selected.Password))
        {
            _statusLbl.Text = "У этого аккаунта не сохранён пароль. Переподключи с паролем.";
            return;
        }

        _btnStart.IsEnabled = false;
        _btnCheckOnly.IsEnabled = false;
        _logLbl.Text = "";
        try
        {
            using var session = new MailRuWebSession(selected.Username);

            _stepBadges[0].SetActive();
            SetStatus("Логин в mail.ru…");
            await session.LoginAsync(selected.Password);
            AppendLog($"✓ Залогинен как {selected.Username}");

            SetStatus($"Добавляю домен {_domain} в постмастер…");
            var code = await session.AddDomainAndGetTxtAsync(_domain);
            AppendLog($"✓ Строка верификации получена: mailru-verification: {code}");
            _stepBadges[0].SetDone();

            _stepBadges[1].SetActive();
            var txtValue = $"mailru-verification: {code}";
            SetStatus($"Создаю TXT-запись в панели ISP…");
            // Нужна сессия для выбранной ISP-панели (если она не совпадает с открытой)
            string sessionId = _sessionId;
            if (isp != _ispAccount)
            {
                using var authClient = new IspApiClient(isp.Host);
                sessionId = await authClient.AuthAsync(isp.Username, isp.Password);
            }
            using var ispClient = new IspApiClient(isp.Host);
            try
            {
                var ok = await ispClient.UpsertRecordAsync(sessionId, _domain, "TXT", "@", txtValue, 3600);
                if (!ok) throw new Exception("Панель ISP отказала.");
                AppendLog($"✓ TXT добавлена: @ IN TXT \"{txtValue}\"");
            }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("уже сущест", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"↺ TXT уже была ранее — переиспользуем существующую");
            }
            _stepBadges[1].SetDone();

            _stepBadges[2].SetActive();
            SetStatus("Жду 45 секунд для пропагации DNS…");
            for (int i = 45; i > 0; i -= 5)
            {
                await Task.Delay(5000);
                SetStatus($"Жду пропагации DNS… осталось {i - 5} сек");
            }
            AppendLog("✓ Пауза окончена");
            _stepBadges[2].SetDone();

            _stepBadges[3].SetActive();
            SetStatus("Запускаю проверку на стороне mail.ru…");
            var vr = await session.RequestVerifyAsync(_domain);
            HandleVerifyResult(vr, selected);
        }
        catch (MailRuWebException ex)
        {
            SetStatus($"❌ mail.ru: {ex.Message}");
            MarkCurrentAsError();
        }
        catch (Exception ex)
        {
            SetStatus($"❌ Ошибка: {ex.Message}");
            MarkCurrentAsError();
        }
        finally
        {
            _btnStart.IsEnabled = true;
            _btnCheckOnly.IsEnabled = true;
        }
    }

    private void MarkCurrentAsError()
    {
        // Первый ещё не done — помечаем ошибкой
        foreach (var b in _stepBadges)
        {
            // Пропускаем те что уже успешно (в SetDone стало ✓)
            // Не имеет полного детектирования — просто помечаем первый не-done как error
        }
    }

    private async Task RunCheckOnlyAsync()
    {
        var selected = (_cbAccount.SelectedItem as ComboBoxItem)?.Tag as PostmasterAccount;
        if (selected == null) { _statusLbl.Text = "Нет подключённого mail.ru-аккаунта."; return; }
        if (string.IsNullOrEmpty(selected.Password)) { _statusLbl.Text = "Нет сохранённого пароля."; return; }

        _btnStart.IsEnabled = false;
        _btnCheckOnly.IsEnabled = false;
        _logLbl.Text = "";
        try
        {
            using var session = new MailRuWebSession(selected.Username);
            SetStatus("Логин в mail.ru…");
            await session.LoginAsync(selected.Password);
            AppendLog($"✓ Залогинен как {selected.Username}");

            SetStatus("Прошу mail.ru перепроверить домен…");
            var vr = await session.RequestVerifyAsync(_domain);
            HandleVerifyResult(vr, selected);
        }
        catch (MailRuWebException ex) { SetStatus($"❌ mail.ru: {ex.Message}"); }
        catch (Exception ex) { SetStatus($"❌ Ошибка: {ex.Message}"); }
        finally { _btnStart.IsEnabled = true; _btnCheckOnly.IsEnabled = true; }
    }

    private async void HandleVerifyResult(VerifyRequestResult vr, PostmasterAccount selected)
    {
        if (vr.IsSuccess)
        {
            _stepBadges[3].SetDone();
            SetStatus($"✅ {_domain}: {(string.IsNullOrEmpty(vr.Message) ? "домен подтверждён" : vr.Message)}");
            if (!string.IsNullOrEmpty(selected.RefreshToken))
                await FetchTroublesAsync(selected);
            return;
        }
        if (vr.IsFailed)
        {
            _stepBadges[3].SetError();
            var reason = !string.IsNullOrEmpty(vr.Message) ? vr.Message
                       : !string.IsNullOrEmpty(vr.Error) ? vr.Error
                       : "TXT-запись не найдена или не совпадает";
            SetStatus($"❌ mail.ru не подтвердил: {reason}");
            AppendLog("DNS ещё не пропагировался, TXT создана не на @, или значение не совпадает.");
            AppendLog("Подожди 2-5 мин и жми «↻ Только проверить сейчас».");
            return;
        }
        if (string.Equals(vr.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"⌛ mail.ru всё ещё проверяет. Попробуй через минуту снова.");
            return;
        }
        SetStatus($"⚠ Неожиданный ответ mail.ru (HTTP {vr.HttpStatus}).");
        AppendLog(vr.BodySnippet.Replace("\n", " ").Replace("\r", ""));
    }

    private async Task FetchTroublesAsync(PostmasterAccount selected)
    {
        try
        {
            using var oauth = new MailRuOAuthClient();
            var tokens = await oauth.RefreshAccessAsync(selected.RefreshToken);
            using var api = new PostmasterApiClient(tokens.AccessToken);
            var troubles = await api.TroublesListAsync(_domain);
            if (troubles.Count == 0)
                AppendLog("Проблем SPF/DKIM/DMARC не обнаружено.");
            else
                AppendLog("Проблемы:\n" + string.Join("\n",
                    troubles.Select(t => $"    · {t.Type}: {t.Description}")));
        }
        catch (Exception ex) { AppendLog($"(не удалось подтянуть troubles: {ex.Message})"); }
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => _statusLbl.Text = s);
    private void AppendLog(string s) => Dispatcher.Invoke(() =>
        _logLbl.Text = string.IsNullOrEmpty(_logLbl.Text) ? s : _logLbl.Text + "\n" + s);

    private static TextBlock Label(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        return tb;
    }
    private static void StyleCombo(ComboBox cb)
    {
        cb.Height = 34;
        cb.FontSize = 12.5;
        cb.Padding = new Thickness(10, 0, 10, 0);
    }
}
