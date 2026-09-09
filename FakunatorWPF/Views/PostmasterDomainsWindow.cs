using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.Postmaster;

namespace Fakunator.Views;

/// <summary>
/// Окно: список доменов из postmaster.mail.ru + статус + troubles + статистика.
/// Слева — список доменов, справа — детали выбранного (метрики за 30 дней + проблемы).
/// </summary>
public class PostmasterDomainsWindow : Window
{
    private readonly PostmasterAccount _account;
    private string? _accessToken;
    private readonly ObservableCollection<DomainRow> _domains = new();
    private readonly ListView _list = new();
    private readonly StackPanel _detailPanel = new() { Margin = new Thickness(16) };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private DomainRow? _selected;

    public PostmasterDomainsWindow(PostmasterAccount acc)
    {
        _account = acc;
        Title = $"Постмастер · {acc.Username}";
        Width = 1000; Height = 620;
        MinWidth = 800; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(14) };

        // ── Header ──────────────────────────────────────────────────
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock
        {
            Text = $"Postmaster · {acc.Username}",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        header.Children.Add(title);

        var btnRefresh = new Button
        {
            Content = "↻ Обновить",
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 11.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRefresh.SetResourceReference(StyleProperty, "GhostBtn");
        btnRefresh.Click += async (_, _) => await LoadDomainsAsync();
        DockPanel.SetDock(btnRefresh, Dock.Right);
        header.Children.Add(btnRefresh);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        // ── Split: list + details ──────────────────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: list of domains
        var listBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0, 0, 8, 0),
        };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");
        Grid.SetColumn(listBorder, 0);
        _list.ItemsSource = _domains;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.SelectionChanged += OnDomainSelected;
        var gv = new GridView { AllowsColumnReorder = false };
        // Selectable domain column — TextBox IsReadOnly для копирования текста мышью
        var domainCellTemplate = new DataTemplate();
        var tbFactory = new FrameworkElementFactory(typeof(TextBox));
        tbFactory.SetBinding(TextBox.TextProperty, new Binding("Domain") { Mode = BindingMode.OneWay });
        tbFactory.SetValue(TextBox.IsReadOnlyProperty, true);
        tbFactory.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
        tbFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
        tbFactory.SetValue(TextBox.PaddingProperty, new Thickness(0));
        tbFactory.SetValue(TextBox.CursorProperty, Cursors.IBeam);
        tbFactory.SetValue(TextBox.IsReadOnlyCaretVisibleProperty, false);
        domainCellTemplate.VisualTree = tbFactory;
        gv.Columns.Add(new GridViewColumn { Header = "ДОМЕН", Width = 200, CellTemplate = domainCellTemplate });
        gv.Columns.Add(new GridViewColumn { Header = "СТАТУС", Width = 100, DisplayMemberBinding = new Binding("StatusText") });
        _list.View = gv;
        listBorder.Child = _list;
        grid.Children.Add(listBorder);

        // Right: details scroll
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        detailScroll.Content = _detailPanel;
        Grid.SetColumn(detailScroll, 1);
        grid.Children.Add(detailScroll);

        root.Children.Add(grid);
        Content = root;

        var hint = new TextBlock
        {
            Text = "Выбери домен слева — справа появятся метрики за 30 дней и проблемы SPF/DKIM/DMARC.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 40, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        _detailPanel.Children.Add(hint);

        Loaded += async (_, _) => await LoadDomainsAsync();
    }

    private async Task<string?> EnsureAccessAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken)) return _accessToken;
        try
        {
            using var oauth = new MailRuOAuthClient();
            var t = await oauth.RefreshAccessAsync(_account.RefreshToken);
            _accessToken = t.AccessToken;
            return _accessToken;
        }
        catch (Exception ex)
        {
            _status.Text = $"Ошибка входа в mail.ru: {ex.Message}";
            return null;
        }
    }

    private async Task LoadDomainsAsync()
    {
        _status.Text = "Загрузка доменов…";
        var token = await EnsureAccessAsync();
        if (token == null) return;
        try
        {
            using var api = new PostmasterApiClient(token);
            var list = await api.RegListAsync();
            var troubles = await api.TroublesListAsync();
            var troublesByDomain = troubles.GroupBy(t => t.Domain)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            _domains.Clear();
            foreach (var d in list)
            {
                var probs = troublesByDomain.TryGetValue(d.Domain, out var t) ? t : new();
                var status = d.Verified ? (probs.Count == 0 ? "OK" : $"⚠ {probs.Count}") : "не верифиц.";
                _domains.Add(new DomainRow(d.Domain, d.Verified, status, probs));
            }
            _status.Text = $"Доменов: {list.Count}, проблемы: {troubles.Count}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Ошибка API: {ex.Message}";
        }
    }

    // Метрики + цвета для линий графика (в порядке важности).
    private static readonly (string Key, string Color)[] GraphSeries =
    {
        ("messages_sent",  "#8b5cf6"), // Отправлено — фиолетовый
        ("delivered",      "#22c55e"), // Доставлено — зелёный
        ("read",           "#3b82f6"), // Прочитано — синий
        ("deleted_unread", "#f59e0b"), // Удалено не читая — оранжевый
        ("deleted_read",   "#a855f7"), // Удалено после прочтения — фиолетовый
        ("spam",           "#ef4444"), // Спам — красный
        ("complaints",     "#dc2626"), // Жалобы — тёмно-красный
    };

    private async void OnDomainSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is not DomainRow row) return;
        _selected = row;
        _detailPanel.Children.Clear();
        _detailPanel.Children.Add(SectionTitle(row.Domain));
        _detailPanel.Children.Add(KeyValue("Статус", row.Verified ? "✓ верифицирован" : "не верифицирован"));

        if (row.Troubles.Count > 0)
        {
            _detailPanel.Children.Add(SectionTitle("Проблемы"));
            foreach (var t in row.Troubles)
            {
                var line = new TextBlock
                {
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"• {t.Type}: {t.Description}",
                };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
                _detailPanel.Children.Add(line);
            }
        }

        _detailPanel.Children.Add(SectionTitle("Статистика (за всё время)"));
        var loading = new TextBlock { Text = "Загрузка…", FontSize = 12 };
        loading.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        _detailPanel.Children.Add(loading);

        var token = await EnsureAccessAsync();
        if (token == null) return;
        try
        {
            using var api = new PostmasterApiClient(token);
            var stat = await api.StatListAsync(row.Domain);
            _detailPanel.Children.Remove(loading);

            if (stat.Metrics.Count == 0)
            {
                var raw = new TextBox
                {
                    Text = stat.RawJson,
                    IsReadOnly = true,
                    Height = 240,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                raw.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
                raw.SetResourceReference(Control.BorderBrushProperty, "Border1Brush");
                raw.SetResourceReference(Control.ForegroundProperty, "Fg2Brush");
                _detailPanel.Children.Add(raw);
                return;
            }
            // Показываем метрики в заранее заданном порядке (важные сверху), а остальное после.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in MetricOrder)
            {
                if (stat.Metrics.TryGetValue(key, out var v))
                {
                    _detailPanel.Children.Add(KeyValue(MetricRu(key), FormatMetric(key, v)));
                    seen.Add(key);
                }
            }
            // Остальные (если Mail.ru вернул что-то новое) — снизу
            foreach (var kv in stat.Metrics)
            {
                if (seen.Contains(kv.Key)) continue;
                _detailPanel.Children.Add(KeyValue(MetricRu(kv.Key), FormatMetric(kv.Key, kv.Value)));
            }

            // ── График по дням ───────────────────────────────────
            _detailPanel.Children.Add(SectionTitle("График (за 30 дней)"));
            var chartLoading = new TextBlock { Text = "Загрузка графика…", FontSize = 12 };
            chartLoading.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            _detailPanel.Children.Add(chartLoading);
            try
            {
                using var api2 = new PostmasterApiClient(token);
                var daily = await api2.StatListDetailedAsync(row.Domain, DateTime.UtcNow.AddDays(-30));
                _detailPanel.Children.Remove(chartLoading);
                if (daily.Count == 0)
                {
                    var empty = new TextBlock { Text = "Нет данных для построения графика.", FontSize = 12 };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
                    _detailPanel.Children.Add(empty);
                }
                else
                {
                    _detailPanel.Children.Add(BuildChart(daily));
                    _detailPanel.Children.Add(BuildLegend());
                }
            }
            catch (Exception ex)
            {
                _detailPanel.Children.Remove(chartLoading);
                var err = new TextBlock { Text = $"Ошибка графика: {ex.Message}", FontSize = 12 };
                err.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
                _detailPanel.Children.Add(err);
            }
        }
        catch (Exception ex)
        {
            _detailPanel.Children.Remove(loading);
            var err = new TextBlock { Text = $"Ошибка: {ex.Message}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            err.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            _detailPanel.Children.Add(err);
        }
    }

    private static TextBlock SectionTitle(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        return tb;
    }
    private static Grid KeyValue(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        var lk = new TextBlock { Text = k, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        lk.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        Grid.SetColumn(lk, 0);
        var lv = new TextBlock
        {
            Text = v, FontSize = 11.5,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        lv.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        Grid.SetColumn(lv, 1);
        g.Children.Add(lk);
        g.Children.Add(lv);
        return g;
    }

    private record DomainRow(string Domain, bool Verified, string StatusText, System.Collections.Generic.List<PostmasterTrouble> Troubles);

    /// <summary>
    /// Строит простой line-chart из ежедневных точек: Canvas с осями
    /// и Polyline на каждую активную метрику. Auto-scale по Y (макс. значение),
    /// точки распределены равномерно по X. Не подхватывает готовый chart-контрол —
    /// нам хватает базового отображения.
    /// </summary>
    private static Border BuildChart(List<PostmasterDaily> daily)
    {
        const double W = 620, H = 220;
        const double padL = 40, padR = 10, padT = 10, padB = 30;

        var canvas = new Canvas { Width = W, Height = H, Background = Brushes.Transparent };

        // Y-макс через max по включённым сериям
        double yMax = 0;
        foreach (var (k, _) in GraphSeries)
            foreach (var d in daily)
                if (d.Metrics.TryGetValue(k, out var v) && v > yMax) yMax = v;
        if (yMax <= 0) yMax = 1; // избегаем div/0 при пустом графике

        double plotW = W - padL - padR;
        double plotH = H - padT - padB;
        double step = daily.Count > 1 ? plotW / (daily.Count - 1) : plotW;

        // Axis lines
        canvas.Children.Add(MakeLine(padL, padT, padL, H - padB, "#3f3f46"));            // Y
        canvas.Children.Add(MakeLine(padL, H - padB, W - padR, H - padB, "#3f3f46"));    // X

        // Y-labels (min/mid/max)
        canvas.Children.Add(MakeText("0", padL - 30, H - padB - 8, "#71717a"));
        canvas.Children.Add(MakeText(((long)(yMax / 2)).ToString("N0"), padL - 32, padT + plotH / 2 - 8, "#71717a"));
        canvas.Children.Add(MakeText(((long)yMax).ToString("N0"), padL - 32, padT - 6, "#71717a"));

        // X-labels (first/mid/last date)
        if (daily.Count > 0)
        {
            canvas.Children.Add(MakeText(ShortDate(daily[0].Date), padL - 20, H - padB + 6, "#71717a"));
            canvas.Children.Add(MakeText(ShortDate(daily[^1].Date), W - padR - 30, H - padB + 6, "#71717a"));
        }

        // Series polylines
        foreach (var (key, color) in GraphSeries)
        {
            var pts = new System.Windows.Media.PointCollection();
            for (int i = 0; i < daily.Count; i++)
            {
                daily[i].Metrics.TryGetValue(key, out var v);
                double x = padL + i * step;
                double y = H - padB - (v / yMax) * plotH;
                pts.Add(new Point(x, y));
            }
            // Пропускаем полностью нулевые серии — не рисуем плоскую линию
            bool anyPositive = false;
            foreach (var d in daily) if (d.Metrics.TryGetValue(key, out var v) && v > 0) { anyPositive = true; break; }
            if (!anyPositive) continue;

            var poly = new System.Windows.Shapes.Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            };
            canvas.Children.Add(poly);
        }

        var brd = new Border
        {
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 6),
            CornerRadius = new CornerRadius(6),
            Child = canvas,
        };
        brd.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        return brd;
    }

    private static WrapPanel BuildLegend()
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, color) in GraphSeries)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var lbl = new TextBlock { Text = MetricRu(key), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            sp.Children.Add(lbl);
            wp.Children.Add(sp);
        }
        return wp;
    }

    private static System.Windows.Shapes.Line MakeLine(double x1, double y1, double x2, double y2, string colorHex) =>
        new System.Windows.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!),
            StrokeThickness = 1,
        };

    private static TextBlock MakeText(string s, double x, double y, string colorHex)
    {
        var tb = new TextBlock
        {
            Text = s,
            FontSize = 10,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    private static string ShortDate(string iso) =>
        DateTime.TryParse(iso, out var dt) ? dt.ToString("dd.MM") : iso;

    // ── Локализация метрик Postmaster API ────────────────────────────
    // Порядок важен: сначала письма (отправлено/доставлено), потом поведение
    // (прочитано/удалено), потом жалобы/спам, в конце — репутация/тренд.
    private static readonly string[] MetricOrder =
    {
        "messages_sent",
        "delivered",
        "read",
        "deleted_read",
        "deleted_unread",
        "spam",
        "spam_percent",
        "probably_spam",
        "probably_spam_percent",
        "complaints",
        "reputation",
        "trend",
    };

    private static readonly Dictionary<string, string> RuLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["messages_sent"]         = "Отправлено писем",
        ["delivered"]             = "Доставлено",
        ["read"]                  = "Прочитано",
        ["deleted_read"]          = "Удалено после прочтения",
        ["deleted_unread"]        = "Удалено не читая",
        ["spam"]                  = "Попало в «Спам»",
        ["spam_percent"]          = "Доля в «Спам»",
        ["probably_spam"]         = "Возможно спам",
        ["probably_spam_percent"] = "Доля «возможно спам»",
        ["complaints"]            = "Жалоб на спам",
        ["reputation"]            = "Репутация домена",
        ["trend"]                 = "Тренд",
    };

    private static string MetricRu(string key) =>
        RuLabels.TryGetValue(key, out var ru) ? ru : key;

    /// <summary>
    /// Форматирует метрику. Percent-поля → «X.XX %», reputation/trend → 2 знака,
    /// всё остальное — целое с разделителями тысяч.
    /// </summary>
    private static string FormatMetric(string key, double value)
    {
        var lk = key.ToLowerInvariant();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (lk.EndsWith("percent") || lk.Contains("_pct"))
            return value.ToString("N2", ru) + " %";
        if (lk == "reputation" || lk == "trend")
            return value.ToString("N2", ru);
        return ((long)value).ToString("N0", ru);
    }
}
