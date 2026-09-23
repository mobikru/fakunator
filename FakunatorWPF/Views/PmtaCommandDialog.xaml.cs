using System.Collections.Generic;
using System.Windows;
using Fakunator.Core;

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
        TxtPanel.Text = string.Format(Loc.T("pmta.commandDialog.panelPrefixFormat"), panelLabel);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (CbPause.IsChecked == true)          Selected.Add((Loc.T("pmta.commandDialog.cbPause"), "pause queue *", false));
        if (CbResume.IsChecked == true)         Selected.Add((Loc.T("pmta.commandDialog.cbResume"), "resume queue *", false));
        if (CbNormal.IsChecked == true)         Selected.Add((Loc.T("pmta.commandDialog.cbNormal"), "set queue --mode=normal *", false));
        if (CbResetCounters.IsChecked == true)  Selected.Add((Loc.T("pmta.commandDialog.cbResetCounters"), "reset counters", false));
        if (CbReload.IsChecked == true)         Selected.Add((Loc.T("pmta.commandDialog.cbReload"), "reload", false));
        if (CbClearQueues.IsChecked == true)    Selected.Add((Loc.T("pmta.commandDialog.cbClearQueues"), "delete --older-than=0s", true));

        if (Selected.Count == 0)
        {
            MessageBox.Show(this, Loc.T("pmta.commandDialog.err.noneSelectedBody"), Loc.T("pmta.commandDialog.err.noneSelectedTitle"),
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
