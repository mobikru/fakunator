using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Fakunator.Core;

public static class Tokens
{
    // ── Category palette ──────────────────────────────────────────────
    public static readonly Dictionary<string, string> CategoryColors = new()
    {
        ["clean"]      = "#22c55e",
        ["duplicate"]  = "#eab308",
        ["invalid"]    = "#ef4444",
        ["reserved"]   = "#9ca3af",
        ["trap"]       = "#dc2626",
        ["role"]       = "#a855f7",
        ["disposable"] = "#f97316",
        ["corporate"]  = "#3b82f6",
        ["typo"]       = "#06b6d4",
    };

    public static readonly Dictionary<string, string> CategoryLabels = new()
    {
        ["clean"]      = "Валидные",
        ["duplicate"]  = "Дубликаты",
        ["invalid"]    = "Битый синтаксис",
        ["reserved"]   = "Зарезервированные",
        ["trap"]       = "Спам-ловушки",
        ["role"]       = "Ролевые",
        ["disposable"] = "Одноразовые",
        ["corporate"]  = "Корпоративные",
        ["typo"]       = "Опечатки",
    };

    public static readonly Dictionary<string, string> CategoryHints = new()
    {
        ["clean"]      = "итоговая база",
        ["duplicate"]  = "учтены правила gmail",
        ["invalid"]    = "не проходит RFC 5322",
        ["reserved"]   = ".test .invalid .localhost",
        ["trap"]       = "honeypot и trap-домены",
        ["role"]       = "info@ support@ admin@",
        ["disposable"] = "mailinator, 10minutemail",
        ["corporate"]  = "не публичный домен",
        ["typo"]       = "gmial.com → gmail.com",
    };

    public static readonly string[] CategoryOrder =
    [
        "clean", "duplicate", "invalid", "reserved",
        "trap", "role", "disposable", "corporate", "typo"
    ];

    // ── Provider palette ──────────────────────────────────────────────
    public static readonly Dictionary<string, string> ProviderColors = new()
    {
        ["gmail"]   = "#ea4335",
        ["yahoo"]   = "#6001d2",
        ["outlook"] = "#0078d4",
        ["icloud"]  = "#a2aaad",
        ["mailru"]  = "#005ff9",
        ["yandex"]  = "#fc3f1d",
        ["proton"]  = "#6d4aff",
        ["other"]   = "#64748b",
    };

    public static readonly Dictionary<string, string> ProviderLabels = new()
    {
        ["gmail"]   = "gmail",
        ["yahoo"]   = "yahoo",
        ["outlook"] = "outlook",
        ["icloud"]  = "icloud",
        ["mailru"]  = "mail.ru",
        ["yandex"]  = "yandex",
        ["proton"]  = "proton",
        ["other"]   = "другие",
    };

    public static readonly Dictionary<string, string> DomainToProvider = new()
    {
        // Gmail
        ["gmail.com"] = "gmail", ["googlemail.com"] = "gmail",
        // Yahoo
        ["yahoo.com"] = "yahoo", ["ymail.com"] = "yahoo", ["rocketmail.com"] = "yahoo",
        ["yahoo.co.uk"] = "yahoo", ["yahoo.fr"] = "yahoo", ["yahoo.com.br"] = "yahoo",
        ["yahoo.de"] = "yahoo", ["yahoo.es"] = "yahoo", ["yahoo.it"] = "yahoo",
        ["yahoo.com.au"] = "yahoo", ["yahoo.com.mx"] = "yahoo",
        ["yahoo.ca"] = "yahoo", ["yahoo.co.in"] = "yahoo", ["yahoo.co.jp"] = "yahoo",
        // Microsoft
        ["hotmail.com"] = "outlook", ["outlook.com"] = "outlook", ["live.com"] = "outlook",
        ["msn.com"] = "outlook", ["hotmail.co.uk"] = "outlook", ["outlook.co.uk"] = "outlook",
        ["live.co.uk"] = "outlook", ["hotmail.fr"] = "outlook", ["hotmail.it"] = "outlook",
        ["hotmail.es"] = "outlook", ["live.it"] = "outlook", ["live.es"] = "outlook",
        ["hotmail.com.br"] = "outlook", ["outlook.com.br"] = "outlook",
        ["live.com.br"] = "outlook", ["hotmail.com.mx"] = "outlook",
        // Apple
        ["icloud.com"] = "icloud", ["me.com"] = "icloud", ["mac.com"] = "icloud",
        // Mail.ru
        ["mail.ru"] = "mailru", ["inbox.ru"] = "mailru", ["list.ru"] = "mailru",
        ["bk.ru"] = "mailru", ["internet.ru"] = "mailru",
        // Yandex
        ["yandex.ru"] = "yandex", ["yandex.com"] = "yandex", ["ya.ru"] = "yandex",
        ["yandex.by"] = "yandex", ["yandex.kz"] = "yandex", ["yandex.ua"] = "yandex",
        // Proton
        ["protonmail.com"] = "proton", ["proton.me"] = "proton", ["pm.me"] = "proton",
    };

    // ── Accent ────────────────────────────────────────────────────────
    public const string Accent      = "#5e6ad2";
    public const string Accent2     = "#818cf8";
    public const string AccentHover = "#6b75d8";
    public const string AccentSoft  = "#0F5e6ad2"; // 6% alpha in ARGB hex

    // ── Theme palettes ────────────────────────────────────────────────
    public record Palette(
        string Bg0, string Bg1, string Bg2, string Bg3, string Bg4, string Bg5,
        string Border1, string Border2, string Border3,
        string Fg1, string Fg2, string Fg3, string Fg4);

    public static readonly Palette Dark = new(
        Bg0: "#0a0a0c", Bg1: "#111114", Bg2: "#16161a", Bg3: "#1c1c21",
        Bg4: "#232329", Bg5: "#2c2c33",
        Border1: "#0F_FFFFFF", Border2: "#1A_FFFFFF", Border3: "#2E_FFFFFF",
        Fg1: "#f4f4f5", Fg2: "#a1a1aa", Fg3: "#71717a", Fg4: "#52525b");

    public static readonly Palette Light = new(
        Bg0: "#ffffff", Bg1: "#fafafa", Bg2: "#f4f4f5", Bg3: "#ffffff",
        Bg4: "#f4f4f5", Bg5: "#e4e4e7",
        Border1: "#0F_000000", Border2: "#1A_000000", Border3: "#29_000000",
        Fg1: "#18181b", Fg2: "#52525b", Fg3: "#71717a", Fg4: "#a1a1aa");

    // ── Typography ────────────────────────────────────────────────────
    public const string FontUI   = "Segoe UI Variable";
    public const string FontMono = "JetBrains Mono";
}
