using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Fakunator.Core.Server;

/// <summary>
/// Открывает внешний SSH-клиент к серверу. Пытается putty (если установлена),
/// иначе — cmd+ssh (Windows OpenSSH). Пароль кладёт в буфер для paste right-click.
/// </summary>
public static class SshLauncher
{
    public static void OpenTerminal(ServerConfig srv)
    {
        var pass = ServerRegistry.DecryptPassword(srv.SshPasswordEnc);

        // Пробуем putty — если найден в PATH или Program Files, у него -pw для авто-логина
        var puttyPath = FindPutty();
        if (puttyPath != null)
        {
            var args = $"-ssh {srv.SshUser}@{srv.Ip} -P {srv.SshPort} -pw \"{pass}\"";
            Process.Start(new ProcessStartInfo(puttyPath, args) { UseShellExecute = true });
            return;
        }

        // Fallback: положим пароль в буфер, запустим ssh в cmd — юзер вставит правой кнопкой
        try { Clipboard.SetText(pass); } catch { }

        var cmdArgs = $"/k ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL {srv.SshUser}@{srv.Ip} -p {srv.SshPort}";
        Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs) { UseShellExecute = true });

        MessageBox.Show(
            $"SSH-сессия открыта в cmd.\n\nПароль скопирован в буфер — вставь правой кнопкой мыши в окно cmd (пароль отображаться не будет).\n\nСервер: {srv.SshUser}@{srv.Ip}:{srv.SshPort}",
            "SSH",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string? FindPutty()
    {
        // PATH
        var pathVal = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVal.Split(';'))
        {
            var candidate = Path.Combine(dir.Trim(), "putty.exe");
            if (File.Exists(candidate)) return candidate;
        }
        // Стандартные локации
        var candidates = new[]
        {
            @"C:\Program Files\PuTTY\putty.exe",
            @"C:\Program Files (x86)\PuTTY\putty.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }
}
