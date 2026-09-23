using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Threading.Tasks;
using Fakunator.Core;
using Fakunator.Core.DomainsManager;

namespace Fakunator.Views;

/// <summary>
/// Окно управления DNS-записями конкретного домена: показывает список,
/// поддерживает добавление, редактирование и удаление записей.
/// </summary>
public class DnsRecordsWindow : Window
{
    private readonly IspAccount _account;
    private readonly string _domain;
    private readonly string _sessionId;
    private readonly ObservableCollection<DnsRecord> _records = new();
    private readonly ListView _list = new();
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(10, 0, 0, 0) };
    private bool _busy;

    public DnsRecordsWindow(IspAccount account, string domain, string sessionId)
    {
        _account = account;
        _domain = domain;
        _sessionId = sessionId;

        Title = string.Format(Loc.T("domains.dnsRecordsWindow.title"), domain);
        Width = 900; Height = 560;
        MinWidth = 700; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };

        // ── Header (title + close via native window) ────────────────
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var domainBlock = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        domainBlock.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        domainBlock.Inlines.Add(Loc.T("domains.dnsRecordsWindow.headingPrefix"));
        var mono = new System.Windows.Documents.Run(domain);
        mono.SetResourceReference(System.Windows.Documents.Run.FontFamilyProperty, "MonoFont");
        domainBlock.Inlines.Add(mono);
        header.Children.Add(domainBlock);
        root.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);

        // ── Toolbar (add + refresh + status) ────────────────────────
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var btnAdd = new Button
        {
            Content = Loc.T("domains.dns.btnAddRecord"),
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        btnAdd.SetResourceReference(StyleProperty, "PrimaryBtn");
        btnAdd.Click += async (_, _) => await AddAsync();
        var btnRefresh = new Button
        {
            Content = Loc.T("domains.dnsRecordsWindow.btnRefresh"),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 11.5,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        btnRefresh.SetResourceReference(StyleProperty, "GhostBtn");
        btnRefresh.Click += async (_, _) => await LoadAsync();
        toolbar.Children.Add(btnAdd);
        toolbar.Children.Add(btnRefresh);

        // ── Шаблоны: dropdown + apply + save-as ─────────────────────
        var btnApplyTemplate = new Button
        {
            Content = Loc.T("domains.dnsRecordsWindow.btnApplyTemplate"),
            Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5,
            Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        btnApplyTemplate.SetResourceReference(StyleProperty, "AccentBtn");
        btnApplyTemplate.Click += async (_, _) => await OnApplyTemplateAsync();
        toolbar.Children.Add(btnApplyTemplate);

        var btnSaveTemplate = new Button
        {
            Content = Loc.T("domains.dnsRecordsWindow.btnSaveTemplate"),
            Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5,
            Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        btnSaveTemplate.SetResourceReference(StyleProperty, "GhostBtn");
        btnSaveTemplate.Click += (_, _) => OnSaveAsTemplate();
        toolbar.Children.Add(btnSaveTemplate);

        var btnManageTemplates = new Button
        {
            Content = "⚙",
            Padding = new Thickness(8, 6, 8, 6), FontSize = 12,
            Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
            ToolTip = Loc.T("domains.dnsRecordsWindow.tooltipManageTemplates"),
        };
        btnManageTemplates.SetResourceReference(StyleProperty, "GhostBtn");
        btnManageTemplates.Click += (_, _) => new DnsTemplateEditorDialog { Owner = this }.ShowDialog();
        toolbar.Children.Add(btnManageTemplates);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        _status.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_status);
        root.Children.Add(toolbar);
        DockPanel.SetDock(toolbar, Dock.Top);

        // ── List of records ─────────────────────────────────────────
        _list.ItemsSource = _records;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        var gv = new GridView { AllowsColumnReorder = false };
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("domains.dns.colType"), Width = 70, DisplayMemberBinding = new Binding("Type") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("domains.dns.colName"), Width = 220, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("domains.dns.colValue"), Width = 340, DisplayMemberBinding = new Binding("Value") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("domains.dns.colTtl"), Width = 70, DisplayMemberBinding = new Binding("Ttl") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("domains.dns.colPrio"), Width = 60, DisplayMemberBinding = new Binding("Priority") });
        // Actions column
        var actionsCol = new GridViewColumn { Header = "", Width = 80 };
        var factory = new System.Windows.FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var btnEditF = new System.Windows.FrameworkElementFactory(typeof(Button));
        btnEditF.SetValue(Button.ContentProperty, "✎");
        btnEditF.SetValue(Button.BackgroundProperty, Brushes.Transparent);
        btnEditF.SetValue(Button.BorderThicknessProperty, new Thickness(0));
        btnEditF.SetValue(Button.CursorProperty, Cursors.Hand);
        btnEditF.SetValue(Button.PaddingProperty, new Thickness(6, 2, 6, 2));
        btnEditF.SetValue(Button.ToolTipProperty, Loc.T("domains.dnsRecordsWindow.tooltipEdit"));
        btnEditF.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (s, _) =>
        {
            if (s is Button b && b.DataContext is DnsRecord rec) await EditAsync(rec);
        }));
        factory.AppendChild(btnEditF);

        var btnDelF = new System.Windows.FrameworkElementFactory(typeof(Button));
        btnDelF.SetValue(Button.ContentProperty, "🗑");
        btnDelF.SetValue(Button.BackgroundProperty, Brushes.Transparent);
        btnDelF.SetValue(Button.BorderThicknessProperty, new Thickness(0));
        btnDelF.SetValue(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
        btnDelF.SetValue(Button.CursorProperty, Cursors.Hand);
        btnDelF.SetValue(Button.PaddingProperty, new Thickness(6, 2, 6, 2));
        btnDelF.SetValue(Button.ToolTipProperty, Loc.T("domains.dnsRecordsWindow.tooltipDelete"));
        btnDelF.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (s, _) =>
        {
            if (s is Button b && b.DataContext is DnsRecord rec) await DeleteAsync(rec);
        }));
        factory.AppendChild(btnDelF);
        actionsCol.CellTemplate = new DataTemplate { VisualTree = factory };
        gv.Columns.Add(actionsCol);

        _list.View = gv;
        root.Children.Add(_list);

        Content = root;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task OnApplyTemplateAsync()
    {
        var dlg = new ApplyDnsTemplateDialog(_account, _domain, _sessionId, _records.ToList())
        {
            Owner = this,
        };
        if (dlg.ShowDialog() == true) await LoadAsync();
    }

    private void OnSaveAsTemplate()
    {
        if (_records.Count == 0)
        {
            MessageBox.Show(Loc.T("domains.dnsRecordsWindow.err.noRecordsBody"), Loc.T("domains.dnsRecordsWindow.err.noRecordsTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.T("domains.dnsRecordsWindow.saveTemplatePrompt"),
            Loc.T("domains.dnsRecordsWindow.saveTemplateTitle"), string.Format(Loc.T("domains.dnsRecordsWindow.saveTemplateDefaultName"), _domain));
        if (string.IsNullOrWhiteSpace(name)) return;

        var tpl = new Core.DomainsManager.DnsTemplate { Name = name.Trim() };
        foreach (var r in _records)
        {
            // Заменяем в Name / Value подстроки домена на {domain}
            string ReplaceDomain(string s) => string.IsNullOrEmpty(s) ? s
                : System.Text.RegularExpressions.Regex.Replace(
                    s, System.Text.RegularExpressions.Regex.Escape(_domain),
                    "{domain}",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            tpl.Records.Add(new Core.DomainsManager.DnsTemplateRecord
            {
                Type = r.Type,
                Name = r.Name == "@" ? "@" : ReplaceDomain(r.Name),
                Value = ReplaceDomain(r.Value),
                Ttl = r.Ttl,
                Priority = r.Priority,
            });
        }
        Core.Config.Current.DnsTemplates.Add(tpl);
        Core.Config.Current.SaveNow();
        MessageBox.Show(string.Format(Loc.T("domains.dnsRecordsWindow.templateSaved"), tpl.Name, tpl.Records.Count),
            Loc.T("domains.dnsRecordsWindow.doneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = Loc.T("domains.dnsRecordsWindow.loading");
        try
        {
            using var client = new IspApiClient(_account.Host);
            var list = await client.GetRecordsAsync(_sessionId, _domain);
            _records.Clear();
            foreach (var r in list) _records.Add(r);
            _status.Text = string.Format(Loc.T("domains.dnsRecordsWindow.recordsCount"), list.Count);
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Loc.T("domains.dnsRecordsWindow.errBody"), ex.Message);
            MessageBox.Show(ex.Message, Loc.T("domains.dnsRecordsWindow.errTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private async Task AddAsync()
    {
        var dlg = new EditDnsRecordDialog(_domain);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var r = dlg.Result;
        _busy = true;
        _status.Text = Loc.T("domains.dnsRecordsWindow.adding");
        try
        {
            using var client = new IspApiClient(_account.Host);
            var ok = await client.UpsertRecordAsync(_sessionId, _domain, r.Type, r.Name, r.Value, r.Ttl, r.Priority);
            if (ok) { await LoadAsync(); return; }
            _status.Text = Loc.T("domains.dnsRecordsWindow.addFailed");
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Loc.T("domains.dnsRecordsWindow.errBody"), ex.Message);
            MessageBox.Show(ex.Message, Loc.T("domains.dnsRecordsWindow.errTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private async Task EditAsync(DnsRecord rec)
    {
        var dlg = new EditDnsRecordDialog(_domain, rec);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var r = dlg.Result;
        _busy = true;
        _status.Text = Loc.T("domains.dnsRecordsWindow.saving");
        try
        {
            using var client = new IspApiClient(_account.Host);
            var ok = await client.UpsertRecordAsync(_sessionId, _domain, r.Type, r.Name, r.Value, r.Ttl, r.Priority, r.Rkey);
            if (ok) { await LoadAsync(); return; }
            _status.Text = Loc.T("domains.dnsRecordsWindow.saveFailed");
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Loc.T("domains.dnsRecordsWindow.errBody"), ex.Message);
            MessageBox.Show(ex.Message, Loc.T("domains.dnsRecordsWindow.errTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private async Task DeleteAsync(DnsRecord rec)
    {
        if (string.IsNullOrEmpty(rec.Rkey))
        {
            MessageBox.Show(Loc.T("domains.dnsRecordsWindow.err.noRkeyBody"), Loc.T("domains.dnsRecordsWindow.err.noRkeyTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(
            string.Format(Loc.T("domains.dnsRecordsWindow.confirmDeleteBody"), rec.Type, rec.Name, rec.Value),
            Loc.T("domains.dnsRecordsWindow.confirmDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        _status.Text = Loc.T("domains.dnsRecordsWindow.deleting");
        try
        {
            using var client = new IspApiClient(_account.Host);
            var ok = await client.DeleteRecordAsync(_sessionId, _domain, rec.Rkey!);
            if (ok) { await LoadAsync(); return; }
            _status.Text = Loc.T("domains.dnsRecordsWindow.deleteFailed");
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Loc.T("domains.dnsRecordsWindow.errBody"), ex.Message);
            MessageBox.Show(ex.Message, Loc.T("domains.dnsRecordsWindow.errTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }
}
