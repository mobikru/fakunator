using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Fakunator.Core.DomainScanner;
using Fakunator.Core.DomainsManager;
using Fakunator.Core.Postmaster;

namespace Fakunator.ViewModels;

internal static class AvatarPalette
{
    // Устойчивый набор цветов для аватарок аккаунтов — тон подбирается по хешу имени.
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x5e, 0x6a, 0xd2), // indigo
        Color.FromRgb(0x8b, 0x5c, 0xf6), // violet
        Color.FromRgb(0x3b, 0x82, 0xf6), // blue
        Color.FromRgb(0x0e, 0xa5, 0xe9), // sky
        Color.FromRgb(0x14, 0xb8, 0xa6), // teal
        Color.FromRgb(0x22, 0xc5, 0x5e), // green
        Color.FromRgb(0xf5, 0x9e, 0x0b), // amber
        Color.FromRgb(0xef, 0x44, 0x44), // red
        Color.FromRgb(0xec, 0x48, 0x99), // pink
    };
    public static Brush BrushFor(string key)
    {
        var s = string.IsNullOrEmpty(key) ? "?" : key;
        int h = 0;
        foreach (var c in s) h = ((h << 5) - h) + c;
        var color = Palette[System.Math.Abs(h) % Palette.Length];
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
    public static string LetterFor(string s) =>
        string.IsNullOrEmpty(s) ? "?" : s[..1].ToUpperInvariant();
}

/// <summary>Обёртка IspAccount для UI-переключателя.</summary>
public class IspAccountChip : INotifyPropertyChanged
{
    public IspAccount Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Host => Model.Host;
    public string Username => Model.Username;
    public string AvatarLetter => AvatarPalette.LetterFor(Model.DisplayName ?? Model.Username);
    public Brush AvatarBrush => AvatarPalette.BrushFor(Model.DisplayName + Model.Host);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPc(); } }
    }
    public IspAccountChip(IspAccount m) { Model = m; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPc([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Обёртка PostmasterAccount для UI-переключателя.</summary>
public class PostmasterAccountChip : INotifyPropertyChanged
{
    public PostmasterAccount Model { get; }
    public string DisplayName => string.IsNullOrEmpty(Model.DisplayName) ? Model.Username : Model.DisplayName;
    public string Username => Model.Username;
    public string AvatarLetter => AvatarPalette.LetterFor(DisplayName);
    public Brush AvatarBrush => AvatarPalette.BrushFor(Model.Username);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPc(); } }
    }
    public PostmasterAccountChip(PostmasterAccount m) { Model = m; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPc([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Строка «домен в DNS-панели» с готовыми к UI полями.</summary>
public class DnsDomainRow : INotifyPropertyChanged
{
    public string Domain { get; }

    private string _ns = "…";
    public string Ns { get => _ns; set { if (_ns != value) { _ns = value; OnPc(); OnPc(nameof(NsColor)); } } }
    public Brush NsColor => NsPalette.BrushFor(_ns);

    private Brush _statusDot = Brushes.Gray;
    public Brush StatusDot { get => _statusDot; set { if (!Equals(_statusDot, value)) { _statusDot = value; OnPc(); } } }

    private string _postmasterStatusText = "не добавлен";
    public string PostmasterStatusText
    {
        get => _postmasterStatusText;
        set { if (_postmasterStatusText != value) { _postmasterStatusText = value; OnPc(); OnPc(nameof(PostmasterChipIcon)); } }
    }

    // Иконка чипа — маленький глиф ✓/○/✕ в зависимости от текста
    public string PostmasterChipIcon => _postmasterStatusText switch
    {
        "верифицирован" => "●",
        "отклонён" => "✕",
        _ => "○"
    };

    private Brush _postmasterChipBg = Brushes.Transparent;
    public Brush PostmasterChipBg { get => _postmasterChipBg; set { _postmasterChipBg = value; OnPc(); } }
    private Brush _postmasterChipBd = Brushes.Transparent;
    public Brush PostmasterChipBd { get => _postmasterChipBd; set { _postmasterChipBd = value; OnPc(); } }
    private Brush _postmasterChipFg = Brushes.Gray;
    public Brush PostmasterChipFg { get => _postmasterChipFg; set { _postmasterChipFg = value; OnPc(); } }

    // ── Health (Возраст / Осталось / RBL) — те же поля что в сканере доменов ──
    private static readonly SolidColorBrush Gray = Frozen(Color.FromRgb(0x9c, 0xa3, 0xaf));
    private static readonly SolidColorBrush Green = Frozen(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly SolidColorBrush Amber = Frozen(Color.FromRgb(0xea, 0xb3, 0x08));
    private static readonly SolidColorBrush Red = Frozen(Color.FromRgb(0xef, 0x44, 0x44));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private DomainHealth? _h;
    public void SetHealth(DomainHealth? h)
    {
        _h = h;
        OnPc(string.Empty); // все health-свойства обновятся одним notify
    }

    public string AgeText
    {
        get
        {
            if (_h == null) return "…";
            if (_h.CreatedAt == null) return "?";
            var d = (DateTime.UtcNow - _h.CreatedAt.Value).TotalDays;
            if (d < 30) return $"{(int)d} дн.";
            if (d < 365) return $"{(int)(d / 30)} мес.";
            return $"{d / 365:F1} лет";
        }
    }
    public Brush AgeColor
    {
        get
        {
            if (_h?.CreatedAt == null) return Gray;
            var d = (DateTime.UtcNow - _h.CreatedAt.Value).TotalDays;
            if (d < 90) return Amber;
            if (d < 730) return Green;
            return Gray;
        }
    }
    public string DaysLeftText
    {
        get
        {
            if (_h == null) return "…";
            if (_h.ExpiresAt == null) return "?";
            var d = (int)(_h.ExpiresAt.Value - DateTime.UtcNow).TotalDays;
            return d < 0 ? $"истёк {-d}д" : d + " дн.";
        }
    }
    public Brush DaysLeftColor
    {
        get
        {
            if (_h?.ExpiresAt == null) return Gray;
            var d = (_h.ExpiresAt.Value - DateTime.UtcNow).TotalDays;
            if (d < 30) return Red;
            if (d < 90) return Amber;
            return Green;
        }
    }
    private static readonly Dictionary<string, string> RuCat = new()
    {
        ["spam"] = "спам", ["phishing"] = "фишинг", ["malware"] = "вирусы",
        ["botnet"] = "ботнет", ["abuse"] = "злоупотр.", ["cracked"] = "взломан",
        ["redirector"] = "редиректор", ["jwspamspy"] = "jwspam",
        ["abused-spam"] = "угнан: спам", ["abused-phishing"] = "угнан: фишинг",
        ["abused-malware"] = "угнан: вирусы", ["abused-botnet"] = "угнан: ботнет",
        ["listed"] = "в списке",
    };
    public string RblText
    {
        get
        {
            if (_h == null) return "…";
            if (_h.RblHits.Count == 0) return "✓ чистый";
            var cats = new HashSet<string>();
            foreach (var hit in _h.RblHits)
            {
                var i = hit.IndexOf(':');
                var cat = i >= 0 ? hit[(i + 1)..] : hit;
                cats.Add(RuCat.TryGetValue(cat, out var ru) ? ru : cat);
            }
            return "✗ " + string.Join(" · ", cats);
        }
    }
    public Brush RblColor => _h == null ? Gray : (_h.RblHits.Count == 0 ? Green : Red);

    public DnsDomainRow(string domain) { Domain = domain; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPc([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

internal static class NsPalette
{
    // Небольшой цветной квадрат перед именем NS-провайдера — быстрая визуальная идентификация.
    public static Brush BrushFor(string ns)
    {
        Color c = ns switch
        {
            "cloudflare" => Color.FromRgb(0xf3, 0x80, 0x20),
            "reg.ru"     => Color.FromRgb(0x00, 0x7a, 0xff),
            "nic.ru"     => Color.FromRgb(0xff, 0x66, 0x00),
            "yandex"     => Color.FromRgb(0xff, 0xcc, 0x00),
            "selectel"   => Color.FromRgb(0xff, 0x00, 0x66),
            "beget"      => Color.FromRgb(0x00, 0xa8, 0x8e),
            "timeweb"    => Color.FromRgb(0x02, 0x1a, 0x2f),
            "isp"        => Color.FromRgb(0x81, 0x8c, 0xf8),
            _            => Color.FromRgb(0x71, 0x71, 0x7a),
        };
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Строка «домен в постмастере».
/// Все UI-биндингуемые поля идут через <see cref="Set{T}"/> с notify — иначе
/// апдейт метрик из фонового <c>LoadPostmasterMetricsAsync</c> не долетал бы до
/// таблицы, и VM приходилось делать Remove+Insert (что сбрасывало SelectedItem
/// и закрывало drawer открытый юзером).</summary>
public class PostmasterDomainRow : INotifyPropertyChanged
{
    public string Domain { get; }
    public PostmasterDomainRow(string domain) { Domain = domain; }

    private Brush _statusDot = Brushes.Gray;
    public Brush StatusDot { get => _statusDot; set => Set(ref _statusDot, value); }

    private string _troublesMark = "";
    public string TroublesMark { get => _troublesMark; set => Set(ref _troublesMark, value); }

    private string _verificationText = "—";
    public string VerificationText { get => _verificationText; set => Set(ref _verificationText, value); }

    private string _verificationIcon = "○";
    public string VerificationIcon { get => _verificationIcon; set => Set(ref _verificationIcon, value); }

    private Brush _verificationFg = Brushes.Gray;
    public Brush VerificationFg { get => _verificationFg; set => Set(ref _verificationFg, value); }

    private string _messagesSentText = "—";
    public string MessagesSentText { get => _messagesSentText; set => Set(ref _messagesSentText, value); }

    private string _spamPercentText = "—";
    public string SpamPercentText { get => _spamPercentText; set => Set(ref _spamPercentText, value); }

    private Brush _spamFg = Brushes.Gray;
    public Brush SpamFg { get => _spamFg; set => Set(ref _spamFg, value); }

    private string _reputationText = "—";
    public string ReputationText { get => _reputationText; set => Set(ref _reputationText, value); }

    private string _reputationPctText = "—";
    public string ReputationPctText { get => _reputationPctText; set => Set(ref _reputationPctText, value); }

    private double _reputationBarWidth;
    public double ReputationBarWidth { get => _reputationBarWidth; set => Set(ref _reputationBarWidth, value); }

    private Brush _reputationBarFill = Brushes.Transparent;
    public Brush ReputationBarFill { get => _reputationBarFill; set => Set(ref _reputationBarFill, value); }

    // ── Доп. метрики (как в веб-постмастере) ──────────────────────────
    private string _complaintsText = "—";
    public string ComplaintsText { get => _complaintsText; set => Set(ref _complaintsText, value); }

    private string _trendText = "—";
    public string TrendText { get => _trendText; set => Set(ref _trendText, value); }

    private Brush _trendFg = Brushes.Gray;
    public Brush TrendFg { get => _trendFg; set => Set(ref _trendFg, value); }

    private string _readText = "—";
    public string ReadText { get => _readText; set => Set(ref _readText, value); }

    private string _deletedReadText = "—";
    public string DeletedReadText { get => _deletedReadText; set => Set(ref _deletedReadText, value); }

    private string _deletedUnreadText = "—";
    public string DeletedUnreadText { get => _deletedUnreadText; set => Set(ref _deletedUnreadText, value); }

    // ── Доставляемость: 3-цветный stacked-bar (зел/жёлт/красн) ────────
    // Ширина = процент × total-width; total-width задаётся в XAML, здесь %.
    private double _deliveredPct;
    public double DeliveredPct { get => _deliveredPct; set => Set(ref _deliveredPct, value); }

    private double _probablySpamPct;
    public double ProbablySpamPct { get => _probablySpamPct; set => Set(ref _probablySpamPct, value); }

    private double _spamPct;
    public double SpamPct { get => _spamPct; set => Set(ref _spamPct, value); }

    private double _deliveredBarWidth;
    public double DeliveredBarWidth { get => _deliveredBarWidth; set => Set(ref _deliveredBarWidth, value); }

    private double _probablySpamBarWidth;
    public double ProbablySpamBarWidth { get => _probablySpamBarWidth; set => Set(ref _probablySpamBarWidth, value); }

    private double _spamBarWidth;
    public double SpamBarWidth { get => _spamBarWidth; set => Set(ref _spamBarWidth, value); }

    private string _deliverabilityTooltip = "";
    public string DeliverabilityTooltip { get => _deliverabilityTooltip; set => Set(ref _deliverabilityTooltip, value); }

    // Данные, не биндящиеся напрямую в таблицу — auto-props без notify.
    public List<PostmasterTrouble> Troubles { get; set; } = new();
    public Dictionary<string, double> AllStats { get; set; } = new();
    public List<PostmasterDaily> Daily { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
