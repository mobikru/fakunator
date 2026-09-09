using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Fakunator.Core.Server;

/// <summary>
/// Опрос здоровья сервера по SSH: uptime, load, disk, RAM, service status.
/// Возвращает снапшот, UI сам решает как рисовать (цвет status-dot, метрики).
/// </summary>
public sealed class ServerMonitor
{
    public Task<ServerHealth?> CheckAsync(ServerConfig srv, TimeSpan? sshTimeout = null)
    {
        // Всё в фоне: SshClient.Connect() синхронный и блокирует UI-поток если вызвать напрямую.
        return Task.Run<ServerHealth?>(() =>
        {
            var pass = ServerRegistry.DecryptPassword(srv.SshPasswordEnc);
            try
            {
                using var ssh = new SshClient(srv.Ip, srv.SshPort, srv.SshUser, pass);
                ssh.ConnectionInfo.Timeout = sshTimeout ?? TimeSpan.FromSeconds(6);
                ssh.Connect();

                var health = new ServerHealth { CheckedAt = DateTime.UtcNow, IsReachable = true };

                var upt = Run(ssh, "uptime -p; uptime | awk -F 'load average:' '{print $2}'");
                var uptLines = upt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                health.Uptime = uptLines.FirstOrDefault()?.Trim() ?? "";
                health.LoadAvg = uptLines.Skip(1).FirstOrDefault()?.Trim().Split(',').FirstOrDefault()?.Trim() ?? "";

                var mem = Run(ssh, "free -m | awk '/^Mem:/ {print $2\" \"$3}'");
                var memParts = mem.Trim().Split(' ');
                if (memParts.Length == 2 && int.TryParse(memParts[0], out var memTot) && int.TryParse(memParts[1], out var memUsed))
                {
                    health.RamTotalMb = memTot;
                    health.RamUsedMb = memUsed;
                }

                var disk = Run(ssh, "df -B1M / | tail -1 | awk '{print $2\" \"$3\" \"$5}'");
                var dParts = disk.Trim().Split(' ');
                if (dParts.Length == 3
                    && int.TryParse(dParts[0], out var dTot)
                    && int.TryParse(dParts[1], out var dUsed))
                {
                    health.DiskTotalMb = dTot;
                    health.DiskUsedMb = dUsed;
                }

                var services = new[] { "postfix", "dovecot", "mariadb", "bind9", "opendkim", "apache2", "pmta", "3proxy" };
                var svcQuery = string.Join(";", services.Select(s => $"systemctl is-active {s} 2>/dev/null || echo inactive"));
                var svcOut = Run(ssh, svcQuery);
                var svcLines = svcOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < services.Length && i < svcLines.Length; i++)
                    health.Services[services[i]] = svcLines[i].Trim();

                health.OsRelease = Run(ssh, "lsb_release -ds 2>/dev/null || cat /etc/os-release | head -1").Trim();

                ssh.Disconnect();
                return health;
            }
            catch (Exception ex)
            {
                return new ServerHealth
                {
                    CheckedAt = DateTime.UtcNow,
                    IsReachable = false,
                    ErrorMessage = ex.GetType().Name + ": " + ex.Message
                };
            }
        });
    }

    private static string Run(SshClient ssh, string cmd)
    {
        var scmd = ssh.CreateCommand(cmd);
        scmd.CommandTimeout = TimeSpan.FromSeconds(6);
        return scmd.Execute();
    }
}

public sealed class ServerHealth
{
    public DateTime CheckedAt { get; set; }
    public bool IsReachable { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string OsRelease { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string LoadAvg { get; set; } = "";
    public int RamTotalMb { get; set; }
    public int RamUsedMb { get; set; }
    public int DiskTotalMb { get; set; }
    public int DiskUsedMb { get; set; }
    public Dictionary<string, string> Services { get; } = new(); // name → "active"/"inactive"/"failed"

    public int DiskPercent  => DiskTotalMb > 0 ? RamPercentCalc(DiskUsedMb, DiskTotalMb) : 0;
    public int RamPercent   => RamTotalMb > 0 ? RamPercentCalc(RamUsedMb, RamTotalMb) : 0;

    private static int RamPercentCalc(int used, int total) => (int)(100.0 * used / total);
}
