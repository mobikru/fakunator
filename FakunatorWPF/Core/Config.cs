using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Fakunator.Core;

public class Config
{
    // ── App ──────────────────────────────────────────────────────────
    public string OutputDir { get; set; } = "";
    public bool GmailStrict { get; set; } = true;
    public List<string> EnabledFilters { get; set; } = new()
    {
        "clean", "duplicate", "invalid", "reserved",
        "trap", "role", "disposable", "corporate", "typo"
    };
    public bool UpdateBlocklistsOnStart { get; set; } = false;

    // ── SMTP ─────────────────────────────────────────────────────────
    public string Proxies { get; set; } = "";
    public int SmtpBatchSize { get; set; } = 16;
    public int SmtpWorkersPerProxy { get; set; } = 3;
    public int SmtpTimeoutSec { get; set; } = 71;
    public int SmtpMaxConsecErrors { get; set; } = 10;
    public int SmtpRetries { get; set; } = 3;
    public int SmtpSessionDelaySec { get; set; } = 2;
    public int SmtpRecoveryMin { get; set; } = 5;
    public string SmtpHelo { get; set; } = "";
    public string SmtpMailFrom { get; set; } = "";
    public List<string> AliveProxies { get; set; } = new();

    // ── AI (future Analyze tab) ──────────────────────────────────────
    public string OpenAiKey { get; set; } = "";
    public string AnthropicKey { get; set; } = "";
    public string AiProvider { get; set; } = "openai";
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string GoogleKey { get; set; } = "";
    public string GoogleModel { get; set; } = "gemini-2.5-flash";

    // ── Analyze tab settings ─────────────────────────────────────────
    public double AnalyzeSpendCap { get; set; } = 105.0;
    public int AnalyzeAiBatchSize { get; set; } = 20;
    public int AnalyzeMaxConcurrent { get; set; } = 8;
    public bool AnalyzeTwoPass { get; set; } = false;
    public bool AnalyzeUseAi { get; set; } = true;
    public bool AnalyzeSkipJunk { get; set; } = true;
    public string Pass2Provider { get; set; } = "anthropic";
    public double AnalyzeBatchDelay { get; set; } = 4.0;
    public bool AnalyzeSkipRoleBased { get; set; } = true;
    // AnalyzeEnableMorphology удалён: флаг никогда не читался в Worker'е.
    // Морфология "по-настоящему" (stemming типа evgeniy→Eugene) не реализована.
    // Тег "db+morph" в результатах — это variant-hit из name_variants таблицы, не флаг.
    public string PrivacyMode { get; set; } = "normal";

    // ── Cleanup additional checks ────────────────────────────────────
    public bool UseSpamhausDbl { get; set; } = false;

    // ── Domains Manager (ISPmanager DNSmanager) ──────────────────────
    // Список аккаунтов доменных панелей. Пароли хранятся как есть
    // (как AI-ключи и SOCKS5-прокси) — по решению пользователя.
    public List<Fakunator.Core.DomainsManager.IspAccount> IspAccounts { get; set; } = new();
    public List<Fakunator.Core.DomainsManager.DnsTemplate> DnsTemplates { get; set; } = new();

    // ── Mail.ru Postmaster (OAuth аккаунты) ──────────────────────────
    // Сохраняем только refresh_token (пожизненный), access обновляем on-demand.
    public List<Fakunator.Core.Postmaster.PostmasterAccount> PostmasterAccounts { get; set; } = new();

    // ── Domain Scanner ────────────────────────────────────────────────
    // ВНИМАНИЕ: ключ НЕ хардкодим в коде — иначе он попадает во все раздачи
    // exe-файла. Юзер вводит свой ключ через Настройки → AI-ключи.
    public string DomainsMonitorApiKey { get; set; } = "";
    public int DomainScanConcurrency { get; set; } = 300;
    public double DomainDnsTimeoutSec { get; set; } = 3.0;
    public string DomainScanZones { get; set; } = "ru, com";
    public string DomainScanTargetNs { get; set; } = "";
    public List<string> FavoriteProviders { get; set; } = new();

    // ── AMS Enterprise API ───────────────────────────────────────────
    // JSON-RPC на http://{host}/api/v1. Ключ генерируется в AMS
    // при первом запуске (окно настроек API). Дока: bspdev.ru → AMS.
    public string AmsApiHost { get; set; } = "";
    public string AmsApiKey { get; set; } = "";
    public bool AmsUseHttps { get; set; } = false;

    // ── PMTA (PowerMTA Web Monitor панели) ───────────────────────────
    public List<Fakunator.Core.Pmta.PmtaPanel> PmtaPanels { get; set; } = new();

    // ── Telegram Bot ─────────────────────────────────────────────────
    // Long-polling — не требует public IP / открытых портов. Бот сам
    // подключается к серверам Telegram и получает updates.
    // AllowedChatIds — whitelist чатов; пустой список = бот принимает
    // команды от любого (небезопасно, используется только в первичной настройке).
    public bool TelegramEnabled { get; set; } = false;
    public string TelegramBotToken { get; set; } = "";
    public List<long> TelegramAllowedChatIds { get; set; } = new();

    // ── Window ───────────────────────────────────────────────────────
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    // ── Serialisation ────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Singleton ────────────────────────────────────────────────────

    private static Config? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Fires after Save completes successfully. ViewModels listen so a change in
    /// SettingsDialog propagates to the live AnalyzeViewModel/Sidebar without restart.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>Current application-wide config instance.</summary>
    public static Config Current
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= Load();
                }
            }
            return _instance;
        }
    }

    // ── Config path resolution ───────────────────────────────────────

    private static string? _configPath;

    public static string ConfigPath
    {
        get
        {
            if (_configPath != null) return _configPath;

            // For frozen exe: next to exe. Environment.ProcessPath — надёжнее
            // AppContext.BaseDirectory для single-file self-contained (там
            // BaseDirectory может указывать на temp-папку с распакованным контентом).
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var candidate = Path.Combine(exeDir, "config.json");
            if (File.Exists(candidate))
            {
                _configPath = candidate;
                return _configPath;
            }

            // For dev: walk up to find existing config.json or project root
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var path = Path.Combine(dir.FullName, "config.json");
                if (File.Exists(path))
                {
                    _configPath = path;
                    return _configPath;
                }

                // Check if this is a project directory (has .csproj)
                if (Directory.GetFiles(dir.FullName, "*.csproj").Length > 0)
                {
                    _configPath = path;
                    return _configPath;
                }

                dir = dir.Parent;
            }

            // Fallback: next to exe
            _configPath = Path.Combine(exeDir, "config.json");
            return _configPath;
        }
    }

    // ── Load / Save ──────────────────────────────────────────────────

    public static Config Load()
    {
        Config result;
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
                result = cfg ?? new Config();
            }
            else
            {
                result = new Config();
            }
        }
        catch
        {
            result = new Config();
        }

        _instance = result;
        return result;
    }

    // ── Debounced save ───────────────────────────────────────────────

    [System.Text.Json.Serialization.JsonIgnore]
    private DispatcherTimer? _saveTimer;

    /// <summary>
    /// Request a save. Actual write happens 500ms after the last call (debounced).
    /// </summary>
    public void Save()
    {
        // If called from non-UI thread, dispatch to UI thread
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Save());
            return;
        }

        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Immediately persist config to disk.</summary>
    public void SaveNow()
    {
        try
        {
            _saveTimer?.Stop();
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Best effort -- don't crash if config dir is read-only
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reset to defaults but preserve API keys and proxies.
    /// </summary>
    public void ResetToDefaults()
    {
        var preservedOpenAiKey = OpenAiKey;
        var preservedAnthropicKey = AnthropicKey;
        var preservedGoogleKey = GoogleKey;
        var preservedProxies = Proxies;

        var fresh = new Config();

        OutputDir = fresh.OutputDir;
        GmailStrict = fresh.GmailStrict;
        EnabledFilters = fresh.EnabledFilters;
        UpdateBlocklistsOnStart = fresh.UpdateBlocklistsOnStart;
        UseSpamhausDbl = fresh.UseSpamhausDbl;
        SmtpBatchSize = fresh.SmtpBatchSize;
        SmtpWorkersPerProxy = fresh.SmtpWorkersPerProxy;
        SmtpTimeoutSec = fresh.SmtpTimeoutSec;
        SmtpMaxConsecErrors = fresh.SmtpMaxConsecErrors;
        SmtpRetries = fresh.SmtpRetries;
        SmtpSessionDelaySec = fresh.SmtpSessionDelaySec;
        SmtpRecoveryMin = fresh.SmtpRecoveryMin;
        SmtpHelo = fresh.SmtpHelo;
        SmtpMailFrom = fresh.SmtpMailFrom;
        AliveProxies = new List<string>();
        AiProvider = fresh.AiProvider;
        AiModel = fresh.AiModel;
        GoogleModel = fresh.GoogleModel;
        AnalyzeSpendCap = fresh.AnalyzeSpendCap;
        AnalyzeAiBatchSize = fresh.AnalyzeAiBatchSize;
        AnalyzeMaxConcurrent = fresh.AnalyzeMaxConcurrent;
        AnalyzeTwoPass = fresh.AnalyzeTwoPass;
        AnalyzeUseAi = fresh.AnalyzeUseAi;
        AnalyzeSkipJunk = fresh.AnalyzeSkipJunk;
        Pass2Provider = fresh.Pass2Provider;
        AnalyzeBatchDelay = fresh.AnalyzeBatchDelay;
        AnalyzeSkipRoleBased = fresh.AnalyzeSkipRoleBased;
        PrivacyMode = fresh.PrivacyMode;
        WindowLeft = fresh.WindowLeft;
        WindowTop = fresh.WindowTop;
        WindowWidth = fresh.WindowWidth;
        WindowHeight = fresh.WindowHeight;
        WindowMaximized = fresh.WindowMaximized;

        // Restore preserved
        OpenAiKey = preservedOpenAiKey;
        AnthropicKey = preservedAnthropicKey;
        GoogleKey = preservedGoogleKey;
        Proxies = preservedProxies;

        Save();
    }
}
