using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakunator.Core.AmsApi;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Fakunator.Core.TelegramBot;

/// <summary>
/// Разбирает команды бота. Все AMS-команды сначала проверяют доступность API
/// через <see cref="TryOpenAmsAsync"/> — если ключа нет или API не отвечает,
/// команда отклоняется с человеческим сообщением.
/// </summary>
public class CommandDispatcher
{
    private readonly ITelegramBotClient _bot;

    public CommandDispatcher(ITelegramBotClient bot) { _bot = bot; }

    public async Task HandleAsync(Update update, bool isWhitelisted, CancellationToken ct)
    {
        // ── Inline-кнопки (CallbackQuery) ─────────────────────────
        if (update.CallbackQuery is { } cq)
        {
            await HandleCallbackAsync(cq, ct);
            return;
        }

        var msg = update.Message;
        if (msg?.Text is not { Length: > 0 } text) return;
        var chatId = msg.Chat.Id;

        // Кнопки reply-клавиатуры шлют текст-лейбл → мапим на команду.
        var mapped = MapKeyboardLabel(text);
        if (mapped != null) text = mapped;

        // Нормализуем: "/status@MyBot arg" → head "status", args "arg"
        var firstSpace = text.IndexOf(' ');
        var head = (firstSpace > 0 ? text[..firstSpace] : text).TrimStart('/').ToLowerInvariant();
        var at = head.IndexOf('@');
        if (at > 0) head = head[..at];
        var args = firstSpace > 0 ? text[(firstSpace + 1)..].Trim() : "";

        switch (head)
        {
            // Служебные
            case "start": await OnStart(chatId, isWhitelisted, ct); break;
            case "help": await OnHelp(chatId, ct); break;
            case "whoami": await OnWhoami(chatId, ct); break;
            case "ping": await OnPing(chatId, ct); break;

            // AMS
            case "status": await OnStatus(chatId, ct); break;
            case "active": await OnActive(chatId, ct); break;
            case "list":
            case "ams": await OnList(chatId, ct); break;
            case "info": await OnInfo(chatId, ParseId(args), ct); break;
            case "preview": await OnPreview(chatId, ParseId(args), ct); break;
            case "start_id": await OnActionStart(chatId, ParseId(args), ct); break;
            case "restart": await OnActionRestartConfirm(chatId, ParseId(args), ct); break;
            case "stop": await OnActionStop(chatId, ParseId(args), ct); break;
            case "scheduler_on": await OnSchedulerOn(chatId, ct); break;

            default:
                await Reply(chatId, "🤔 Не знаю такой команды. /help — список.", ct);
                break;
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────

    private async Task OnStart(long chatId, bool isWhitelisted, CancellationToken ct)
    {
        var cfg = Config.Current;
        var text = new StringBuilder();
        text.AppendLine("👋 Привет! Это бот <b>Факунатор</b>.");
        text.Append("Твой chat ID: <code>").Append(chatId).AppendLine("</code>");
        if (isWhitelisted)
        {
            text.AppendLine("✅ Ты в whitelist — команды доступны.");
        }
        else if (cfg.TelegramAllowedChatIds.Count == 0)
        {
            cfg.TelegramAllowedChatIds.Add(chatId);
            cfg.Save();
            text.AppendLine("✅ Ты первый — добавил тебя в whitelist автоматически.");
        }
        else
        {
            text.AppendLine("⛔ Ты не в whitelist. Добавь этот chat ID в");
            text.AppendLine("   Настройки → Telegram → Разрешённые чаты.");
        }
        text.AppendLine("\nЖми на кнопки внизу — ничего набирать не надо.");
        // Отправляем с постоянной reply-клавиатурой.
        await _bot.SendMessage(chatId, text.ToString(),
            parseMode: ParseMode.Html, replyMarkup: BuildMainKeyboard(),
            cancellationToken: ct);
    }

    private async Task OnHelp(long chatId, CancellationToken ct)
    {
        var text = new StringBuilder();
        text.AppendLine("<b>Быстрое управление</b> — кнопки внизу.");
        text.AppendLine();
        text.AppendLine("<b>📮 AMS — рассылки</b>");
        text.AppendLine("/status — сводка + активные");
        text.AppendLine("/active — только идущие сейчас");
        text.AppendLine("/list — топ-20 последних");
        text.AppendLine("/info &lt;id&gt; — детали + кнопки управления");
        text.AppendLine("/preview &lt;id&gt; — превью письма (текст + HTML-файл)");
        text.AppendLine("/start_id &lt;id&gt; — запустить (продолжить)");
        text.AppendLine("/restart &lt;id&gt; — запустить с нуля");
        text.AppendLine("/stop &lt;id&gt; — остановить");
        text.AppendLine("/scheduler_on — запустить планировщик");
        text.AppendLine();
        text.AppendLine("<b>🔔 Автопуши</b> о запуске / финале / остановке приходят сами.");
        text.AppendLine();
        text.AppendLine("<b>Служебные</b>");
        text.AppendLine("/ping — связь с AMS   /whoami — мой ID");
        // Восстановим клавиатуру если юзер её случайно скрыл.
        await _bot.SendMessage(chatId, text.ToString(),
            parseMode: ParseMode.Html, replyMarkup: BuildMainKeyboard(),
            cancellationToken: ct);
    }

    private async Task OnWhoami(long chatId, CancellationToken ct)
    {
        var isIn = Config.Current.TelegramAllowedChatIds.Contains(chatId);
        await Reply(chatId,
            $"Chat ID: <code>{chatId}</code>\nВ whitelist: {(isIn ? "✅ да" : "❌ нет")}", ct);
    }

    private async Task OnPing(long chatId, CancellationToken ct)
    {
        var (api, err) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var running = await api.IsSchedulerRunningAsync(ct);
            sw.Stop();
            await Reply(chatId,
                $"✅ AMS доступен ({sw.ElapsedMilliseconds} мс)\nПланировщик: {(running ? "▶ работает" : "⏸ остановлен")}",
                ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private async Task OnStatus(long chatId, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var running = await api.IsSchedulerRunningAsync(ct);
            var mailings = await api.GetMailingsAsync(ct) ?? new();
            var working = mailings.Where(m => m.State == "working").ToList();
            var text = new StringBuilder();
            text.AppendLine($"<b>📊 AMS</b> — планировщик {(running ? "▶ работает" : "⏸ остановлен")}");
            text.AppendLine($"всего рассылок: <b>{mailings.Count}</b>, идёт: <b>{working.Count}</b>");
            if (working.Count > 0)
            {
                text.AppendLine("\n<b>Активные:</b>");
                foreach (var m in working.Take(10))
                {
                    text.AppendLine($"▶ <code>{m.Id}</code> {Esc(m.Name)}");
                    text.AppendLine($"   {ProgressBar(m.ProgressInfo.PercentDone)} {m.ProgressInfo.PercentDone}% · {m.ApproxSpeed}");
                }
            }
            else text.AppendLine("\n<i>Активных рассылок нет.</i>");
            await Reply(chatId, text.ToString(), ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private async Task OnActive(long chatId, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var mailings = await api.GetMailingsAsync(ct) ?? new();
            var working = mailings.Where(m => m.State == "working").ToList();
            if (working.Count == 0)
            {
                await Reply(chatId, "Активных рассылок нет.", ct);
                return;
            }
            var text = new StringBuilder();
            text.AppendLine($"<b>▶ Активные рассылки ({working.Count})</b>\n");
            foreach (var m in working)
            {
                var p = m.ProgressInfo;
                text.AppendLine($"<code>{m.Id}</code> {Esc(m.Name)}");
                text.AppendLine($"{ProgressBar(p.PercentDone)} <b>{p.PercentDone}%</b>");
                text.AppendLine($"   {p.Sent:N0} / {p.Total:N0} · {m.ApproxSpeed}");
                text.AppendLine();
            }
            await Reply(chatId, text.ToString(), ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private async Task OnList(long chatId, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var mailings = await api.GetMailingsAsync(ct) ?? new();
            // Топ-20: работающие сверху, дальше по дате последнего запуска (свежие сверху)
            var ordered = mailings
                .OrderByDescending(m => m.State == "working")
                .ThenByDescending(m => DateTime.TryParse(m.LastStartDate, out var dt) ? dt : DateTime.MinValue)
                .Take(20)
                .ToList();
            if (ordered.Count == 0)
            {
                await Reply(chatId, "Список рассылок пуст.", ct);
                return;
            }

            // Одно сообщение со списком (без кнопок — их не влезет столько),
            // потом отдельные компактные карточки с кнопками для активных.
            var text = new StringBuilder();
            text.AppendLine($"<b>📮 Топ-20 рассылок</b>");
            foreach (var m in ordered)
            {
                var badge = m.State switch
                {
                    "working" => "▶",
                    "stopping" => "⏸",
                    _ => m.ProgressInfo.PercentDone >= 100 ? "✓" : "•",
                };
                var date = DateTime.TryParse(m.LastStartDate, out var dt)
                    ? dt.ToString("dd.MM HH:mm") : "—";
                text.AppendLine($"{badge} <code>{m.Id}</code> {Esc(m.Name)} — {m.ProgressInfo.PercentDone}% · {date}");
            }
            text.AppendLine("\nЖми /info &lt;id&gt; для деталей и кнопок управления.");
            await Reply(chatId, text.ToString(), ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private async Task OnInfo(long chatId, int id, CancellationToken ct)
    {
        if (id <= 0) { await Reply(chatId, "Использование: /info &lt;id&gt;", ct); return; }
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var m = await api.GetMailingAsync(id, ct);
            if (m == null) { await Reply(chatId, $"Рассылка #{id} не найдена.", ct); return; }
            var p = m.ProgressInfo;
            var s = m.Settings;

            var typeRu = m.Type switch
            {
                "mailing" => "рассылка",
                "transactional" => "транзакц.",
                "validation" => "валидация",
                _ => m.Type,
            };
            var stateRu = m.State switch
            {
                "idle" => "остановлена",
                "working" => "▶ работает",
                "stopping" => "⏸ останавл-я",
                _ => m.State,
            };

            var text = new StringBuilder();
            text.AppendLine($"<b>{Esc(m.Name)}</b>");
            text.AppendLine($"<code>id {m.Id}</code> · {typeRu} · {stateRu}");
            text.AppendLine();
            text.AppendLine($"{ProgressBar(p.PercentDone)} <b>{p.PercentDone}%</b>");
            text.AppendLine();
            if (m.Type == "validation")
            {
                text.AppendLine($"<b>Валидация:</b>");
                text.AppendLine($"   good: {p.Good:N0}");
                text.AppendLine($"   bad: {p.Bad:N0}");
                text.AppendLine($"   не определено: {p.Undetermined:N0}");
                text.AppendLine($"   исключено: {p.Excluded:N0}");
                text.AppendLine($"   всего: {p.Total:N0}");
            }
            else
            {
                text.AppendLine($"<b>Отправка:</b>");
                text.AppendLine($"   отправлено: <b>{p.Sent:N0}</b> / {p.Total:N0}");
                text.AppendLine($"   не отправлено: {p.NotSent:N0}");
                text.AppendLine($"   плохих: {p.Bad:N0}");
                text.AppendLine($"   не принято: {p.Refused:N0}");
                text.AppendLine($"   исключено: {p.Excluded:N0}");
                text.AppendLine();
                text.AppendLine($"<b>Открытий:</b> {p.Opened:N0}   <b>кликов:</b> {p.Clicks:N0}");
            }
            text.AppendLine();
            text.AppendLine($"<b>Скорость:</b> {m.ApproxSpeed}");
            if (DateTime.TryParse(m.LastStartDate, out var dt))
                text.AppendLine($"<b>Последний запуск:</b> {dt:dd.MM.yyyy HH:mm}");
            if (m.LastStopByPostmaster)
                text.AppendLine("⚠ <b>Остановлена постмастером</b>");
            if (s != null)
            {
                text.AppendLine();
                text.AppendLine("<b>Настройки:</b>");
                if (s.SenderAccount != null) text.AppendLine($"   отправитель: {Esc(s.SenderAccount.Name)}");
                if (s.MailList != null) text.AppendLine($"   список: {Esc(s.MailList.Name)}");
                if (s.Message != null) text.AppendLine($"   письмо: {Esc(s.Message.Name)}");
                if (s.DeliveryPreset != null) text.AppendLine($"   профиль: {Esc(s.DeliveryPreset.Name)}");
            }

            // Кнопки: старт/стоп/перезапуск в зависимости от состояния.
            var kb = BuildActionKeyboard(m.State == "idle", m.State != "idle", m.State == "idle");
            await _bot.SendMessage(chatId, text.ToString(),
                parseMode: ParseMode.Html, replyMarkup: kb, cancellationToken: ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }

        InlineKeyboardMarkup BuildActionKeyboard(bool canStart, bool canStop, bool canRestart)
        {
            var row1 = new List<InlineKeyboardButton>();
            if (canStart) row1.Add(InlineKeyboardButton.WithCallbackData("▶ Старт", $"ams:start:{id}"));
            if (canRestart) row1.Add(InlineKeyboardButton.WithCallbackData("⟳ С начала", $"ams:restart:{id}"));
            if (canStop) row1.Add(InlineKeyboardButton.WithCallbackData("⏸ Стоп", $"ams:stop:{id}"));
            var row2 = new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("👁 Превью письма", $"ams:preview:{id}"),
                InlineKeyboardButton.WithCallbackData("↻ Обновить", $"ams:info:{id}"),
            };
            return new InlineKeyboardMarkup(new[] { row1.ToArray(), row2.ToArray() });
        }
    }

    /// <summary>Превью письма привязанного к рассылке.
    /// Показывает decoded subject + plain-text превью + прицепом HTML-файл.</summary>
    private async Task OnPreview(long chatId, int mailingId, CancellationToken ct)
    {
        if (mailingId <= 0) { await Reply(chatId, "Использование: /preview &lt;id рассылки&gt;", ct); return; }
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var mailing = await api.GetMailingAsync(mailingId, ct);
            if (mailing?.Settings?.Message == null)
            {
                await Reply(chatId, $"У рассылки #{mailingId} нет привязанного письма.", ct);
                return;
            }
            var msg = await api.GetMessageAsync(mailing.Settings.Message.Id, ct);
            if (msg == null)
            {
                await Reply(chatId, $"Не удалось загрузить письмо id={mailing.Settings.Message.Id}.", ct);
                return;
            }

            var subject = AmsApiClient.FromBase64(msg.Subject);
            var htmlDecoded = string.IsNullOrEmpty(msg.HtmlPart) ? "" : AmsApiClient.FromBase64(msg.HtmlPart);
            var htmlSize = htmlDecoded.Length;

            // Только шапка + HTML-файл. Код письма в чат не выводим — открывать в браузере.
            if (htmlSize > 0)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(htmlDecoded);
                await using var stream = new System.IO.MemoryStream(bytes);
                var caption = $"<b>👁 Превью письма</b> · рассылка <code>#{mailingId}</code>\n" +
                              $"<b>Название:</b> {Esc(msg.MessageName)}\n" +
                              $"<b>Тема:</b> {Esc(subject)}\n" +
                              $"<b>Тип:</b> {Esc(msg.MessageType)} · {htmlSize:N0} симв.\n" +
                              $"<i>Открой файл в браузере — отрендерится как настоящее письмо.</i>";
                await _bot.SendDocument(chatId,
                    new Telegram.Bot.Types.InputFileStream(stream, $"mailing_{mailingId}.html"),
                    caption: caption, parseMode: ParseMode.Html,
                    cancellationToken: ct);
            }
            else
            {
                await Reply(chatId,
                    $"У письма «{Esc(msg.MessageName)}» пустая HTML-часть.",
                    ct);
            }
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Убираем <style>, <script>, <head> целиком с содержимым — они не текст.
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(html,
            @"<(script|style|head)\b[^<]*(?:(?!</\1>)<[^<]*)*</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        // Убираем HTML-комментарии.
        withoutBlocks = System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"<!--.*?-->", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        // <br> и <p> → переносы.
        withoutBlocks = System.Text.RegularExpressions.Regex.Replace(withoutBlocks,
            @"<(br|/p|/div|/h[1-6])[^>]*>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Остальные теги — убираем.
        var text = System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"<[^>]+>", "");
        // HTML-сущности.
        text = System.Net.WebUtility.HtmlDecode(text);
        // Сжать белые символы.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private async Task OnActionStart(long chatId, int id, CancellationToken ct)
    {
        if (id <= 0) { await Reply(chatId, "Использование: /start_id &lt;id&gt;", ct); return; }
        await DoStart(chatId, id, "continue", ct);
    }

    private async Task OnActionRestartConfirm(long chatId, int id, CancellationToken ct)
    {
        if (id <= 0) { await Reply(chatId, "Использование: /restart &lt;id&gt;", ct); return; }
        var kb = new InlineKeyboardMarkup(new[]
        {
            new [] {
                InlineKeyboardButton.WithCallbackData("✅ Да, стереть прогресс", $"ams:restart_yes:{id}"),
                InlineKeyboardButton.WithCallbackData("❌ Отмена", $"ams:cancel:{id}"),
            }
        });
        await _bot.SendMessage(chatId,
            $"⚠ Запустить рассылку <code>#{id}</code> <b>с нуля</b>?\n\nПрогресс будет стёрт, всем адресатам придёт письмо заново.",
            parseMode: ParseMode.Html, replyMarkup: kb, cancellationToken: ct);
    }

    private async Task OnActionStop(long chatId, int id, CancellationToken ct)
    {
        if (id <= 0) { await Reply(chatId, "Использование: /stop &lt;id&gt;", ct); return; }
        await DoStop(chatId, id, ct);
    }

    private async Task OnSchedulerOn(long chatId, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var ok = await api.RunSchedulerAsync(ct);
            await Reply(chatId, ok ? "▶ Планировщик запущен." : "AMS не подтвердил запуск.", ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    // ── Callback (inline buttons) ─────────────────────────────────────

    private async Task HandleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        var chatId = cq.Message?.Chat.Id ?? 0;
        if (chatId == 0) return;
        var data = cq.Data ?? "";
        try
        {
            // Формат "ams:<action>:<id>"
            var parts = data.Split(':');
            if (parts.Length < 3 || parts[0] != "ams") return;
            var action = parts[1];
            if (!int.TryParse(parts[2], out var id) || id <= 0) return;

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct); // убрать «крутилку»
            switch (action)
            {
                case "start": await DoStart(chatId, id, "continue", ct); break;
                case "restart":
                    await OnActionRestartConfirm(chatId, id, ct); break;
                case "restart_yes": await DoStart(chatId, id, "restart", ct); break;
                case "stop": await DoStop(chatId, id, ct); break;
                case "info": await OnInfo(chatId, id, ct); break;
                case "preview": await OnPreview(chatId, id, ct); break;
                case "cancel": await Reply(chatId, "Отменено.", ct); break;
            }
        }
        catch (Exception ex)
        {
            try { await _bot.AnswerCallbackQuery(cq.Id, "Ошибка: " + ex.Message, cancellationToken: ct); }
            catch { }
        }
    }

    // ── Executors ─────────────────────────────────────────────────────

    private async Task DoStart(long chatId, int id, string startMode, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            // Проверяем планировщик — без него startMailing вернёт OK, но ничего не пойдёт.
            var sched = await api.IsSchedulerRunningAsync(ct);
            if (!sched)
            {
                await api.RunSchedulerAsync(ct);
                await Reply(chatId, "▶ Планировщик запущен автоматически.", ct);
            }
            var ok = await api.StartMailingAsync(id, startMode, ct);
            var verb = startMode == "restart" ? "перезапущена с нуля" : "запущена (continue)";
            await Reply(chatId,
                ok ? $"✅ Рассылка <code>#{id}</code> {verb}."
                   : $"❌ AMS не подтвердил запуск <code>#{id}</code>.", ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    private async Task DoStop(long chatId, int id, CancellationToken ct)
    {
        var (api, _) = await TryOpenAmsAsync(chatId, ct);
        if (api == null) return;
        try
        {
            var ok = await api.StopMailingAsync(id, ct);
            await Reply(chatId, ok ? $"⏸ Рассылка <code>#{id}</code> остановлена." :
                                     $"❌ Не удалось остановить <code>#{id}</code>.", ct);
        }
        catch (Exception ex) { await Reply(chatId, "❌ Ошибка: " + Esc(ex.Message), ct); }
        finally { api.Dispose(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Открывает AmsApiClient если настроен, иначе сама шлёт юзеру
    /// сообщение об ошибке и возвращает null. Все AMS-команды должны через это.</summary>
    private async Task<(AmsApiClient? api, string? err)> TryOpenAmsAsync(long chatId, CancellationToken ct)
    {
        var cfg = Config.Current;
        if (string.IsNullOrWhiteSpace(cfg.AmsApiHost) || string.IsNullOrWhiteSpace(cfg.AmsApiKey))
        {
            await Reply(chatId, "⚠ AMS не настроен.\nОткрой Настройки → AMS Enterprise API в приложении.", ct);
            return (null, "not-configured");
        }
        AmsApiClient? api = null;
        try
        {
            api = new AmsApiClient(cfg.AmsApiHost, cfg.AmsApiKey, cfg.AmsUseHttps,
                TimeSpan.FromSeconds(10));
            // Легкий health-check: isSchedulerRunning — самая быстрая точка.
            await api.IsSchedulerRunningAsync(ct);
            return (api, null);
        }
        catch (Exception ex)
        {
            api?.Dispose();
            await Reply(chatId,
                $"⚠ AMS недоступен: <code>{Esc(ex.Message)}</code>\n\nКоманда заблокирована. Проверь хост / ключ / сеть.",
                ct);
            return (null, ex.Message);
        }
    }

    private static int ParseId(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return 0;
        return int.TryParse(args.Split(' ')[0], out var id) ? id : 0;
    }

    /// <summary>Постоянная клавиатура снизу — чтобы не набирать команды руками.</summary>
    private static ReplyKeyboardMarkup BuildMainKeyboard()
    {
        var kb = new ReplyKeyboardMarkup(new[]
        {
            new [] { new KeyboardButton("📊 Статус"), new KeyboardButton("▶ Активные") },
            new [] { new KeyboardButton("📋 Список"), new KeyboardButton("⚙ Планировщик") },
            new [] { new KeyboardButton("🔗 Пинг"), new KeyboardButton("❓ Помощь") },
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true,
        };
        return kb;
    }

    /// <summary>Кнопки reply-клавиатуры шлют лейбл. Мапим лейбл на реальную команду.</summary>
    private static string? MapKeyboardLabel(string text) => text switch
    {
        "📊 Статус" => "/status",
        "▶ Активные" => "/active",
        "📋 Список" => "/list",
        "⚙ Планировщик" => "/scheduler_on",
        "🔗 Пинг" => "/ping",
        "❓ Помощь" => "/help",
        _ => null,
    };

    /// <summary>ASCII progress-bar 20 клеток: ██████░░░░ 30%.</summary>
    private static string ProgressBar(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var full = percent / 5; // 0..20
        var empty = 20 - full;
        return "<code>" + new string('█', full) + new string('░', empty) + "</code>";
    }

    private async Task Reply(long chatId, string text, CancellationToken ct)
    {
        // Каждый ответ прицепляет reply-клавиатуру внизу — она persistent, но если
        // юзер её случайно скрыл кнопкой ➖, следующий ответ её вернёт.
        try
        {
            await _bot.SendMessage(chatId, text,
                parseMode: ParseMode.Html,
                replyMarkup: BuildMainKeyboard(),
                cancellationToken: ct);
        }
        catch { }
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
