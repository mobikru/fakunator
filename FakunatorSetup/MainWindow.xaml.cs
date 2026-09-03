using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace FakunatorSetup;

public partial class MainWindow : Window
{
    // ▸▸▸ ЗАМЕНИ на свой репо: user/repo ▸▸▸
    private const string GITHUB_REPO = "yourname/fakunator";

    // Стабильный URL манифеста — GitHub "latest" редиректит на самый свежий релиз.
    private static string ManifestUrl =>
        $"https://github.com/{GITHUB_REPO}/releases/latest/download/latest.json";

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private ReleaseManifest? _manifest;
    private enum InstallStage { Ready, Installing, Done, Error }
    private InstallStage _stage = InstallStage.Ready;
    private string _defaultPath;

    public MainWindow()
    {
        InitializeComponent();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FakunatorSetup/1.0");
        _http.Timeout = TimeSpan.FromMinutes(5);

        _defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Fakunator");
        TxtInstallPath.Text = _defaultPath;

        Loaded += async (_, _) =>
        {
            if (App.Uninstall) { await StartUninstallAsync(); return; }
            await FetchManifestAsync();
        };
    }

    // ── Проверка обновлений (стартовый экран) ────────────────────
    private async Task FetchManifestAsync()
    {
        TxtVersionLine.Text = "Проверка обновлений…";
        BtnPrimary.IsEnabled = false;
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl);
            _manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (_manifest == null || string.IsNullOrWhiteSpace(_manifest.ExeUrl))
                throw new InvalidOperationException("Пустой манифест");

            var sizeMb = (_manifest.ExeSize + _manifest.DataSize) / (1024.0 * 1024.0);
            TxtVersionLine.Text = $"Готов установить  ·  Fakunator {_manifest.Version}";
            TxtDescription.Text = string.IsNullOrWhiteSpace(_manifest.Notes)
                ? $"Установщик скачает и настроит последнюю версию (~{sizeMb:0.#} MB)."
                : _manifest.Notes;
            TxtFooter.Text = $"v{_manifest.Version}  ·  GitHub Releases";
            BtnPrimary.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtVersionLine.Text = "Не удалось проверить обновления";
            TxtDescription.Text = $"Проверь интернет-соединение и повтори. Ошибка: {ex.Message}";
            BtnPrimary.Content = "Повторить";
            BtnPrimary.IsEnabled = true;
        }
    }

    // ── Главная кнопка (Установить / Повторить / Запустить) ──────
    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_stage == InstallStage.Done) { LaunchInstalledApp(); return; }
        if (_manifest == null) { await FetchManifestAsync(); return; }

        // Валидация папки
        var dir = TxtInstallPath.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir))
        {
            MessageBox.Show(this, "Укажи полный путь установки.", "Fakunator Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        SwitchStage(InstallStage.Installing);
        BtnPrimary.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        TxtSubtitle.Text = "Установка…";

        try
        {
            await DoInstallAsync(dir, _cts.Token);
            RegisterInAddRemovePrograms(dir);
            if (ChkDesktop.IsChecked  == true) CreateShortcut(dir, desktop: true);
            if (ChkStartMenu.IsChecked == true) CreateShortcut(dir, desktop: false);

            TxtDoneTitle.Text = $"Fakunator {_manifest.Version} установлен";
            TxtDoneSub.Text = $"Установлено в {dir}";
            TxtSubtitle.Text = "Готово";
            SwitchStage(InstallStage.Done);
            BtnPrimary.Content = "Запустить";
            BtnPrimary.IsEnabled = true;
            BtnCancel.Content = "Закрыть";
        }
        catch (OperationCanceledException)
        {
            ShowError("Установка отменена пользователем.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    // ── Основной сценарий инсталла ───────────────────────────────
    private async Task DoInstallAsync(string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var tmpDir = Path.Combine(Path.GetTempPath(), "FakunatorSetup_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        // 1. Fakunator.exe
        var exeTmp = Path.Combine(tmpDir, "Fakunator.exe");
        SetStatus("Скачивание Fakunator.exe…");
        await DownloadWithProgressAsync(_manifest!.ExeUrl, exeTmp, _manifest.ExeSize, 0, 70, ct);

        // 2. data.zip (blocklists + БД)
        var dataTmp = Path.Combine(tmpDir, "data.zip");
        SetStatus("Скачивание data.zip…");
        await DownloadWithProgressAsync(_manifest.DataUrl, dataTmp, _manifest.DataSize, 70, 95, ct);

        // 3. Убить работающий Fakunator если запущен
        SetStatus("Установка файлов…");
        TryKillRunning();

        // 4. Копируем exe в targetDir
        var exeDst = Path.Combine(targetDir, "Fakunator.exe");
        File.Copy(exeTmp, exeDst, overwrite: true);

        // 5. Распаковка data.zip → targetDir\data
        var dataDst = Path.Combine(targetDir, "data");
        if (Directory.Exists(dataDst)) Directory.Delete(dataDst, recursive: true);
        ZipFile.ExtractToDirectory(dataTmp, targetDir);

        // 6. Кладём копию setup рядом — как uninstaller
        var selfPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath))
        {
            var uninstallExe = Path.Combine(targetDir, "unins.exe");
            try { File.Copy(selfPath, uninstallExe, overwrite: true); } catch { }
        }

        SetProgress(100, "Завершение…");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        await Task.Delay(300, ct);
    }

    // ── Скачивание с прогрессом (маппится в диапазон fromPct-toPct) ─
    private async Task DownloadWithProgressAsync(string url, string dst, long expectedSize,
        int fromPct, int toPct, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dstFs = File.Create(dst);
        var buf = new byte[81920];
        long got = 0;
        int read;
        while ((read = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
        {
            await dstFs.WriteAsync(buf, 0, read, ct);
            got += read;
            if (total > 0)
            {
                var localPct = (double)got / total;
                var mapped = fromPct + (toPct - fromPct) * localPct;
                SetProgress(mapped, $"{FmtMb(got)} / {FmtMb(total)}");
            }
        }
    }

    private static string FmtMb(long b) => (b / (1024.0 * 1024.0)).ToString("0.0") + " MB";

    // ── UI переключалки ──────────────────────────────────────────
    private void SwitchStage(InstallStage s)
    {
        _stage = s;
        PanelReady.Visibility      = s == InstallStage.Ready      ? Visibility.Visible : Visibility.Collapsed;
        PanelInstalling.Visibility = s == InstallStage.Installing ? Visibility.Visible : Visibility.Collapsed;
        PanelDone.Visibility       = s == InstallStage.Done       ? Visibility.Visible : Visibility.Collapsed;
        PanelError.Visibility      = s == InstallStage.Error      ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text) => Dispatcher.Invoke(() => TxtStatus.Text = text);
    private void SetProgress(double pct, string bytesLine) => Dispatcher.Invoke(() =>
    {
        Progress.Value = pct;
        TxtProgressPct.Text = $"{pct:0} %";
        TxtBytes.Text = bytesLine;
    });

    private void ShowError(string text)
    {
        TxtError.Text = text;
        TxtSubtitle.Text = "Ошибка";
        SwitchStage(InstallStage.Error);
        BtnPrimary.Content = "Повторить";
        BtnPrimary.IsEnabled = true;
        BtnCancel.Content = "Закрыть";
    }

    // ── Кнопка Обзор ─────────────────────────────────────────────
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Выбери папку для установки",
            InitialDirectory = TxtInstallPath.Text
        };
        if (dlg.ShowDialog() == true)
            TxtInstallPath.Text = Path.Combine(dlg.FolderName, "Fakunator");
    }

    // ── Закрыть / отмена ─────────────────────────────────────────
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void OnDragTop(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    // ── Запустить установленный Fakunator ────────────────────────
    private void LaunchInstalledApp()
    {
        try
        {
            var exe = Path.Combine(TxtInstallPath.Text, "Fakunator.exe");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = TxtInstallPath.Text });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось запустить: " + ex.Message, "Fakunator",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }

    // ── Убить запущенный процесс если есть ───────────────────────
    private static void TryKillRunning()
    {
        foreach (var p in Process.GetProcessesByName("Fakunator"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    // ── Регистрация в «Установка/удаление программ» ──────────────
    private void RegisterInAddRemovePrograms(string targetDir)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fakunator";
        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        if (key == null) return;
        var exePath = Path.Combine(targetDir, "Fakunator.exe");
        var uninsPath = Path.Combine(targetDir, "unins.exe");
        key.SetValue("DisplayName", "Факунатор");
        key.SetValue("DisplayVersion", _manifest?.Version ?? "");
        key.SetValue("Publisher", "batalov");
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("InstallLocation", targetDir);
        key.SetValue("UninstallString", $"\"{uninsPath}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://t.me/batalov");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            var size = new DirectoryInfo(targetDir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length) / 1024;
            key.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
        }
        catch { }
    }

    // ── Ярлыки ───────────────────────────────────────────────────
    private void CreateShortcut(string targetDir, bool desktop)
    {
        var folder = desktop
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var lnkPath = Path.Combine(folder, "Факунатор.lnk");
        var exePath = Path.Combine(targetDir, "Fakunator.exe");
        try
        {
            // Используем WScript.Shell через COM — самый простой способ без сторонних либ
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = exePath;
            lnk.WorkingDirectory = targetDir;
            lnk.IconLocation = exePath + ",0";
            lnk.Description = "Факунатор — email marketing suite";
            lnk.Save();
        }
        catch { /* ярлык не создан — не критично */ }
    }

    // ── Удаление ─────────────────────────────────────────────────
    private async Task StartUninstallAsync()
    {
        TxtVersionLine.Text = "Удаление Факунатора…";
        TxtDescription.Text = "Файлы, ярлыки и запись в «Программах и компонентах» будут удалены.";
        BtnPrimary.Content = "Удалить";
        BtnPrimary.IsEnabled = true;
        BtnPrimary.Click -= OnPrimaryClick;
        BtnPrimary.Click += async (_, _) =>
        {
            BtnPrimary.IsEnabled = false;
            SwitchStage(InstallStage.Installing);
            TxtStatus.Text = "Удаление…";
            SetProgress(30, "");
            try
            {
                var dir = TxtInstallPath.Text.Trim();
                TryKillRunning();
                await Task.Delay(500);
                // Ярлыки
                foreach (var f in new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Факунатор.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Факунатор.lnk"),
                }) try { if (File.Exists(f)) File.Delete(f); } catch { }
                SetProgress(60, "");
                // Registry
                try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fakunator", throwOnMissingSubKey: false); } catch { }
                SetProgress(80, "");
                // Файлы — по возможности целиком
                if (Directory.Exists(dir))
                {
                    // Оставляем unins.exe удалиться самому через cmd
                    var bat = Path.Combine(Path.GetTempPath(), "fak_unins.bat");
                    File.WriteAllText(bat,
                        "@echo off\r\nping -n 2 127.0.0.1 >nul\r\n" +
                        $"rmdir /S /Q \"{dir}\"\r\n" +
                        "del \"%~f0\"\r\n");
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false });
                }
                SetProgress(100, "");
                TxtDoneTitle.Text = "Факунатор удалён";
                TxtDoneSub.Text = "Спасибо, что попробовал.";
                SwitchStage(InstallStage.Done);
                BtnPrimary.Visibility = Visibility.Collapsed;
                BtnCancel.Content = "Закрыть";
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
    }

    // ── Манифест ─────────────────────────────────────────────────
    private class ReleaseManifest
    {
        public string Version { get; set; } = "";
        public string ExeUrl  { get; set; } = "";
        public long   ExeSize { get; set; }
        public string ExeSha256 { get; set; } = "";
        public string DataUrl { get; set; } = "";
        public long   DataSize { get; set; }
        public string DataSha256 { get; set; } = "";
        public string Notes { get; set; } = "";
    }
}
