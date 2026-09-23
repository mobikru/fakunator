using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using Fakunator.Core;
using Fakunator.Core.DomainsManager;
using Microsoft.Web.WebView2.Core;

namespace Fakunator.Views;

/// <summary>
/// Модалка подключения FirstVDS через WebView2. Юзер логинится вручную (капча),
/// программа сама fetch'ит список серверов, следует по gotoserver.additionalpanel
/// и извлекает sessionId + host панели ISP. Результат — <see cref="Result"/>.
/// </summary>
public partial class ConnectFirstVdsDialog : Window
{
    /// <summary>Готовый аккаунт для добавления в Config.IspAccounts. Null если юзер отменил.</summary>
    public IspAccount? Result { get; private set; }

    private bool _servicesFetched;
    private bool _panelOpened;
    private string? _pickedServerId;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;

    public ConnectFirstVdsDialog()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        Loaded += async (_, _) =>
        {
            try
            {
                // Отдельный user-data-folder — чтобы кэш кук FirstVDS не смешивался с системным Edge
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Path.GetTempPath(), "fakunator-webview-firstvds"));
                await Web.EnsureCoreWebView2Async(env);
                Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                Web.CoreWebView2.Navigate("https://my.firstvds.ru/billmgr");
                // Опрос DOM: как только на странице найдётся span.isp-cell-content__data
                // с числовым содержимым (это ID сервера в таблице «Виртуальные серверы»)
                // — автоматически запускаем переход на панель.
                _pollTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2),
                };
                _pollTimer.Tick += async (_, _) => await PollDomForServerIdAsync();
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc.T("domains.connectVdsDialog.errWebview2"), ex.Message));
                MessageBox.Show(
                    string.Format(Loc.T("domains.connectVdsDialog.errWebview2Body"), ex.Message),
                    Loc.T("domains.connectVdsDialog.errWebview2Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    /// <summary>
    /// После каждого перехода: если пришли в личный кабинет и ещё не подхватили — fetch список
    /// серверов и запускаем flow. Если пришли на панель ISP — извлекаем sessionId и закрываем окно.
    /// </summary>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = Web.CoreWebView2.Source ?? "";
        try
        {
            if (_panelOpened && IsPanelUrl(url))
                await ExtractPanelSessionAndFinishAsync(url);
        }
        catch (Exception ex) { SetStatus("Ошибка: " + ex.Message); }
    }

    /// <summary>Ручной запуск через кнопку — берём ID из поля ввода (fallback если DOM-poll ничего не нашёл).</summary>
    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        var elid = TxtServerId.Text?.Trim();
        if (string.IsNullOrEmpty(elid) || !elid.All(char.IsDigit))
        {
            SetStatus(Loc.T("domains.connectVdsDialog.enterServerId"));
            return;
        }
        OpenPanel(elid);
    }

    /// <summary>Тикер: читает DOM залогиненной страницы, ищет ID серверов в ячейках таблицы.</summary>
    private async Task PollDomForServerIdAsync()
    {
        if (_panelOpened) { _pollTimer?.Stop(); return; }
        if (Web?.CoreWebView2 == null) return;
        try
        {
            // Извлекаем все числовые тексты из ячеек data-таблицы. У FirstVDS ячейки —
            // <span class="isp-cell-content__data">18048899</span>, но подстрахуемся через
            // data-ispui-dropdown-text (там точная копия текста) и общий querySelector.
            var script = @"
                (() => {
                    const found = new Set();
                    const scan = (nodes, prop) => {
                        for (const n of nodes) {
                            const t = ((prop === 'text' ? n.textContent : n.getAttribute(prop)) || '').trim();
                            if (/^\d{5,}$/.test(t)) found.add(t);
                        }
                    };
                    scan(document.querySelectorAll('.isp-cell-content__data'), 'text');
                    scan(document.querySelectorAll('[data-ispui-dropdown-text]'), 'data-ispui-dropdown-text');
                    return Array.from(found).join(',');
                })()
            ";
            var raw = await Web.CoreWebView2.ExecuteScriptAsync(script);
            var ids = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
            if (string.IsNullOrEmpty(ids))
            {
                SetStatus(Loc.T("domains.connectVdsDialog.waitingServers"));
                return;
            }
            var arr = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            if (arr.Count == 0) return;
            // Если только что открытая страница НЕ /billmgr?func=vds — перейдём туда, чтобы юзер
            // не увидел проблему что мы взяли что-то со случайной страницы.
            var elid = arr[0];
            if (arr.Count > 1)
            {
                Dispatcher.Invoke(() =>
                {
                    var msg = string.Format(Loc.T("domains.connectVdsDialog.multiServerPrompt"),
                        arr.Count, string.Join("\n", arr.Select((s, i) => $"{i + 1}. id={s}")));
                    var input = Microsoft.VisualBasic.Interaction.InputBox(msg, Loc.T("domains.connectVdsDialog.pickServerTitle"), "1");
                    if (int.TryParse(input, out var idx) && idx >= 1 && idx <= arr.Count)
                        elid = arr[idx - 1];
                });
            }
            TxtServerId.Text = elid;
            OpenPanel(elid);
        }
        catch (Exception ex) { SetStatus("DOM-poll: " + ex.Message); }
    }

    private void OpenPanel(string elid)
    {
        _panelOpened = true;
        _pollTimer?.Stop();
        _pickedServerId = elid;
        SetStatus(string.Format(Loc.T("domains.connectVdsDialog.openingPanel"), elid));
        var gotoUrl = $"https://my.firstvds.ru/billmgr?func=gotoserver.additionalpanel"
                      + $"&elid={elid}&panel=dnsmgr&newwindow=yes";
        Web.CoreWebView2.Navigate(gotoUrl);
    }

    /// <summary>Пробуем несколько known endpoints ISPmanager billmgr для получения списка серверов.
    /// Возвращаем первый, который вернул валидный XML с элементами.</summary>
    private async Task TryFetchServicesAsync()
    {
        // Endpoints вроде vds/service/vds.filter — билл-менеджер FirstVDS может использовать любой.
        // Возвращаем "STATUS|BODY" для каждого, чтобы можно было залогировать всё что пришло.
        var script = @"
            (async () => {
                const urls = [
                    '/billmgr?func=vds&out=xml',
                    '/billmgr?func=vds&out=xjson',
                    '/billmgr?func=service&out=xml',
                    '/billmgr?func=service.filter&out=xml&metadata=on',
                    '/billmgr?func=vds.filter&out=xml',
                    '/billmgr?func=vds&out=xml&elid=&plid='
                ];
                const out = [];
                for (const u of urls) {
                    try {
                        const r = await fetch(u, {credentials:'same-origin', headers:{'X-Requested-With':'XMLHttpRequest'}});
                        const t = await r.text();
                        out.push(u + ' → ' + r.status + ' (' + t.length + ' chars): ' + t.substring(0, 200));
                        if (r.status === 200 && t.length > 100 && t.indexOf('<elem') >= 0) {
                            return 'FOUND@' + u + '\n' + t;
                        }
                    } catch(e) { out.push(u + ' → ERR ' + e.message); }
                }
                return 'NONE\n' + out.join('\n');
            })()
        ";
        var rawJson = await Web.CoreWebView2.ExecuteScriptAsync(script);
        string body = "";
        if (!string.IsNullOrEmpty(rawJson) && rawJson != "null")
        {
            try { body = JsonSerializer.Deserialize<string>(rawJson) ?? ""; }
            catch { body = rawJson; } // fallback: strip кавычки вручную
        }
        if (body.StartsWith("NONE"))
        {
            // Показываем диагностику всех попыток
            SetStatus("Ни один endpoint не отдал список. " + body.Replace("\n", " | ").Substring(0, Math.Min(400, body.Length)));
            return;
        }
        if (body.StartsWith("FOUND@"))
        {
            // Отрезаем "FOUND@<url>\n" и парсим что за ним
            var nl = body.IndexOf('\n');
            SetStatus($"нашёл: {body.Substring(6, nl - 6)}");
            body = body.Substring(nl + 1);
        }
        if (body.StartsWith("ERR ") || body.Length < 30) { SetStatus($"pending… (body={body.Length} chars)"); return; }
        if (body.Contains("type=\"auth\"", StringComparison.OrdinalIgnoreCase)
            || body.Contains("captcha_verification_failed", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Ожидание входа в кабинет…");
            return;
        }
        // Пробуем распарсить. Debug: логируем длину и первые 200 символов.
        List<(string Id, string Name)> services;
        try
        {
            var xml = XDocument.Parse(body);
            // ISPmanager может возвращать <elem> напрямую или обёрнутыми в что-то ещё
            var elems = xml.Descendants("elem").ToList();
            services = elems
                .Select(el => (
                    Id:   el.Element("id")?.Value
                          ?? el.Element("elid")?.Value
                          ?? el.Attribute("id")?.Value
                          ?? el.Attribute("elid")?.Value
                          ?? "",
                    Name: el.Element("name")?.Value
                          ?? el.Element("domain")?.Value
                          ?? el.Element("caption")?.Value
                          ?? el.Element("pricelist")?.Value
                          ?? "server"))
                .Where(s => !string.IsNullOrEmpty(s.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            SetStatus($"XML parse error: {ex.Message}. body starts with: {body.Substring(0, Math.Min(120, body.Length))}");
            return;
        }

        if (services.Count == 0)
        {
            SetStatus($"Серверов не найдено. XML: {body.Substring(0, Math.Min(300, body.Length))}");
            return;
        }
        _servicesFetched = true;

        // Один сервер — сразу открываем панель; несколько — простой prompt (MessageBox с номерами)
        var pick = services[0];
        if (services.Count > 1)
        {
            var msg = "Найдено серверов: " + services.Count + "\n\n"
                    + string.Join("\n", services.Select((s, i) => $"{i + 1}. {s.Name} (id={s.Id})"))
                    + "\n\nВведи номер сервера (по умолчанию — 1):";
            var input = Microsoft.VisualBasic.Interaction.InputBox(msg, "Выбор сервера FirstVDS", "1");
            if (int.TryParse(input, out var idx) && idx >= 1 && idx <= services.Count)
                pick = services[idx - 1];
        }
        _pickedServerId = pick.Id;
        _panelOpened = true;
        SetStatus($"Переходим в панель ISP сервера {pick.Name}…");
        var gotoUrl = $"https://my.firstvds.ru/billmgr?func=gotoserver.additionalpanel"
                      + $"&elid={pick.Id}&panel=dnsmgr&newwindow=yes";
        Web.CoreWebView2.Navigate(gotoUrl);
    }

    /// <summary>URL похож на панель ISPmanager (не my.firstvds.ru).</summary>
    private static bool IsPanelUrl(string url) =>
        !string.IsNullOrEmpty(url)
        && !url.StartsWith("https://my.firstvds.ru", StringComparison.OrdinalIgnoreCase)
        && (url.Contains("/dnsmgr") || url.Contains("/ispmgr") || url.Contains(":1500"));

    /// <summary>
    /// После navigate на панель: извлекаем host из URL, sessionId из cookie `dnsmgrses5=`
    /// (или из query ?authinfo=). Собираем IspAccount и закрываем окно.
    /// </summary>
    private async Task ExtractPanelSessionAndFinishAsync(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            // sessionId: сначала из cookie
            var cm = Web.CoreWebView2.CookieManager;
            var cookies = await cm.GetCookiesAsync($"{uri.Scheme}://{uri.Host}");
            var sessionCookie = cookies.FirstOrDefault(c => c.Name.Equals("dnsmgrses5", StringComparison.OrdinalIgnoreCase))
                              ?? cookies.FirstOrDefault(c => c.Name.StartsWith("dnsmgrses", StringComparison.OrdinalIgnoreCase))
                              ?? cookies.FirstOrDefault(c => c.Name.StartsWith("ispmgrses", StringComparison.OrdinalIgnoreCase));
            string? sid = sessionCookie?.Value;
            // fallback — из query
            if (string.IsNullOrEmpty(sid))
            {
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                sid = q["authinfo"] ?? q["auth"];
            }
            if (string.IsNullOrEmpty(sid))
            {
                SetStatus(string.Format(Loc.T("domains.connectVdsDialog.sessionExtracted"), url));
                return;
            }

            SetStatus(string.Format(Loc.T("domains.connectVdsDialog.sessionSuccess"), host));
            Result = new IspAccount
            {
                Host = host,
                Username = "firstvds",
                Password = "", // не сохраняем — сессия обновляется через новый WebView-логин
                DisplayName = $"FirstVDS · {host}",
                Provider = "ispmanager",
                AuthType = "firstvds",
                FirstVdsServerId = _pickedServerId ?? "",
                CachedSessionId = sid,
            };
            DialogResult = true;
            await Task.Delay(400); // даём юзеру увидеть «успех»
            Close();
        }
        catch (Exception ex) { SetStatus(string.Format(Loc.T("domains.connectVdsDialog.errExtractSession"), ex.Message)); }
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusLine.Text = s);

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        base.OnClosed(e);
    }
}
