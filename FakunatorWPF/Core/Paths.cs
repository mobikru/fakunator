using System;
using System.IO;

namespace Fakunator.Core;

/// <summary>
/// Единая точка для путей результатов. До v2.7 всё писалось рядом с exe — если программа
/// стоит в Program Files (дефолт инсталлятора), обычный пользователь без прав администратора
/// не может туда писать: Очистка/SMTP/Анализ мгновенно "завершались" с пустым результатом,
/// апдейт блок-листов молча падал и т.п. Теперь пишем в %APPDATA%\Fakunator — туда может
/// писать любой пользователь Windows без исключений, exe при этом может оставаться где угодно.
/// Существующие данные переносятся один раз при первом запуске — см. DataMigration.
/// </summary>
public static class Paths
{
    private static string? _exeDir;
    private static string? _appDataRoot;

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

    /// <summary>%APPDATA%\Fakunator — писать сюда может любой пользователь Windows, без
    /// прав администратора, независимо от того, куда установлен сам exe. Та же папка,
    /// где App.xaml.cs уже пишет crash.log.</summary>
    public static string AppDataRoot
    {
        get
        {
            if (_appDataRoot != null) return _appDataRoot;
            _appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fakunator");
            try { Directory.CreateDirectory(_appDataRoot); } catch { }
            return _appDataRoot;
        }
    }

    /// <summary>Корневая папка для всех результатов работы.</summary>
    public static string OutputRoot => Path.Combine(AppDataRoot, "output");

    public static string CleanupRoot => EnsureDir(Path.Combine(OutputRoot, "cleanup"));
    public static string SmtpRoot    => EnsureDir(Path.Combine(OutputRoot, "smtp"));
    public static string AnalyzeRoot => EnsureDir(Path.Combine(OutputRoot, "analyze"));

    /// <summary>Папка data/ (блок-листы, names.db, domains.db, кэши).</summary>
    public static string DataDir => EnsureDir(Path.Combine(AppDataRoot, "data"));

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }
}
