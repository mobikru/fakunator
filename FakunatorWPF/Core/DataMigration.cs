using System.IO;

namespace Fakunator.Core;

/// <summary>
/// Одноразовый перенос данных из старого расположения (рядом с exe — в Program Files, куда
/// инсталлятор ставит по умолчанию, это недоступно для записи обычному пользователю без прав
/// администратора) в %APPDATA%\Fakunator. Отсюда симптом, который чинит эта миграция: у части
/// пользователей Очистка/SMTP-проверка/Анализ мгновенно "завершались" с пустым результатом,
/// а обновление блок-листов молча падало — Directory.CreateDirectory/File-запись в Program
/// Files кидали UnauthorizedAccessException, который где-то по пути тихо проглатывался.
/// Запускается один раз, в самом начале App.OnStartup — до первого обращения к Config.Current
/// (которое лениво грузит config.json) и до создания MainWindow (чьи {core:Tr} биндинги тоже
/// трогают Config.Current через Loc).
/// </summary>
public static class DataMigration
{
    /// <summary>true, если только что перенесли данные РЕАЛЬНО существовавшей установки —
    /// не просто seed-данные свежего инсталлятора (data-seed.zip кладёт болванку data/ рядом
    /// с exe при любой первой установке, это не повод тревожить юзера уведомлением).</summary>
    public static bool MigratedExistingInstall { get; private set; }

    public static void RunIfNeeded()
    {
        try { Run(); }
        catch { /* best effort — не блокируем старт приложения из-за миграции */ }
    }

    private static void Run()
    {
        var newConfig = Path.Combine(Paths.AppDataRoot, "config.json");
        if (File.Exists(newConfig)) return; // уже мигрировали при прошлом запуске

        var legacyConfig = Config.FindLegacyConfigPath();
        var legacyOutput = Path.Combine(Paths.ExeDir, "output");
        var legacyData = FindLegacyDataSource();

        bool hasAnything = legacyConfig != null || Directory.Exists(legacyOutput) || legacyData != null;
        if (!hasAnything) return; // совсем свежий запуск без предыстории — переносить нечего

        if (legacyConfig != null && File.Exists(legacyConfig))
            File.Copy(legacyConfig, newConfig, overwrite: false);

        if (Directory.Exists(legacyOutput))
            CopyDirectory(legacyOutput, Path.Combine(Paths.AppDataRoot, "output"));

        if (legacyData != null)
            CopyDirectory(legacyData, Path.Combine(Paths.AppDataRoot, "data"));

        // Уведомляем только если был реальный config.json предыдущей установки — если это
        // просто seed из инсталлятора, для юзера ничего не "переехало", это его первый запуск.
        MigratedExistingInstall = legacyConfig != null;
    }

    /// <summary>Ищет старую data/ — сначала прямо рядом с exe (там, где её всегда искал
    /// Paths.DataDir и куда data-seed.zip инсталлятора кладёт блок-листы + domains.db
    /// создавался при работе), а если её там нет (dev-режим — репозиторий, а не установленный
    /// exe) — поднимается вверх в поисках data/free_providers.txt, как раньше делал
    /// Blocklists.FindDataDir().</summary>
    private static string? FindLegacyDataSource()
    {
        var exeDir = Paths.ExeDir;
        var direct = Path.Combine(exeDir, "data");
        if (Directory.Exists(direct)) return direct;

        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(p, "free_providers.txt"))) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            var destSubDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destSubDir)) Directory.CreateDirectory(destSubDir);
            if (!File.Exists(dest)) File.Copy(file, dest, overwrite: false);
        }
    }
}
