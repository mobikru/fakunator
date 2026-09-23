using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.AmsApi;

namespace Fakunator.ViewModels;

/// <summary>
/// UI-строка для одной рассылки — обёртка над <see cref="AmsMailing"/> с
/// человеко-читаемыми полями и notify. Пересобирается через <see cref="Update"/>
/// при каждом poll, чтобы DataGrid не сбрасывал выделение.
/// </summary>
public class AmsMailingRow : INotifyPropertyChanged
{
    public AmsMailing Model { get; private set; }

    public AmsMailingRow(AmsMailing m)
    {
        Model = m;
    }

    public int Id => Model.Id;
    public string Name => Model.Name;

    // ── Тип рассылки: mailing / transactional / validation ───────────
    // Ключ, не текст — иначе сравнение/отображение ломается при смене языка (ru/en).
    public string TypeText => Model.Type switch
    {
        "mailing" => Loc.T("ams.type.mailing"),
        "transactional" => Loc.T("ams.type.transactional"),
        "validation" => Loc.T("ams.type.validation"),
        _ => Model.Type,
    };

    // ── Состояние (idle / working / stopping) ────────────────────────
    public string StateText => Model.State switch
    {
        "idle" => Loc.T("ams.state.idle"),
        "working" => Loc.T("ams.state.working"),
        "stopping" => Loc.T("ams.state.stopping"),
        _ => Model.State,
    };

    private static readonly Brush Gray = Frozen(Color.FromRgb(0x9c, 0xa3, 0xaf));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xf5, 0x9e, 0x0b));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x3b, 0x82, 0xf6));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public Brush StateColor => Model.State switch
    {
        "working" => Green,
        "stopping" => Amber,
        _ => Model.LastStopByPostmaster ? Red : Gray,
    };

    public bool IsWorking => Model.State == "working" || Model.State == "stopping";
    public bool CanStart => Model.State == "idle";
    public bool CanStop => IsWorking;

    /// <summary>«▶ Старт» (continue) имеет смысл только пока прогресс не 100%.</summary>
    public bool CanContinue => CanStart && Model.ProgressInfo.PercentDone < 100;
    /// <summary>«⟳ С начала» доступна всегда пока рассылка не запущена (и стёрт прогресс = OK).</summary>
    public bool CanRestart => CanStart;
    /// <summary>Рассылка полностью отправлена — Старт нажимать бесполезно.</summary>
    public bool IsFullyCompleted => Model.ProgressInfo.PercentDone >= 100;

    // ── Прогресс ─────────────────────────────────────────────────────
    public int PercentDone => Model.ProgressInfo.PercentDone;
    /// <summary>PercentDone / 100 — для MultiBinding в прогресс-баре.</summary>
    public double PercentFraction => Model.ProgressInfo.PercentDone / 100.0;
    public int Total => Model.ProgressInfo.Total;
    public int Sent => Model.ProgressInfo.Sent;
    public int NotSent => Model.ProgressInfo.NotSent;
    public int Opened => Model.ProgressInfo.Opened;
    public int Clicks => Model.ProgressInfo.Clicks;
    public int Bad => Model.ProgressInfo.Bad;
    public int Refused => Model.ProgressInfo.Refused;
    public int Excluded => Model.ProgressInfo.Excluded;
    public int Good => Model.ProgressInfo.Good;
    public int Undetermined => Model.ProgressInfo.Undetermined;

    public string ProgressText
    {
        get
        {
            var p = Model.ProgressInfo;
            if (Model.Type == "validation")
                return $"{Fmt(p.Good + p.Bad + p.Undetermined)} / {Fmt(p.Total)}";
            return $"{Fmt(p.Sent)} / {Fmt(p.Total)}";
        }
    }
    public string PercentText => $"{PercentDone}%";
    public string SpeedText => string.IsNullOrEmpty(Model.ApproxSpeed) ? "—" : Model.ApproxSpeed;

    /// <summary>Дата последнего запуска в коротком формате.</summary>
    public string LastStartText
    {
        get
        {
            if (string.IsNullOrEmpty(Model.LastStartDate)) return "—";
            return DateTime.TryParse(Model.LastStartDate, out var dt)
                ? dt.ToString("dd.MM.yy HH:mm")
                : Model.LastStartDate;
        }
    }

    /// <summary>Псевдо-ID вроде «AMS-2718» — по образцу макета.</summary>
    public string DisplayId => $"AMS-{Model.Id}";

    /// <summary>Обновлено (относительное время) — «сегодня, 12:41» / «вчера» / dd.MM HH:mm.</summary>
    public string UpdatedText
    {
        get
        {
            if (!DateTime.TryParse(Model.LastStartDate, out var dt)) return "—";
            var delta = DateTime.Now - dt;
            if (delta.TotalHours < 24) return string.Format(Loc.T("ams.row.todayFormat"), dt.ToString("HH:mm"));
            if (delta.TotalDays < 2) return string.Format(Loc.T("ams.row.yesterdayFormat"), dt.ToString("HH:mm"));
            return dt.ToString("dd.MM HH:mm");
        }
    }

    /// <summary>Компактный формат: 12K / 89K, 493K / 1.2M и т.п. — как в макете.</summary>
    public string CompactProgress
    {
        get
        {
            var p = Model.ProgressInfo;
            if (Model.Type == "validation")
                return $"{Compact(p.Good + p.Bad + p.Undetermined)} / {Compact(p.Total)}";
            return $"{Compact(p.Sent)} / {Compact(p.Total)}";
        }
    }

    public string TotalCompact => Compact(Model.ProgressInfo.Total);

    /// <summary>Цветная полоска-«акцент» под названием в списке слева — определяет статус.</summary>
    public Brush AccentBar => Model.State switch
    {
        "working" => Green,
        "stopping" => Amber,
        _ => Model.LastStopByPostmaster ? Red
            : Model.ProgressInfo.PercentDone >= 100 ? Green
            : Model.ProgressInfo.PercentDone > 0 ? Amber
            : Gray,
    };

    /// <summary>Категория для фильтра левой панели.</summary>
    public bool IsRunning => Model.State == "working";
    public bool IsPaused => Model.State == "stopping" || (Model.State == "idle" && Model.ProgressInfo.PercentDone > 0 && Model.ProgressInfo.PercentDone < 100);
    public bool HasError => Model.LastStopByPostmaster;

    // Быстрый доступ для правой панели realtime — API не даёт всех полей,
    // остальное показываем как «—» из макетных карточек.
    public int UnsubscribeCount => Model.ProgressInfo.NotSent;  // приближение, API не даёт «отписки» напрямую

    private static string Fmt(int n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>«1234» → «1K», «123456» → «123K», «1234567» → «1.2M».</summary>
    private static string Compact(int n)
    {
        if (n < 1000) return n.ToString();
        if (n < 1_000_000) return (n / 1000d).ToString(n < 10_000 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + "K";
        return (n / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    public void Update(AmsMailing m)
    {
        Model = m;
        // Дёшево — сразу все свойства (WPF не тормозит на одном "" notify по строке).
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Локально переключить state (после StartMailing/StopMailing до poll'а).</summary>
    public void SetState(string state)
    {
        Model.State = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Пере-notify всех свойств без смены модели — вызывается при смене языка (ru/en),
    /// чтобы TypeText/StateText/UpdatedText (локализуются через Loc.T) переоценились в UI.</summary>
    public void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;
}
