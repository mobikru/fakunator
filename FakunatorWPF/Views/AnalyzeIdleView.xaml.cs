using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Fakunator.ViewModels;
using Microsoft.Win32;

namespace Fakunator.Views;

public partial class AnalyzeIdleView : UserControl
{
    public AnalyzeIdleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AnalyzeViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is AnalyzeViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            SyncFromVm(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AnalyzeViewModel vm) return;

        if (e.PropertyName is nameof(AnalyzeViewModel.TotalLines) or
            nameof(AnalyzeViewModel.PreviewLines) or
            nameof(AnalyzeViewModel.FileName) or
            nameof(AnalyzeViewModel.FileSize))
        {
            SyncFromVm(vm);
        }
    }

    private void SyncFromVm(AnalyzeViewModel vm)
    {
        TxtEmailCount.Text = $"{vm.TotalLines:N0} строк";

        if (!string.IsNullOrEmpty(vm.FileName))
        {
            var sizeKb = vm.FileSize / 1024.0;
            TxtFileInfo.Text = sizeKb >= 1024
                ? $"{vm.FileName} · {sizeKb / 1024.0:F1} МБ"
                : $"{vm.FileName} · {sizeKb:F0} КБ";
        }
        else
        {
            TxtFileInfo.Text = "";
        }

        // Populate preview grid
        var rows = vm.PreviewLines
            .Select((email, idx) => new { Index = idx + 1, Email = email })
            .ToList();
        PreviewGrid.ItemsSource = rows;
    }

    // ── File loading ────────────────────────────────────────────────

    private void OnLoadFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите файл с email-адресами",
            Filter = "Text files|*.txt;*.csv;*.tsv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
        {
            if (DataContext is AnalyzeViewModel vm)
                vm.LoadFile(dlg.FileName);
        }
    }

    // ── Start ───────────────────────────────────────────────────────

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnalyzeViewModel vm)
            vm.StartCommand.Execute(null);
    }
}
