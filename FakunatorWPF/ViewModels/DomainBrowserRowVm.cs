using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Fakunator.Core.DomainScanner;

namespace Fakunator.ViewModels;

/// <summary>
/// UI-строка браузера доменов: базовые поля из DomainDb + ленивая health-info
/// (возраст / до истечения / RBL). Health подгружается фоново после отображения
/// страницы браузера — apply через SetHealth() который триггерит INotify.
/// </summary>
public class DomainBrowserRowVm : INotifyPropertyChanged
{
    public string Domain { get; }
    public string Zone { get; }
    public string NsProvider { get; }
    public string NsHosts { get; }

    private static readonly SolidColorBrush Gray = Frozen(Color.FromRgb(0x9c, 0xa3, 0xaf));
    private static readonly SolidColorBrush Green = Frozen(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly SolidColorBrush Amber = Frozen(Color.FromRgb(0xea, 0xb3, 0x08));
    private static readonly SolidColorBrush Red = Frozen(Color.FromRgb(0xef, 0x44, 0x44));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private DomainHealth? _h;

    public DomainBrowserRowVm(DomainDb.DomainRow row)
    {
        Domain = row.Domain;
        Zone = row.Zone;
        NsProvider = row.NsProvider ?? "";
        NsHosts = row.NsHosts ?? "";
    }

    /// <summary>Применить свежие health-данные, одним notify обновляем всё (пустая строка = все свойства).</summary>
    public void SetHealth(DomainHealth? h)
    {
        _h = h;
        // Пустая строка в PropertyChangedEventArgs = все свойства row обновляются, WPF читает все bindings.
        OnPc(string.Empty);
    }

    // ── Age (возраст) ────────────────────────────────────────────────
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

    // ── DaysLeft (до истечения) ──────────────────────────────────────
    public string DaysLeftText
    {
        get
        {
            if (_h == null) return "…";
            if (_h.ExpiresAt == null) return "?";
            var d = (int)(_h.ExpiresAt.Value - DateTime.UtcNow).TotalDays;
            if (d < 0) return $"истёк {-d}д";
            return d + " дн.";
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

    // ── RBL ───────────────────────────────────────────────────────────
    // Каждый hit хранится как "SPAMHAUS:spam" / "SURBL:phishing" — локализуем.
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
            // Собираем уникальные категории по всем hits, локализуем
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
    public Brush RblColor => _h == null ? Gray
                            : (_h.RblHits.Count == 0 ? Green : Red);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPc([CallerMemberName] string? p = null)
    {
        // Non-blocking dispatch — если вызывается с background-thread (whois worker),
        // не тормозим UI-поток. Invoke блокировал бы очередь при 6+ concurrent notify.
        var dp = System.Windows.Application.Current?.Dispatcher;
        if (dp == null || dp.CheckAccess())
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        else
            dp.BeginInvoke(new Action(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p))));
    }
}
