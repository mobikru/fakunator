using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Fakunator.Core.Updater;

/// <summary>
/// Синглтон-сервис проверки/применения split-релизов.
/// - Периодически чекает latest.json с GitHub Releases.
/// - При появлении новой версии стреляет событие <see cref="UpdateAvailable"/>.
/// - <see cref="ApplyAsync"/> качает code.zip (+ опционально deps.zip если изменился),
///   готовит PowerShell-скрипт замены и рестарта, запрашивает UAC, запускает и уходит.
/// - НИКОГДА не трогает config.json / data/ / output/ — эти файлы отсутствуют в архивах.
/// </summary>
public class UpdateService
{
    private const string GITHUB_REPO = "mobikru/fakunator";
    private static string ManifestUrl =>
        $"https://github.com/{GITHUB_REPO}/releases/latest/download/latest.json";

    public static UpdateService Instance { get; } = new();

    public event Action<UpdateInfo>? UpdateAvailable;
    public UpdateInfo? Pending { get; private set; }

    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private bool _started;

    private UpdateService()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fakunator-Updater/1.0");
        _http.Timeout = TimeSpan.FromMinutes(10);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>Запустить периодический чекер. Первый check — через 30 сек после старта.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Start();
        Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(30)); await CheckAsync(); });
    }

    /// <summary>DEBUG-hook: искусственно выставляет Pending — только для проверки UI.
    /// В релизе не вызывается.</summary>
    public void DebugSimulateUpdate(string remoteVersion, string notes, long codeBytes)
    {
        Pending = new UpdateInfo(
            Manifest: new ReleaseManifest { Version = remoteVersion, Notes = notes, CodeSize = codeBytes },
            LocalVersion: typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            NeedsDeps: false,
            InstallDir: "",
            DownloadBytes: codeBytes);
        RepublishPending();
    }

    /// <summary>Повторно шлёт событие UpdateAvailable с текущим Pending — если он есть.
    /// Используется когда UI-элемент (кнопка в настройках) хочет вернуть баннер.</summary>
    public void RepublishPending()
    {
        if (Pending == null) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateAvailable?.Invoke(Pending);
        }));
    }

    /// <summary>Ручной чек (кнопка «Проверить обновления» в настройках).</summary>
    public async Task<bool> CheckAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version)) return false;

            var localVer = typeof(UpdateService).Assembly.GetName().Version;
            var localVerStr = localVer != null
                ? $"{localVer.Major}.{localVer.Minor}.{localVer.Build}"
                : "0.0.0";
            if (!IsNewer(manifest.Version, localVerStr))
            {
                Pending = null;
                return false;
            }

            var installDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                             ?? AppContext.BaseDirectory;
            var depsMarker = Path.Combine(installDir, "deps.version");
            var localDepsVer = File.Exists(depsMarker)
                ? File.ReadAllText(depsMarker).Trim()
                : "";
            var needsDeps = !string.Equals(localDepsVer, manifest.DepsVersion, StringComparison.Ordinal);

            Pending = new UpdateInfo(
                Manifest: manifest,
                LocalVersion: localVerStr,
                NeedsDeps: needsDeps,
                InstallDir: installDir,
                DownloadBytes: manifest.CodeSize + (needsDeps ? manifest.DepsSize : 0));

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateAvailable?.Invoke(Pending);
            }));
            return true;
        }
        catch
        {
            return false; // сеть/GitHub — не критично
        }
    }

    /// <summary>Скачивает нужные архивы, распаковывает в staging, готовит и запускает
    /// PowerShell-скрипт замены + рестарта. Требует UAC (Program Files под админом).
    /// Вызывающий должен закрыть приложение сразу после return.</summary>
    public async Task ApplyAsync(IProgress<(double pct, string status)> progress, CancellationToken ct)
    {
        var upd = Pending ?? throw new InvalidOperationException("Нет доступного обновления");
        var m = upd.Manifest;

        var tempRoot = Path.Combine(Path.GetTempPath(), "FakunatorUpdate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var stage = Path.Combine(tempRoot, "stage");
        Directory.CreateDirectory(stage);

        progress.Report((0, "Скачивание кода приложения…"));
        var codeZip = Path.Combine(tempRoot, "code.zip");
        await DownloadWithProgressAsync(m.CodeUrl, codeZip, m.CodeSize,
            0, upd.NeedsDeps ? 30 : 85, progress, ct);
        await VerifySha256Async(codeZip, m.CodeSha256, ct);
        ExtractZipOverwrite(codeZip, stage);

        if (upd.NeedsDeps)
        {
            progress.Report((30, "Скачивание библиотек…"));
            var depsZip = Path.Combine(tempRoot, "deps.zip");
            await DownloadWithProgressAsync(m.DepsUrl, depsZip, m.DepsSize, 30, 85, progress, ct);
            await VerifySha256Async(depsZip, m.DepsSha256, ct);
            ExtractZipOverwrite(depsZip, stage);
        }

        progress.Report((90, "Подготовка установки…"));

        // Пишем PowerShell-скрипт замены. PowerShell — потому что path может содержать
        // Cyrillic (%TEMP% под "Александр"), cmd.bat с этим не справится корректно.
        var script = Path.Combine(tempRoot, "apply-update.ps1");
        var pid = Process.GetCurrentProcess().Id;
        var newExe = Path.Combine(upd.InstallDir, "Fakunator.exe");
        var depsMarker = Path.Combine(upd.InstallDir, "deps.version");

        var ps = $@"$ErrorActionPreference = 'SilentlyContinue'
# Ждём пока родительский Fakunator (PID {pid}) закроется и отпустит файлы
$parentId = {pid}
$try = 0
while ((Get-Process -Id $parentId -ErrorAction SilentlyContinue) -and $try -lt 30) {{
    Start-Sleep -Milliseconds 500
    $try++
}}
Start-Sleep -Milliseconds 500

# Атомарная замена: копируем всё из staging поверх install-папки.
# config.json, data/, output/ не трогаются — их нет в staging.
Copy-Item -Path '{stage.Replace("'","''")}\*' -Destination '{upd.InstallDir.Replace("'","''")}' -Recurse -Force

# Обновляем маркер deps.version
'{m.DepsVersion}' | Set-Content -Path '{depsMarker.Replace("'","''")}' -Encoding UTF8 -NoNewline

# Рестарт приложения
Start-Process -FilePath '{newExe.Replace("'","''")}' -WorkingDirectory '{upd.InstallDir.Replace("'","''")}'

# Cleanup temp
Start-Sleep -Milliseconds 500
Remove-Item -Path '{tempRoot.Replace("'","''")}' -Recurse -Force
";
        await File.WriteAllTextAsync(script, ps, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

        progress.Report((100, "Запуск обновления…"));

        // Запускаем PowerShell С UAC (нужен для записи в C:\Program Files).
        // Если юзер отменит UAC — Process.Start кинет Win32Exception.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = true,   // требуется для Verb
            Verb = "runas",           // UAC prompt
        };
        Process.Start(psi);
        // Возвращаемся — вызывающий должен вызвать Application.Current.Shutdown().
    }

    // ── helpers ──────────────────────────────────────────────────

    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(remote, out var r) && Version.TryParse(local, out var l))
            return r > l;
        // fallback — просто разные строки
        return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadWithProgressAsync(string url, string dst, long expectedSize,
        int fromPct, int toPct, IProgress<(double, string)> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(dst);
        var buf = new byte[81920];
        long got = 0;
        int read;
        while ((read = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
        {
            await fs.WriteAsync(buf, 0, read, ct);
            got += read;
            if (total > 0)
            {
                var localPct = (double)got / total;
                var mapped = fromPct + (toPct - fromPct) * localPct;
                progress.Report((mapped, $"{FmtMb(got)} / {FmtMb(total)}"));
            }
        }
    }

    private static string FmtMb(long b) => (b / (1024.0 * 1024.0)).ToString("0.0") + " MB";

    private static async Task VerifySha256Async(string path, string expectedHex, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return;
        await using var fs = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
        var got = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(got, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"SHA-256 не совпал для {Path.GetFileName(path)}. Файл повреждён при скачивании.");
    }

    /// <summary>ExtractToDirectory кидает если файл уже есть — обходим ручной распаковкой
    /// с Zip Slip защитой.</summary>
    private static void ExtractZipOverwrite(string zipPath, string targetDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(targetDir);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}

/// <summary>Модель latest.json на GitHub Release.</summary>
public class ReleaseManifest
{
    public string Version      { get; set; } = "";
    public string CodeUrl      { get; set; } = "";
    public long   CodeSize     { get; set; }
    public string CodeSha256   { get; set; } = "";
    public string DepsUrl      { get; set; } = "";
    public long   DepsSize     { get; set; }
    public string DepsSha256   { get; set; } = "";
    public string DepsVersion  { get; set; } = "";
    public string DataSeedUrl  { get; set; } = "";
    public long   DataSeedSize { get; set; }
    public string Notes        { get; set; } = "";
}

/// <summary>Состояние доступного обновления.</summary>
public record UpdateInfo(
    ReleaseManifest Manifest,
    string LocalVersion,
    bool NeedsDeps,
    string InstallDir,
    long DownloadBytes)
{
    public string RemoteVersion => Manifest.Version;
    public string DownloadSizeText => (DownloadBytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
}
