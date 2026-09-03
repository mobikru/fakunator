using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakunator.Core.AmsApi;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fakunator.Core.TelegramBot;

/// <summary>
/// Long-polling Telegram-бот. Живёт в фоне пока приложение открыто:
/// подключается к api.telegram.org, получает обновления, диспатчит на
/// <see cref="CommandDispatcher"/>. Работает БЕЗ открытых портов —
/// клиент сам стучит наружу.
/// </summary>
public class TelegramBotService
{
    private static TelegramBotService? _instance;
    public static TelegramBotService Instance => _instance ??= new TelegramBotService();

    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private CommandDispatcher? _dispatcher;
    private string? _currentToken;

    /// <summary>Событие изменения статуса — для UI-индикатора.</summary>
    public event EventHandler<BotStatus>? StatusChanged;

    public BotStatus Status { get; private set; } = BotStatus.Stopped;
    public string? LastError { get; private set; }
    public string? BotUsername { get; private set; }

    /// <summary>Стартовать бота согласно текущему Config. Idempotent — повторный вызов
    /// перезапускает если токен поменялся.</summary>
    public async Task StartAsync()
    {
        var cfg = Config.Current;
        if (!cfg.TelegramEnabled || string.IsNullOrWhiteSpace(cfg.TelegramBotToken))
        {
            await StopAsync();
            return;
        }
        // Если тот же токен и уже работает — ничего не делаем.
        if (_bot != null && _currentToken == cfg.TelegramBotToken && Status == BotStatus.Running) return;

        await StopAsync();
        _currentToken = cfg.TelegramBotToken.Trim();
        try
        {
            _bot = new TelegramBotClient(_currentToken);
            // Проверим токен — getMe вернёт username или бросит.
            var me = await _bot.GetMe();
            BotUsername = me.Username;

            // Регистрируем список команд — Telegram покажет их в menu-кнопке (слева от input).
            try
            {
                await _bot.SetMyCommands(new[]
                {
                    new Telegram.Bot.Types.BotCommand { Command = "status", Description = "📊 Сводка + активные" },
                    new Telegram.Bot.Types.BotCommand { Command = "active", Description = "▶ Только идущие" },
                    new Telegram.Bot.Types.BotCommand { Command = "list", Description = "📋 Топ-20 последних" },
                    new Telegram.Bot.Types.BotCommand { Command = "info", Description = "🔍 Детали: /info <id>" },
                    new Telegram.Bot.Types.BotCommand { Command = "preview", Description = "👁 Превью письма: /preview <id>" },
                    new Telegram.Bot.Types.BotCommand { Command = "start_id", Description = "▶ Запустить: /start_id <id>" },
                    new Telegram.Bot.Types.BotCommand { Command = "restart", Description = "⟳ С нуля: /restart <id>" },
                    new Telegram.Bot.Types.BotCommand { Command = "stop", Description = "⏸ Остановить: /stop <id>" },
                    new Telegram.Bot.Types.BotCommand { Command = "scheduler_on", Description = "⚙ Запустить планировщик" },
                    new Telegram.Bot.Types.BotCommand { Command = "ping", Description = "🔗 Связь с AMS" },
                    new Telegram.Bot.Types.BotCommand { Command = "help", Description = "❓ Помощь" },
                });
                // Кнопка «Меню» слева от поля ввода — по клику открывает список команд.
                await _bot.SetChatMenuButton(
                    menuButton: new Telegram.Bot.Types.MenuButtonCommands());
            }
            catch { /* не критично если BotFather дурит */ }

            _dispatcher = new CommandDispatcher(_bot);
            _cts = new CancellationTokenSource();
            var opts = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
                DropPendingUpdates = true, // на старте не разгребаем старые накопившиеся
            };
            _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, opts, _cts.Token);

            SetStatus(BotStatus.Running, null);
        }
        catch (Exception ex)
        {
            SetStatus(BotStatus.Error, ex.Message);
            _bot = null;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        finally
        {
            _cts = null;
            _bot = null;
            _dispatcher = null;
            SetStatus(BotStatus.Stopped, null);
        }
        await Task.CompletedTask;
    }

    /// <summary>Отправить сообщение во все whitelisted чаты. Используется для
    /// уведомлений (окончание рассылки, ошибка планировщика и т.п.).</summary>
    public async Task BroadcastAsync(string text)
    {
        var bot = _bot;
        if (bot == null || Status != BotStatus.Running) return;
        var chats = Config.Current.TelegramAllowedChatIds.ToList();
        foreach (var chatId in chats)
        {
            try
            {
                await bot.SendMessage(chatId, text, ParseMode.Html);
            }
            catch { /* один упавший чат не должен блокировать остальные */ }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;
            if (chatId == 0) return;

            // Проверка whitelist — если пустой, разрешаем всем ТОЛЬКО для команд
            // /start и /whoami (для получения chatId). Иначе игнор.
            var allowed = Config.Current.TelegramAllowedChatIds;
            var isWhitelisted = allowed.Contains(chatId);
            var text = update.Message?.Text ?? "";
            var isBootstrap = text.StartsWith("/start") || text.StartsWith("/whoami");

            if (!isWhitelisted && !isBootstrap && allowed.Count > 0)
            {
                await bot.SendMessage(chatId,
                    $"⛔ Этот чат не в whitelist.\nТвой chat ID: <code>{chatId}</code>\n" +
                    "Добавь его в Настройки → Telegram → Разрешённые чаты.",
                    ParseMode.Html, cancellationToken: ct);
                return;
            }

            if (_dispatcher != null)
                await _dispatcher.HandleAsync(update, isWhitelisted, ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        LastError = ex.Message;
        // Не переводим бота в Error state — polling сам восстановится.
        return Task.CompletedTask;
    }

    private void SetStatus(BotStatus s, string? err)
    {
        Status = s;
        LastError = err;
        StatusChanged?.Invoke(this, s);
    }
}

public enum BotStatus { Stopped, Running, Error }
