using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Fakunator.Core;
using Fakunator.Core.TelegramBot;

namespace Fakunator;

public partial class App : Application
{
    private static string _currentTheme = "dark";

    public static string CurrentTheme => _currentTheme;

    /// <summary>
    /// Fires after MergedDictionaries swap. Custom-rendered controls
    /// (SparklineChart, ParticleBackground, ToggleSwitch) subscribe so they
    /// can InvalidateVisual and pick up new theme brushes.
    /// </summary>
    public static event EventHandler? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--test")
        {
            DebugTest.Run();
            Shutdown(0);
            return;
        }

        // Auto-update blocklists at startup if enabled in config.
        // Fire-and-forget on a worker thread so window-load isn't blocked.
        if (Config.Current.UpdateBlocklistsOnStart)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    return BlocklistUpdater.UpdateAsync(
                        Blocklists.FindDataDir(), null, CancellationToken.None);
                }
                catch
                {
                    return Task.FromResult<(int, string?)>((0, null));
                }
            });
        }

        // Telegram-бот: старт если включён и токен есть. Ошибки токена
        // (401/etc) не блокируют старт приложения — сервис уйдёт в Error state.
        _ = Task.Run(() => TelegramBotService.Instance.StartAsync());
        // Параллельно — AMS-монитор для push-уведомлений (запуск/финал рассылок).
        // Он сам разбирается есть ли Telegram/AMS-настройки и молчит если нет.
        AmsMonitorService.Instance.Start();
        // Config.Changed → пересобрать сервис (если сменили токен / включили).
        Config.Changed += (_, _) => _ = TelegramBotService.Instance.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Fire-and-forget: не ждём завершения тасков, иначе на UI-потоке возникает
        // deadlock (StopAsync внутри await'ит фоновые Task'и, а UI поток блокирован).
        // Все фоновые сервисы работают на ThreadPool = background threads —
        // процесс завершится корректно, HTTP-запросы порвутся на CTS.Cancel().
        try { _ = TelegramBotService.Instance.StopAsync(); } catch { }
        try { _ = AmsMonitorService.Instance.StopAsync(); } catch { }
        base.OnExit(e);
    }

    public static void SwitchTheme(string theme)
    {
        if (theme == _currentTheme) return;
        _currentTheme = theme;

        var mergedDicts = Current.Resources.MergedDictionaries;

        // Remove current theme dictionary (index 1 — SharedStyles is at 0)
        if (mergedDicts.Count > 1)
            mergedDicts.RemoveAt(1);

        var uri = theme == "light"
            ? new Uri("Themes/LightTheme.xaml", UriKind.Relative)
            : new Uri("Themes/DarkTheme.xaml", UriKind.Relative);

        mergedDicts.Add(new ResourceDictionary { Source = uri });

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
