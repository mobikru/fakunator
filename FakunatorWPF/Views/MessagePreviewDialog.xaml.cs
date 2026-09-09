using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Fakunator.Core.AmsApi;
using Microsoft.Web.WebView2.Core;

namespace Fakunator.Views;

/// <summary>
/// Превью письма AMS с эмуляцией Gmail: рамка Google-почтовика вокруг +
/// изолированный iframe с настоящим HTML письма. WebView2 = Chromium,
/// градиенты/шрифты/media-queries рендерятся как в реальном браузере.
/// Четыре режима: Gmail-shell, «как есть», мобильный, plain-text.
/// </summary>
public partial class MessagePreviewDialog : Window
{
    private string _htmlContent = "";
    private string _plainText = "";
    private string _subject = "";
    private string _senderName = "Отправитель";
    private string _senderEmail = "sender@example.com";
    private bool _webReady;

    public MessagePreviewDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Загрузить сообщение по id и открыть диалог.</summary>
    public static async Task ShowFromAmsAsync(Window owner, AmsApiClient api, int messageId,
        string? senderName = null, string? senderEmail = null)
    {
        AmsMessage? msg;
        try { msg = await api.GetMessageAsync(messageId); }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Ошибка загрузки письма: " + ex.Message,
                "Превью", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (msg == null)
        {
            MessageBox.Show(owner, $"Не удалось загрузить письмо id={messageId}.",
                "Превью", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new MessagePreviewDialog { Owner = owner };
        dlg.SetPendingMessage(msg, senderName, senderEmail);
        dlg.ShowDialog();
    }

    private void SetPendingMessage(AmsMessage msg, string? senderName, string? senderEmail)
    {
        _subject = AmsApiClient.FromBase64(msg.Subject);
        _htmlContent = string.IsNullOrEmpty(msg.HtmlPart) ? "" : AmsApiClient.FromBase64(msg.HtmlPart);
        _plainText = HtmlToPlainText(_htmlContent);
        if (!string.IsNullOrWhiteSpace(senderName)) _senderName = senderName;
        if (!string.IsNullOrWhiteSpace(senderEmail)) _senderEmail = senderEmail;

        TxtInfo.Text = $"«{Trunc(msg.MessageName, 40)}» · {msg.MessageType} · {_htmlContent.Length:N0} симв.";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var envFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "FakunatorPreview_" + System.Environment.UserName);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: envFolder);
            await Web.EnsureCoreWebView2Async(env);
            _webReady = true;
            RenderCurrentMode();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            TxtFallback.Visibility = Visibility.Visible;
            TxtFallback.Text = $"WebView2 недоступен ({ex.Message})\n\n{_plainText}";
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        RenderCurrentMode();
    }

    private void RenderCurrentMode()
    {
        Web.Visibility = Visibility.Visible;
        TxtFallback.Visibility = Visibility.Collapsed;

        string html;
        if (RbCode?.IsChecked == true)
        {
            // Код-режим: рендерим source в WebView2 с VS Code-подобной подсветкой.
            html = BuildCodeView(_htmlContent);
        }
        else
        {
            // Предобработка сырого письма от AMS: -[CHARSET]- → utf-8, добавляем DOCTYPE.
            var cleaned = PreprocessAmsHtml(_htmlContent);
            cleaned = HighlightAmsMacros(cleaned);

            if (RbRaw?.IsChecked == true)
            {
                html = string.IsNullOrEmpty(cleaned)
                    ? "<html><body style='font-family:sans-serif;padding:20px;color:#666'>Письмо пустое</body></html>"
                    : cleaned;
            }
            else
            {
                var mobile = RbMobile?.IsChecked == true;
                html = BuildGmailShell(cleaned, mobile);
            }
        }
        Web.NavigateToString(html);
    }

    /// <summary>Рендер сырого HTML как в VS Code — тёмная тема, подсветка тегов/атрибутов/макросов.</summary>
    private static string BuildCodeView(string source)
    {
        var highlighted = HighlightHtmlSource(source ?? "");
        return @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><style>
  html, body { margin:0; padding:0; background:#1e1e1e; }
  body { color:#d4d4d4; font-family: 'JetBrains Mono','Cascadia Code','Consolas','Menlo',monospace;
         font-size:12.5px; line-height:1.55; }
  pre { margin:0; padding:20px 24px; white-space: pre-wrap; word-break: break-all;
        tab-size: 2; }
  .tag  { color:#569cd6; }
  .name { color:#4ec9b0; font-weight:600; }
  .an   { color:#9cdcfe; }
  .av   { color:#ce9178; }
  .cmt  { color:#6a9955; font-style: italic; }
  .doc  { color:#c586c0; font-weight:600; }
  .mac  { background:#3a2f00; color:#ffd76a; padding:1px 6px; border-radius:4px; }
  .txt  { color:#d4d4d4; }
  ::-webkit-scrollbar { width:10px; height:10px; }
  ::-webkit-scrollbar-track { background:#1e1e1e; }
  ::-webkit-scrollbar-thumb { background:#3e3e42; border-radius:5px; }
  ::-webkit-scrollbar-thumb:hover { background:#525258; }
</style></head><body><pre>" + highlighted + @"</pre></body></html>";
    }

    /// <summary>Подсветка HTML-источника: doctype, комментарии, теги, имена/значения
    /// атрибутов, AMS-макросы [%%…%%]. Простой regex-пайплайн, но результат читается
    /// как в редакторе.</summary>
    private static string HighlightHtmlSource(string src)
    {
        if (string.IsNullOrEmpty(src)) return "";
        // Escape HTML entities целиком, потом переклеиваем подсветку.
        var s = src.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // 1. Комментарии <!-- ... -->
        s = Regex.Replace(s, @"(&lt;!--.*?--&gt;)",
            "<span class=\"cmt\">$1</span>", RegexOptions.Singleline);

        // 2. DOCTYPE
        s = Regex.Replace(s, @"(&lt;!DOCTYPE[^&]*?&gt;)",
            "<span class=\"doc\">$1</span>", RegexOptions.IgnoreCase);

        // 3. Открытые/закрытые теги с атрибутами: <tag ...> </tag>
        s = Regex.Replace(s,
            @"(&lt;/?)([a-zA-Z][a-zA-Z0-9:-]*)([^&]*?)(/?&gt;)",
            m =>
            {
                var lt = m.Groups[1].Value;
                var name = m.Groups[2].Value;
                var attrs = m.Groups[3].Value;
                var gt = m.Groups[4].Value;
                return $"<span class=\"tag\">{lt}</span>" +
                       $"<span class=\"name\">{name}</span>" +
                       HighlightAttrs(attrs) +
                       $"<span class=\"tag\">{gt}</span>";
            }, RegexOptions.Singleline);

        // 4. AMS-макросы [%%…%%]
        s = Regex.Replace(s, @"\[%%([^%\]]+)%%\]",
            m => "<span class=\"mac\">[%%" + m.Groups[1].Value + "%%]</span>");

        return s;
    }

    private static string HighlightAttrs(string attrs)
    {
        if (string.IsNullOrWhiteSpace(attrs)) return attrs;
        // name="value" / name='value' / name=value / bare-name
        return Regex.Replace(attrs,
            "([a-zA-Z][a-zA-Z0-9:-]*)(=(&quot;[^&]*?&quot;|'[^']*'|[^\\s&\"'>]+))?",
            m =>
            {
                var name = m.Groups[1].Value;
                var val = m.Groups[2].Value;
                if (string.IsNullOrEmpty(val))
                    return $"<span class=\"an\">{name}</span>";
                return $"<span class=\"an\">{name}</span>=<span class=\"av\">{val[1..]}</span>";
            });
    }

    /// <summary>Заменяет AMS-макросы [%%Kind,args%%] на HTML-чипы с иконкой и коротким
    /// именем — в превью сразу видно «здесь будет случайный текст/линк/email/…».
    /// API AMS не даёт содержимое списков-макросов, поэтому реальный текст подставить
    /// нельзя — только визуально пометить.</summary>
    private static string HighlightAmsMacros(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Осторожно: макросы могут быть внутри href/src/action — там HTML-разметка
        // сломает атрибут. Поэтому обрабатываем только те что вне атрибутов.
        // Ленивое решение: замена только тех что окружены > и <, т.е. в тексте.
        return Regex.Replace(html,
            @"(?<=>)([^<]*?)\[%%([^%\]]+)%%\]",
            m =>
            {
                var before = m.Groups[1].Value;
                var payload = m.Groups[2].Value;
                var (icon, label) = ParseMacroLabel(payload);
                var chip = $"<span style=\"display:inline-block;background:#fff3cd;color:#664d03;" +
                           $"padding:1px 8px;border-radius:10px;font-size:0.85em;font-weight:600;" +
                           $"border:1px solid #ffd76a;font-family:'Segoe UI',sans-serif;\" " +
                           $"title=\"AMS-макрос: [%%{HtmlEscape(payload)}%%]\">{icon} {HtmlEscape(label)}</span>";
                return before + chip;
            },
            RegexOptions.Singleline);
    }

    private static (string icon, string label) ParseMacroLabel(string payload)
    {
        // Форматы: ORandText,list  ORandStr,params  Base64,Email  TLink,id,url  TUnsubscribeLink,...
        var parts = payload.Split(',');
        var kind = parts[0].Trim();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        return kind switch
        {
            "ORandText" => ("🎲", "случайный «" + Trunc(arg, 24) + "»"),
            "ORandStr" => ("🔤", "случайная строка"),
            "Base64" => ("🔒", "base64: " + Trunc(arg, 20)),
            "TLink" => ("🔗", "трек-ссылка"),
            "TUnsubscribeLink" => ("🚫", "unsubscribe"),
            _ => ("⚙", Trunc(payload, 32)),
        };
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "…";

    /// <summary>Осторожная чистка AMS-плейсхолдеров. Меняем ТОЛЬКО те что реально
    /// ломают парсинг (charset, http-equiv content-type). Остальные -[VAR]-
    /// оставляем как есть — они могут быть цветами внутри linear-gradient()
    /// или другими CSS-значениями. Если стереть — сломается весь CSS-declaration.
    /// Пусть браузер игнорирует невалидные значения сам, тогда рядом стоящие
    /// правила (типа fallback background-color) продолжат работать.</summary>
    private static string PreprocessAmsHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Только charset — иначе WebView не поймёт кодировку и часть текста поедет.
        html = html.Replace("-[CHARSET]-", "utf-8", StringComparison.OrdinalIgnoreCase);
        // Если DOCTYPE отсутствует — добавляем, чтоб рендер шёл в standards mode
        // (в quirks mode многие CSS-фичи ведут себя иначе).
        if (!Regex.IsMatch(html, @"^\s*<!DOCTYPE\b", RegexOptions.IgnoreCase))
            html = "<!DOCTYPE html>\n" + html;
        return html;
    }

    /// <summary>Строит HTML-шелл Gmail: шапка почтовика вокруг + iframe с письмом
    /// (base64 data URL — никаких escape-ада с srcdoc). Внутри iframe письмо
    /// живёт в собственном document context, стили не текут наружу.</summary>
    private string BuildGmailShell(string letterHtml, bool mobile)
    {
        var subj = HtmlEscape(_subject);
        var senderName = HtmlEscape(_senderName);
        var senderEmail = HtmlEscape(_senderEmail);
        var senderInit = string.IsNullOrEmpty(_senderName) ? "?" : _senderName[..1].ToUpper();
        var date = DateTime.Now.ToString("d MMM, HH:mm",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        var avatarColor = ColorForString(_senderName + _senderEmail);
        var maxWidth = "960px";

        var body = string.IsNullOrEmpty(letterHtml)
            ? "<html><body style='font-family:sans-serif;padding:20px;color:#666'>Письмо пустое</body></html>"
            : letterHtml;
        var b64Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

        var sizeBar = mobile
            ? @"<div class=""g-size-bar"">
                <button class=""g-chip"" data-w=""360"">360 · Android S</button>
                <button class=""g-chip"" data-w=""375"">375 · iPhone SE</button>
                <button class=""g-chip active"" data-w=""390"">390 · iPhone 12–15</button>
                <button class=""g-chip"" data-w=""414"">414 · iPhone Plus</button>
                <button class=""g-chip"" data-w=""480"">480 · Android XL</button>
                <button class=""g-chip"" data-w=""600"">600 · планшет</button>
                <button class=""g-chip"" data-w=""768"">768 · iPad</button>
                <span id=""widthReadout"" class=""g-readout"">390 px</span>
              </div>"
            : "";
        var frameWrapClass = mobile ? "g-frame-wrap is-mobile" : "g-frame-wrap";
        var frameWrapStyle = mobile ? "width:390px;height:640px;" : "width:100%;height:640px;";
        var hint = mobile
            ? @"<div class=""g-hint"">⤡ потяните правый нижний угол блока — свободное изменение ширины</div>"
            : "";

        // Gmail-подобный layout. Используем системные Google-шрифты и палитру
        // из реального Gmail. Iframe изолирует стили письма.
        var css = @"
:root { color-scheme: light; }
* { box-sizing: border-box; }
body {
  margin: 0; padding: 20px 0;
  background: #f6f8fc;
  font-family: 'Google Sans','Segoe UI','Roboto','Arial',sans-serif;
  color: #202124;
}
.g-card { max-width: " + maxWidth + @"; margin: 0 auto; background: white;
  border-radius: 12px; box-shadow: 0 1px 2px rgba(60,64,67,.15), 0 1px 3px rgba(60,64,67,.1); }
.g-header { padding: 24px 32px 16px; border-bottom: 1px solid #f1f3f4; }
.g-subject { font-size: 22px; font-weight: 400; color: #202124; margin: 0 0 20px; word-wrap: break-word; }
.g-sender { display: flex; align-items: center; gap: 14px; }
.g-avatar { width: 40px; height: 40px; border-radius: 50%;
  display: flex; align-items: center; justify-content: center;
  color: white; font-size: 16px; font-weight: 500; flex-shrink: 0; }
.g-sender-info { flex: 1; min-width: 0; }
.g-sender-line { font-size: 14px; color: #202124; }
.g-sender-name { font-weight: 500; }
.g-sender-email { color: #5f6368; font-size: 12px; }
.g-sender-to { font-size: 12px; color: #5f6368; margin-top: 2px; }
.g-date { font-size: 12px; color: #5f6368; white-space: nowrap; }
.g-body-wrap { padding: 8px 0; background: white; }
.g-size-bar { display: flex; align-items: center; justify-content: center; gap: 6px;
  padding: 4px 24px 12px; flex-wrap: wrap; }
.g-chip { border: 1px solid #dadce0; background: white; border-radius: 14px;
  padding: 4px 12px; font-size: 12px; color: #3c4043; cursor: pointer;
  font-family: inherit; }
.g-chip:hover { background: #f1f3f4; }
.g-chip.active { background: #e8f0fe; border-color: #1a73e8; color: #1967d2; font-weight: 600; }
.g-readout { font-size: 12px; color: #5f6368; font-variant-numeric: tabular-nums;
  min-width: 68px; text-align: center; align-self: center; }
.g-frame-wrap { margin: 0 auto; position: relative; }
.g-frame-wrap.is-mobile { resize: horizontal; overflow: hidden; min-width: 280px;
  max-width: 900px; border: 1px solid #dadce0; border-radius: 4px; }
.g-frame-wrap:not(.is-mobile) { width: 100% !important; }
.g-frame { width: 100%; height: 100%; display: block; border: 0; background: white; }
.g-hint { text-align: center; font-size: 11px; color: #9aa0a6; padding: 6px 0 0; }
.g-actions { padding: 14px 24px 20px; display: flex; gap: 8px; border-top: 1px solid #f1f3f4; }
.g-btn { border: 1px solid #dadce0; background: white; border-radius: 24px;
  padding: 8px 22px; font-size: 14px; color: #202124;
  font-family: inherit; cursor: pointer; }
.g-btn:hover { background: #f8f9fa; }
.g-badge { display: inline-block; background: #e8f0fe; color: #1967d2;
  border-radius: 4px; padding: 2px 6px; font-size: 10px; margin-left: 8px;
  font-weight: 500; text-transform: uppercase; letter-spacing: .3px; }
";

        // Авто-высота по контенту письма + управление шириной (пресеты/drag) в мобильном режиме.
        var js = @"
window.addEventListener('DOMContentLoaded', function() {
  var f = document.getElementById('mail');
  var wrap = document.getElementById('frameWrap');
  if (!f) return;
  function applyHeight() {
    try {
      var doc = f.contentDocument || f.contentWindow.document;
      var h = Math.max(doc.body.scrollHeight, doc.documentElement.scrollHeight, 320);
      if (wrap && wrap.classList.contains('is-mobile')) wrap.style.height = h + 'px';
      else f.style.height = h + 'px';
    } catch(e) { /* cross-origin — оставим текущую высоту */ }
  }
  f.addEventListener('load', applyHeight);

  var readout = document.getElementById('widthReadout');
  if (wrap && readout && window.ResizeObserver) {
    var ro = new ResizeObserver(function() {
      readout.textContent = Math.round(wrap.getBoundingClientRect().width) + ' px';
    });
    ro.observe(wrap);
  }
  document.querySelectorAll('.g-chip[data-w]').forEach(function(chip) {
    chip.addEventListener('click', function() {
      wrap.style.width = chip.getAttribute('data-w') + 'px';
      document.querySelectorAll('.g-chip[data-w]').forEach(function(c) { c.classList.remove('active'); });
      chip.classList.add('active');
      applyHeight();
    });
  });
});
";

        return $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Gmail preview</title>
  <style>{css}</style>
  <script>{js}</script>
</head>
<body>
  <div class=""g-card"">
    <div class=""g-header"">
      <h1 class=""g-subject"">{subj}<span class=""g-badge"">Входящие</span></h1>
      <div class=""g-sender"">
        <div class=""g-avatar"" style=""background:{avatarColor};"">{HtmlEscape(senderInit)}</div>
        <div class=""g-sender-info"">
          <div class=""g-sender-line""><span class=""g-sender-name"">{senderName}</span>
            <span class=""g-sender-email"">&lt;{senderEmail}&gt;</span></div>
          <div class=""g-sender-to"">кому: мне ▾</div>
        </div>
        <div class=""g-date"">{date}</div>
      </div>
    </div>
    <div class=""g-body-wrap"">
      {sizeBar}
      <div id=""frameWrap"" class=""{frameWrapClass}"" style=""{frameWrapStyle}"">
        <iframe id=""mail"" class=""g-frame"" src=""data:text/html;charset=utf-8;base64,{b64Body}""></iframe>
      </div>
      {hint}
    </div>
    <div class=""g-actions"">
      <button class=""g-btn"">← Ответить</button>
      <button class=""g-btn"">↳ Переслать</button>
    </div>
  </div>
</body>
</html>";
    }

    private void OnCopyHtml(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_htmlContent); BtnCopy.Content = "✓ Скопировано"; }
        catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ──────────────────────────────────────────────────────

    private static string HtmlEscape(string s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                 .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>Стабильный «Gmail-подобный» цвет аватара по имени.</summary>
    private static string ColorForString(string s)
    {
        var palette = new[] { "#1a73e8", "#137333", "#a50e0e", "#e37400", "#5f6368",
                              "#8430ce", "#00695c", "#c5221f", "#f9ab00", "#1967d2" };
        int h = 0;
        foreach (var c in s ?? "") h = ((h << 5) - h) + c;
        return palette[Math.Abs(h) % palette.Length];
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Regex.Replace(html,
            @"<(script|style|head)\b[^<]*(?:(?!</\1>)<[^<]*)*</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<(br|/p|/div|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}
