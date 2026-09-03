// Temporary debug test — run via: dotnet run -- --test
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fakunator.Core;

namespace Fakunator;

public static class DebugTest
{
    public static void Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        // 1. Find data dir
        var dataDir = Blocklists.FindDataDir();
        Console.WriteLine($"[1] DataDir: {dataDir}");
        Console.WriteLine($"    Exists: {Directory.Exists(dataDir)}");
        var fpPath = Path.Combine(dataDir, "free_providers.txt");
        Console.WriteLine($"    free_providers.txt exists: {File.Exists(fpPath)}");

        // 2. Load blocklists
        var lists = Blocklists.Load(dataDir);
        Console.WriteLine($"\n[2] Blocklists loaded:");
        Console.WriteLine($"    FreeProviders: {lists.FreeProviders.Count}");
        Console.WriteLine($"    Disposable: {lists.Disposable.Count}");
        Console.WriteLine($"    DisposableWildcards: {lists.DisposableWildcards.Count}");
        Console.WriteLine($"    AllowDomains: {lists.AllowDomains.Count}");
        Console.WriteLine($"    RolePrefixes: {lists.RolePrefixes.Count}");
        Console.WriteLine($"    TrapKeywords: {lists.TrapKeywords.Count}");
        Console.WriteLine($"    TrapDomains: {lists.TrapDomains.Count}");
        Console.WriteLine($"    TypoMap: {lists.TypoMap.Count}");

        // Check if gmail.com is in FreeProviders
        Console.WriteLine($"\n    gmail.com in FreeProviders: {lists.FreeProviders.Contains("gmail.com")}");
        Console.WriteLine($"    yahoo.com in FreeProviders: {lists.FreeProviders.Contains("yahoo.com")}");
        Console.WriteLine($"    example-corp.com in FreeProviders: {lists.FreeProviders.Contains("example-corp.com")}");

        // 3. Create classifier with all filters enabled
        var enabled = new HashSet<string>(Tokens.CategoryOrder, StringComparer.OrdinalIgnoreCase);
        var classifier = new Classifier(lists, enabled, gmailStrict: true);

        // 4. Test some emails
        var testEmails = new[]
        {
            "test@gmail.com",
            "user@yahoo.com",
            "info@gmail.com",        // role
            "user@mailinator.com",   // disposable
            "admin@example.com",     // reserved
            "test@gmial.com",        // typo
            "user@somecorp.net",     // corporate
            "not-an-email",          // invalid
            "12345678@gmail.com",    // trap (digits only)
        };

        Console.WriteLine("\n[3] Classification tests:");
        foreach (var email in testEmails)
        {
            var norm = EmailHelpers.Normalize(email);
            var v = classifier.Classify(norm);
            Console.WriteLine($"    {email,-30} → {v.Category,-12} ({v.Note})");
        }

        // 5. Read first 20 lines of 1.txt and classify
        var testFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "1.txt");
        testFile = Path.GetFullPath(testFile);
        if (!File.Exists(testFile))
        {
            // Try project root
            testFile = @"c:\Users\Александр\Desktop\Clear Base\1.txt";
        }

        if (File.Exists(testFile))
        {
            Console.WriteLine($"\n[4] First 20 lines of {Path.GetFileName(testFile)}:");
            using var reader = new StreamReader(testFile, System.Text.Encoding.UTF8);
            var seen = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var norm = EmailHelpers.Normalize(line);
                if (string.IsNullOrEmpty(norm)) continue;
                var key = EmailHelpers.CanonicalForDedup(norm, true);
                var cat = seen.Contains(key) ? "duplicate" : classifier.Classify(norm).Category;
                seen.Add(key);
                Console.WriteLine($"    {norm,-40} → {cat}");
            }
        }
        else
        {
            Console.WriteLine($"\n[4] Test file not found: {testFile}");
        }

        Console.WriteLine("\n[DONE] Press Enter...");
        Console.ReadLine();
    }
}
