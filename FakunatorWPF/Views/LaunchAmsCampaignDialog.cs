using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core.AmsApi;

namespace Fakunator.Views;

/// <summary>
/// Менеджер запуска компании: юзер выбирает учётку отправителя, список рассылки,
/// письмо и профиль отправки → жмёт «Создать» (или «Создать и запустить»).
/// Под капотом — <c>addMailing</c> + опциональный <c>startMailing</c>.
/// </summary>
public class LaunchAmsCampaignDialog : Window
{
    private readonly AmsApiClient _api;
    private readonly List<AmsSenderAccount> _senders;
    private readonly List<AmsMailingList> _lists;
    private readonly List<AmsMessage> _messages;
    private readonly List<AmsDeliveryPreset> _presets;

    private readonly TextBox _tbName = new();
    private readonly ComboBox _cbType = new();
    private readonly ComboBox _cbSender = new();
    private readonly ComboBox _cbList = new();
    private readonly ComboBox _cbMessage = new();
    private readonly ComboBox _cbPreset = new();
    private readonly CheckBox _cbAutostart = new()
    { Content = "Сразу запустить рассылку после создания", IsChecked = true };
    private readonly TextBlock _status = new()
    { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _btnGo;

    public LaunchAmsCampaignDialog(
        AmsApiClient api,
        List<AmsSenderAccount> senders,
        List<AmsMailingList> lists,
        List<AmsMessage> messages,
        List<AmsDeliveryPreset> presets)
    {
        _api = api;
        _senders = senders;
        _lists = lists;
        _messages = messages;
        _presets = presets;

        Title = "Запуск рассылки";
        Width = 720; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(24) };
        scroll.Content = panel;

        // Header
        var title = new TextBlock
        {
            Text = "Менеджер запуска компании",
            FontSize = 17, FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        panel.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "Соберите рассылку из четырёх компонентов. Учётка — от кого. Список — кому. " +
                   "Письмо — что. Профиль отправки — как. После создания рассылку можно запустить сразу.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 20),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(hint);

        // Name
        panel.Children.Add(Lbl("Название рассылки"));
        StyleInput(_tbName);
        _tbName.Text = $"Рассылка {DateTime.Now:dd.MM HH:mm}";
        panel.Children.Add(_tbName);

        // Type
        panel.Children.Add(Lbl("Тип"));
        foreach (var t in new[] {
            new { V = "mailing", L = "Обычная рассылка" },
            new { V = "transactional", L = "Транзакционная" },
            new { V = "validation", L = "Валидация" } })
        {
            _cbType.Items.Add(new ComboBoxItem { Content = t.L, Tag = t.V });
        }
        _cbType.SelectedIndex = 0;
        StyleCombo(_cbType);
        panel.Children.Add(_cbType);

        // Sender
        panel.Children.Add(Lbl("Учётная запись отправителя"));
        foreach (var s in _senders)
            _cbSender.Items.Add(new ComboBoxItem
            {
                Content = $"#{s.Id}  {Truncate(s.AccountName, 60)}  ·  {s.SenderEmail}",
                Tag = s.Id
            });
        StyleCombo(_cbSender);
        panel.Children.Add(_cbSender);

        // List
        panel.Children.Add(Lbl("Список рассылки"));
        foreach (var l in _lists.OrderBy(x => x.ListName))
            _cbList.Items.Add(new ComboBoxItem
            {
                Content = $"#{l.Id}  {Truncate(l.ListName, 60)}  ·  {l.Size:N0} адр.",
                Tag = l.Id
            });
        StyleCombo(_cbList);
        panel.Children.Add(_cbList);

        // Message
        panel.Children.Add(Lbl("Письмо"));
        foreach (var m in _messages.OrderBy(x => x.MessageName))
            _cbMessage.Items.Add(new ComboBoxItem
            {
                Content = $"#{m.Id}  {Truncate(m.MessageName, 55)}  ·  [{m.MessageType}]",
                Tag = m.Id
            });
        StyleCombo(_cbMessage);
        panel.Children.Add(_cbMessage);

        // Delivery preset
        panel.Children.Add(Lbl("Профиль отправки"));
        foreach (var p in _presets)
            _cbPreset.Items.Add(new ComboBoxItem
            {
                Content = $"#{p.Id}  {p.Name}  ·  {p.DeliveryMode} / {p.SendingThreads} потоков",
                Tag = p.Id
            });
        if (_cbPreset.Items.Count > 0) _cbPreset.SelectedIndex = 0;
        StyleCombo(_cbPreset);
        panel.Children.Add(_cbPreset);

        // Autostart checkbox
        _cbAutostart.FontSize = 12;
        _cbAutostart.Margin = new Thickness(0, 18, 0, 0);
        _cbAutostart.SetResourceReference(Control.ForegroundProperty, "Fg2Brush");
        panel.Children.Add(_cbAutostart);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(_status);

        // Buttons row
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 20, 0, 0) };
        var btnCancel = new Button { Content = "Отмена", Padding = new Thickness(16, 8, 16, 8), MinWidth = 100, Cursor = Cursors.Hand };
        btnCancel.SetResourceReference(StyleProperty, "GhostBtn");
        btnCancel.Click += (_, _) => Close();
        DockPanel.SetDock(btnCancel, Dock.Right);
        _btnGo = new Button
        {
            Content = "▶  Создать и запустить",
            Padding = new Thickness(22, 8, 22, 8), MinWidth = 200,
            Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        _btnGo.SetResourceReference(StyleProperty, "AccentBtn");
        _btnGo.Click += async (_, _) => await CreateAsync();
        DockPanel.SetDock(_btnGo, Dock.Right);
        row.Children.Add(_btnGo);
        row.Children.Add(btnCancel);
        panel.Children.Add(row);

        Content = scroll;
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_tbName.Text)) { Fail("Введи название рассылки."); return; }
        var senderId = TagInt(_cbSender.SelectedItem);
        var listId = TagInt(_cbList.SelectedItem);
        var messageId = TagInt(_cbMessage.SelectedItem);
        var presetId = TagInt(_cbPreset.SelectedItem);
        var typeStr = (_cbType.SelectedItem as ComboBoxItem)?.Tag as string ?? "mailing";

        if (senderId <= 0) { Fail("Выбери учётку отправителя."); return; }
        if (listId <= 0) { Fail("Выбери список рассылки."); return; }
        if (messageId <= 0) { Fail("Выбери письмо."); return; }
        if (presetId <= 0) { Fail("Выбери профиль отправки."); return; }

        _btnGo.IsEnabled = false;
        _status.Text = "Создаю рассылку…";
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        try
        {
            var newId = await _api.AddMailingAsync(new AmsMailingCreate
            {
                Name = _tbName.Text.Trim(),
                Type = typeStr,
                SenderAccountId = senderId,
                MessageId = messageId,
                DeliveryPresetId = presetId,
                MailingListIds = new List<int> { listId },
            });
            if (newId <= 0)
            {
                Fail("AMS не вернул id новой рассылки. Проверь права API и корректность полей.");
                _btnGo.IsEnabled = true;
                return;
            }

            if (_cbAutostart.IsChecked == true)
            {
                _status.Text = $"Рассылка создана (id {newId}). Запускаю…";
                var ok = await _api.StartMailingAsync(newId);
                if (!ok) { Fail($"Рассылка создана (id {newId}), но запуск не удался."); DialogResult = true; return; }
            }

            _status.Text = $"OK — рассылка #{newId} " +
                            (_cbAutostart.IsChecked == true ? "запущена." : "создана.");
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Fail("Ошибка: " + ex.Message);
            _btnGo.IsEnabled = true;
        }
    }

    private void Fail(string msg)
    {
        _status.Text = msg;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    }

    private static int TagInt(object? item)
    {
        if (item is ComboBoxItem ci && ci.Tag is int i) return i;
        return 0;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static TextBlock Lbl(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11, FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 14, 0, 4),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }

    private static void StyleInput(TextBox tb)
    {
        tb.Height = 32; tb.FontSize = 12.5;
        tb.Padding = new Thickness(10, 0, 10, 0);
        tb.VerticalContentAlignment = VerticalAlignment.Center;
        tb.BorderThickness = new Thickness(1);
        tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
    }

    private static void StyleCombo(ComboBox cb)
    {
        cb.Height = 32; cb.FontSize = 12.5;
        cb.Padding = new Thickness(8, 0, 8, 0);
        cb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        cb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        cb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
    }
}
