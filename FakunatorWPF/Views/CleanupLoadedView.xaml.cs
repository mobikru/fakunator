using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class CleanupLoadedView : UserControl
{
    public CleanupLoadedView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is CleanupViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateFileInfo(vm);
            UpdatePreview(vm);
        }

        if (e.OldValue is CleanupViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CleanupViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(CleanupViewModel.FileName):
            case nameof(CleanupViewModel.TotalLines):
            case nameof(CleanupViewModel.FileSize):
                UpdateFileInfo(vm);
                break;
            case nameof(CleanupViewModel.PreviewLines):
                UpdatePreview(vm);
                break;
        }
    }

    private void UpdateFileInfo(CleanupViewModel vm)
    {
        TxtFileName.Text = vm.FileName ?? "???";
        TxtFileMeta.Text = $"{vm.TotalLines:N0} строк  ·  {FormatSize(vm.FileSize)}  ·  UTF-8";
    }

    private void UpdatePreview(CleanupViewModel vm)
    {
        var items = new List<PreviewRow>();
        for (int i = 0; i < vm.PreviewLines.Count; i++)
        {
            items.Add(new PreviewRow { RowNumber = i + 1, Email = vm.PreviewLines[i] });
        }
        PreviewGrid.ItemsSource = items;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private async void OnStartCleanup(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanupViewModel vm)
        {
            await vm.StartCleanup();
        }
    }
}

public class PreviewRow
{
    public int RowNumber { get; set; }
    public string Email { get; set; } = "";
}
