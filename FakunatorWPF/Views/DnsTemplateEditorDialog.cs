using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.DomainsManager;

namespace Fakunator.Views;

/// <summary>
/// Редактор пользовательских DNS-шаблонов. Слева — список (builtin + user),
/// справа — details выбранного (name, description, записи). Builtin можно
/// только клонировать; user — редактировать и удалять. Сохранение в config.
/// </summary>
public class DnsTemplateEditorDialog : Window
{
    private readonly ObservableCollection<TemplateItem> _items = new();
    private readonly ListBox _list = new();
    private readonly TextBox _tbName = new();
    private readonly TextBox _tbDesc = new();
    private readonly ObservableCollection<DnsTemplateRecord> _records = new();
    private readonly ListView _recordsList = new();
    private readonly StackPanel _rightPanel = new();
    private TemplateItem? _selected;

    public DnsTemplateEditorDialog()
    {
        Title = "Редактор DNS-шаблонов";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg1Brush");

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── LEFT: list of templates ─────────────────────────────────
        var leftDock = new DockPanel();
        var leftHdr = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var addBtn = new Button
        {
            Content = "+ Новый", Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5,
            Cursor = Cursors.Hand,
        };
        addBtn.SetResourceReference(StyleProperty, "AccentBtn");
        addBtn.Click += (_, _) => OnNewTemplate();
        var delBtn = new Button
        {
            Content = "× Удалить", Padding = new Thickness(10, 6, 10, 6), FontSize = 11.5,
            Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0),
        };
        delBtn.SetResourceReference(StyleProperty, "DangerBtn");
        delBtn.Click += (_, _) => OnDeleteTemplate();
        DockPanel.SetDock(delBtn, Dock.Right);
        leftHdr.Children.Add(delBtn);
        leftHdr.Children.Add(addBtn);
        DockPanel.SetDock(leftHdr, Dock.Top);
        leftDock.Children.Add(leftHdr);

        _list.ItemsSource = _items;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.DisplayMemberPath = "Display";
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        leftDock.Children.Add(_list);
        Grid.SetColumn(leftDock, 0);
        root.Children.Add(leftDock);

        // ── RIGHT: details ──────────────────────────────────────────
        _rightPanel.Margin = new Thickness(0);
        _rightPanel.Children.Add(Lbl("Название"));
        _rightPanel.Children.Add(StyleInput(_tbName));
        _rightPanel.Children.Add(Lbl("Описание"));
        _rightPanel.Children.Add(StyleInput(_tbDesc));

        _rightPanel.Children.Add(Lbl("Записи"));
        _recordsList.ItemsSource = _records;
        _recordsList.BorderThickness = new Thickness(0);
        _recordsList.Background = Brushes.Transparent;
        _recordsList.Height = 260;
        _recordsList.SelectionMode = SelectionMode.Single;
        _recordsList.MouseDoubleClick += (_, _) => OnEditRecord();
        var gv = new GridView { AllowsColumnReorder = false };
        gv.Columns.Add(Col("ТИП", 60, "Type"));
        gv.Columns.Add(Col("ИМЯ", 140, "Name"));
        gv.Columns.Add(Col("ЗНАЧЕНИЕ", 320, "Value"));
        gv.Columns.Add(Col("TTL", 55, "Ttl"));
        gv.Columns.Add(Col("PRIO", 50, "Priority"));
        _recordsList.View = gv;
        _rightPanel.Children.Add(_recordsList);

        var recBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var addRecBtn = new Button { Content = "+ Запись", Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5, Cursor = Cursors.Hand };
        addRecBtn.SetResourceReference(StyleProperty, "GhostBtn");
        addRecBtn.Click += (_, _) => OnAddRecord();
        var editRecBtn = new Button { Content = "✎ Изменить", Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0) };
        editRecBtn.SetResourceReference(StyleProperty, "GhostBtn");
        editRecBtn.Click += (_, _) => OnEditRecord();
        var delRecBtn = new Button { Content = "× Удалить", Padding = new Thickness(12, 6, 12, 6), FontSize = 11.5, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0) };
        delRecBtn.SetResourceReference(StyleProperty, "DangerBtn");
        delRecBtn.Click += (_, _) => OnDeleteRecord();
        recBtns.Children.Add(addRecBtn);
        recBtns.Children.Add(editRecBtn);
        recBtns.Children.Add(delRecBtn);
        _rightPanel.Children.Add(recBtns);

        // Save + Close
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        var closeBtn = new Button { Content = "Закрыть", Padding = new Thickness(16, 8, 16, 8), MinWidth = 100 };
        closeBtn.SetResourceReference(StyleProperty, "GhostBtn");
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        var saveBtn = new Button { Content = "Сохранить", Padding = new Thickness(20, 8, 20, 8), MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        saveBtn.SetResourceReference(StyleProperty, "PrimaryBtn");
        saveBtn.Click += (_, _) => SaveCurrent();
        DockPanel.SetDock(saveBtn, Dock.Right);
        bottom.Children.Add(saveBtn);
        bottom.Children.Add(closeBtn);
        _rightPanel.Children.Add(bottom);

        Grid.SetColumn(_rightPanel, 2);
        root.Children.Add(_rightPanel);

        Content = root;

        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var t in DnsTemplatePresets.All)
            _items.Add(new TemplateItem(t, true));
        foreach (var t in Config.Current.DnsTemplates)
            _items.Add(new TemplateItem(t, false));
        if (_items.Count > 0) _list.SelectedIndex = 0;
    }

    private void OnSelectionChanged()
    {
        _selected = _list.SelectedItem as TemplateItem;
        if (_selected == null)
        {
            _tbName.Text = ""; _tbDesc.Text = ""; _records.Clear(); return;
        }
        _tbName.Text = _selected.Template.Name;
        _tbDesc.Text = _selected.Template.Description;
        _records.Clear();
        foreach (var r in _selected.Template.Records) _records.Add(Clone(r));
        _tbName.IsEnabled = !_selected.IsBuiltin;
        _tbDesc.IsEnabled = !_selected.IsBuiltin;
    }

    private void OnNewTemplate()
    {
        var t = new DnsTemplate { Name = "Новый шаблон", Description = "" };
        Config.Current.DnsTemplates.Add(t);
        Config.Current.SaveNow();
        Reload();
        _list.SelectedItem = _items.FirstOrDefault(i => i.Template == t);
    }

    private void OnDeleteTemplate()
    {
        if (_selected == null) return;
        if (_selected.IsBuiltin)
        {
            MessageBox.Show("Встроенные шаблоны нельзя удалить (только клонировать через «+ Новый»).",
                "Только для чтения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Удалить шаблон «{_selected.Template.Name}»?", "Подтвердить",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Config.Current.DnsTemplates.Remove(_selected.Template);
        Config.Current.SaveNow();
        Reload();
    }

    private void OnAddRecord()
    {
        if (_selected == null || _selected.IsBuiltin) { WarnReadOnly(); return; }
        var r = new DnsTemplateRecord();
        if (EditRecord(r)) _records.Add(r);
    }

    private void OnEditRecord()
    {
        if (_selected == null || _selected.IsBuiltin) { WarnReadOnly(); return; }
        if (_recordsList.SelectedItem is not DnsTemplateRecord r)
        {
            MessageBox.Show("Сначала выделите запись в списке — потом жми «✎ Изменить».",
                "Не выбрано", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (EditRecord(r))
        {
            var i = _records.IndexOf(r);
            if (i >= 0) { _records.RemoveAt(i); _records.Insert(i, r); }
        }
    }

    private void OnDeleteRecord()
    {
        if (_selected == null || _selected.IsBuiltin) { WarnReadOnly(); return; }
        if (_recordsList.SelectedItem is not DnsTemplateRecord r)
        {
            MessageBox.Show("Сначала выделите запись в списке — потом жми «× Удалить».",
                "Не выбрано", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _records.Remove(r);
    }

    private bool EditRecord(DnsTemplateRecord r)
    {
        // Простейший инлайн-редактор через InputBox (5 полей — слишком много для VB.Interaction,
        // делаем модалку на лету).
        var dlg = new Window
        {
            Title = "Запись шаблона", Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Owner = this,
        };
        dlg.SetResourceReference(BackgroundProperty, "Bg1Brush");
        var p = new StackPanel { Margin = new Thickness(20) };
        var typeBox = new ComboBox { Height = 32, FontSize = 12 };
        foreach (var t in new[] { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV" }) typeBox.Items.Add(t);
        typeBox.SelectedItem = r.Type;
        p.Children.Add(Lbl("Тип")); p.Children.Add(typeBox);
        var nameBox = MkInput(r.Name); p.Children.Add(Lbl("Имя (@ = корень)")); p.Children.Add(nameBox);
        var valueBox = MkInput(r.Value); p.Children.Add(Lbl("Значение (можно {domain}, {ip}, …)")); p.Children.Add(valueBox);
        var ttlBox = MkInput(r.Ttl.ToString()); p.Children.Add(Lbl("TTL")); p.Children.Add(ttlBox);
        var prioBox = MkInput(r.Priority?.ToString() ?? ""); p.Children.Add(Lbl("Приоритет (для MX)")); p.Children.Add(prioBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var okB = new Button { Content = "OK", Padding = new Thickness(20, 6, 20, 6), MinWidth = 90 };
        okB.SetResourceReference(StyleProperty, "PrimaryBtn");
        var caB = new Button { Content = "Отмена", Padding = new Thickness(16, 6, 16, 6), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        caB.SetResourceReference(StyleProperty, "GhostBtn");
        bool ok = false;
        okB.Click += (_, _) => { ok = true; dlg.Close(); };
        caB.Click += (_, _) => dlg.Close();
        btnRow.Children.Add(okB); btnRow.Children.Add(caB);
        p.Children.Add(btnRow);
        dlg.Content = p;
        dlg.ShowDialog();
        if (!ok) return false;

        r.Type = typeBox.SelectedItem?.ToString() ?? "A";
        r.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "@" : nameBox.Text.Trim();
        r.Value = valueBox.Text.Trim();
        int.TryParse(ttlBox.Text, out var ttl);
        r.Ttl = ttl > 0 ? ttl : 3600;
        r.Priority = int.TryParse(prioBox.Text, out var pr) && pr > 0 ? pr : null;
        return true;
    }

    private void SaveCurrent()
    {
        if (_selected == null || _selected.IsBuiltin) { WarnReadOnly(); return; }
        _selected.Template.Name = _tbName.Text.Trim();
        _selected.Template.Description = _tbDesc.Text.Trim();
        _selected.Template.Records = _records.ToList();
        Config.Current.SaveNow();
        MessageBox.Show("Шаблон сохранён.", "OK", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    private void WarnReadOnly() =>
        MessageBox.Show("Встроенные шаблоны только для чтения — клонируй через «+ Новый».",
            "Только для чтения", MessageBoxButton.OK, MessageBoxImage.Information);

    private static DnsTemplateRecord Clone(DnsTemplateRecord r) => new()
    { Type = r.Type, Name = r.Name, Value = r.Value, Ttl = r.Ttl, Priority = r.Priority };

    private class TemplateItem
    {
        public DnsTemplate Template { get; }
        public bool IsBuiltin { get; }
        public string Display => (IsBuiltin ? "⚙ " : "  ") + Template.Name + $"  ({Template.Records.Count})";
        public TemplateItem(DnsTemplate t, bool builtin) { Template = t; IsBuiltin = builtin; }
    }

    private static TextBlock Lbl(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.Medium };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        return tb;
    }
    private static Control StyleInput(TextBox tb)
    {
        tb.Height = 30; tb.FontSize = 12; tb.Padding = new Thickness(10, 0, 10, 0);
        tb.VerticalContentAlignment = VerticalAlignment.Center; tb.BorderThickness = new Thickness(1);
        tb.SetResourceReference(Control.BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(Control.BorderBrushProperty, "Border2Brush");
        tb.SetResourceReference(Control.ForegroundProperty, "Fg1Brush");
        return tb;
    }
    private static TextBox MkInput(string text) => (TextBox)StyleInput(new TextBox { Text = text });
    private static GridViewColumn Col(string header, int width, string bindPath)
    {
        var col = new GridViewColumn { Header = header, Width = width };
        var factory = new System.Windows.FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding(bindPath));
        factory.SetValue(TextBlock.FontSizeProperty, 11.5);
        col.CellTemplate = new DataTemplate { VisualTree = factory };
        return col;
    }
}
