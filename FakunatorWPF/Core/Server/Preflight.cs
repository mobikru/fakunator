using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using DnsClient;
using Renci.SshNet;

namespace Fakunator.Core.Server;

public enum PreflightStatus { Pending, Ok, Warn, Fail }

public sealed class PreflightPackageResult
{
    public string Name { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class PreflightCheck : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _name = "";
    private PreflightStatus _status = PreflightStatus.Pending;
    private string _message = "";
    private List<PreflightPackageResult>? _packages;
    public string Name { get => _name; set { _name = value; Notify(); Notify(nameof(Icon)); Notify(nameof(IconBrush)); Notify(nameof(RowIcon)); } }

    public System.Windows.Media.Geometry RowIcon => Name switch
    {
        "SSH"                      => Fakunator.Core.PhosphorIcons.TerminalWindow,
        "OS"                       => Fakunator.Core.PhosphorIcons.Gear,
        "Диск"                     => Fakunator.Core.PhosphorIcons.HardDrive,
        "RAM"                      => Fakunator.Core.PhosphorIcons.Memory,
        "Порт 25 (исходящий)"      => Fakunator.Core.PhosphorIcons.Envelope,
        "DNS домена"               => Fakunator.Core.PhosphorIcons.Globe,
        "Установленные пакеты"     => Fakunator.Core.PhosphorIcons.Cube,
        "Источники пакетов"        => Fakunator.Core.PhosphorIcons.DownloadSimple,
        _                          => Fakunator.Core.PhosphorIcons.Cube,
    };
    public PreflightStatus Status { get => _status; set { _status = value; Notify(); Notify(nameof(Icon)); Notify(nameof(IconBrush)); Notify(nameof(StatusLabel)); Notify(nameof(StatusBrush)); Notify(nameof(StatusIcon)); } }

    public System.Windows.Media.Geometry StatusIcon => Status switch
    {
        PreflightStatus.Ok   => Fakunator.Core.PhosphorIcons.CheckCircle,
        PreflightStatus.Warn => Fakunator.Core.PhosphorIcons.Warning,
        PreflightStatus.Fail => Fakunator.Core.PhosphorIcons.XCircle,
        _                    => Fakunator.Core.PhosphorIcons.SpinnerGap,
    };
    public string Message { get => _message; set { _message = value; Notify(); } }

    private string? _note;
    public string? Note { get => _note; set { _note = value; Notify(); Notify(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public List<PreflightPackageResult>? Packages { get => _packages; set { _packages = value; Notify(); Notify(nameof(HasPackages)); } }
    public bool HasPackages => Packages is { Count: > 0 };

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Notify(); Notify(nameof(ToggleLabel)); } }
    public string ToggleLabel => IsExpanded ? "Скрыть" : "Подробнее";

    public string StatusLabel => Status switch
    {
        PreflightStatus.Ok   => "Готово",
        PreflightStatus.Warn => "Внимание",
        PreflightStatus.Fail => "Ошибка",
        _                    => "Ожидание",
    };
    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            var hex = Status switch
            {
                PreflightStatus.Ok   => "#0AAD70",
                PreflightStatus.Warn => "#E27B00",
                PreflightStatus.Fail => "#EC224D",
                _                    => "#7B88BA",
            };
            var b = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }

    public string Icon => Status switch
    {
        PreflightStatus.Ok      => "✓",
        PreflightStatus.Warn    => "⚠",
        PreflightStatus.Fail    => "✗",
        _                       => "…",
    };
    public System.Windows.Media.Brush IconBrush
    {
        get
        {
            var hex = Status switch
            {
                PreflightStatus.Ok   => "#22c55e",
                PreflightStatus.Warn => "#f59e0b",
                PreflightStatus.Fail => "#ef4444",
                _                    => "#94a3b8",
            };
            var b = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }

    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}

/// <summary>
/// Реальные предустановочные проверки: SSH, OS, ресурсы, порт 25, DNS, URLs.
/// Каждая проверка обновляется по мере готовности через callback → UI видит прогресс.
/// </summary>
public sealed class Preflight
{
    public event Action<PreflightCheck>? Updated;

    public async Task<List<PreflightCheck>> RunAsync(ServerConfig srv, InstallOptions opts)
    {
        var checks = new Dictionary<string, PreflightCheck>();
        void Emit(string key, PreflightStatus status, string msg, List<PreflightPackageResult>? packages = null, string? note = null)
        {
            if (!checks.TryGetValue(key, out var c))
                checks[key] = c = new PreflightCheck { Name = key };
            c.Status = status;
            c.Message = msg;
            c.Note = note;
            if (packages != null) c.Packages = packages;
            Updated?.Invoke(c);
        }

        // Начинаем все pending
        var keys = new[] { "SSH", "OS", "Диск", "RAM", "Порт 25 (исходящий)", "DNS домена",
                           "Установленные пакеты", "Источники пакетов" };
        foreach (var k in keys) Emit(k, PreflightStatus.Pending, "проверяется…");

        // ── SSH + получаем данные для последующих проверок ──
        SshClient? ssh = null;
        try
        {
            await Task.Run(() =>
            {
                var pass = ServerRegistry.DecryptPassword(srv.SshPasswordEnc);
                ssh = new SshClient(srv.Ip, srv.SshPort, srv.SshUser, pass);
                ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8);
                ssh.Connect();
            });
            Emit("SSH", PreflightStatus.Ok, $"{srv.SshUser}@{srv.Ip}:{srv.SshPort}");
        }
        catch (Exception ex)
        {
            Emit("SSH", PreflightStatus.Fail, $"{ex.GetType().Name}: {ex.Message}");
            Emit("OS", PreflightStatus.Fail, "SSH недоступен");
            Emit("Диск", PreflightStatus.Fail, "SSH недоступен");
            Emit("RAM", PreflightStatus.Fail, "SSH недоступен");
            Emit("Порт 25 (исходящий)", PreflightStatus.Fail, "SSH недоступен");
            Emit("Установленные пакеты", PreflightStatus.Fail, "SSH недоступен");
            await CheckDnsAsync(srv, (k, s, m) => Emit(k, s, m));
            await CheckUrlsAsync(opts, (k, s, m, p) => Emit(k, s, m, p));
            return checks.Values.ToList();
        }

        try
        {
            // OS
            var os = await RunSsh(ssh!, "lsb_release -ds 2>/dev/null || grep PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '\"'");
            if (os.Contains("Ubuntu 22.04"))
                Emit("OS", PreflightStatus.Ok, os.Trim());
            else if (os.Contains("Ubuntu"))
                Emit("OS", PreflightStatus.Warn, $"{os.Trim()} — рекомендуется 22.04 LTS");
            else
                Emit("OS", PreflightStatus.Fail, $"{os.Trim()} — не Ubuntu 22.04");

            // Disk
            var disk = await RunSsh(ssh!, "df -B1G / | tail -1 | awk '{print $4}'");
            if (int.TryParse(disk.Trim(), out var diskFreeGb))
            {
                if (diskFreeGb >= 10)  Emit("Диск", PreflightStatus.Ok, $"свободно {diskFreeGb} GB");
                else                   Emit("Диск", PreflightStatus.Fail, $"свободно {diskFreeGb} GB (нужно ≥10)");
            }
            else Emit("Диск", PreflightStatus.Warn, "не удалось прочитать df");

            // RAM
            var ram = await RunSsh(ssh!, "free -m | awk '/^Mem:/ {print $2}'");
            if (int.TryParse(ram.Trim(), out var ramMb))
            {
                if (ramMb >= 2048)       Emit("RAM", PreflightStatus.Ok, $"{ramMb / 1024.0:F1} GB");
                else if (ramMb >= 1024)  Emit("RAM", PreflightStatus.Warn, $"{ramMb} MB (мало для PMTA)");
                else                     Emit("RAM", PreflightStatus.Fail, $"{ramMb} MB (нужно ≥1024)");
            }
            else Emit("RAM", PreflightStatus.Warn, "не удалось прочитать");

            // Port 25 outgoing (некоторые провайдеры блокируют исходящий 25)
            var port25 = await RunSsh(ssh!, "timeout 6 bash -c '(echo > /dev/tcp/gmail-smtp-in.l.google.com/25) 2>&1'; echo \"exit=$?\"");
            if (port25.Contains("exit=0"))
                Emit("Порт 25 (исходящий)", PreflightStatus.Ok, "может подключиться к gmail-smtp-in:25");
            else
                Emit("Порт 25 (исходящий)", PreflightStatus.Fail, "провайдер блокирует исходящий 25 — почта не будет отправляться");

            // Уже установленные пакеты (warn если что-то стоит — конфиги могут затереться)
            var installed = await RunSsh(ssh!, "dpkg -l postfix dovecot-core mariadb-server apache2 2>/dev/null | grep '^ii' | awk '{print $2}'");
            var installedList = installed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            if (installedList.Count == 0)
                Emit("Установленные пакеты", PreflightStatus.Ok, "чистый сервер, готов к установке");
            else
                Emit("Установленные пакеты", PreflightStatus.Warn, $"уже стоят: {string.Join(", ", installedList)}",
                    note: "При переустановке конфигурации будут перезаписаны");
        }
        catch (Exception ex)
        {
            Emit("OS", PreflightStatus.Warn, $"ошибка команды: {ex.Message}");
        }
        finally { try { ssh?.Disconnect(); ssh?.Dispose(); } catch { } }

        // DNS — параллельно
        await CheckDnsAsync(srv, (k, s, m) => Emit(k, s, m));

        // URL пакетов
        await CheckUrlsAsync(opts, (k, s, m, p) => Emit(k, s, m, p));

        return checks.Values.ToList();
    }

    private async Task CheckDnsAsync(ServerConfig srv, Action<string, PreflightStatus, string> emit)
    {
        if (string.IsNullOrWhiteSpace(srv.Domain) || string.IsNullOrWhiteSpace(srv.Ip))
        {
            emit("DNS домена", PreflightStatus.Warn, "домен или IP не задан");
            return;
        }
        try
        {
            var lookup = new LookupClient();
            var result = await lookup.QueryAsync(srv.Domain, QueryType.A);
            var ips = result.Answers.ARecords().Select(a => a.Address.ToString()).ToList();
            if (ips.Count == 0)
                emit("DNS домена", PreflightStatus.Warn, $"{srv.Domain} → A-запись не найдена (пропиши A→{srv.Ip})");
            else if (ips.Contains(srv.Ip))
                emit("DNS домена", PreflightStatus.Ok, $"{srv.Domain} → {srv.Ip} ✓");
            else
                emit("DNS домена", PreflightStatus.Warn, $"{srv.Domain} → {string.Join(", ", ips)} (ожидался {srv.Ip} — DNS кэш?)");
        }
        catch (Exception ex)
        {
            emit("DNS домена", PreflightStatus.Warn, $"ошибка DNS-запроса: {ex.Message}");
        }
    }

    private async Task CheckUrlsAsync(InstallOptions opts, Action<string, PreflightStatus, string, List<PreflightPackageResult>?> emit)
    {
        var urls = new (string Name, string Url)[]
        {
            ("PMTA .deb",    opts.PmtaDebUrl),
            ("PMTA pmtad",   opts.PmtadUrl),
            ("PMTA pmtahttpd", opts.PmtaHttpdUrl),
            ("PMTA license", opts.PmtaLicenseUrl),
            ("zTDS",         opts.ZtdsUrl),
            ("mailer",       opts.MailerUrl),
            ("landing",      opts.LandingUrl),
        };
        var toCheck = urls.Where(u => !string.IsNullOrWhiteSpace(u.Url)).ToList();
        if (toCheck.Count == 0)
        {
            emit("Источники пакетов", PreflightStatus.Ok, "поля пустые — доп. пакеты пропускаем", null);
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var packages = new List<PreflightPackageResult>();
        var anyFail = false;
        foreach (var (name, url) in toCheck)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    packages.Add(new PreflightPackageResult { Name = name, Result = "Доступен", Ok = true });
                else
                {
                    packages.Add(new PreflightPackageResult { Name = name, Result = $"HTTP {(int)resp.StatusCode}", Ok = false });
                    anyFail = true;
                }
            }
            catch (Exception ex)
            {
                packages.Add(new PreflightPackageResult { Name = name, Result = ex.GetType().Name, Ok = false });
                anyFail = true;
            }
        }
        var summary = anyFail
            ? $"{packages.Count(p => p.Ok)} из {packages.Count} источников доступны"
            : $"все {packages.Count} источника(ов) доступны";
        emit("Источники пакетов", anyFail ? PreflightStatus.Warn : PreflightStatus.Ok, summary, packages);
    }

    private static Task<string> RunSsh(SshClient ssh, string cmd)
    {
        return Task.Run(() =>
        {
            var scmd = ssh.CreateCommand(cmd);
            scmd.CommandTimeout = TimeSpan.FromSeconds(10);
            return scmd.Execute();
        });
    }
}
