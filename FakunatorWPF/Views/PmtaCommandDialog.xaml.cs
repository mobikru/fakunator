using System.Collections.Generic;
using System.Windows;

namespace Fakunator.Views;

/// <summary>Диалог выбора набора PMTA-команд к последовательному применению.
/// Каждая пара (label, command) — checkbox; при Apply возвращается список
/// выбранных пар. Опасные (delete queue) визуально помечены.</summary>
public partial class PmtaCommandDialog : Window
{
    public List<(string Label, string Command, bool Dangerous)> Selected { get; } = new();

    public PmtaCommandDialog(string panelLabel)
    {
        InitializeComponent();
        TxtPanel.Text = "Панель: " + panelLabel;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (CbPause.IsChecked == true)          Selected.Add(("⏸ Пауза", "pause queue *", false));
        if (CbResume.IsChecked == true)         Selected.Add(("▶ Убрать паузу", "resume queue *", false));
        if (CbNormal.IsChecked == true)         Selected.Add(("⚡ Режим нормал", "set queue --mode=normal *", false));
        if (CbResetCounters.IsChecked == true)  Selected.Add(("🔢 Сброс счётчиков", "reset counters", false));
        if (CbReload.IsChecked == true)         Selected.Add(("🔄 Reload конфига", "reload", false));
        if (CbClearQueues.IsChecked == true)    Selected.Add(("🗑 Очистить очереди", "delete --older-than=0s", true));

        if (Selected.Count == 0)
        {
            MessageBox.Show(this, "Отметь хотя бы одну команду.", "Управление PMTA",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
