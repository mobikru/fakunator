using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Fakunator.Core.Server;

/// <summary>
/// Простой Replace-шаблонизатор: подставляет {placeholder} в текстовые файлы.
/// Шаблоны лежат в Assets/ServerTemplates/ и копируются в bin/ как Content.
/// </summary>
public static class TemplateEngine
{
    /// <summary>
    /// Рендерит шаблон из Assets/ServerTemplates/{path}.template, подставляя переменные из vars.
    /// Пример: Render("postfix/main.cf", new{["domain"]="w00w.site"})
    /// </summary>
    public static string Render(string templatePath, IReadOnlyDictionary<string, string> vars)
    {
        var full = FindTemplateFile(templatePath);
        var text = File.ReadAllText(full, Encoding.UTF8);
        return Substitute(text, vars);
    }

    /// <summary>
    /// Заменяет каждый ключ (plain substring — как есть) на значение.
    /// Пример: {"w00w.site":"mydomain.xyz","fakir":"newP@ss"} → все "w00w.site" и "fakir" в тексте заменены.
    /// Ключи применяются в порядке от самых длинных к коротким чтобы избежать частичных перекрытий
    /// (например "nserren.w00w.site" должно замениться до "w00w.site").
    /// </summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder(text);
        foreach (var kv in vars.OrderByDescending(kv => kv.Key.Length))
        {
            sb.Replace(kv.Key, kv.Value ?? "");
        }
        return sb.ToString();
    }

    private static string FindTemplateFile(string relativePath)
    {
        // Для single-file self-contained Assembly.Location возвращает "" —
        // Environment.ProcessPath даёт реальный путь к exe.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "Assets", "ServerTemplates", relativePath);
        if (File.Exists(candidate)) return candidate;

        // Fallback: с .template суффиксом
        var alt = Path.Combine(exeDir, "Assets", "ServerTemplates", relativePath + ".template");
        if (File.Exists(alt)) return alt;

        throw new FileNotFoundException($"Template not found: {relativePath} (looked in {candidate})");
    }
}
