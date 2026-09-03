using System.Threading.Tasks;

namespace Fakunator.Core.TelegramBot;

/// <summary>
/// Тонкий помощник для отправки пушей из ViewModel'ей.
/// Fire-and-forget, тихо игнорирует ошибки — не хотим блокировать UI-логику
/// проблемами с интернетом или неверным токеном.
/// </summary>
public static class NotificationHub
{
    public static void Notify(string text)
    {
        if (!Config.Current.TelegramEnabled) return;
        _ = SafeBroadcastAsync(text);
    }

    private static async Task SafeBroadcastAsync(string text)
    {
        try { await TelegramBotService.Instance.BroadcastAsync(text); }
        catch { }
    }
}
