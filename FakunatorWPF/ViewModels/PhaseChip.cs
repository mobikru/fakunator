using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Fakunator.ViewModels;

public enum PhaseChipState { Pending, Active, Done, Failed }

public sealed class PhaseChip : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _label = "";
    private PhaseChipState _state = PhaseChipState.Pending;
    private string? _detail;
    private TimeSpan? _duration;

    public string Label { get => _label; set { _label = value; Notify(); Notify(nameof(DisplayLabel)); } }

    /// <summary>Реальное сообщение фазы из лога установщика (напр. "Roundcube webmail") — показывается под именем текущего компонента.</summary>
    public string? Detail { get => _detail; set { _detail = value; Notify(); Notify(nameof(StatusLabel)); } }

    /// <summary>Реальное время выполнения фазы (StartedAt → момент завершения), не заглушка.</summary>
    public DateTime? StartedAt { get; set; }

    public TimeSpan? Duration { get => _duration; set { _duration = value; Notify(); Notify(nameof(DurationText)); } }

    public string DurationText => Duration.HasValue
        ? $"{(int)Duration.Value.TotalMinutes:00}:{Duration.Value.Seconds:00}"
        : (State == PhaseChipState.Active ? "…" : "—");

    public PhaseChipState State
    {
        get => _state;
        set
        {
            _state = value;
            Notify(); Notify(nameof(DisplayLabel)); Notify(nameof(Bg)); Notify(nameof(Fg));
            Notify(nameof(IconGlyph)); Notify(nameof(StatusLabel));
            Notify(nameof(RowIconBrush)); Notify(nameof(RowNameBrush)); Notify(nameof(RowStatusBrush)); Notify(nameof(RowBackground));
            Notify(nameof(DurationText));
        }
    }

    public string DisplayLabel => State switch
    {
        PhaseChipState.Done   => "✓ " + Label,
        PhaseChipState.Active => "⟳ " + Label,
        PhaseChipState.Failed => "✗ " + Label,
        _                     => Label,
    };

    public Brush Bg => MakeBrush(State switch
    {
        PhaseChipState.Done   => "#22c55e",
        PhaseChipState.Active => "#3b82f6",
        PhaseChipState.Failed => "#ef4444",
        _                     => "#e5e7eb", // fallback (Bg3 in light theme)
    });

    public Brush Fg => MakeBrush(State == PhaseChipState.Pending ? "#6b7280" : "#ffffff");

    // ── Строчный вид (список пакетов установки, "test design" реф) ──
    public string IconGlyph => State switch
    {
        PhaseChipState.Done   => "✓",
        PhaseChipState.Active => "◐",
        PhaseChipState.Failed => "✕",
        _                     => "○",
    };

    public string StatusLabel => State switch
    {
        PhaseChipState.Done   => "Установлен и настроен",
        PhaseChipState.Active => string.IsNullOrEmpty(Detail) ? "Установка…" : Detail!,
        PhaseChipState.Failed => "Ошибка",
        _                     => "В очереди",
    };

    public Brush RowIconBrush => MakeBrush(State switch
    {
        PhaseChipState.Done   => "#0AAD70",
        PhaseChipState.Active => "#4E68FF",
        PhaseChipState.Failed => "#D04455",
        _                     => "#8B97B3",
    });

    public Brush RowNameBrush => MakeBrush(State switch
    {
        PhaseChipState.Active => "#4E68FF",
        PhaseChipState.Failed => "#D04455",
        _                     => "#131C3D",
    });

    public Brush RowStatusBrush => MakeBrush(State == PhaseChipState.Failed ? "#D04455" : "#7582A3");

    public Brush RowBackground => MakeBrush(State switch
    {
        PhaseChipState.Active => "#EFF2FF",
        PhaseChipState.Failed => "#FFF0F0",
        _                     => "#00FFFFFF",
    });

    private static readonly System.Collections.Generic.Dictionary<string, Brush> _cache = new();
    private static Brush MakeBrush(string hex)
    {
        if (_cache.TryGetValue(hex, out var b)) return b;
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        if (brush.CanFreeze) brush.Freeze();
        _cache[hex] = brush;
        return brush;
    }

    private void Notify([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
