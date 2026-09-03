using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.Core.TelegramBot;

namespace Fakunator.ViewModels;

public class AnalyzeViewModel : INotifyPropertyChanged
{
    public enum AnalyzeState { Idle, Running, Done }

    // ── Backing fields ───────────────────────────────────────────────
    private AnalyzeState _state = AnalyzeState.Idle;
    private string? _filePath;
    private string? _fileName;
    private long _fileSize;
    private int _totalLines;
    private List<string> _previewLines = new();

    // Settings
    private string _provider = "openai";
    private string _model = "gpt-4o-mini";
    private double _spendCap = 105.0;
    private int _aiBatchSize = 20;
    private int _maxConcurrent = 8;
    private bool _skipJunk = true;
    private bool _twoPass;
    private bool _useAi = true;

    // Runtime
    private AnalyzeSnapshot? _currentSnapshot;
    private AnalyzeSnapshot? _finalSnapshot;
    private List<AnalysisResult>? _results;
    private string? _outputDir;

    // ── Properties ───────────────────────────────────────────────────

    public AnalyzeState State
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

    public string Provider
    {
        get => _provider;
        set { if (SetField(ref _provider, value)) SaveSettingsToConfig(); }
    }

    public string Model
    {
        get => _model;
        set { if (SetField(ref _model, value)) SaveSettingsToConfig(); }
    }

    public double SpendCap
    {
        get => _spendCap;
        set { if (SetField(ref _spendCap, value)) SaveSettingsToConfig(); }
    }

    public int AiBatchSize
    {
        get => _aiBatchSize;
        set { if (SetField(ref _aiBatchSize, value)) SaveSettingsToConfig(); }
    }

    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set { if (SetField(ref _maxConcurrent, value)) SaveSettingsToConfig(); }
    }

    public bool SkipJunk
    {
        get => _skipJunk;
        set { if (SetField(ref _skipJunk, value)) SaveSettingsToConfig(); }
    }

    public bool TwoPass
    {
        get => _twoPass;
        set { if (SetField(ref _twoPass, value)) SaveSettingsToConfig(); }
    }

    public bool UseAi
    {
        get => _useAi;
        set { if (SetField(ref _useAi, value)) SaveSettingsToConfig(); }
    }

    public AnalyzeSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        set => SetField(ref _currentSnapshot, value);
    }

    public AnalyzeSnapshot? FinalSnapshot
    {
        get => _finalSnapshot;
        set => SetField(ref _finalSnapshot, value);
    }

    public List<AnalysisResult>? Results
    {
        get => _results;
        set => SetField(ref _results, value);
    }

    /// <summary>
    /// Live stream of analysis results pushed by the worker as snapshots arrive.
    /// Bound to the DataGrid in <c>AnalyzeRunningView</c>.
    /// Mutation is marshalled to the UI thread (collection synchronization is enabled in ctor).
    /// </summary>
    public ObservableCollection<AnalysisResult> LiveResults { get; } = new();

    /// <summary>
    /// Max number of items kept in <see cref="LiveResults"/>. Oldest are evicted past this.
    /// Set to <c>int.MaxValue</c> to disable trimming.
    /// </summary>
    public int LiveResultsCap { get; set; } = 200;

    public string? OutputDir
    {
        get => _outputDir;
        set => SetField(ref _outputDir, value);
    }

    // ── Commands ─────────────────────────────────────────────────────

    public ICommand LoadFileCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    // ── Internal state ───────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private AnalyzeWorker? _worker;
    private readonly object _liveResultsLock = new();

    public AnalyzeViewModel()
    {
        // Enable cross-thread reads of LiveResults so that DataGrid virtualization can
        // safely access the collection while the UI thread mutates it.
        BindingOperations.EnableCollectionSynchronization(LiveResults, _liveResultsLock);

        LoadSettingsFromConfig();

        // Listen for external config changes (e.g. SettingsDialog edit) so sidebar
        // and analysis pick up the new provider/model without restart.
        Config.Changed += OnConfigChanged;

        LoadFileCommand = new RelayCommand<string>(LoadFile);
        StartCommand = new RelayCommand(
            _ => _ = StartAnalysis(),
            _ => State == AnalyzeState.Idle && TotalLines > 0);
        StopCommand = new RelayCommand(
            _ => StopAnalysis(),
            _ => State == AnalyzeState.Running);
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Avoid re-saving while we re-read; SetField guards no-op assignments anyway.
        var cfg = Config.Current;
        Provider = cfg.AiProvider;
        Model = cfg.AiModel;
        SpendCap = cfg.AnalyzeSpendCap;
        AiBatchSize = cfg.AnalyzeAiBatchSize;
        MaxConcurrent = cfg.AnalyzeMaxConcurrent;
        TwoPass = cfg.AnalyzeTwoPass;
        UseAi = cfg.AnalyzeUseAi;
        SkipJunk = cfg.AnalyzeSkipJunk;
    }

    // ── Config persistence ──────────────────────────────────────────

    private void LoadSettingsFromConfig()
    {
        var cfg = Config.Current;
        _provider = cfg.AiProvider;
        _model = cfg.AiModel;
        _spendCap = cfg.AnalyzeSpendCap;
        _aiBatchSize = cfg.AnalyzeAiBatchSize;
        _maxConcurrent = cfg.AnalyzeMaxConcurrent;
        _twoPass = cfg.AnalyzeTwoPass;
        _useAi = cfg.AnalyzeUseAi;
        _skipJunk = cfg.AnalyzeSkipJunk;
    }

    private void SaveSettingsToConfig()
    {
        var cfg = Config.Current;
        cfg.AiProvider = _provider;
        cfg.AiModel = _model;
        cfg.AnalyzeSpendCap = _spendCap;
        cfg.AnalyzeAiBatchSize = _aiBatchSize;
        cfg.AnalyzeMaxConcurrent = _maxConcurrent;
        cfg.AnalyzeTwoPass = _twoPass;
        cfg.AnalyzeUseAi = _useAi;
        cfg.AnalyzeSkipJunk = _skipJunk;
        cfg.Save();
    }

    // ── Public methods ──────────────────────────────────────────────

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
                    if (!string.IsNullOrEmpty(normalized) && normalized.Contains('@'))
                    {
                        preview.Add(normalized);
                        count++;
                    }
                }
            }
            PreviewLines = preview;

            CurrentSnapshot = null;
            FinalSnapshot = null;
            Results = null;

            // Trigger re-evaluation
            OnPropertyChanged(nameof(TotalLines));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Ошибка при загрузке файла:\n{ex.Message}",
                "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public async Task StartAnalysis()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        // Read all emails from file (in background to not freeze UI)
        var filePath = FilePath;
        var emails = await Task.Run(() =>
        {
            var list = new List<string>();
            using var reader = new StreamReader(filePath, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var normalized = EmailHelpers.Normalize(line);
                if (!string.IsNullOrEmpty(normalized) && normalized.Contains('@'))
                    list.Add(normalized);
            }
            return list;
        });

        if (emails.Count == 0) return;

        // Resolve API key
        var apiKey = _provider.ToLowerInvariant() switch
        {
            "anthropic" => Config.Current.AnthropicKey,
            _ => Config.Current.OpenAiKey,
        };

        // If AI not enabled, clear key to skip AI pass
        if (!_useAi)
            apiKey = "";

        OutputDir = Paths.AnalyzeRoot;

        _cts = new CancellationTokenSource();
        State = AnalyzeState.Running;
        CurrentSnapshot = null;
        FinalSnapshot = null;
        ClearLiveResults();
        NotificationHub.Notify($"🔬 <b>AI-анализ запущен</b>\nадресов: <b>{TotalLines:N0}</b>, провайдер: {_provider}, модель: {_model}");

        _worker = new AnalyzeWorker(
            emails, _provider, apiKey, _model,
            _spendCap, _aiBatchSize, _maxConcurrent,
            _skipJunk, _twoPass, NameDb.FindDbPath(),
            Config.Current.AnalyzeBatchDelay);

        var progressHandler = new Progress<AnalyzeSnapshot>(snapshot =>
        {
            CurrentSnapshot = snapshot;
            PushFreshFeed(snapshot.FreshFeed);
        });

        try
        {
            await Task.Run(() => _worker.RunAsync(progressHandler, _cts.Token));
            FinalSnapshot = CurrentSnapshot;
            Results = _worker.GetResults();
            State = AnalyzeState.Done;
            var processed = CurrentSnapshot?.Processed ?? 0;
            NotificationHub.Notify($"✅ <b>AI-анализ завершён</b>\nобработано: <b>{processed:N0}</b>");
        }
        catch (OperationCanceledException)
        {
            FinalSnapshot = CurrentSnapshot;
            Results = _worker.GetResults();
            State = AnalyzeState.Done;
            NotificationHub.Notify($"⏸ <b>AI-анализ остановлен</b>\nобработано: {CurrentSnapshot?.Processed ?? 0:N0}");
        }
        catch (Exception ex)
        {
            FinalSnapshot = CurrentSnapshot;
            Results = _worker.GetResults();
            State = AnalyzeState.Done;
            Debug.WriteLine($"Analyze error: {ex}");
            NotificationHub.Notify($"❌ <b>AI-анализ упал</b>\n<code>{ex.Message}</code>");
        }
    }

    private void StopAnalysis()
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
        Results = null;
        OutputDir = null;
        ClearLiveResults();
        State = AnalyzeState.Idle;
    }

    // ── LiveResults helpers (UI thread) ─────────────────────────────

    private void ClearLiveResults()
    {
        lock (_liveResultsLock)
        {
            LiveResults.Clear();
        }
    }

    private void PushFreshFeed(List<AnalysisResult>? feed)
    {
        if (feed is null || feed.Count == 0) return;
        lock (_liveResultsLock)
        {
            foreach (var item in feed)
                LiveResults.Add(item);

            // Trim from the front (oldest first) to keep the collection bounded.
            // RemoveAt(0) is O(n) and fires CollectionChanged per item — under heavy
            // AI-batch load this swamps the UI dispatcher. Drop in one Move op via
            // Move-and-Remove-from-end pattern.
            int cap = LiveResultsCap;
            if (cap > 0 && LiveResults.Count > cap)
            {
                int drop = LiveResults.Count - cap;
                for (int i = 0; i < drop; i++)
                    LiveResults.RemoveAt(0);
            }
        }
    }

    // ── Estimation helpers ──────────────────────────────────────────

    /// <summary>
    /// Rough cost estimate for the loaded file, given current settings.
    /// Returns the raw estimate plus whether the spend cap will clamp it.
    /// </summary>
    public (double EstCost, int EstTokensIn, int EstTokensOut, bool CappedByLimit) EstimateBudget()
    {
        if (TotalLines == 0) return (0, 0, 0, false);

        // If AI-резерв выключен — pass 1 не идёт в AI, остаются только DB-хиты.
        if (!_useAi) return (0, 0, 0, false);

        // Эвристика: ~40% покрывается DB (фамилия/имя в локал-парте), остальное в AI.
        // SkipJunk выкидывает ~15% мусорных адресов (цифры/хеши) до AI.
        double aiRatio = 0.6;
        if (_skipJunk) aiRatio *= 0.85;

        int aiEmails = (int)(TotalLines * aiRatio);
        int batches = (int)Math.Ceiling((double)aiEmails / _aiBatchSize);

        const int tokensPerEmailIn = 25;
        const int tokensPerEmailOut = 20;
        const int systemPromptTokens = 200;

        int totalIn = batches * systemPromptTokens + aiEmails * tokensPerEmailIn;
        int totalOut = aiEmails * tokensPerEmailOut;

        double cost = AiClient.EstimateCost(_model, totalIn, totalOut);

        // 2-pass: повторно прогоняет неудачные ответы (~30% обычно).
        if (_twoPass)
        {
            int pass2Emails = (int)(aiEmails * 0.30);
            int pass2Batches = (int)Math.Ceiling((double)pass2Emails / _aiBatchSize);
            int pass2In = pass2Batches * systemPromptTokens + pass2Emails * tokensPerEmailIn;
            int pass2Out = pass2Emails * tokensPerEmailOut;
            totalIn += pass2In;
            totalOut += pass2Out;
            cost += AiClient.EstimateCost(_model, pass2In, pass2Out);
        }

        bool capped = _spendCap > 0 && cost > _spendCap;
        if (capped) cost = _spendCap;

        return (cost, totalIn, totalOut, capped);
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

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
