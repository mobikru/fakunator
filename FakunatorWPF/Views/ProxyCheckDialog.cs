using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;

namespace Fakunator.Views;

public class ProxyCheckDialog : Window
{
    private readonly string[] _proxyLines;
    private readonly StackPanel _resultPanel;
    private readonly TextBlock _statusText;
    private readonly Button _closeBtn;
    private readonly ProgressBar _progressBar;
    private CancellationTokenSource? _cts;

    private int _aliveCount;
    private int _deadCount;
    public int AliveCount => _aliveCount;
    public int DeadCount => _deadCount;
    public List<string> AliveProxies { get; } = new();

    public ProxyCheckDialog(string[] proxyLines)
    {
        _proxyLines = proxyLines;

        Title = "Проверка прокси";
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg1Brush");
        Foreground = (Brush)Application.Current.FindResource("Fg1Brush");
        ResizeMode = ResizeMode.NoResize;

        var root = new DockPanel { Margin = new Thickness(20) };

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);

        var title = new TextBlock
        {
            Text = $"Проверяю {proxyLines.Length} прокси...",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Fg1Brush"),
        };
        header.Children.Add(title);

        _progressBar = new ProgressBar
        {
            Maximum = proxyLines.Length, Value = 0, Height = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Background = (Brush)Application.Current.FindResource("Bg3Brush"),
        };
        header.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Text = "0 / 0", FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("Fg3Brush"),
        };
        header.Children.Add(_statusText);
        root.Children.Add(header);

        // Bottom close button
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(bottom, Dock.Bottom);

        var useAliveBtn = new Button
        {
            Content = "✓  Вставить рабочие",
            Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        useAliveBtn.Click += (_, _) => { DialogResult = true; Close(); };
        bottom.Children.Add(useAliveBtn);

        _closeBtn = new Button
        {
            Content = "Закрыть", Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)Application.Current.FindResource("GhostBtn"),
        };
        _closeBtn.Click += (_, _) => Close();
        bottom.Children.Add(_closeBtn);
        root.Children.Add(bottom);

        // Scrollable results
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _resultPanel = new StackPanel();
        scroll.Content = _resultPanel;
        root.Children.Add(scroll);

        Content = root;

        Loaded += async (_, _) => await RunChecks();
        Closing += (_, _) => _cts?.Cancel();
    }

    private async Task RunChecks()
    {
        _cts = new CancellationTokenSource();
        int checked_ = 0;

        var semaphore = new SemaphoreSlim(10);

        async Task CheckOne(string line)
        {
            await semaphore.WaitAsync(_cts!.Token);
            try
            {
                var proxy = ProxySpec.TryParse(line);
                string status;
                string pingMs;
                bool alive;

                if (proxy == null)
                {
                    status = "неверный формат";
                    pingMs = "—";
                    alive = false;
                }
                else
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        cts2.CancelAfter(10000);

                        using var tcp = new TcpClient();
                        await tcp.ConnectAsync(proxy.Host, proxy.Port, cts2.Token);

                        var stream = tcp.GetStream();
                        stream.ReadTimeout = 10000;
                        stream.WriteTimeout = 10000;
                        var buf = new byte[512];

                        // 1. SOCKS5 greeting
                        byte[] hello = proxy.User != null
                            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                            : new byte[] { 0x05, 0x01, 0x00 };
                        await stream.WriteAsync(hello, cts2.Token);
                        int n = await stream.ReadAsync(buf.AsMemory(0, 2), cts2.Token);
                        if (n < 2 || buf[0] != 0x05)
                        { sw.Stop(); status = "не SOCKS5"; alive = false; pingMs = $"{sw.ElapsedMilliseconds}ms"; goto done; }

                        // 2. Auth if needed
                        if (buf[1] == 0x02 && proxy.User != null)
                        {
                            var user = System.Text.Encoding.ASCII.GetBytes(proxy.User);
                            var pass = System.Text.Encoding.ASCII.GetBytes(proxy.Pass ?? "");
                            var auth = new byte[3 + user.Length + pass.Length];
                            auth[0] = 0x01;
                            auth[1] = (byte)user.Length;
                            user.CopyTo(auth, 2);
                            auth[2 + user.Length] = (byte)pass.Length;
                            pass.CopyTo(auth, 3 + user.Length);
                            await stream.WriteAsync(auth, cts2.Token);
                            n = await stream.ReadAsync(buf.AsMemory(0, 2), cts2.Token);
                            if (n < 2 || buf[1] != 0x00)
                            { sw.Stop(); status = "auth rejected"; alive = false; pingMs = $"{sw.ElapsedMilliseconds}ms"; goto done; }
                        }

                        // 3. CONNECT to gmail SMTP (port 25) — real SMTP check
                        var target = System.Text.Encoding.ASCII.GetBytes("gmail-smtp-in.l.google.com");
                        var conn = new byte[7 + target.Length];
                        conn[0] = 0x05; // ver
                        conn[1] = 0x01; // connect
                        conn[2] = 0x00; // rsv
                        conn[3] = 0x03; // domain
                        conn[4] = (byte)target.Length;
                        target.CopyTo(conn, 5);
                        conn[5 + target.Length] = 0x00; // port hi (25 = 0x0019)
                        conn[6 + target.Length] = 0x19; // port lo
                        await stream.WriteAsync(conn, cts2.Token);
                        n = await stream.ReadAsync(buf.AsMemory(0, 10), cts2.Token);

                        if (n < 2 || buf[0] != 0x05 || buf[1] != 0x00)
                        {
                            sw.Stop();
                            if (n >= 2 && buf[0] == 0x05)
                                status = $"порт 25 заблокирован (0x{buf[1]:X2})";
                            else
                                status = "CONNECT failed";
                            alive = false;
                            pingMs = $"{sw.ElapsedMilliseconds}ms";
                            goto done;
                        }

                        // 4. Read SMTP banner (220) to confirm mail server responds
                        n = await stream.ReadAsync(buf.AsMemory(0, 256), cts2.Token);
                        sw.Stop();
                        var banner = System.Text.Encoding.ASCII.GetString(buf, 0, Math.Min(n, 60));

                        if (banner.StartsWith("220"))
                        { status = "OK · SMTP:25 открыт"; alive = true; }
                        else
                        { status = $"SMTP banner: {banner.Trim()}"; alive = false; }
                    }
                    catch (OperationCanceledException)
                    { sw.Stop(); status = "таймаут (10с)"; alive = false; }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        status = ex.InnerException?.Message ?? ex.Message;
                        if (status.Length > 60) status = status[..60] + "…";
                        alive = false;
                    }
                    done:
                    pingMs = $"{sw.ElapsedMilliseconds}ms";
                }

                if (alive)
                {
                    Interlocked.Increment(ref _aliveCount);
                    lock (AliveProxies) AliveProxies.Add(line);
                }
                else Interlocked.Increment(ref _deadCount);
                var total = Interlocked.Increment(ref checked_);

                Dispatcher.Invoke(() =>
                {
                    AddResultRow(line, alive, status, pingMs);
                    _progressBar.Value = total;
                    _statusText.Text = $"✓ {_aliveCount} живых · ✗ {_deadCount} мёртвых  ({total}/{_proxyLines.Length})";
                });
            }
            finally { semaphore.Release(); }
        }

        try { await Task.WhenAll(_proxyLines.Select(CheckOne)); }
        catch (OperationCanceledException) { }

        Dispatcher.Invoke(() =>
        {
            _statusText.Text = $"Готово: ✓ {AliveCount} живых · ✗ {DeadCount} мёртвых";
            _statusText.Foreground = (Brush)Application.Current.FindResource(
                AliveCount > 0 ? "CleanBrush" : "DangerBrush");
        });
    }

    private void AddResultRow(string proxyLine, bool alive, string status, string ping)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        // Status icon
        var icon = new TextBlock
        {
            Text = alive ? "✓" : "✗",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Width = 20,
            Foreground = alive
                ? (Brush)Application.Current.FindResource("CleanBrush")
                : (Brush)Application.Current.FindResource("DangerBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);

        // Ping (right)
        var pingText = new TextBlock
        {
            Text = ping, FontSize = 11,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            Foreground = (Brush)Application.Current.FindResource("Fg4Brush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Width = 50, TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(pingText, Dock.Right);
        row.Children.Add(pingText);

        // Status text (right)
        var statusText = new TextBlock
        {
            Text = status, FontSize = 11,
            Foreground = alive
                ? (Brush)Application.Current.FindResource("CleanBrush")
                : (Brush)Application.Current.FindResource("Fg3Brush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(statusText, Dock.Right);
        row.Children.Add(statusText);

        // Proxy address
        var addr = new TextBlock
        {
            Text = proxyLine.Length > 40 ? proxyLine[..40] + "…" : proxyLine,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            Foreground = (Brush)Application.Current.FindResource("Fg1Brush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(addr);

        _resultPanel.Children.Add(row);

        // Auto-scroll
        if (_resultPanel.Parent is ScrollViewer sv)
            sv.ScrollToEnd();
    }
}
