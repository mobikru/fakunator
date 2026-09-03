using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.Core.TelegramBot;

namespace Fakunator.ViewModels;

public class SmtpViewModel : INotifyPropertyChanged
{
    public enum SmtpState { Idle, Running, Done }

    // ── Backing fields ───────────────────────────────────────────────
    private SmtpState _state = SmtpState.Idle;
    private string _emailsText = "";
    private string _proxiesText = "";
    private int _emailCount;
    private int _proxyCount;

    // Settings
    private int _batchSize = 16;
    private int _workersPerProxy = 3;
    private int _timeoutSec = 71;
    private int _maxConsecErrors = 10;
    private int _retries = 3;
    private int _sessionDelaySec = 2;
    private int _recoveryMin = 5;
    private string _heloDomain = "";
    private string _mailFrom = "";

    // Runtime
    private SmtpSnapshotBatch? _currentSnapshot;
    private SmtpSnapshotBatch? _finalSnapshot;
    private string? _outputDir;

    // ── Properties ───────────────────────────────────────────────────

    public SmtpState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string EmailsText
    {
        get => _emailsText;
        set
        {
            if (SetField(ref _emailsText, value))
            {
                EmailCount = string.IsNullOrWhiteSpace(value) ? 0
                    : value.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
            }
        }
    }

    public string ProxiesText
    {
        get => _proxiesText;
        set
        {
            if (SetField(ref _proxiesText, value))
            {
                ProxyCount = string.IsNullOrWhiteSpace(value) ? 0
                    : value.Split('\n').Count(l => ProxySpec.TryParse(l) != null);
                // Persist proxies to config
                Config.Current.Proxies = value;
                Config.Current.Save();
            }
        }
    }

    public int EmailCount
    {
        get => _emailCount;
        set => SetField(ref _emailCount, value);
    }

    public int ProxyCount
    {
        get => _proxyCount;
        set => SetField(ref _proxyCount, value);
    }

    public int BatchSize
    {
        get => _batchSize;
        set { if (SetField(ref _batchSize, value)) SaveSettingsToConfig(); }
    }

    public int WorkersPerProxy
    {
        get => _workersPerProxy;
        set { if (SetField(ref _workersPerProxy, value)) SaveSettingsToConfig(); }
    }

    public int TimeoutSec
    {
        get => _timeoutSec;
        set { if (SetField(ref _timeoutSec, value)) SaveSettingsToConfig(); }
    }

    public int MaxConsecErrors
    {
        get => _maxConsecErrors;
        set { if (SetField(ref _maxConsecErrors, value)) SaveSettingsToConfig(); }
    }

    public int Retries
    {
        get => _retries;
        set { if (SetField(ref _retries, value)) SaveSettingsToConfig(); }
    }

    public int SessionDelaySec
    {
        get => _sessionDelaySec;
        set { if (SetField(ref _sessionDelaySec, value)) SaveSettingsToConfig(); }
    }

    public int RecoveryMin
    {
        get => _recoveryMin;
        set { if (SetField(ref _recoveryMin, value)) SaveSettingsToConfig(); }
    }

    public string HeloDomain
    {
        get => _heloDomain;
        set { if (SetField(ref _heloDomain, value ?? "")) SaveSettingsToConfig(); }
    }

    public string MailFrom
    {
        get => _mailFrom;
        set { if (SetField(ref _mailFrom, value ?? "")) SaveSettingsToConfig(); }
    }

    public SmtpSnapshotBatch? CurrentSnapshot
    {
        get => _currentSnapshot;
        set => SetField(ref _currentSnapshot, value);
    }

    public SmtpSnapshotBatch? FinalSnapshot
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

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    // ── Internal ─────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    public SmtpViewModel()
    {
        // Load settings from config
        LoadSettingsFromConfig();

        StartCommand = new RelayCommand(
            _ => _ = StartValidation(),
            _ => State == SmtpState.Idle && EmailCount > 0 && ProxyCount > 0);
        StopCommand = new RelayCommand(
            _ => StopValidation(),
            _ => State == SmtpState.Running);
    }

    private void LoadSettingsFromConfig()
    {
        var cfg = Config.Current;

        _batchSize = cfg.SmtpBatchSize;
        _workersPerProxy = cfg.SmtpWorkersPerProxy;
        _timeoutSec = cfg.SmtpTimeoutSec;
        _maxConsecErrors = cfg.SmtpMaxConsecErrors;
        _retries = cfg.SmtpRetries;
        _sessionDelaySec = cfg.SmtpSessionDelaySec;
        _recoveryMin = cfg.SmtpRecoveryMin;
        _heloDomain = cfg.SmtpHelo;
        _mailFrom = cfg.SmtpMailFrom;

        // Load proxies
        if (!string.IsNullOrEmpty(cfg.Proxies))
        {
            _proxiesText = cfg.Proxies;
            _proxyCount = cfg.Proxies
                .Split('\n')
                .Count(l => ProxySpec.TryParse(l) != null);
        }
    }

    private void SaveSettingsToConfig()
    {
        var cfg = Config.Current;
        cfg.SmtpBatchSize = _batchSize;
        cfg.SmtpWorkersPerProxy = _workersPerProxy;
        cfg.SmtpTimeoutSec = _timeoutSec;
        cfg.SmtpMaxConsecErrors = _maxConsecErrors;
        cfg.SmtpRetries = _retries;
        cfg.SmtpSessionDelaySec = _sessionDelaySec;
        cfg.SmtpRecoveryMin = _recoveryMin;
        cfg.SmtpHelo = _heloDomain;
        cfg.SmtpMailFrom = _mailFrom;
        cfg.Save();
    }

    // ── Public methods ───────────────────────────────────────────────

    public async Task StartValidation()
    {
        var allEmails = GetEmailList()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // SMTP-проверка ограничена gmail.com/googlemail.com: остальные провайдеры
        // блочат RCPT-probing через наши прокси, тратить время смысла нет.
        // Отфильтрованные пишем в skipped_non_gmail.txt чтобы юзер видел что пропущено.
        static bool IsGmail(string e)
        {
            var at = e.LastIndexOf('@');
            if (at < 0) return false;
            var dom = e[(at + 1)..].ToLowerInvariant();
            return dom == "gmail.com" || dom == "googlemail.com";
        }

        var emails = allEmails.Where(IsGmail).ToList();
        var skipped = allEmails.Where(e => !IsGmail(e)).ToList();

        var proxies = ProxiesText
            .Split('\n')
            .Select(l => ProxySpec.TryParse(l))
            .Where(p => p != null)
            .Cast<ProxySpec>()
            .ToList();

        if (emails.Count == 0)
            return;

        // Прокси нет — стартуем в direct-режиме (коннект с твоего IP).
        // ProxySpec.Direct — sentinel, SmtpProber распознаёт и минует SOCKS5.
        if (proxies.Count == 0)
            proxies.Add(ProxySpec.Direct);

        // Output dir: <exe>/output/smtp/
        var outputDir = Paths.SmtpRoot;
        OutputDir = outputDir;

        // Пропущенные не-Gmail сохраняем отдельно, чтобы юзер видел что не проверялось.
        if (skipped.Count > 0)
        {
            try
            {
                System.IO.Directory.CreateDirectory(outputDir);
                System.IO.File.WriteAllLines(
                    System.IO.Path.Combine(outputDir, "skipped_non_gmail.txt"),
                    skipped, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        var settings = new SmtpSettings
        {
            BatchSize = BatchSize,
            WorkersPerProxy = WorkersPerProxy,
            TimeoutMs = TimeoutSec * 1000,
            MaxConsecErrors = MaxConsecErrors,
            Retries = Retries,
            SessionDelayMs = SessionDelaySec * 1000,
            RecoverySeconds = RecoveryMin * 60,
            HeloDomain = HeloDomain,
            MailFromAddress = MailFrom,
        };

        _cts = new CancellationTokenSource();
        State = SmtpState.Running;
        CurrentSnapshot = null;
        FinalSnapshot = null;
        NotificationHub.Notify($"✉ <b>SMTP-проверка запущена</b>\nадресов: <b>{emails.Count:N0}</b>, прокси: {proxies.Count}");

        var worker = new SmtpValidateWorker(emails, proxies, settings, outputDir);

        var progressHandler = new Progress<SmtpSnapshotBatch>(snapshot =>
        {
            CurrentSnapshot = snapshot;
        });

        try
        {
            await worker.RunAsync(progressHandler, _cts.Token);
            FinalSnapshot = CurrentSnapshot;
            State = SmtpState.Done;
            var processed = CurrentSnapshot?.Processed ?? 0;
            NotificationHub.Notify($"✅ <b>SMTP-проверка завершена</b>\nобработано: <b>{processed:N0}</b>");
        }
        catch (OperationCanceledException)
        {
            // Если воркер успел зарепортить хотя бы один snapshot → показываем что есть.
            // Иначе (Stop при 0% прогресса) — возвращаемся в Idle, чтобы не показывать
            // stale UI от предыдущего прогона.
            if (CurrentSnapshot is not null && CurrentSnapshot.Processed > 0)
            {
                FinalSnapshot = CurrentSnapshot;
                State = SmtpState.Done;
                NotificationHub.Notify($"⏸ <b>SMTP-проверка остановлена</b>\nобработано: {CurrentSnapshot.Processed:N0}");
            }
            else
            {
                State = SmtpState.Idle;
                NotificationHub.Notify("⏸ <b>SMTP-проверка остановлена</b>");
                return;  // не пишем выходные файлы — нет данных
            }
        }
        catch (Exception ex)
        {
            FinalSnapshot = CurrentSnapshot;
            State = SmtpState.Done;
            Debug.WriteLine($"SMTP validation error: {ex}");
            NotificationHub.Notify($"❌ <b>SMTP-проверка упала</b>\n<code>{ex.Message}</code>");
        }

        // Write output files
        WriteOutputFiles();
    }

    private void StopValidation()
    {
        _cts?.Cancel();
    }

    public void ResetToIdle()
    {
        CurrentSnapshot = null;
        FinalSnapshot = null;
        OutputDir = null;
        State = SmtpState.Idle;
    }

    private void WriteOutputFiles()
    {
        var snap = FinalSnapshot;
        if (snap == null || string.IsNullOrEmpty(OutputDir))
            return;

        try
        {
            Directory.CreateDirectory(OutputDir);

            // Write errors.log from RecentErrors
            var errorsPath = Path.Combine(OutputDir, "errors.log");
            using (var w = new StreamWriter(errorsPath, false, System.Text.Encoding.UTF8))
            {
                w.NewLine = "\n";
                w.WriteLine($"# SMTP Validation Error Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"# Last {snap.RecentErrors.Count} errors");
                w.WriteLine();
                foreach (var err in snap.RecentErrors)
                {
                    w.WriteLine($"{err.Email}\t{err.MxHost}\t{err.ViaProxy}\t{err.Error}\t{err.ElapsedMs:F0}ms");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error writing SMTP output files: {ex}");
        }
    }

    // ── Helpers for loading from last cleanup ────────────────────────

    // Full email list (for worker) — NOT in TextBox to avoid 500K line freeze
    private List<string> _loadedEmails = new();

    public List<string> GetEmailList()
    {
        if (_loadedEmails.Count > 0)
            return _loadedEmails;
        // Parse from TextBox if user typed manually
        return EmailsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Contains('@'))
            .ToList();
    }

    public async void LoadFromLastCleanup()
    {
        try
        {
            // Cleanup-результаты лежат в <exe>/output/cleanup/<filename>/.
            // Берём ВСЕГДА самую свежую папку (даже если clean.txt пуст) — иначе при
            // последней чистке без валидных адресов незаметно подтянется СТАРАЯ база.
            var baseDir = Paths.CleanupRoot;
            if (!Directory.Exists(baseDir)) return;

            // Время определяем по самому свежему файлу внутри, а не по mtime папки
            // (mtime папки может не обновляться при перезаписи существующих файлов).
            var dirs = Directory.GetDirectories(baseDir)
                .Select(d => new {
                    Path = d,
                    Mtime = Directory.EnumerateFiles(d).Select(f => File.GetLastWriteTime(f))
                                .DefaultIfEmpty(Directory.GetLastWriteTime(d))
                                .Max()
                })
                .OrderByDescending(x => x.Mtime)
                .ToList();

            if (dirs.Count == 0) return;

            var latest = dirs[0];
            var cleanFile = Path.Combine(latest.Path, "clean.txt");
            if (!File.Exists(cleanFile))
            {
                System.Windows.MessageBox.Show(
                    $"В последней чистке ({Path.GetFileName(latest.Path)}) нет clean.txt.",
                    "Загрузка из чистки",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var lines = await Task.Run(() =>
                File.ReadAllLines(cleanFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList());

            if (lines.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"В последней чистке ({Path.GetFileName(latest.Path)}) clean.txt пуст — 0 валидных адресов.",
                    "Загрузка из чистки",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            _loadedEmails = lines;
            EmailCount = lines.Count;

            var label = Path.GetFileName(latest.Path);
            var preview = string.Join("\n", lines.Take(100));
            if (lines.Count > 100)
                preview += $"\n\n... ещё {lines.Count - 100:N0} адресов (всего {lines.Count:N0} из «{label}»)";
            else
                preview = $"# Загружено {lines.Count:N0} адресов из «{label}»\n" + preview;
            EmailsText = preview;
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading from last cleanup: {ex}");
        }
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
