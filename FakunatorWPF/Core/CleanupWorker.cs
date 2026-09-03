using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

public class CleanupWorker
{
    private readonly string _inputPath;
    private readonly string _outputDir;
    private readonly Blocklists _lists;
    private readonly HashSet<string> _enabled;
    private readonly bool _gmailStrict;
    private readonly bool _useSpamhausDbl;

    private const int FeedMaxPerTick = 12;

    public CleanupWorker(string inputPath, string outputDir, Blocklists lists,
        HashSet<string> enabled, bool gmailStrict, bool useSpamhausDbl = false)
    {
        _inputPath = inputPath;
        _outputDir = outputDir;
        _lists = lists;
        _enabled = enabled;
        _gmailStrict = gmailStrict;
        _useSpamhausDbl = useSpamhausDbl;
    }

    /// <summary>
    /// Process the input file line-by-line, classify each email, write outputs,
    /// report progress ~10 Hz. Supports cancellation.
    /// </summary>
    public async Task RunAsync(IProgress<SnapshotBatch> progress, CancellationToken ct)
    {
        await Task.Run(() => RunCore(progress, ct), ct).ConfigureAwait(false);
    }

    private void RunCore(IProgress<SnapshotBatch> progress, CancellationToken ct)
    {
        // Count lines first
        int total = EmailHelpers.CountLines(_inputPath);

        // Create output directory
        Directory.CreateDirectory(_outputDir);

        // Open output StreamWriters
        var handles = new Dictionary<string, StreamWriter>();
        foreach (var key in Tokens.CategoryOrder)
        {
            var fname = key == "clean" ? "clean.txt" : $"rejected_{key}.txt";
            var path = Path.Combine(_outputDir, fname);
            handles[key] = new StreamWriter(path, false, System.Text.Encoding.UTF8)
            {
                NewLine = "\n"
            };
        }

        var allHandle = new StreamWriter(Path.Combine(_outputDir, "rejected_all.txt"), false, System.Text.Encoding.UTF8)
        {
            NewLine = "\n"
        };

        // Log of typo auto-corrections: original → suggested. Helps user audit changes.
        var correctedHandle = new StreamWriter(Path.Combine(_outputDir, "typo_corrected.txt"), false, System.Text.Encoding.UTF8)
        {
            NewLine = "\n"
        };
        correctedHandle.WriteLine("# Автокоррекции опечаток: original → corrected");

        var classifier = new Classifier(_lists, _enabled, _gmailStrict, _useSpamhausDbl);

        var counts = new Dictionary<string, int>();
        foreach (var key in Tokens.CategoryOrder)
            counts[key] = 0;

        var providers = new Dictionary<string, int>();
        foreach (var key in Tokens.ProviderColors.Keys)
            providers[key] = 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var sw = Stopwatch.StartNew();
        double lastTickTime = 0;
        int lastProcessedAtTick = 0;
        int processed = 0;
        var freshFeed = new List<(int, string, string, string)>();

        try
        {
            using var reader = new StreamReader(_inputPath, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();

                processed++;
                var email = EmailHelpers.Normalize(rawLine);

                if (string.IsNullOrEmpty(email))
                {
                    counts["invalid"]++;
                    handles["invalid"].WriteLine();
                    allHandle.WriteLine("<empty>\tinvalid\tempty line");
                    if (freshFeed.Count < FeedMaxPerTick)
                        freshFeed.Add((processed, "<empty>", "invalid", "empty"));
                }
                else
                {
                    var key = EmailHelpers.CanonicalForDedup(email, _gmailStrict);
                    if (seen.Contains(key))
                    {
                        counts["duplicate"]++;
                        handles["duplicate"].WriteLine(email);
                        allHandle.WriteLine($"{email}\tduplicate\t");
                        if (freshFeed.Count < FeedMaxPerTick)
                            freshFeed.Add((processed, email, "duplicate", ""));
                    }
                    else
                    {
                        seen.Add(key);
                        var v = classifier.Classify(email);

                        // Typo: автокоррекция вместо удаления. Suggested email кладём в clean.txt.
                        if (v.Category == "typo" && !string.IsNullOrEmpty(v.Suggested))
                        {
                            var corrected = v.Suggested;
                            var correctedKey = EmailHelpers.CanonicalForDedup(corrected, _gmailStrict);

                            if (seen.Contains(correctedKey))
                            {
                                // Исправленная версия уже была в файле — считаем дубликатом
                                counts["duplicate"]++;
                                handles["duplicate"].WriteLine(email);
                                allHandle.WriteLine($"{email}\tduplicate\tcorrected to {corrected} (already seen)");
                                correctedHandle.WriteLine($"{email}\t→\t{corrected}\t(duplicate)");
                                if (freshFeed.Count < FeedMaxPerTick)
                                    freshFeed.Add((processed, email, "duplicate", $"→ {corrected}"));
                            }
                            else
                            {
                                seen.Add(correctedKey);
                                counts["typo"]++;
                                handles["clean"].WriteLine(corrected);
                                correctedHandle.WriteLine($"{email}\t→\t{corrected}");

                                // Определяем провайдера по исправленному домену
                                var atIdx = corrected.LastIndexOf('@');
                                var correctedDomain = atIdx >= 0 ? corrected[(atIdx + 1)..] : "";
                                var prov = Tokens.DomainToProvider.TryGetValue(correctedDomain, out var pp) ? pp : "other";
                                if (providers.ContainsKey(prov))
                                    providers[prov]++;
                                else
                                    providers[prov] = 1;

                                if (freshFeed.Count < FeedMaxPerTick)
                                    freshFeed.Add((processed, email, "typo", $"→ {corrected}"));
                            }
                        }
                        else
                        {
                            counts[v.Category]++;

                            if (v.Category == "clean")
                            {
                                handles["clean"].WriteLine(email);
                                var prov = string.IsNullOrEmpty(v.Provider) ? "other" : v.Provider;
                                if (providers.ContainsKey(prov))
                                    providers[prov]++;
                                else
                                    providers[prov] = 1;
                            }
                            else
                            {
                                handles[v.Category].WriteLine(email);
                                allHandle.WriteLine($"{email}\t{v.Category}\t{v.Note}");
                            }

                            if (freshFeed.Count < FeedMaxPerTick)
                                freshFeed.Add((processed, email, v.Category, v.Note));
                        }
                    }
                }

                // Tick the UI every ~100ms
                double now = sw.Elapsed.TotalSeconds;
                if (now - lastTickTime >= 0.10)
                {
                    double elapsed = now;
                    double instSpeed = (now - lastTickTime) > 0
                        ? (processed - lastProcessedAtTick) / (now - lastTickTime)
                        : 0;
                    double avgSpeed = elapsed > 0 ? processed / elapsed : 0;

                    progress.Report(new SnapshotBatch
                    {
                        Processed = processed,
                        Total = total,
                        Counts = new Dictionary<string, int>(counts),
                        Providers = new Dictionary<string, int>(providers),
                        Elapsed = elapsed,
                        Speed = avgSpeed,
                        ThroughputPoint = instSpeed,
                        FreshFeed = new List<(int, string, string, string)>(freshFeed),
                    });

                    freshFeed.Clear();
                    lastProcessedAtTick = processed;
                    lastTickTime = now;
                }
            }
        }
        finally
        {
            // Flush + close all files
            foreach (var h in handles.Values)
            {
                h.Flush();
                h.Dispose();
            }
            allHandle.Flush();
            allHandle.Dispose();
            correctedHandle.Flush();
            correctedHandle.Dispose();
        }

        // Final snapshot
        double finalElapsed = sw.Elapsed.TotalSeconds;
        double finalSpeed = finalElapsed > 0 ? processed / finalElapsed : 0;

        progress.Report(new SnapshotBatch
        {
            Processed = processed,
            Total = total,
            Counts = new Dictionary<string, int>(counts),
            Providers = new Dictionary<string, int>(providers),
            Elapsed = finalElapsed,
            Speed = finalSpeed,
            ThroughputPoint = finalSpeed,
            FreshFeed = new List<(int, string, string, string)>(freshFeed),
        });

        // Sort output files (same as Python worker._sort_outputs)
        SortOutputs(_outputDir, counts);
    }

    private static void SortOutputs(string outDir, Dictionary<string, int> counts)
    {
        static (string domain, string local) SortKey(string email)
        {
            var at = email.LastIndexOf('@');
            if (at < 0)
                return (email, "");
            return (email[(at + 1)..], email[..at]);
        }

        foreach (var key in Tokens.CategoryOrder)
        {
            if (!counts.TryGetValue(key, out var cnt) || cnt == 0)
                continue;

            var fname = key == "clean" ? "clean.txt" : $"rejected_{key}.txt";
            var path = Path.Combine(outDir, fname);
            if (!File.Exists(path))
                continue;

            var lines = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .OrderBy(l => SortKey(l).domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => SortKey(l).local, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8) { NewLine = "\n" };
            foreach (var line in lines)
                writer.WriteLine(line);
        }

        // rejected_all.txt — group by category, sort within each group
        var allPath = Path.Combine(outDir, "rejected_all.txt");
        if (!File.Exists(allPath))
            return;

        var fi = new FileInfo(allPath);
        if (fi.Length == 0)
            return;

        var orderIdx = new Dictionary<string, int>();
        for (int i = 0; i < Tokens.CategoryOrder.Length; i++)
            orderIdx[Tokens.CategoryOrder[i]] = i;

        var rows = new List<(string email, string cat, string note)>();
        foreach (var line in File.ReadAllLines(allPath))
        {
            var trimmed = line.TrimEnd('\r', '\n');
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var parts = trimmed.Split('\t', 3);
            var email = parts[0];
            var cat = parts.Length > 1 ? parts[1] : "";
            var note = parts.Length > 2 ? parts[2] : "";
            rows.Add((email, cat, note));
        }

        rows.Sort((a, b) =>
        {
            int oa = orderIdx.TryGetValue(a.cat, out var ia) ? ia : 999;
            int ob = orderIdx.TryGetValue(b.cat, out var ib) ? ib : 999;
            if (oa != ob) return oa.CompareTo(ob);

            var ka = SortKey(a.email);
            var kb = SortKey(b.email);
            int cmp = string.Compare(ka.domain, kb.domain, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(ka.local, kb.local, StringComparison.OrdinalIgnoreCase);
        });

        using var allWriter = new StreamWriter(allPath, false, System.Text.Encoding.UTF8) { NewLine = "\n" };
        string? currentCat = null;
        foreach (var (email, cat, note) in rows)
        {
            if (cat != currentCat)
            {
                if (currentCat != null)
                    allWriter.WriteLine();
                allWriter.WriteLine($"# ===== {cat.ToUpperInvariant()} =====");
                currentCat = cat;
            }
            allWriter.WriteLine($"{email}\t{cat}\t{note}");
        }
    }
}
