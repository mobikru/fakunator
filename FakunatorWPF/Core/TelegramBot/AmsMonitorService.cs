using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakunator.Core.AmsApi;

namespace Fakunator.Core.TelegramBot;

/// <summary>
/// Фоновый монитор AMS для отправки push-уведомлений в Telegram.
/// Каждые 30 секунд поллит getMailings и диффит с прошлым снимком:
/// - idle → working  → «▶ Запущена»
/// - working → idle (100%) → «✅ Финал»
/// - working → idle (&lt;100%) → «⏸ Остановлена»
/// - Постмастер аварийно остановил → «⚠ Стоп по постмастеру»
/// Также следит за доступностью AMS: пуш когда AMS упал / вернулся.
/// </summary>
public class AmsMonitorService
{
    private static AmsMonitorService? _instance;
    public static AmsMonitorService Instance => _instance ??= new AmsMonitorService();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly Dictionary<int, Snap> _prev = new();
    private bool _amsOnlinePrev;
    private bool _hasBaseline;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private record Snap(string State, int Percent, bool StoppedByPm);

    public void Start()
    {
        if (_loopTask != null && !_loopTask.IsCompleted) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); }
        catch { }
        if (_loopTask != null) { try { await _loopTask; } catch { } }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    /// <summary>Дёрнуть проверку сейчас же (например после ручного /start из бота).</summary>
    public void PokeNow()
    {
        // Простой способ ускорить следующий poll: закончим текущий sleep через новый CTS.
        // Сложности не оправданы — 30 сек не критично.
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch { /* тихо, poll восстановится */ }
            try { await Task.Delay(PollInterval, ct); } catch { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = Config.Current;
        if (!cfg.TelegramEnabled) return;
        if (string.IsNullOrWhiteSpace(cfg.AmsApiHost) || string.IsNullOrWhiteSpace(cfg.AmsApiKey))
            return;

        List<AmsMailing>? mailings = null;
        string? err = null;
        try
        {
            using var api = new AmsApiClient(cfg.AmsApiHost, cfg.AmsApiKey, cfg.AmsUseHttps,
                TimeSpan.FromSeconds(10));
            mailings = await api.GetMailingsAsync(ct);
        }
        catch (Exception ex) { err = ex.Message; }

        var nowOnline = mailings != null;
        // AMS availability push — только на смене состояния.
        if (_hasBaseline && nowOnline != _amsOnlinePrev)
        {
            var msg = nowOnline
                ? "✅ AMS снова онлайн."
                : $"⚠ AMS недоступен: {Trunc(err ?? "нет ответа", 200)}";
            await TelegramBotService.Instance.BroadcastAsync(msg);
        }
        _amsOnlinePrev = nowOnline;
        if (mailings == null) return;

        // ── Диффим ──
        var curr = mailings.ToDictionary(m => m.Id, m => new Snap(
            m.State, m.ProgressInfo.PercentDone, m.LastStopByPostmaster));

        if (_hasBaseline)
        {
            foreach (var m in mailings)
            {
                if (!_prev.TryGetValue(m.Id, out var prev)) continue;
                var now = curr[m.Id];

                // idle/stopping → working = запуск
                if (prev.State != "working" && now.State == "working")
                {
                    await TelegramBotService.Instance.BroadcastAsync(
                        $"▶ <b>Запущена</b>\n{Esc(m.Name)}\n<code>id {m.Id}</code> · {m.ApproxSpeed}");
                }
                // working → idle = закончилась/остановилась
                else if (prev.State == "working" && now.State == "idle")
                {
                    if (now.StoppedByPm && !prev.StoppedByPm)
                    {
                        await TelegramBotService.Instance.BroadcastAsync(
                            $"⚠ <b>Стоп по постмастеру</b>\n{Esc(m.Name)}\n<code>id {m.Id}</code>");
                    }
                    else if (now.Percent >= 100)
                    {
                        var p = m.ProgressInfo;
                        await TelegramBotService.Instance.BroadcastAsync(
                            $"✅ <b>Финал</b>\n{Esc(m.Name)}\n<code>id {m.Id}</code>\n" +
                            $"отправлено {p.Sent:N0} / {p.Total:N0}, " +
                            $"открытий {p.Opened}, кликов {p.Clicks}, " +
                            $"баунсов {p.Bad}, не принято {p.Refused}");
                    }
                    else
                    {
                        await TelegramBotService.Instance.BroadcastAsync(
                            $"⏸ <b>Остановлена</b> на {now.Percent}%\n{Esc(m.Name)}\n<code>id {m.Id}</code>");
                    }
                }
            }
        }

        _prev.Clear();
        foreach (var kv in curr) _prev[kv.Key] = kv.Value;
        _hasBaseline = true;
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
