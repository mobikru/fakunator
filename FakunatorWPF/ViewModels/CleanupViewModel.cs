using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakunator.Core;

namespace Fakunator.ViewModels;

public class CleanupViewModel : INotifyPropertyChanged
{
    public enum CleanupState { Idle, Loaded, Running, Paused, Done }

    // ── Backing fields ───────────────────────────────────────────────
    private CleanupState _state = CleanupState.Idle;
    private string? _filePath;
    private string? _fileName;
    private long _fileSize;
    private int _totalLines;
    private List<string> _previewLines = new();
    private SnapshotBatch? _currentSnapshot;
    private SnapshotBatch? _finalSnapshot;
    private string? _outputDir;

    // ── Properties ───────────────────────────────────────────────────
    public CleanupState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string? FilePath
    {
        get => _filePath;
        set => SetField(ref _filePath, value);
    }

    public string? FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public long FileSize
    {
        get => _fileSize;
        set => SetField(ref _fileSize, value);
    }

    public int TotalLines
    {
        get => _totalLines;
        set => SetField(ref _totalLines, value);
    }

    public List<string> PreviewLines
    {
        get => _previewLines;
        set => SetField(ref _previewLines, value);
    }

    public SnapshotBatch? CurrentSnapshot
    {
        get => _currentSnapshot;
        set => SetField(ref _currentSnapshot, value);
    }

    public SnapshotBatch? FinalSnapshot
    {
        get => _finalSnapshot;
        set => SetField(ref _finalSnapshot, value);
    }

    public string? OutputDir
    {
        get => _outputDir;
        set => SetField(ref _outputDir, value);
    }

    // ── Commands ─────────────────────────────────────────────────────
    public ICommand LoadFileCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }

    // ── Internal state ───────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private Blocklists? _blocklists;

    public CleanupViewModel()
    {
        LoadFileCommand = new RelayCommand<string>(LoadFile);
        StartCommand = new RelayCommand(_ => _ = StartCleanup(), _ => State == CleanupState.Loaded);
        PauseCommand = new RelayCommand(_ => TogglePause(), _ => State == CleanupState.Running || State == CleanupState.Paused);
        StopCommand = new RelayCommand(_ => StopCleanup(), _ => State == CleanupState.Running || State == CleanupState.Paused);
    }

    // ── Public methods ───────────────────────────────────────────────

    public void LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            FilePath = path;
            FileName = Path.GetFileName(path);
            FileSize = new FileInfo(path).Length;

            TotalLines = EmailHelpers.CountLines(path);

            var preview = new List<string>(200);
            using (var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                int count = 0;
                while (count < 200 && (line = reader.ReadLine()) != null)
                {
                    var normalized = EmailHelpers.Normalize(line);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        preview.Add(normalized);
                        count++;
                    }
                }
            }
            PreviewLines = preview;

            CurrentSnapshot = null;
            FinalSnapshot = null;
            State = CleanupState.Loaded;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                string.Format(Loc.T("cleanup.err.loadFileBody"), ex.Message, ex.StackTrace),
                Loc.T("cleanup.err.loadFileTitle"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public async Task StartCleanup()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        // Load blocklists if not loaded
        _blocklists ??= Blocklists.Load(Blocklists.FindDataDir());

        // Determine output dir: <exe>/output/cleanup/<filename_without_ext>/
        var fileNameNoExt = Path.GetFileNameWithoutExtension(FilePath);
        var outputDir = string.IsNullOrEmpty(Config.Current.OutputDir)
            ? Path.Combine(Paths.CleanupRoot, fileNameNoExt ?? "output")
            : Config.Current.OutputDir;

        OutputDir = outputDir;

        var enabledSet = new HashSet<string>(Config.Current.EnabledFilters, StringComparer.OrdinalIgnoreCase);

        _cts = new CancellationTokenSource();
        State = CleanupState.Running;

        var worker = new CleanupWorker(
            FilePath, outputDir, _blocklists, enabledSet,
            Config.Current.GmailStrict, Config.Current.UseSpamhausDbl);

        var progressHandler = new Progress<SnapshotBatch>(snapshot =>
        {
            CurrentSnapshot = snapshot;
        });

        try
        {
            await worker.RunAsync(progressHandler, _cts.Token);
            FinalSnapshot = CurrentSnapshot;
            State = CleanupState.Done;
        }
        catch (OperationCanceledException)
        {
            FinalSnapshot = CurrentSnapshot;
            State = CleanupState.Done;
        }
        catch (Exception ex)
        {
            FinalSnapshot = CurrentSnapshot;
            State = CleanupState.Done;
            System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex}");
            System.Windows.MessageBox.Show(
                string.Format(Loc.T("cleanup.err.runBody"), ex.Message, ex.StackTrace),
                Loc.T("cleanup.err.runTitle"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void TogglePause()
    {
        // Pause/resume not implemented at the Task level in this milestone
        // (would require ManualResetEventSlim in the worker)
    }

    private void StopCleanup()
    {
        _cts?.Cancel();
    }

    public void ResetToIdle()
    {
        FilePath = null;
        FileName = null;
        FileSize = 0;
        TotalLines = 0;
        PreviewLines = new List<string>();
        CurrentSnapshot = null;
        FinalSnapshot = null;
        OutputDir = null;
        State = CleanupState.Idle;
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

// ── Simple RelayCommand implementation ───────────────────────────────
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke((T?)parameter) ?? true;

    public void Execute(object? parameter) =>
        _execute((T?)parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) =>
        _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
