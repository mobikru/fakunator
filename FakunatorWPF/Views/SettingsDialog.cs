using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;
using Telegram.Bot;

namespace Fakunator.Views;

public class SettingsDialog : Window
{
    private readonly Config _cfg;

    // Tab buttons
    private readonly Button _tabAi;
    private readonly Button _tabApp;
    private readonly Button _tabAdvanced;
    private readonly Border _contentArea;

    // AI tab controls
    private TextBox? _openAiKeyBox;
    private TextBox? _anthropicKeyBox;
    private TextBox? _dmApiKeyBox;
    private TextBox? _amsHostBox;
    private TextBox? _amsKeyBox;
    private CheckBox? _amsHttpsBox;
    private TextBlock? _amsTestResult;

    // App tab controls
    private RadioButton? _rbDark;
    private RadioButton? _rbLight;
    private TextBox? _outputDirBox;
    private CheckBox? _autoUpdateCb;

    public SettingsDialog(Window owner)
    {
        _cfg = Config.Current;

        Owner = owner;
        Title = "Настройки";
        Width = 700;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)FindRes("Bg1Brush");
        Foreground = (Brush)FindRes("Fg1Brush");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Left sidebar with tab buttons ────────────────────────────
        var sidebar = new StackPanel
        {
            Margin = new Thickness(12, 16, 0, 16)
        };
        Grid.SetColumn(sidebar, 0);

        var sidebarTitle = MakeText("Настройки", 16, FontWeights.SemiBold);
        sidebarTitle.Margin = new Thickness(8, 0, 0, 16);
        sidebar.Children.Add(sidebarTitle);

        _tabAi = MakeTabButton("🔑  AI-ключи");
        _tabApp = MakeTabButton("🎨  Приложение");
        _tabAdvanced = MakeTabButton("⚙  Продвинутое");

        _tabAi.Click += (_, _) => ShowTab(0);
        _tabApp.Click += (_, _) => ShowTab(1);
        _tabAdvanced.Click += (_, _) => ShowTab(2);

        sidebar.Children.Add(_tabAi);
        sidebar.Children.Add(_tabApp);
        sidebar.Children.Add(_tabAdvanced);

        root.Children.Add(sidebar);

        // ── Right content area ───────────────────────────────────────
        _contentArea = new Border
        {
            Margin = new Thickness(0, 12, 12, 12),
            Background = (Brush)FindRes("Bg2Brush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
        };
        _contentArea.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        Grid.SetColumn(_contentArea, 1);
        root.Children.Add(_contentArea);

        Content = root;

        ShowTab(0);
    }

    // ── Tab switching ────────────────────────────────────────────────

    private int _activeTab = -1;

    private void ShowTab(int index)
    {
        if (index == _activeTab) return;
        _activeTab = index;

        // Highlight active tab button
        var buttons = new[] { _tabAi, _tabApp, _tabAdvanced };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i == index)
            {
                buttons[i].SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                buttons[i].SetResourceReference(ForegroundProperty, "Accent2Brush");
            }
            else
            {
                buttons[i].Background = Brushes.Transparent;
                buttons[i].SetResourceReference(ForegroundProperty, "Fg2Brush");
            }
        }

        _contentArea.Child = index switch
        {
            0 => BuildAiTab(),
            1 => BuildAppTab(),
            2 => BuildAdvancedTab(),
            _ => null,
        };
    }

    // ── Tab 1: AI Keys ──────────────────────────────────────────────

    private UIElement BuildAiTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(MakeText("AI-ключи", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        // OpenAI Key
        panel.Children.Add(MakeLabel("OpenAI API Key"));
        _openAiKeyBox = MakeTextBox(_cfg.OpenAiKey, true);
        _openAiKeyBox.TextChanged += (_, _) =>
        {
            _cfg.OpenAiKey = _openAiKeyBox.Text;
            _cfg.Save();
        };
        panel.Children.Add(_openAiKeyBox);

        var testOpenAi = MakeSmallButton("Тест подключения");
        testOpenAi.Click += async (_, _) => await TestOpenAi();
        panel.Children.Add(testOpenAi);
        panel.Children.Add(MakeSpacer(12));

        // Anthropic Key
        panel.Children.Add(MakeLabel("Anthropic API Key"));
        _anthropicKeyBox = MakeTextBox(_cfg.AnthropicKey, true);
        _anthropicKeyBox.TextChanged += (_, _) =>
        {
            _cfg.AnthropicKey = _anthropicKeyBox.Text;
            _cfg.Save();
        };
        panel.Children.Add(_anthropicKeyBox);

        var testAnthropic = MakeSmallButton("Тест подключения");
        testAnthropic.Click += async (_, _) => await TestAnthropic();
        panel.Children.Add(testAnthropic);
        panel.Children.Add(MakeSpacer(12));

        var providerHint = new TextBlock
        {
            Text = "Провайдер и модель выбираются на вкладке «AI-Анализ» в боковой панели.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        providerHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(providerHint);
        panel.Children.Add(MakeSpacer(20));

        // ── Domain scanner API ─────────────────────────────────────
        panel.Children.Add(MakeText("Сканер доменов", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        panel.Children.Add(MakeLabel("domains-monitor.com API Token"));
        _dmApiKeyBox = MakeTextBox(_cfg.DomainsMonitorApiKey, true);
        _dmApiKeyBox.TextChanged += (_, _) =>
        {
            _cfg.DomainsMonitorApiKey = _dmApiKeyBox.Text;
            _cfg.Save();
        };
        panel.Children.Add(_dmApiKeyBox);

        var dmHint = new TextBlock
        {
            Text = "Получить токен: domains-monitor.com → личный кабинет → API. " +
                   "Используется для стрима списков доменов по зонам (.ru/.com/.net/…).",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        dmHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(dmHint);
        panel.Children.Add(MakeSpacer(24));

        // ── AMS Enterprise API ─────────────────────────────────────
        panel.Children.Add(MakeText("AMS Enterprise API", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        panel.Children.Add(MakeLabel("Хост AMS (IP или домен, без схемы)"));
        _amsHostBox = MakeTextBox(_cfg.AmsApiHost, false);
        _amsHostBox.TextChanged += (_, _) => { _cfg.AmsApiHost = _amsHostBox.Text.Trim(); _cfg.Save(); };
        panel.Children.Add(_amsHostBox);
        panel.Children.Add(MakeSpacer(10));

        panel.Children.Add(MakeLabel("API-ключ AMS"));
        _amsKeyBox = MakeTextBox(_cfg.AmsApiKey, true);
        _amsKeyBox.TextChanged += (_, _) => { _cfg.AmsApiKey = _amsKeyBox.Text.Trim(); _cfg.Save(); };
        panel.Children.Add(_amsKeyBox);
        panel.Children.Add(MakeSpacer(8));

        _amsHttpsBox = new CheckBox
        {
            Content = "Использовать HTTPS (по умолчанию HTTP — рекомендация BSPdev)",
            IsChecked = _cfg.AmsUseHttps,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _amsHttpsBox.SetResourceReference(ForegroundProperty, "Fg2Brush");
        _amsHttpsBox.Checked += (_, _) => { _cfg.AmsUseHttps = true; _cfg.Save(); };
        _amsHttpsBox.Unchecked += (_, _) => { _cfg.AmsUseHttps = false; _cfg.Save(); };
        panel.Children.Add(_amsHttpsBox);
        panel.Children.Add(MakeSpacer(10));

        var amsTestBtn = MakeSmallButton("Тест подключения");
        amsTestBtn.Click += async (_, _) => await TestAmsAsync();
        panel.Children.Add(amsTestBtn);

        _amsTestResult = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _amsTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(_amsTestResult);

        var amsHint = new TextBlock
        {
            Text = "Ключ генерируется в AMS Enterprise → окно настроек API. " +
                   "Используется для управления рассылками через JSON-RPC.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        amsHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(amsHint);
        panel.Children.Add(MakeSpacer(24));

        // ── Telegram Bot ────────────────────────────────────────────
        panel.Children.Add(MakeText("Telegram-бот", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        _tgEnabledBox = new CheckBox
        {
            Content = "Включить бота (long-polling, не требует открытых портов)",
            IsChecked = _cfg.TelegramEnabled,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _tgEnabledBox.SetResourceReference(ForegroundProperty, "Fg2Brush");
        _tgEnabledBox.Checked += (_, _) => { _cfg.TelegramEnabled = true; _cfg.Save(); };
        _tgEnabledBox.Unchecked += (_, _) => { _cfg.TelegramEnabled = false; _cfg.Save(); };
        panel.Children.Add(_tgEnabledBox);

        panel.Children.Add(MakeLabel("Токен бота (из @BotFather)"));
        _tgTokenBox = MakeTextBox(_cfg.TelegramBotToken, true);
        _tgTokenBox.TextChanged += (_, _) => { _cfg.TelegramBotToken = _tgTokenBox.Text.Trim(); _cfg.Save(); };
        panel.Children.Add(_tgTokenBox);
        panel.Children.Add(MakeSpacer(10));

        var tgTestBtn = MakeSmallButton("Тест подключения");
        tgTestBtn.Click += async (_, _) => await TestTelegramAsync();
        panel.Children.Add(tgTestBtn);

        _tgTestResult = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _tgTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(_tgTestResult);
        panel.Children.Add(MakeSpacer(10));

        panel.Children.Add(MakeLabel("Разрешённые chat ID (whitelist)"));
        _tgChatsBlock = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        };
        _tgChatsBlock.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        RefreshTgChats();
        panel.Children.Add(_tgChatsBlock);

        var tgChatsRow = new StackPanel { Orientation = Orientation.Horizontal };
        var tgClearBtn = MakeSmallButton("Очистить whitelist");
        tgClearBtn.Click += (_, _) =>
        {
            _cfg.TelegramAllowedChatIds.Clear();
            _cfg.Save();
            RefreshTgChats();
        };
        tgChatsRow.Children.Add(tgClearBtn);
        panel.Children.Add(tgChatsRow);

        var tgHint = new TextBlock
        {
            Text = "Как настроить: 1) Открой @BotFather в Telegram → /newbot → получи токен. " +
                   "2) Вставь токен сюда, включи бота. 3) Напиши боту /start — твой chat ID " +
                   "автоматически добавится в whitelist.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        tgHint.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(tgHint);

        return WrapScroll(panel);
    }

    // ── Telegram tab helpers ──────────────────────────────────────────
    private CheckBox? _tgEnabledBox;
    private TextBox? _tgTokenBox;
    private TextBlock? _tgTestResult;
    private TextBlock? _tgChatsBlock;

    private void RefreshTgChats()
    {
        if (_tgChatsBlock == null) return;
        _tgChatsBlock.Text = _cfg.TelegramAllowedChatIds.Count == 0
            ? "нет разрешённых чатов — напиши боту /start чтобы добавить"
            : string.Join(", ", _cfg.TelegramAllowedChatIds);
    }

    private async Task TestTelegramAsync()
    {
        if (_tgTestResult == null) return;
        var token = _cfg.TelegramBotToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            _tgTestResult.Text = "Вставь токен.";
            _tgTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            return;
        }
        _tgTestResult.Text = "Проверяю…";
        _tgTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        try
        {
            var bot = new Telegram.Bot.TelegramBotClient(token);
            var me = await bot.GetMe();
            _tgTestResult.Text = $"OK: @{me.Username} (id {me.Id}). Открой чат — https://t.me/{me.Username} — и напиши /start.";
            _tgTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            // Если галка «включить» стоит — перезапускаем сервис на актуальный токен
            if (_cfg.TelegramEnabled)
                _ = Fakunator.Core.TelegramBot.TelegramBotService.Instance.StartAsync();
        }
        catch (Exception ex)
        {
            _tgTestResult.Text = "Ошибка: " + ex.Message;
            _tgTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        }
    }

    private async Task TestAmsAsync()
    {
        if (_amsTestResult == null) return;
        if (string.IsNullOrWhiteSpace(_cfg.AmsApiHost) || string.IsNullOrWhiteSpace(_cfg.AmsApiKey))
        {
            _amsTestResult.Text = "Заполни хост и ключ.";
            _amsTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            return;
        }
        _amsTestResult.Text = "Подключаюсь…";
        _amsTestResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        try
        {
            using var api = new Fakunator.Core.AmsApi.AmsApiClient(
                _cfg.AmsApiHost, _cfg.AmsApiKey, _cfg.AmsUseHttps,
                TimeSpan.FromSeconds(10));
            var running = await api.IsSchedulerRunningAsync();
            var mailings = await api.GetMailingsAsync() ?? new();
            _amsTestResult.Text = $"OK. Планировщик: {(running ? "работает" : "остановлен")}. Рассылок в базе: {mailings.Count}.";
            _amsTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        catch (Exception ex)
        {
            _amsTestResult.Text = "Ошибка: " + ex.Message;
            _amsTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        }
    }

    // ── Tab 2: App settings ─────────────────────────────────────────

    private UIElement BuildAppTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(MakeText("Приложение", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        // Theme
        panel.Children.Add(MakeLabel("Тема оформления"));
        var themePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _rbLight = new RadioButton
        {
            Content = "Светлая",
            GroupName = "SettingsTheme",
            IsChecked = _cfg.Theme == "light",
            Margin = new Thickness(0, 0, 16, 0),
            FontSize = 13,
        };
        _rbLight.SetResourceReference(ForegroundProperty, "Fg1Brush");
        _rbLight.Checked += (_, _) =>
        {
            _cfg.Theme = "light";
            App.SwitchTheme("light");
            _cfg.Save();
            RefreshDialogBackground();
        };

        _rbDark = new RadioButton
        {
            Content = "Тёмная",
            GroupName = "SettingsTheme",
            IsChecked = _cfg.Theme == "dark",
            FontSize = 13,
        };
        _rbDark.SetResourceReference(ForegroundProperty, "Fg1Brush");
        _rbDark.Checked += (_, _) =>
        {
            _cfg.Theme = "dark";
            App.SwitchTheme("dark");
            _cfg.Save();
            RefreshDialogBackground();
        };

        themePanel.Children.Add(_rbLight);
        themePanel.Children.Add(_rbDark);
        panel.Children.Add(themePanel);
        panel.Children.Add(MakeSpacer(16));

        // Output directory
        panel.Children.Add(MakeLabel("Папка результатов"));
        var dirRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

        var browseBtn = MakeSmallButton("Обзор...");
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browseBtn, Dock.Right);

        _outputDirBox = MakeTextBox(_cfg.OutputDir, false);
        _outputDirBox.TextChanged += (_, _) =>
        {
            _cfg.OutputDir = _outputDirBox.Text;
            _cfg.Save();
        };

        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку для результатов"
            };
            if (!string.IsNullOrEmpty(_cfg.OutputDir) && Directory.Exists(_cfg.OutputDir))
                dlg.InitialDirectory = _cfg.OutputDir;
            if (dlg.ShowDialog(this) == true)
            {
                _outputDirBox.Text = dlg.FolderName;
            }
        };

        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(_outputDirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(MakeSpacer(16));

        // Auto-update blocklists on start
        _autoUpdateCb = new CheckBox
        {
            Content = "Обновлять blocklists при старте",
            IsChecked = _cfg.UpdateBlocklistsOnStart,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _autoUpdateCb.SetResourceReference(ForegroundProperty, "Fg1Brush");
        _autoUpdateCb.Checked += (_, _) => { _cfg.UpdateBlocklistsOnStart = true; _cfg.Save(); };
        _autoUpdateCb.Unchecked += (_, _) => { _cfg.UpdateBlocklistsOnStart = false; _cfg.Save(); };
        panel.Children.Add(_autoUpdateCb);

        return WrapScroll(panel);
    }

    // ── Tab 3: Advanced ─────────────────────────────────────────────

    private UIElement BuildAdvancedTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(MakeText("Продвинутое", 15, FontWeights.SemiBold));
        panel.Children.Add(MakeSpacer(12));

        // Open config.json
        var openConfigBtn = MakeSmallButton("Открыть config.json");
        openConfigBtn.Click += (_, _) =>
        {
            try
            {
                var path = Config.ConfigPath;
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    MessageBox.Show($"Файл не найден:\n{path}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(openConfigBtn);
        panel.Children.Add(MakeSpacer(8));

        // Open logs folder
        var openLogsBtn = MakeSmallButton("Открыть папку логов");
        openLogsBtn.Click += (_, _) =>
        {
            try
            {
                var logsDir = Path.Combine(Fakunator.Core.Paths.ExeDir, "_logs");
                if (!Directory.Exists(logsDir))
                    Directory.CreateDirectory(logsDir);
                Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(openLogsBtn);
        panel.Children.Add(MakeSpacer(16));

        // Reset settings
        var resetBtn = new Button
        {
            Content = "Сбросить настройки",
            Padding = new Thickness(16, 6, 16, 6),
            Style = (Style)FindRes("DangerBtn"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        resetBtn.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                "Сбросить все настройки на значения по умолчанию?\n\nAPI-ключи и прокси будут сохранены.",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                _cfg.ResetToDefaults();
                App.SwitchTheme(_cfg.Theme);
                RefreshDialogBackground();
                // Refresh current tab
                var currentTab = _activeTab;
                _activeTab = -1;
                ShowTab(currentTab);
                MessageBox.Show("Настройки сброшены.", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        panel.Children.Add(resetBtn);
        panel.Children.Add(MakeSpacer(20));

        // About section
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 12),
        };
        separator.SetResourceReference(Border.BackgroundProperty, "Border2Brush");
        panel.Children.Add(separator);

        panel.Children.Add(MakeLabel("О программе"));
        panel.Children.Add(MakeSpacer(6));

        var version = typeof(SettingsDialog).Assembly.GetName().Version;
        var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.5.0";
        panel.Children.Add(MakeInfoRow("Версия:", versionStr));

        // Кнопка «Проверить обновления» + статус доступного апдейта
        AddUpdateRow(panel);

        panel.Children.Add(MakeLinkRow("Автор:", "@batalov", "https://t.me/batalov"));

        // Небольшая сноска под логином автора — предложение услуг.
        var authorNote = new TextBlock
        {
            Text = "• Партнёрство по рассылке\n• Покупаю качественные базы",
            FontSize = 10,
            LineHeight = 14,
            Margin = new Thickness(160, 2, 0, 8),
            Opacity = 0.75,
        };
        authorNote.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
        panel.Children.Add(authorNote);

        var dataDir = Blocklists.FindDataDir();
        panel.Children.Add(MakeInfoRow("Папка данных:", dataDir));

        // Blocklist counts
        try
        {
            var disposableCount = CountLinesInFile(Path.Combine(dataDir, "disposable_domains.txt"));
            var freeCount = CountLinesInFile(Path.Combine(dataDir, "free_providers.txt"));
            var roleCount = CountLinesInFile(Path.Combine(dataDir, "role_prefixes.txt"));

            panel.Children.Add(MakeInfoRow("Одноразовые домены:", $"{disposableCount:N0}"));
            panel.Children.Add(MakeInfoRow("Бесплатные провайдеры:", $"{freeCount:N0}"));
            panel.Children.Add(MakeInfoRow("Ролевые префиксы:", $"{roleCount:N0}"));
        }
        catch { }

        panel.Children.Add(MakeInfoRow("config.json:", Config.ConfigPath));

        return WrapScroll(panel);
    }

    // ── API test methods ────────────────────────────────────────────

    private async Task TestOpenAi()
    {
        var key = _openAiKeyBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Введите API-ключ OpenAI.", "Тест", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            var response = await http.SendAsync(req);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show("Подключение успешно! Ключ валиден.", "OpenAI",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var snippet = body.Length > 600 ? body.Substring(0, 600) + "..." : body;
                var hint = (int)response.StatusCode switch
                {
                    401 => "\n\nКлюч недействителен или отозван.",
                    429 => "\n\nПревышен лимит запросов (rate limit или quota).",
                    403 => "\n\nДоступ запрещён — проверьте права ключа.",
                    _ => "",
                };
                MessageBox.Show(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{hint}\n\n{snippet}",
                    "OpenAI — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("Тайм-аут подключения (10 сек).", "OpenAI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка подключения:\n{ex.GetType().Name}: {ex.Message}",
                "OpenAI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task TestAnthropic()
    {
        var key = _anthropicKeyBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Введите API-ключ Anthropic.", "Тест", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Content = new StringContent(
                """{"model":"claude-haiku-4-5","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""",
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await http.SendAsync(req);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show("Подключение успешно! Ключ валиден.", "Anthropic",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 400 invalid_request_error (e.g. bad model) still means the key authenticated.
            // 401 = bad key.  402/429 = key OK but billing/quota issue (still valid).
            var status = (int)response.StatusCode;
            var snippet = body.Length > 600 ? body.Substring(0, 600) + "..." : body;
            var lowBody = body.ToLowerInvariant();

            if (status == 400 && !lowBody.Contains("authentication") && !lowBody.Contains("invalid api key"))
            {
                MessageBox.Show($"Ключ валиден (модель/запрос отклонены, но авторизация прошла).\n\n{snippet}",
                    "Anthropic", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var hint = status switch
            {
                401 => "\n\nКлюч недействителен.",
                403 => "\n\nДоступ запрещён.",
                429 => "\n\nRate limit / quota.",
                402 => "\n\nПроблема с биллингом.",
                _ => "",
            };
            MessageBox.Show(
                $"HTTP {status} {response.ReasonPhrase}{hint}\n\n{snippet}",
                "Anthropic — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("Тайм-аут подключения (10 сек).", "Anthropic",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка подключения:\n{ex.GetType().Name}: {ex.Message}",
                "Anthropic", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Helper: refresh dialog background after theme switch ─────────

    private void RefreshDialogBackground()
    {
        Background = (Brush)FindRes("Bg1Brush");
        Foreground = (Brush)FindRes("Fg1Brush");
    }

    // ── UI builder helpers ──────────────────────────────────────────

    private static object FindRes(string key) =>
        Application.Current.FindResource(key);

    private Button MakeTabButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.SetResourceReference(ForegroundProperty, "Fg2Brush");

        // Custom template for rounded corners + hover effect
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        factory.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };

        // Hover trigger
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(BackgroundProperty, (Brush)FindRes("Bg3Brush")));
        template.Triggers.Add(hoverTrigger);

        btn.Template = template;

        return btn;
    }

    private static TextBlock MakeText(string text, double size, FontWeight weight)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
        return tb;
    }

    private static TextBlock MakeLabel(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        return tb;
    }

    private static TextBox MakeTextBox(string initialText, bool isPassword)
    {
        var tb = new TextBox
        {
            Text = initialText,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            FontFamily = isPassword
                ? (FontFamily)Application.Current.FindResource("MonoFont")
                : (FontFamily)Application.Current.FindResource("UiFont"),
        };
        tb.SetResourceReference(TextBox.BackgroundProperty, "Bg3Brush");
        tb.SetResourceReference(TextBox.ForegroundProperty, "Fg1Brush");
        tb.SetResourceReference(TextBox.BorderBrushProperty, "Border2Brush");
        return tb;
    }

    private static Button MakeSmallButton(string text)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
        };
    }

    private static Border MakeSpacer(double height)
    {
        return new Border { Height = height };
    }

    private static UIElement WrapScroll(UIElement content)
    {
        // Right padding, чтобы контент не прилипал к скроллбару
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 12, 0),
            Content = content,
        };
    }

    private static DockPanel MakeInfoRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = 160,
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        row.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = value,
        };
        val.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        val.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        row.Children.Add(val);

        return row;
    }

    private static DockPanel MakeLinkRow(string label, string value, string url)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = 160,
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        row.Children.Add(lbl);

        var link = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = url,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            TextDecorations = TextDecorations.Underline,
        };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Accent2Brush");
        link.MouseLeftButtonUp += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        };
        row.Children.Add(link);

        return row;
    }

    // ── Панель управления обновлениями (в разделе «О программе») ──
    private void AddUpdateRow(StackPanel panel)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };

        var lbl = new TextBlock
        {
            Text = "Обновления:",
            FontSize = 12,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
        row.Children.Add(lbl);

        var statusText = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");

        var actionBtn = MakeSmallButton("Проверить");
        actionBtn.Padding = new Thickness(14, 4, 14, 4);
        actionBtn.MinWidth = 100;

        // Первоначальное состояние: если сервис уже нашёл апдейт — сразу показываем «Обновить»
        var pending = Fakunator.Core.Updater.UpdateService.Instance.Pending;
        if (pending != null)
        {
            statusText.Text = $"Доступно {pending.RemoteVersion} · {pending.DownloadSizeText}";
            statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            actionBtn.Content = "Обновить сейчас";
        }
        else
        {
            statusText.Text = "нажми чтобы проверить";
        }

        actionBtn.Click += async (_, _) =>
        {
            var svc = Fakunator.Core.Updater.UpdateService.Instance;
            if (svc.Pending != null)
            {
                // Уже знаем что апдейт есть — закрываем настройки, воскрешаем баннер.
                this.Close();
                svc.RepublishPending();
                return;
            }

            actionBtn.IsEnabled = false;
            statusText.Text = "Проверка…";
            statusText.Foreground = (Brush)Application.Current.FindResource("Fg4Brush");
            var found = await svc.CheckAsync();
            actionBtn.IsEnabled = true;
            if (found && svc.Pending != null)
            {
                statusText.Text = $"Доступно {svc.Pending.RemoteVersion} · {svc.Pending.DownloadSizeText}";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                actionBtn.Content = "Обновить сейчас";
            }
            else
            {
                statusText.Text = $"всё актуально · проверено {DateTime.Now:HH:mm}";
            }
        };

        DockPanel.SetDock(actionBtn, Dock.Right);
        row.Children.Add(actionBtn);
        row.Children.Add(statusText);
        panel.Children.Add(row);
    }

    private static int CountLinesInFile(string path)
    {
        if (!File.Exists(path)) return 0;
        int count = 0;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#') && !trimmed.StartsWith('['))
                count++;
        }
        return count;
    }
}
