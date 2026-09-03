using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Fakunator.Core.DomainsManager;

namespace Fakunator.Views;

/// <summary>
/// Модалка добавления/редактирования DNS-записи. Chip-selector типов, живой
/// preview записи в моно-шрифте, компактный layout.
/// </summary>
public class EditDnsRecordDialog : Window
{
    public DnsRecord? Result { get; private set; }
    public DnsRecord? EditingRecord { get; }

    private readonly string _domain;
    private string _type = "A";
    private readonly TextBox _tbName;
    private readonly TextBox _tbValue;
    private readonly TextBox _tbTtl;
    private readonly TextBox _tbPrio;
    private readonly StackPanel _prioBlock;
    private readonly TextBlock _previewText;
    private readonly (string Key, string Label, string Hint)[] _types = new (string, string, string)[]
    {
        ("A",     "A",     "IPv4-адрес"),
        ("AAAA",  "AAAA",  "IPv6-адрес"),
        ("CNAME", "CNAME", "Альтернативное имя"),
        ("MX",    "MX",    "Почтовый сервер"),
        ("TXT",   "TXT",   "Произвольный текст (SPF/DKIM/DMARC)"),
        ("NS",    "NS",    "Делегирование поддомена"),
        ("SRV",   "SRV",   "Сервисная запись"),
    };
    private readonly RadioButton[] _typeChips;
    private readonly TextBlock _typeHint;

    public EditDnsRecordDialog(string domain, DnsRecord? editing = null)
    {
        _domain = domain;
        EditingRecord = editing;
        Title = editing == null ? $"Новая запись · {domain}" : $"Редактирование · {domain}";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Owner = Application.Current?.MainWindow;

        // ── Внешняя обёртка с закруглением и тенью ──────────────────
        var shell = new Border
        {
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(20),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 32, ShadowDepth = 0, Opacity = 0.55,
            },
        };
        shell.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "Border2Brush");
        shell.BorderThickness = new Thickness(1);

        var root = new StackPanel();

        // ── Header ──────────────────────────────────────────────────
        var header = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(22, 20, 14, 0),
        };
        var closeBtn = new Button
        {
            Content = "✕", Width = 32, Height = 32,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, FontSize = 14,
        };
        closeBtn.SetResourceReference(Control.ForegroundProperty, "Fg3Brush");
        closeBtn.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);

        // Иконка
        var iconBox = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x81, 0x8c, 0xf8),
                Color.FromRgb(0x5e, 0x6a, 0xd2),
                new Point(0, 0), new Point(1, 1)),
        };
        iconBox.Child = new TextBlock
        {
            Text = editing == null ? "＋" : "✎",
            FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(iconBox, Dock.Left);
        header.Children.Add(iconBox);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t1 = new TextBlock
        {
            Text = editing == null ? "Новая DNS-запись" : "Редактирование записи",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
        };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        titles.Children.Add(t1);
        var t2 = new TextBlock
        {
            Text = domain, FontSize = 11,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        titles.Children.Add(t2);
        header.Children.Add(titles);
        root.Children.Add(header);

        // ── Тип: chip-selector ──────────────────────────────────────
        var typesWrap = new WrapPanel { Margin = new Thickness(22, 18, 22, 0) };
        _typeChips = new RadioButton[_types.Length];
        for (int i = 0; i < _types.Length; i++)
        {
            var (key, label, _) = _types[i];
            var rb = new RadioButton
            {
                GroupName = "RtypeGrp",
                Content = label,
                Style = MakeChipStyle(),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = key,
            };
            int idx = i;
            rb.Checked += (_, _) => { _type = _types[idx].Key; _typeHint.Text = _types[idx].Hint; UpdatePreview(); UpdatePrioVisibility(); };
            _typeChips[i] = rb;
            typesWrap.Children.Add(rb);
        }
        root.Children.Add(new TextBlock
        {
            Text = "ТИП ЗАПИСИ", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(22, 20, 22, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)),
        });
        root.Children.Add(typesWrap);

        _typeHint = new TextBlock
        {
            Text = _types[0].Hint,
            FontSize = 10.5,
            Margin = new Thickness(22, 4, 22, 0),
        };
        _typeHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        root.Children.Add(_typeHint);

        // ── Имя + Значение (grid) ───────────────────────────────────
        var fieldGrid = new Grid { Margin = new Thickness(22, 18, 22, 0) };
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameCol = new StackPanel();
        nameCol.Children.Add(FieldLabel("Имя"));
        _tbName = MakeInput();
        _tbName.Text = "@";
        _tbName.TextChanged += (_, _) => UpdatePreview();
        nameCol.Children.Add(_tbName);
        nameCol.Children.Add(FieldHint("«@» — корень, «www» и т.п."));
        Grid.SetColumn(nameCol, 0);
        fieldGrid.Children.Add(nameCol);

        var valCol = new StackPanel();
        valCol.Children.Add(FieldLabel("Значение"));
        _tbValue = MakeInput();
        _tbValue.TextChanged += (_, _) => UpdatePreview();
        valCol.Children.Add(_tbValue);
        valCol.Children.Add(FieldHint("IP для A, hostname для CNAME/MX, текст для TXT"));
        Grid.SetColumn(valCol, 2);
        fieldGrid.Children.Add(valCol);
        root.Children.Add(fieldGrid);

        // ── TTL + Priority ──────────────────────────────────────────
        var extraRow = new Grid { Margin = new Thickness(22, 12, 22, 0) };
        extraRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        extraRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        extraRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ttlCol = new StackPanel();
        ttlCol.Children.Add(FieldLabel("TTL, сек"));
        _tbTtl = MakeInput();
        _tbTtl.Text = "3600";
        ttlCol.Children.Add(_tbTtl);
        Grid.SetColumn(ttlCol, 0);
        extraRow.Children.Add(ttlCol);

        _prioBlock = new StackPanel();
        _prioBlock.Children.Add(FieldLabel("Приоритет (MX)"));
        _tbPrio = MakeInput();
        _tbPrio.Text = "10";
        _prioBlock.Children.Add(_tbPrio);
        Grid.SetColumn(_prioBlock, 2);
        extraRow.Children.Add(_prioBlock);
        root.Children.Add(extraRow);

        // ── Preview ────────────────────────────────────────────────
        root.Children.Add(new TextBlock
        {
            Text = "ПРЕДПРОСМОТР", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(22, 20, 22, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)),
        });
        var previewBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(22, 0, 22, 0),
            BorderThickness = new Thickness(1),
        };
        previewBox.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        previewBox.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");
        _previewText = new TextBlock
        {
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        _previewText.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        previewBox.Child = _previewText;
        root.Children.Add(previewBox);

        // ── Кнопки ─────────────────────────────────────────────────
        var btnRow = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(22, 20, 22, 22),
        };
        var cancel = new Button
        {
            Content = "Отмена", Padding = new Thickness(18, 9, 18, 9), MinWidth = 100,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancel.SetResourceReference(StyleProperty, "GhostBtn");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        var ok = new Button
        {
            Content = editing == null ? "Добавить запись" : "Сохранить",
            Padding = new Thickness(20, 9, 20, 9), MinWidth = 160,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.SetResourceReference(StyleProperty, "PrimaryBtn");
        ok.Click += OnSave;
        DockPanel.SetDock(ok, Dock.Right);
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        shell.Child = root;
        Content = shell;

        // ── Drag окна за header ─────────────────────────────────────
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };

        // Prefill
        if (editing != null)
        {
            SetType(editing.Type);
            _tbName.Text = editing.Name;
            _tbValue.Text = editing.Value;
            _tbTtl.Text = editing.Ttl > 0 ? editing.Ttl.ToString() : "3600";
            if (editing.Priority.HasValue) _tbPrio.Text = editing.Priority.Value.ToString();
        }
        else
        {
            SetType("A");
        }
        UpdatePreview();
        UpdatePrioVisibility();
    }

    private void SetType(string key)
    {
        for (int i = 0; i < _types.Length; i++)
            if (string.Equals(_types[i].Key, key, System.StringComparison.OrdinalIgnoreCase))
                _typeChips[i].IsChecked = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(_tbName.Text) ? "@" : _tbName.Text.Trim();
        var value = _tbValue.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            MessageBox.Show("Введите значение записи.", "Пусто",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int.TryParse(_tbTtl.Text.Trim(), out var ttl);
        if (ttl <= 0) ttl = 3600;
        int? prio = null;
        if (_type.Equals("MX", System.StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(_tbPrio.Text.Trim(), out var p);
            prio = p > 0 ? p : 10;
        }
        Result = new DnsRecord(_type, name, value, ttl, prio, EditingRecord?.Rkey);
        DialogResult = true;
        Close();
    }

    private void UpdatePreview()
    {
        var name = string.IsNullOrWhiteSpace(_tbName?.Text) ? "@" : _tbName.Text.Trim();
        var full = name == "@" ? _domain : $"{name}.{_domain}";
        var val = _tbValue?.Text?.Trim() ?? "";
        var display = _type.Equals("TXT", System.StringComparison.OrdinalIgnoreCase)
                      ? $"\"{val}\""
                      : val;
        _previewText.Inlines.Clear();
        _previewText.Inlines.Add(new Run(full + "  ")
        { Foreground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa)) });
        _previewText.Inlines.Add(new Run(_type + "  ")
        { Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8c, 0xf8)), FontWeight = FontWeights.Bold });
        _previewText.Inlines.Add(new Run(display));
    }

    private void UpdatePrioVisibility()
    {
        var isMx = _type.Equals("MX", System.StringComparison.OrdinalIgnoreCase);
        _prioBlock.Visibility = isMx ? Visibility.Visible : Visibility.Hidden;
    }

    private static TextBlock FieldLabel(string t)
    {
        var tb = new TextBlock
        {
            Text = t, FontSize = 11, FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }
    private static TextBlock FieldHint(string t)
    {
        var tb = new TextBlock
        {
            Text = t, FontSize = 10,
            Margin = new Thickness(0, 5, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        return tb;
    }
    private static TextBox MakeInput()
    {
        var tb = new TextBox
        {
            Height = 34, FontSize = 12.5, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1),
        };
        tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
        return tb;
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Style MakeChipStyle()
    {
        // RadioButton в виде chip'а: closed rectangle + accent когда checked
        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var tpl = new ControlTemplate(typeof(RadioButton));
        var brd = new FrameworkElementFactory(typeof(Border), "bd");
        brd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        brd.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        brd.SetResourceReference(Border.BorderBrushProperty, "Border2Brush");
        brd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        brd.SetValue(Border.PaddingProperty, new Thickness(14, 6, 14, 6));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        brd.AppendChild(content);
        tpl.VisualTree = brd;

        // Setter НЕ принимает ResourceReferenceExpression напрямую (крашится Seal()),
        // поэтому цвета хардкодим — accent-палитра одинаковая для light/dark темы.
        var accentBrush = Frozen(Color.FromRgb(0x5e, 0x6a, 0xd2));
        var accentSoftBrush = Frozen(Color.FromArgb(0x24, 0x5e, 0x6a, 0xd2));
        var accent2Brush = Frozen(Color.FromRgb(0x81, 0x8c, 0xf8));
        var bg3Brush = Frozen(Color.FromRgb(0x1c, 0x1c, 0x21));

        var hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, bg3Brush, "bd"));
        tpl.Triggers.Add(hoverTrig);

        var checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrig.Setters.Add(new Setter(Border.BackgroundProperty, accentSoftBrush, "bd"));
        checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, accentBrush, "bd"));
        checkedTrig.Setters.Add(new Setter(TextElement.ForegroundProperty, accent2Brush));
        tpl.Triggers.Add(checkedTrig);

        style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return style;
    }
}
