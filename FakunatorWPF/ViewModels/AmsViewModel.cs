using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fakunator.Core;
using Fakunator.Core.AmsApi;

namespace Fakunator.ViewModels;

public class AmsViewModel : INotifyPropertyChanged
{
    private readonly object _lock = new();
    private AmsApiClient? _api;
    private string _lastCfgHost = "";
    private string _lastCfgKey = "";
    private bool _lastCfgHttps;
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    private CancellationTokenSource? _pollCts;
    private DateTime _lastFullRefresh = DateTime.MinValue;

    // ── Коллекции для каждого раздела ────────────────────────────────
    public ObservableCollection<AmsMailingRow> Mailings { get; } = new();
    public ObservableCollection<AmsMailingRow> FilteredMailings { get; } = new();
    public ObservableCollection<AmsSenderAccount> SenderAccounts { get; } = new();
    public ObservableCollection<AmsMailingList> MailingLists { get; } = new();
    /// <summary>Списки с полным путём «Родитель &gt; Ребёнок &gt; Лист» — для ComboBox с иерархией.</summary>
    public ObservableCollection<AmsMailingListNode> FlatMailingLists { get; } = new();
    public ObservableCollection<AmsMessage> Messages { get; } = new();
    public ObservableCollection<AmsDeliveryPreset> DeliveryPresets { get; } = new();

    // ── Выбранная рассылка + поиск + фильтр ──────────────────────────
    private AmsMailingRow? _selectedMailing;
    public AmsMailingRow? SelectedMailing
    {
        get => _selectedMailing;
        set
        {
            if (SetField(ref _selectedMailing, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(NoSelection));
                _ = LoadSelectedDetailsAsync();
            }
        }
    }
    public bool HasSelection => _selectedMailing != null;
    public bool NoSelection => _selectedMailing == null;

    // ── Расшифровка выбранной рассылки (для центральных карточек) ────
    // Заполняется через getMailing(id) + справочники (senders/lists/messages/presets).
    private string _selectedSenderName = "—";
    public string SelectedSenderName { get => _selectedSenderName; set => SetField(ref _selectedSenderName, value); }

    private string _selectedSenderEmail = "—";
    public string SelectedSenderEmail { get => _selectedSenderEmail; set => SetField(ref _selectedSenderEmail, value); }

    private string _selectedSenderReply = "—";
    public string SelectedSenderReply { get => _selectedSenderReply; set => SetField(ref _selectedSenderReply, value); }

    private string _selectedListName = "—";
    public string SelectedListName { get => _selectedListName; set => SetField(ref _selectedListName, value); }

    private string _selectedMessageName = "—";
    public string SelectedMessageName { get => _selectedMessageName; set => SetField(ref _selectedMessageName, value); }

    private string _selectedPresetName = "—";
    public string SelectedPresetName { get => _selectedPresetName; set => SetField(ref _selectedPresetName, value); }

    private int _selectedPresetThreads;
    public int SelectedPresetThreads { get => _selectedPresetThreads; set => SetField(ref _selectedPresetThreads, value); }
    public string SelectedPresetThreadsText => _selectedPresetThreads > 0 ? string.Format(Loc.T("ams.threadsFormat"), _selectedPresetThreads) : "—";

    private string _selectedPresetMethod = "—";
    public string SelectedPresetMethod { get => _selectedPresetMethod; set => SetField(ref _selectedPresetMethod, value); }

    private string _selectedPresetMode = "—";
    public string SelectedPresetMode { get => _selectedPresetMode; set => SetField(ref _selectedPresetMode, value); }

    private string _selectedPresetProxy = "—";
    public string SelectedPresetProxy { get => _selectedPresetProxy; set => SetField(ref _selectedPresetProxy, value); }

    // ── Bound-модели для ComboBox'ов в 4 карточках центра ────────────
    // При смене значения через UI (не при загрузке) — вызывается editMailing.
    // Гард _updatingSelection защищает от рекурсии при заливке из getMailing.
    private bool _updatingSelection;

    private AmsSenderAccount? _selectedSender;
    public AmsSenderAccount? SelectedSender
    {
        get => _selectedSender;
        set
        {
            if (SetField(ref _selectedSender, value) && !_updatingSelection && value != null)
                _ = ApplyComponentChangeAsync("senderAccount", value.Id, "ams.component.sender");
        }
    }

    private AmsMailingListNode? _selectedList;
    public AmsMailingListNode? SelectedList
    {
        get => _selectedList;
        set
        {
            if (SetField(ref _selectedList, value) && !_updatingSelection && value != null)
                _ = ApplyComponentChangeAsync("mailList", value.Id, "ams.component.mailList");
        }
    }

    private AmsMessage? _selectedMessage;
    public AmsMessage? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (SetField(ref _selectedMessage, value) && !_updatingSelection && value != null)
                _ = ApplyComponentChangeAsync("message", value.Id, "ams.component.message");
        }
    }

    private AmsDeliveryPreset? _selectedPreset;
    public AmsDeliveryPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value) && !_updatingSelection && value != null)
                _ = ApplyComponentChangeAsync("deliveryPreset", value.Id, "ams.component.deliveryPreset");
        }
    }

    /// <summary>labelKey — ключ локализации нейтральной (именительный падеж) формы компонента,
    /// подставляется в кавычки в сообщениях ниже (см. ams.confirm.workingLockedBody и т.п.).</summary>
    private async Task ApplyComponentChangeAsync(string key, int refId, string labelKey)
    {
        var row = _selectedMailing;
        if (row == null || _api == null) return;
        var label = Loc.T(labelKey);
        if (row.IsWorking)
        {
            MessageBox.Show(
                string.Format(Loc.T("ams.confirm.workingLockedBody"), label),
                "AMS", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadSelectedDetailsAsync();
            return;
        }
        try
        {
            var ok = await _api.SetMailingComponentAsync(row.Model.Id, key, refId);
            if (!ok)
            {
                MessageBox.Show(string.Format(Loc.T("ams.err.componentRejectedFormat"), label), "AMS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // В любом случае — перечитать актуальную конфигурацию рассылки
            await LoadSelectedDetailsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.componentChangeFormat"), label, ex.Message), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await LoadSelectedDetailsAsync();
        }
    }

    private async Task LoadSelectedDetailsAsync()
    {
        var row = _selectedMailing;
        if (row == null || _api == null)
        {
            ClearSelectedDetails();
            return;
        }
        try
        {
            var detail = await _api.GetMailingAsync(row.Model.Id);
            var s = detail?.Settings;
            if (s == null) { ClearSelectedDetails(); return; }

            SelectedSenderName = s.SenderAccount?.Name ?? "—";
            SelectedListName = s.MailList?.Name ?? "—";
            SelectedMessageName = s.Message?.Name ?? s.Message?.Subject ?? "—";
            SelectedPresetName = s.DeliveryPreset?.Name ?? "—";

            // Обогащаем из справочников — email/reply, threads/method/mode/proxy
            var sender = SenderAccounts.FirstOrDefault(x => x.Id == (s.SenderAccount?.Id ?? -1));
            var list = FlatMailingLists.FirstOrDefault(x => x.Id == (s.MailList?.Id ?? -1));
            var message = Messages.FirstOrDefault(x => x.Id == (s.Message?.Id ?? -1));
            var preset = DeliveryPresets.FirstOrDefault(x => x.Id == (s.DeliveryPreset?.Id ?? -1));

            SelectedSenderEmail = sender?.SenderEmail ?? "—";
            SelectedSenderReply = string.IsNullOrEmpty(sender?.ReplyToEmail) ? Loc.T("ams.replyNotSet") : sender.ReplyToEmail;
            SelectedPresetThreads = preset?.SendingThreads ?? 0;
            OnPropertyChanged(nameof(SelectedPresetThreadsText));
            SelectedPresetMethod = preset?.SendingMethod ?? "—";
            SelectedPresetMode = preset?.DeliveryMode ?? "—";
            SelectedPresetProxy = preset != null ? Loc.T(preset.ProxyUsed ? "ams.proxyOn" : "ams.proxyOff") : "—";

            // Ставим SelectedItem для ComboBox'ов БЕЗ триггера editMailing.
            _updatingSelection = true;
            try
            {
                SelectedSender = sender;
                SelectedList = list;
                SelectedMessage = message;
                SelectedPreset = preset;
            }
            finally { _updatingSelection = false; }
        }
        catch { ClearSelectedDetails(); }
    }

    private void ClearSelectedDetails()
    {
        SelectedSenderName = SelectedSenderEmail = SelectedSenderReply = "—";
        SelectedListName = SelectedMessageName = SelectedPresetName = "—";
        SelectedPresetThreads = 0;
        OnPropertyChanged(nameof(SelectedPresetThreadsText));
        SelectedPresetMethod = SelectedPresetMode = SelectedPresetProxy = "—";
        _updatingSelection = true;
        try { SelectedSender = null; SelectedList = null; SelectedMessage = null; SelectedPreset = null; }
        finally { _updatingSelection = false; }
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (SetField(ref _search, value)) RebuildFilter(); }
    }

    // "all" | "working" | "recent" | "errors"
    private string _filter = "all";
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterWorking));
                OnPropertyChanged(nameof(IsFilterRecent));
                RebuildFilter();
            }
        }
    }
    public bool IsFilterAll => _filter == "all";
    public bool IsFilterWorking => _filter == "working";
    public bool IsFilterRecent => _filter == "recent";

    // Максимум строк в фильтре «недавние».
    private const int RecentLimit = 20;

    // Счётчики для чипов фильтра
    public int CountAll => Mailings.Count;
    public int CountWorking => Mailings.Count(m => m.IsRunning);
    public int CountRecent => Math.Min(RecentLimit, Mailings.Count(m => !string.IsNullOrEmpty(m.Model.LastStartDate)));
    public bool HasWorking => CountWorking > 0;

    // Ссылка на API-эндпоинт для отображения в шапке (host из Config)
    public string EndpointText
    {
        get
        {
            var cfg = Config.Current;
            if (string.IsNullOrWhiteSpace(cfg.AmsApiHost)) return Loc.T("ams.endpoint.noHost");
            return cfg.AmsApiHost;
        }
    }

    /// <summary>true если хост и ключ AMS заданы — раскрываем функциональный UI.
    /// Иначе показываем заглушку «подключи AMS» вместо пустого скелета.</summary>
    public bool IsHostConfigured
    {
        get
        {
            var cfg = Config.Current;
            return !string.IsNullOrWhiteSpace(cfg.AmsApiHost)
                && !string.IsNullOrWhiteSpace(cfg.AmsApiKey);
        }
    }
    public bool NoHostConfigured => !IsHostConfigured;

    /// <summary>Пере-notify свойств, зависящих от Config.AmsApi*.
    /// Вызывается когда пользователь сохранил настройки.</summary>
    public void RaiseConfigDependentProps()
    {
        OnPropertyChanged(nameof(EndpointText));
        OnPropertyChanged(nameof(IsHostConfigured));
        OnPropertyChanged(nameof(NoHostConfigured));
    }

    // ── Секция (какую таблицу показываем) ────────────────────────────
    // Значения: "mailings" | "senders" | "lists" | "messages" | "presets"
    private string _section = "mailings";
    public string Section
    {
        get => _section;
        set
        {
            if (SetField(ref _section, value))
            {
                OnPropertyChanged(nameof(IsMailings));
                OnPropertyChanged(nameof(IsSenders));
                OnPropertyChanged(nameof(IsLists));
                OnPropertyChanged(nameof(IsMessages));
                OnPropertyChanged(nameof(IsPresets));
            }
        }
    }
    public bool IsMailings => _section == "mailings";
    public bool IsSenders => _section == "senders";
    public bool IsLists => _section == "lists";
    public bool IsMessages => _section == "messages";
    public bool IsPresets => _section == "presets";

    // ── Статус подключения к API ─────────────────────────────────────
    private Brush _statusDot = Brushes.Gray;
    public Brush StatusDot { get => _statusDot; set => SetField(ref _statusDot, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    private string _footerText = "";
    public string FooterText { get => _footerText; set => SetField(ref _footerText, value); }

    // Статус/футер хранятся как ключ+параметры (не готовый текст) — так StatusText/FooterText
    // можно пересобрать заново при смене языка (см. подписку в конструкторе).
    private string _statusKey = "ams.status.notConnected";
    private object[] _statusArgs = Array.Empty<object>();
    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        ApplyStatusText();
    }
    private void ApplyStatusText()
    {
        var fmt = Loc.T(_statusKey);
        StatusText = _statusArgs.Length > 0 ? string.Format(fmt, _statusArgs) : fmt;
    }

    private string _footerKey = "";
    private object[] _footerArgs = Array.Empty<object>();
    private void SetFooter(string key, params object[] args)
    {
        _footerKey = key;
        _footerArgs = args;
        ApplyFooterText();
    }
    private void ApplyFooterText()
    {
        if (string.IsNullOrEmpty(_footerKey)) { FooterText = ""; return; }
        var fmt = Loc.T(_footerKey);
        FooterText = _footerArgs.Length > 0 ? string.Format(fmt, _footerArgs) : fmt;
    }

    private bool _schedulerRunning;
    public bool SchedulerRunning
    {
        get => _schedulerRunning;
        set { if (SetField(ref _schedulerRunning, value)) OnPropertyChanged(nameof(SchedulerStopped)); }
    }
    public bool SchedulerStopped => !_schedulerRunning;

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    private static readonly Brush DotGreen = Freeze(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly Brush DotAmber = Freeze(Color.FromRgb(0xf5, 0x9e, 0x0b));
    private static readonly Brush DotRed = Freeze(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly Brush DotGray = Freeze(Color.FromRgb(0x9c, 0xa3, 0xaf));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ── Commands ─────────────────────────────────────────────────────
    public ICommand SelectSectionCommand { get; }
    public ICommand SelectFilterCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand StartMailingCommand { get; }
    public ICommand RestartMailingCommand { get; }
    public ICommand StopMailingCommand { get; }
    public ICommand DeleteMailingCommand { get; }
    public ICommand CloneMailingCommand { get; }
    public ICommand EditMailingCommand { get; }
    public ICommand LaunchCampaignCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand StartSchedulerCommand { get; }
    public ICommand PreviewMessageCommand { get; }

    public AmsViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(Mailings, _lock);
        BindingOperations.EnableCollectionSynchronization(FilteredMailings, _lock);
        BindingOperations.EnableCollectionSynchronization(SenderAccounts, _lock);
        BindingOperations.EnableCollectionSynchronization(MailingLists, _lock);
        BindingOperations.EnableCollectionSynchronization(FlatMailingLists, _lock);
        BindingOperations.EnableCollectionSynchronization(Messages, _lock);
        BindingOperations.EnableCollectionSynchronization(DeliveryPresets, _lock);

        SelectSectionCommand = new RelayCommand(o => { if (o is string s) Section = s; });
        SelectFilterCommand = new RelayCommand(o => { if (o is string s) Filter = s; });
        RefreshCommand = new RelayCommand(_ => _ = RefreshAllAsync());
        StartMailingCommand = new RelayCommand(o => _ = StartMailingAsync(RowFrom(o), "continue"));
        RestartMailingCommand = new RelayCommand(o => _ = RestartMailingAsync(RowFrom(o)));
        StopMailingCommand = new RelayCommand(o => _ = StopMailingAsync(RowFrom(o)));
        DeleteMailingCommand = new RelayCommand(o => _ = DeleteMailingAsync(RowFrom(o)));
        CloneMailingCommand = new RelayCommand(_ => _ = CloneMailingAsync(_selectedMailing));
        EditMailingCommand = new RelayCommand(_ => _ = EditMailingNameAsync(_selectedMailing));
        LaunchCampaignCommand = new RelayCommand(_ => OpenLaunchDialog());
        OpenSettingsCommand = new RelayCommand(_ => new Views.SettingsDialog(Application.Current?.MainWindow!).ShowDialog());
        StartSchedulerCommand = new RelayCommand(_ => _ = StartSchedulerAsync());
        PreviewMessageCommand = new RelayCommand(_ => _ = PreviewSelectedMessageAsync());

        // Config.Changed — общий сигнал "конфиг сохранён", дёргается на ЛЮБОЕ изменение
        // (включая переключение языка интерфейса). Пересоздавать AMS-клиента нужно только
        // если реально поменялись хост/ключ/https — иначе смена языка рвала бы соединение.
        Config.Changed += (_, _) =>
        {
            var cfg = Config.Current;
            if (cfg.AmsApiHost != _lastCfgHost || cfg.AmsApiKey != _lastCfgKey || cfg.AmsUseHttps != _lastCfgHttps)
                ResetClient();
            RaiseConfigDependentProps();
        };

        ApplyStatusText();

        // При смене языка: пересобрать StatusText/FooterText из сохранённых ключей+параметров,
        // переоценить вычисляемые EndpointText/SelectedPresetThreadsText, обновить строки рассылок
        // (TypeText/StateText/UpdatedText локализуются на лету через Loc.T) и пересчитать готовый
        // текст SelectedSenderReply/SelectedPresetProxy локально — БЕЗ повторного похода в сеть
        // (LoadSelectedDetailsAsync раньше дёргался заново, но лишний GetMailingAsync на каждое
        // переключение языка мог упасть на любой сетевой заминке — а тихий catch в этом методе
        // обнулял все поля карточки, из-за чего смена языка выглядела как "пропали все данные").
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            ApplyStatusText();
            ApplyFooterText();
            OnPropertyChanged(nameof(EndpointText));
            OnPropertyChanged(nameof(SelectedPresetThreadsText));
            foreach (var r in Mailings) r.RefreshLocalization();
            if (_selectedMailing != null)
            {
                SelectedSenderReply = string.IsNullOrEmpty(_selectedSender?.ReplyToEmail)
                    ? Loc.T("ams.replyNotSet") : _selectedSender.ReplyToEmail;
                SelectedPresetProxy = _selectedPreset != null
                    ? Loc.T(_selectedPreset.ProxyUsed ? "ams.proxyOn" : "ams.proxyOff") : "—";
            }
        };
    }

    private AmsMailingRow? RowFrom(object? o) => o as AmsMailingRow;

    /// <summary>Вкладка активирована — если ещё не грузили, стартуем.</summary>
    public void OnTabActivated()
    {
        if ((DateTime.UtcNow - _lastFullRefresh).TotalSeconds < 10) return;
        _ = RefreshAllAsync();
    }

    private void ResetClient()
    {
        _api?.Dispose();
        _api = null;
    }

    private AmsApiClient? BuildClient()
    {
        var cfg = Config.Current;
        if (string.IsNullOrWhiteSpace(cfg.AmsApiHost) || string.IsNullOrWhiteSpace(cfg.AmsApiKey))
        {
            StatusDot = DotGray;
            SetStatus("ams.status.noHostKey");
            return null;
        }
        try
        {
            _lastCfgHost = cfg.AmsApiHost;
            _lastCfgKey = cfg.AmsApiKey;
            _lastCfgHttps = cfg.AmsUseHttps;
            _api ??= new AmsApiClient(cfg.AmsApiHost, cfg.AmsApiKey, cfg.AmsUseHttps);
            return _api;
        }
        catch (Exception ex)
        {
            StatusDot = DotRed;
            SetStatus("ams.status.configErrorFormat", ex.Message);
            return null;
        }
    }

    public async Task RefreshAllAsync()
    {
        var api = BuildClient();
        if (api == null) return;
        Busy = true;
        StatusDot = DotAmber;
        SetStatus("ams.status.loading");
        try
        {
            SchedulerRunning = await api.IsSchedulerRunningAsync();

            var mailingsTask = api.GetMailingsAsync();
            var sendersTask = api.GetSenderAccountsAsync();
            var listsTask = api.GetMailingListsAsync();
            var messagesTask = api.GetMessagesAsync();
            var presetsTask = api.GetDeliveryPresetsAsync();
            await Task.WhenAll(mailingsTask, sendersTask, listsTask, messagesTask, presetsTask);

            var ms = mailingsTask.Result ?? new();
            var ss = sendersTask.Result ?? new();
            var ls = listsTask.Result ?? new();
            var msgs = messagesTask.Result ?? new();
            var ps = presetsTask.Result ?? new();

            ApplyMailings(ms);
            lock (_lock)
            {
                SenderAccounts.Clear(); foreach (var x in ss) SenderAccounts.Add(x);
                MailingLists.Clear(); foreach (var x in ls) MailingLists.Add(x);
                RebuildFlatLists(ls);
                Messages.Clear(); foreach (var x in msgs) Messages.Add(x);
                DeliveryPresets.Clear(); foreach (var x in ps) DeliveryPresets.Add(x);
            }
            StatusDot = DotGreen;
            SetStatus(SchedulerRunning ? "ams.status.connectedSchedulerRunning" : "ams.status.connectedSchedulerStopped");
            SetFooter("ams.footer.summaryFormat", ms.Count, ss.Count, ls.Count, msgs.Count, ps.Count);
            _lastFullRefresh = DateTime.UtcNow;

            EnsurePollTimer();
        }
        catch (Exception ex)
        {
            StatusDot = DotRed;
            SetStatus("ams.status.errorPrefixFormat", ex.Message);
        }
        finally { Busy = false; }
    }

    /// <summary>Быстрый poll только рассылок — каждые 3 секунды если есть working.</summary>
    private async Task PollMailingsAsync()
    {
        if (_polling) return;
        var api = _api;
        if (api == null) return;
        _polling = true;
        try
        {
            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;
            var ms = await api.GetMailingsAsync(ct);
            if (ms == null || ct.IsCancellationRequested) return;
            ApplyMailings(ms);
            SchedulerRunning = await api.IsSchedulerRunningAsync(ct);
        }
        catch { /* тихо — poll не критичен */ }
        finally { _polling = false; }
    }

    private void ApplyMailings(List<AmsMailing> incoming)
    {
        lock (_lock)
        {
            // Сохраняем существующие строки по id, обновляем инкрементально —
            // так ListBox не сбрасывает выделение при каждом poll.
            var byId = Mailings.ToDictionary(r => r.Model.Id);
            var seen = new HashSet<int>();
            foreach (var m in incoming)
            {
                seen.Add(m.Id);
                if (byId.TryGetValue(m.Id, out var existing))
                    existing.Update(m);
                else
                    Mailings.Add(new AmsMailingRow(m));
            }
            for (int i = Mailings.Count - 1; i >= 0; i--)
                if (!seen.Contains(Mailings[i].Model.Id)) Mailings.RemoveAt(i);
        }
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountWorking));
        OnPropertyChanged(nameof(CountRecent));
        OnPropertyChanged(nameof(HasWorking));
        // Автопадение обратно на «все» если «идут» опустел — не показываем пустой центр.
        if (_filter == "working" && CountWorking == 0)
        {
            Filter = "all";
            return; // Filter setter сам вызовет RebuildFilter
        }
        RebuildFilter();
    }

    /// <summary>Применяет поиск + фильтр к FilteredMailings.
    /// Обновление ИНКРЕМЕНТАЛЬНОЕ — без Clear+Add, иначе ListBox моргает и
    /// сбрасывает SelectedItem при каждом poll (раз в 3 секунды).</summary>
    private void RebuildFilter()
    {
        var q = (_search ?? "").Trim();
        var desired = new List<AmsMailingRow>(Mailings.Count);
        foreach (var m in Mailings)
        {
            if (!string.IsNullOrEmpty(q) && m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            switch (_filter)
            {
                case "working": if (!m.IsRunning) continue; break;
                case "recent":  if (string.IsNullOrEmpty(m.Model.LastStartDate)) continue; break;
                case "errors":  if (!m.HasError) continue; break;
            }
            desired.Add(m);
        }
        // Для «недавних» — сортировка по дате последнего запуска (desc) и топ-20.
        if (_filter == "recent")
        {
            desired = desired
                .OrderByDescending(m => DateTime.TryParse(m.Model.LastStartDate, out var dt) ? dt : DateTime.MinValue)
                .Take(RecentLimit)
                .ToList();
        }

        lock (_lock)
        {
            var desiredSet = new HashSet<AmsMailingRow>(desired);
            // 1. Удаляем те что больше не подходят под фильтр
            for (int i = FilteredMailings.Count - 1; i >= 0; i--)
                if (!desiredSet.Contains(FilteredMailings[i])) FilteredMailings.RemoveAt(i);
            // 2. Двигаем/добавляем чтобы порядок совпал с desired
            for (int i = 0; i < desired.Count; i++)
            {
                var target = desired[i];
                if (i < FilteredMailings.Count && ReferenceEquals(FilteredMailings[i], target)) continue;
                var cur = FilteredMailings.IndexOf(target);
                if (cur < 0) FilteredMailings.Insert(i, target);
                else if (cur != i) FilteredMailings.Move(cur, i);
            }
        }

        // SelectedMailing НЕ трогаем если он всё ещё в списке — иначе LoadSelectedDetailsAsync
        // будет крутиться при каждом poll, а UI детали моргать.
        if (_selectedMailing != null && FilteredMailings.Contains(_selectedMailing)) return;
        SelectedMailing = FilteredMailings.FirstOrDefault();
    }

    /// <summary>Строит плоский список с полными путями «Родитель &gt; Ребёнок». Порядок — DFS
    /// по родителям, чтобы дочерние шли сразу за родителем (как в дереве AMS).</summary>
    private void RebuildFlatLists(List<AmsMailingList> lists)
    {
        FlatMailingLists.Clear();
        var byParent = lists.GroupBy(l => l.ParentID).ToDictionary(g => g.Key, g => g.OrderBy(x => x.ListName).ToList());
        void Walk(int parentId, int depth, string parentPath)
        {
            if (!byParent.TryGetValue(parentId, out var kids)) return;
            foreach (var l in kids)
            {
                var path = string.IsNullOrEmpty(parentPath) ? l.ListName : $"{parentPath}  ›  {l.ListName}";
                FlatMailingLists.Add(new AmsMailingListNode
                {
                    Model = l,
                    FullPath = path,
                    Depth = depth,
                    IsFolder = byParent.ContainsKey(l.Id),
                });
                Walk(l.Id, depth + 1, path);
            }
        }
        // Начинаем от корня (-1). Если у AMS корень имеет ParentID=-1 → всё цепочка сработает.
        Walk(-1, 0, "");
        // Если что-то не привязано к дереву (сирота — parent не найден) — добавим отдельно
        var placed = new HashSet<int>(FlatMailingLists.Select(x => x.Id));
        foreach (var l in lists.Where(x => !placed.Contains(x.Id)).OrderBy(x => x.ListName))
        {
            FlatMailingLists.Add(new AmsMailingListNode
            { Model = l, FullPath = l.ListName, Depth = 0 });
        }
    }

    private void EnsurePollTimer()
    {
        if (_pollTimer != null) return;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += async (_, _) =>
        {
            // Poll только если есть работающие рассылки — иначе смысл дёргать API редко.
            var hasRunning = Mailings.Any(r => r.IsWorking);
            if (hasRunning || SchedulerRunning)
                await PollMailingsAsync();
        };
        _pollTimer.Start();
    }

    private async Task StartMailingAsync(AmsMailingRow? row, string startMode = "continue")
    {
        if (row == null || _api == null) return;
        try
        {
            // Планировщик AMS обязан быть запущен — иначе startMailing возвращает OK,
            // но реальная отправка не идёт (в GUI ничего не меняется, юзер думает что баг).
            if (!SchedulerRunning)
            {
                var ok = await _api.IsSchedulerRunningAsync();
                if (!ok)
                {
                    var confirm = MessageBox.Show(
                        Loc.T("ams.confirm.schedulerStoppedBody"),
                        Loc.T("ams.confirm.schedulerStoppedTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.Yes) return;
                    await _api.RunSchedulerAsync();
                }
                SchedulerRunning = true;
            }

            var startOk = await _api.StartMailingAsync(row.Model.Id, startMode);
            if (startOk)
            {
                row.SetState("working");
                SetStatus("ams.status.launchSentFormat", row.Model.Id, startMode);
                await PollMailingsAsync();
            }
            else MessageBox.Show(Loc.T("ams.err.startRejected"), "AMS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.genericFormat"), ex.Message), "AMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task PreviewSelectedMessageAsync()
    {
        if (_selectedMessage == null) { MessageBox.Show(Loc.T("ams.err.noMessageSelectedBody"), Loc.T("ams.err.noMessageSelectedTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_api == null && BuildClient() == null)
        {
            MessageBox.Show(Loc.T("ams.err.notConfiguredBody"), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await Views.MessagePreviewDialog.ShowFromAmsAsync(
            Application.Current?.MainWindow!, _api!, _selectedMessage.Id,
            senderName: _selectedSenderName == "—" ? null : _selectedSenderName,
            senderEmail: _selectedSenderEmail == "—" ? null : _selectedSenderEmail);
    }

    private async Task StartSchedulerAsync()
    {
        if (_api == null) return;
        try
        {
            await _api.RunSchedulerAsync();
            SchedulerRunning = await _api.IsSchedulerRunningAsync();
            SetStatus(SchedulerRunning ? "ams.status.connectedSchedulerRunning" : "ams.status.schedulerNotResponding");
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.schedulerStartFailedFormat"), ex.Message), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RestartMailingAsync(AmsMailingRow? row)
    {
        if (row == null || _api == null) return;
        // Стираем прогресс необратимо — предупреждаем. Особенно для рассылок с
        // высоким процентом, чтобы юзер не «переотправил» всё с нуля случайно.
        var pct = row.PercentDone;
        var confirm = MessageBox.Show(
            string.Format(Loc.T("ams.confirm.restartBodyFormat"), row.Name, pct),
            Loc.T("ams.confirm.restartTitle"),
            MessageBoxButton.YesNo,
            pct >= 100 ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        await StartMailingAsync(row, "restart");
    }

    private async Task StopMailingAsync(AmsMailingRow? row)
    {
        if (row == null || _api == null) return;
        try
        {
            var ok = await _api.StopMailingAsync(row.Model.Id);
            if (ok) { row.SetState("stopping"); await PollMailingsAsync(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.genericFormat"), ex.Message), "AMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Клонирует рассылку: читает её настройки и создаёт новую с суффиксом «(копия)».</summary>
    private async Task CloneMailingAsync(AmsMailingRow? row)
    {
        if (row == null || _api == null) return;
        try
        {
            var detail = await _api.GetMailingAsync(row.Model.Id);
            var s = detail?.Settings;
            if (s == null || s.SenderAccount == null || s.MailList == null
                || s.Message == null || s.DeliveryPreset == null)
            {
                MessageBox.Show(Loc.T("ams.err.cloneReadFailedBody"), Loc.T("ams.err.cloneTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var newId = await _api.AddMailingAsync(new AmsMailingCreate
            {
                Name = row.Name + Loc.T("ams.cloneSuffix"),
                Type = row.Model.Type,
                SenderAccountId = s.SenderAccount.Id,
                MessageId = s.Message.Id,
                DeliveryPresetId = s.DeliveryPreset.Id,
                MailingListIds = new List<int> { s.MailList.Id },
            });
            if (newId > 0)
            {
                SetStatus("ams.status.cloneCreatedFormat", newId);
                await RefreshAllAsync();
                var newRow = Mailings.FirstOrDefault(x => x.Model.Id == newId);
                if (newRow != null) SelectedMailing = newRow;
            }
            else MessageBox.Show(Loc.T("ams.err.cloneNoIdBody"), Loc.T("ams.err.cloneTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.cloneFailedFormat"), ex.Message), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Правит только название рассылки — остальные поля меняются в ComboBox'ах карточек.</summary>
    private async Task EditMailingNameAsync(AmsMailingRow? row)
    {
        if (row == null || _api == null) return;
        var newName = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.T("ams.rename.prompt"), Loc.T("ams.rename.title"), row.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == row.Name) return;
        try
        {
            await _api.EditMailingAsync(row.Model.Id, new Dictionary<string, object?> { ["name"] = newName.Trim() });
            row.Model.Name = newName.Trim();
            // Строка сама переисуется через notify — вызываем через SetState (шире):
            row.SetState(row.Model.State);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.renameFailedFormat"), ex.Message), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteMailingAsync(AmsMailingRow? row)
    {
        if (row == null || _api == null) return;
        var confirm = MessageBox.Show(
            string.Format(Loc.T("ams.confirm.deleteBodyFormat"), row.Name, row.Model.Id),
            Loc.T("ams.confirm.deleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var ok = await _api.DeleteMailingAsync(row.Model.Id);
            if (ok) lock (_lock) Mailings.Remove(row);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("ams.err.genericFormat"), ex.Message), "AMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLaunchDialog()
    {
        if (_api == null && BuildClient() == null)
        {
            MessageBox.Show(Loc.T("ams.err.notConfiguredBody"), "AMS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Views.LaunchAmsCampaignDialog(_api!, SenderAccounts.ToList(),
            MailingLists.ToList(), Messages.ToList(), DeliveryPresets.ToList())
        { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() == true) _ = RefreshAllAsync();
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
