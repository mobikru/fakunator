using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.DomainsManager;

namespace Fakunator.Views;

/// <summary>
/// Диалог применения DNS-шаблона к домену: выбор шаблона, заполнение плейсхолдеров,
/// проверка конфликтов с существующими записями, bulk-upsert с прогрессом.
/// </summary>
public class ApplyDnsTemplateDialog : Window
{
    private readonly IspAccount _account;
    private readonly string _domain;
    private readonly string _sessionId;
    private readonly List<DnsRecord> _existing;

    private readonly ComboBox _cbTemplate = new() { Height = 34, FontSize = 12.5, Padding = new Thickness(10, 0, 10, 0) };
    private readonly StackPanel _placeholdersPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly ObservableCollection<PreviewRow> _preview = new();
    private readonly ListView _previewList = new() { Height = 240 };
    private readonly TextBlock _summary = new() { FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _btnApply;
    private readonly CheckBox _cbOverwrite = new() { Content = Loc.T("domains.dnsTemplateDialog.cbOverwrite"), IsChecked = false };
    private DnsTemplate? _selectedTpl;
    private readonly Dictionary<string, TextBox> _placeholderInputs = new();

    public ApplyDnsTemplateDialog(IspAccount account, string domain, string sessionId, List<DnsRecord> existing)
    {
        _account = account;
        _domain = domain;
        _sessionId = sessionId;
        _existing = existing;

        Title = string.Format(Loc.T("domains.dnsTemplateDialog.title"), domain);
        Width = 780;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var panel = new StackPanel { Margin = new Thickness(20) };

        // Header
        var title = new TextBlock
        {
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        title.Text = string.Format(Loc.T("domains.dnsTemplateDialog.heading"), domain);
        panel.Children.Add(title);

        var hint = new TextBlock
        {
            Text = Loc.T("domains.dnsTemplateDialog.hint"),
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(hint);

        // Selector
        panel.Children.Add(Lbl(Loc.T("domains.dnsTemplateDialog.lblTemplate")));
        var allTemplates = DnsTemplatePresets.All.Concat(Config.Current.DnsTemplates).ToList();
        foreach (var t in allTemplates)
            _cbTemplate.Items.Add(new ComboBoxItem
            {
                Content = t.IsBuiltin
                    ? string.Format(Loc.T("domains.dnsTemplateDialog.itemBuiltin"), t.Name, t.Records.Count)
                    : string.Format(Loc.T("domains.dnsTemplateDialog.itemUser"), t.Name, t.Records.Count),
                Tag = t,
            });
        _cbTemplate.SelectionChanged += (_, _) => OnTemplateChanged();
        panel.Children.Add(_cbTemplate);

        // Placeholders (динамически)
        panel.Children.Add(_placeholdersPanel);

        // Preview
        panel.Children.Add(Lbl(Loc.T("domains.dnsTemplateDialog.lblPreview")));
        _previewList.ItemsSource = _preview;
        _previewList.BorderThickness = new Thickness(0);
        _previewList.Background = Brushes.Transparent;
        var gv = new GridView { AllowsColumnReorder = false };
        gv.Columns.Add(Col(Loc.T("domains.dns.colStatus"), 90, "StatusText", "StatusFg"));
        gv.Columns.Add(Col(Loc.T("domains.dns.colType"), 60, "Type"));
        gv.Columns.Add(Col(Loc.T("domains.dns.colName"), 140, "Name"));
        gv.Columns.Add(Col(Loc.T("domains.dns.colValue"), 300, "Value"));
        gv.Columns.Add(Col(Loc.T("domains.dns.colTtl"), 50, "TtlText"));
        gv.Columns.Add(Col(Loc.T("domains.dns.colPrio"), 50, "PriorityText"));
        _previewList.View = gv;
        panel.Children.Add(_previewList);

        _cbOverwrite.SetResourceReference(Control.ForegroundProperty, "Fg2Brush");
        _cbOverwrite.Margin = new Thickness(0, 10, 0, 0);
        _cbOverwrite.FontSize = 11.5;
        panel.Children.Add(_cbOverwrite);

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        panel.Children.Add(_summary);

        // Buttons
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var btnCancel = new Button { Content = Loc.T("domains.dnsTemplateDialog.btnCancel"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 100 };
        btnCancel.SetResourceReference(StyleProperty, "GhostBtn");
        btnCancel.Click += (_, _) => Close();
        DockPanel.SetDock(btnCancel, Dock.Right);
        _btnApply = new Button
        {
            Content = Loc.T("domains.dnsTemplateDialog.btnApply"),
            Padding = new Thickness(20, 8, 20, 8), MinWidth = 160, Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        _btnApply.SetResourceReference(StyleProperty, "PrimaryBtn");
        _btnApply.Click += async (_, _) => await ApplyAsync();
        DockPanel.SetDock(_btnApply, Dock.Right);
        row.Children.Add(_btnApply);
        row.Children.Add(btnCancel);
        panel.Children.Add(row);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        if (_cbTemplate.Items.Count > 0) _cbTemplate.SelectedIndex = 0;
    }

    private void OnTemplateChanged()
    {
        _selectedTpl = (_cbTemplate.SelectedItem as ComboBoxItem)?.Tag as DnsTemplate;
        _placeholdersPanel.Children.Clear();
        _placeholderInputs.Clear();
        _preview.Clear();
        _btnApply.IsEnabled = _selectedTpl != null;
        if (_selectedTpl == null) return;

        // Собираем все плейсхолдеры из шаблона
        var keys = _selectedTpl.Records
            .SelectMany(r => r.ExtractPlaceholders())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k == "domain" ? "" : k)   // {domain} первым
            .ToList();

        if (keys.Count > 0)
        {
            _placeholdersPanel.Children.Add(Lbl(Loc.T("domains.dnsTemplateDialog.lblPlaceholders")));
            foreach (var key in keys)
            {
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock
                {
                    Text = "{" + key + "}",
                    FontFamily = new FontFamily("JetBrains Mono, Consolas"),
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
                Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                var tb = new TextBox { Height = 30, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
                tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
                tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
                tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
                tb.VerticalContentAlignment = VerticalAlignment.Center;
                // {domain} — предзаполнено
                if (key.Equals("domain", StringComparison.OrdinalIgnoreCase)) tb.Text = _domain;
                tb.TextChanged += (_, _) => RefreshPreview();
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);

                _placeholderInputs[key] = tb;
                _placeholdersPanel.Children.Add(grid);
            }
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _preview.Clear();
        if (_selectedTpl == null) return;
        var vars = _placeholderInputs.ToDictionary(kv => kv.Key, kv => kv.Value.Text?.Trim() ?? "",
            StringComparer.OrdinalIgnoreCase);
        if (!vars.ContainsKey("domain")) vars["domain"] = _domain;

        int newCount = 0, conflictCount = 0, errorCount = 0;
        foreach (var tr in _selectedTpl.Records)
        {
            var name = tr.ResolveName(vars);
            var value = tr.ResolveValue(vars);
            // Валидация
            var v1 = DnsTemplateValidator.Validate(tr);
            var v2 = v1.Ok ? DnsTemplateValidator.ValidateResolved(tr.Type, name, value) : v1;
            string status; Brush fg;
            if (!v2.Ok) { status = "❌ " + v2.Error; fg = RedBrush; errorCount++; }
            else
            {
                // Ищем конфликт: та же комбинация type+name уже есть у домена?
                var conflict = _existing.FirstOrDefault(er =>
                    string.Equals(er.Type, tr.Type, StringComparison.OrdinalIgnoreCase)
                    && NormalizeName(er.Name).Equals(NormalizeName(name), StringComparison.OrdinalIgnoreCase));
                if (conflict != null)
                {
                    bool sameValue = string.Equals(conflict.Value?.Trim().TrimEnd('.'), value?.Trim().TrimEnd('.'),
                        StringComparison.OrdinalIgnoreCase);
                    if (sameValue) { status = Loc.T("domains.dnsTemplateDialog.statusIdentical"); fg = GrayBrush; }
                    else { status = Loc.T("domains.dnsTemplateDialog.statusConflict"); fg = AmberBrush; conflictCount++; }
                }
                else { status = Loc.T("domains.dnsTemplateDialog.statusNew"); fg = GreenBrush; newCount++; }
            }
            _preview.Add(new PreviewRow(tr.Type, name, value,
                tr.Ttl.ToString(), tr.Priority?.ToString() ?? "",
                status, fg));
        }
        _summary.Text = string.Format(Loc.T("domains.dnsTemplateDialog.summary"), newCount, conflictCount, errorCount);
        _btnApply.IsEnabled = errorCount == 0 && _preview.Count > 0;
    }

    private async Task ApplyAsync()
    {
        if (_selectedTpl == null) return;
        _btnApply.IsEnabled = false;
        var vars = _placeholderInputs.ToDictionary(kv => kv.Key, kv => kv.Value.Text?.Trim() ?? "",
            StringComparer.OrdinalIgnoreCase);
        if (!vars.ContainsKey("domain")) vars["domain"] = _domain;
        bool overwrite = _cbOverwrite.IsChecked == true;

        int ok = 0, skipped = 0, failed = 0;
        try
        {
            using var client = new IspApiClient(_account.Host);
            foreach (var tr in _selectedTpl.Records)
            {
                var name = tr.ResolveName(vars);
                var value = tr.ResolveValue(vars);
                var v = DnsTemplateValidator.ValidateResolved(tr.Type, name, value);
                if (!v.Ok) { failed++; continue; }

                var conflict = _existing.FirstOrDefault(er =>
                    string.Equals(er.Type, tr.Type, StringComparison.OrdinalIgnoreCase)
                    && NormalizeName(er.Name).Equals(NormalizeName(name), StringComparison.OrdinalIgnoreCase));

                if (conflict != null && !overwrite)
                {
                    bool sameValue = string.Equals(conflict.Value?.Trim().TrimEnd('.'), value?.Trim().TrimEnd('.'),
                        StringComparison.OrdinalIgnoreCase);
                    if (!sameValue) { skipped++; continue; }
                    // Идентично — тоже пропускаем (ничего делать не надо)
                    ok++; continue;
                }

                try
                {
                    var rkey = conflict?.Rkey; // обновление существующей записи если overwrite
                    var res = await client.UpsertRecordAsync(_sessionId, _domain,
                        tr.Type, name, value, tr.Ttl, tr.Priority, rkey);
                    if (res) ok++; else failed++;
                }
                catch { failed++; }
            }

            MessageBox.Show(
                string.Format(Loc.T("domains.dnsTemplateDialog.doneBody"), ok, skipped, failed),
                Loc.T("domains.dnsTemplateDialog.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("domains.dnsTemplateDialog.errBody"), ex.Message), Loc.T("domains.dnsTemplateDialog.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            _btnApply.IsEnabled = true;
        }
    }

    private string NormalizeName(string name)
    {
        var n = (name ?? "").Trim().TrimEnd('.');
        var d = _domain.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(n)) return "@";
        if (n.Equals(d, StringComparison.OrdinalIgnoreCase)) return "@";
        if (n.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
            return n[..^(d.Length + 1)];
        return n;
    }

    private static readonly SolidColorBrush GreenBrush = Frozen(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly SolidColorBrush AmberBrush = Frozen(Color.FromRgb(0xea, 0xb3, 0x08));
    private static readonly SolidColorBrush RedBrush = Frozen(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly SolidColorBrush GrayBrush = Frozen(Color.FromRgb(0x9c, 0xa3, 0xaf));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static TextBlock Lbl(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }

    private static GridViewColumn Col(string header, int width, string bindPath, string? fgPath = null)
    {
        var col = new GridViewColumn { Header = header, Width = width };
        var factory = new System.Windows.FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding(bindPath));
        if (fgPath != null) factory.SetBinding(TextBlock.ForegroundProperty, new Binding(fgPath));
        factory.SetValue(TextBlock.FontSizeProperty, 11.5);
        factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        col.CellTemplate = new DataTemplate { VisualTree = factory };
        return col;
    }

    private record PreviewRow(string Type, string Name, string Value,
        string TtlText, string PriorityText, string StatusText, Brush StatusFg);
}
