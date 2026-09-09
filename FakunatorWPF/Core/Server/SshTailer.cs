using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Fakunator.Core.Server;

/// <summary>
/// Стримит вывод команды (например tail -F mail.log) по SSH.
/// Каждую новую строку кидает через LineReceived на UI-поток (пользователь маршалит сам).
/// </summary>
public sealed class SshTailer : IDisposable
{
    public event Action<string>? LineReceived;
    public event Action<string>? Error;

    private readonly ServerConfig _srv;
    private readonly string _command;
    private CancellationTokenSource? _cts;
    private SshClient? _ssh;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public SshTailer(ServerConfig srv, string command)
    {
        _srv = srv;
        _command = command;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ssh?.Disconnect(); } catch { }
        try { _ssh?.Dispose(); } catch { }
        _ssh = null;
        _cts = null;
    }

    private void RunLoop(CancellationToken ct)
    {
        try
        {
            var pass = ServerRegistry.DecryptPassword(_srv.SshPasswordEnc);
            _ssh = new SshClient(_srv.Ip, _srv.SshPort, _srv.SshUser, pass);
            _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8);
            _ssh.Connect();

            var cmd = _ssh.CreateCommand(_command);
            var asyncResult = cmd.BeginExecute();
            using var reader = new StreamReader(cmd.OutputStream);

            while (!ct.IsCancellationRequested && !asyncResult.IsCompleted)
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) break;
                    LineReceived?.Invoke(line);
                }
                Thread.Sleep(200);
            }

            try { cmd.EndExecute(asyncResult); } catch { }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { _ssh?.Disconnect(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
