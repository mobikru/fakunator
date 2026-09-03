using System;
using System.IO;

namespace Fakunator.Core;

/// <summary>
/// Единая точка для путей результатов. Всё пишется рядом с exe в подпапку <c>output/</c>,
/// чтобы пользователь не искал файлы по системе.
/// </summary>
public static class Paths
{
    private static string? _exeDir;

    /// <summary>
    /// Папка с запущенным exe. Для single-file публикации <c>AppContext.BaseDirectory</c> может
    /// указывать в bundle-extraction directory (temp), поэтому в первую очередь используем
    /// <c>Environment.ProcessPath</c> (.NET 6+).
    /// </summary>
    public static string ExeDir
    {
        get
        {
            if (_exeDir != null) return _exeDir;
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                _exeDir = Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
            }
            else
            {
                _exeDir = AppContext.BaseDirectory;
            }
            return _exeDir;
        }
    }

    /// <summary>Корневая папка для всех результатов работы. Лежит рядом с exe.</summary>
    public static string OutputRoot => Path.Combine(ExeDir, "output");

    public static string CleanupRoot => EnsureDir(Path.Combine(OutputRoot, "cleanup"));
    public static string SmtpRoot    => EnsureDir(Path.Combine(OutputRoot, "smtp"));
    public static string AnalyzeRoot => EnsureDir(Path.Combine(OutputRoot, "analyze"));

    /// <summary>Папка data/ рядом с exe (блок-листы, names.db, domains.db, кэши).</summary>
    public static string DataDir => EnsureDir(Path.Combine(ExeDir, "data"));

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }
}
