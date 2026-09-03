using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.ViewModels;
using Microsoft.Win32;

namespace Fakunator.Views;

public partial class CleanupIdleView : UserControl
{
    private Brush? _originalBorderBrush;

    public CleanupIdleView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;

            // Highlight border with accent color
            _originalBorderBrush ??= DropBorder.BorderBrush;
            DropBorder.BorderBrush = (Brush)FindResource("AccentBrush");
            DropBorder.BorderThickness = new Thickness(2);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Restore original dashed border
        if (_originalBorderBrush != null)
        {
            DropBorder.BorderBrush = _originalBorderBrush;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // Restore border
        if (_originalBorderBrush != null)
        {
            DropBorder.BorderBrush = _originalBorderBrush;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files is { Length: > 0 })
            {
                if (DataContext is CleanupViewModel vm)
                {
                    vm.LoadFile(files[0]);
                }
            }
        }

        e.Handled = true;
    }

    private void OnSelectFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите файл с email-адресами",
            Filter = "Text files|*.txt;*.csv;*.tsv|All files|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() == true)
        {
            if (DataContext is CleanupViewModel vm)
            {
                vm.LoadFile(dlg.FileName);
            }
        }
    }
}
