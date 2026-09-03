using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

public static class BlocklistUpdater
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Primary: jsdelivr CDN — проксирует GitHub, БЕЗ rate-limit'а.
    // Fallback: raw.githubusercontent.com — часто выдаёт 429 при частых обновлениях.
    // GitHub переименовал default branch с "master" на "main" во многих репо:
    // disposable-email-domains → main, willwhite/freemail → всё ещё master.
    private static readonly (string FileName, string[] Urls)[] Sources =
    {
        (
            "disposable_domains.txt",
            new[]
            {
                "https://cdn.jsdelivr.net/gh/disposable-email-domains/disposable-email-domains@main/disposable_email_blocklist.conf",
                "https://raw.githubusercontent.com/disposable-email-domains/disposable-email-domains/main/disposable_email_blocklist.conf",
            }
        ),
        (
            "free_providers.txt",
            new[]
            {
                "https://cdn.jsdelivr.net/gh/willwhite/freemail@master/data/free.txt",
                "https://raw.githubusercontent.com/willwhite/freemail/master/data/free.txt",
            }
        ),
    };

    /// <summary>
    /// Download fresh blocklist files from GitHub and save to the data/ directory.
    /// role_prefixes.txt is kept as-is (rarely changes).
    /// </summary>
    /// <returns>Tuple of (number of files that changed, error message if any).</returns>
    public static async Task<(int updated, string? error)> UpdateAsync(
        string dataDir, IProgress<string>? status, CancellationToken ct)
    {
        int updated = 0;
        string? lastError = null;

        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        foreach (var (fileName, urls) in Sources)
        {
            ct.ThrowIfCancellationRequested();

            status?.Report($"Загрузка {fileName}...");

            try
            {
                // Пробуем URLs по очереди — первый успешный выигрывает (jsdelivr → raw fallback).
                string? content = null;
                string? lastFetchError = null;
                foreach (var url in urls)
                {
                    try
                    {
                        var response = await Http.GetAsync(url, ct);
                        response.EnsureSuccessStatusCode();
                        content = await response.Content.ReadAsStringAsync(ct);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastFetchError = ex.Message;
                    }
                }
                if (content == null)
                    throw new HttpRequestException(lastFetchError ?? "все источники недоступны");

                // Normalize line endings
                content = content.Replace("\r\n", "\n").Replace("\r", "\n");

                var targetPath = Path.Combine(dataDir, fileName);
                var tempPath = targetPath + ".tmp";

                await File.WriteAllTextAsync(tempPath, content, ct);

                // Compare with existing
                bool different = true;
                if (File.Exists(targetPath))
                {
                    var existing = await File.ReadAllTextAsync(targetPath, ct);
                    existing = existing.Replace("\r\n", "\n").Replace("\r", "\n");
                    different = !string.Equals(existing, content, StringComparison.Ordinal);
                }

                if (different)
                {
                    File.Copy(tempPath, targetPath, overwrite: true);
                    updated++;

                    // Count entries (non-empty, non-comment lines)
                    int count = 0;
                    foreach (var line in content.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                            count++;
                    }

                    status?.Report($"Обновлено: {fileName} — {count:N0} записей");
                }
                else
                {
                    status?.Report($"{fileName} — без изменений");
                }

                // Clean up temp
                try { File.Delete(tempPath); } catch { }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = $"Ошибка при загрузке {fileName}: {ex.Message}";
                status?.Report(msg);
                lastError ??= msg;
            }
        }

        if (updated > 0)
            status?.Report($"Готово: {updated} файл(ов) обновлено");
        else if (lastError == null)
            status?.Report("Все списки актуальны");

        return (updated, lastError);
    }
}
