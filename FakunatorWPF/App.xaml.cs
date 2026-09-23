using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Fakunator.Core;
using Fakunator.Core.TelegramBot;

namespace Fakunator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ДО base.OnStartup — WPF создаёт MainWindow (StartupUri) прямо внутри этого вызова,
        // а его {core:Tr ...} биндинги уже трогают Config.Current через Loc. Миграция должна
        // успеть перенести config.json/output/data в %APPDATA%\Fakunator раньше первого чтения.
        DataMigration.RunIfNeeded();

        base.OnStartup(e);

        if (DataMigration.MigratedExistingInstall)
        {
            MessageBox.Show(
                Loc.T("startup.migration.body"),
                Loc.T("startup.migration.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Глобальные обработчики крашей — записываем stack trace в файл + показываем
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash("Dispatcher", ev.Exception);
            MessageBox.Show(
                $"UI-поток упал:\n{ev.Exception.GetType().Name}: {ev.Exception.Message}\n\n" +
                $"Полный лог: %APPDATA%\\Fakunator\\crash.log",
                "Fakunator crash", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            LogCrash("AppDomain", ev.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("Task", ev.Exception);
            ev.SetObserved();
        };

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

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fakunator");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "crash.log");
            System.IO.File.AppendAllText(path,
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n");
        }
        catch { }
    }
}
