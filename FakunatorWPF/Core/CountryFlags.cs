using System.Collections.Generic;

namespace Fakunator.Core;

/// <summary>
/// ISO 3166-1 alpha-2 code helpers: flag emoji + Russian country names.
/// </summary>
public static class CountryFlags
{
    /// <summary>Globe fallback emoji for unknown/invalid ISO codes.</summary>
    public const string Globe = "\U0001F310";

    /// <summary>
    /// Convert ISO 3166-1 alpha-2 code to a flag emoji.
    /// Each letter A-Z maps to a Regional Indicator Symbol (U+1F1E6..U+1F1FF);
    /// concatenating two of them produces the country flag glyph.
    /// </summary>
    /// <param name="iso">Two-letter country code (e.g. "RU"). "?" or invalid -> globe.</param>
    public static string GetFlag(string iso)
    {
        if (string.IsNullOrEmpty(iso) || iso == "?" || iso.Length != 2) return Globe;
        var upper = iso.ToUpperInvariant();
        if (upper[0] < 'A' || upper[0] > 'Z' || upper[1] < 'A' || upper[1] > 'Z') return Globe;
        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + (upper[0] - 'A')),
            char.ConvertFromUtf32(0x1F1E6 + (upper[1] - 'A'))
        );
    }

    /// <summary>
    /// Returns flag emoji + space + ISO code (e.g. "🇷🇺 RU"). For unknown/empty ISO returns "?".
    /// </summary>
    public static string GetFlagWithIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || iso == "?") return "?";
        return $"{GetFlag(iso)} {iso.ToUpperInvariant()}";
    }

    /// <summary>
    /// Country flag color (hex) — first color of the national flag, used for badges.
    /// </summary>
    public static readonly Dictionary<string, string> IsoToColor = new()
    {
        ["RU"] = "#0033A0", ["US"] = "#3C3B6E", ["UA"] = "#005BBB", ["DE"] = "#000000",
        ["FR"] = "#0055A4", ["GB"] = "#012169", ["IN"] = "#FF9933", ["BR"] = "#009C3B",
        ["IT"] = "#009246", ["ES"] = "#AA151B", ["PL"] = "#DC143C", ["TR"] = "#E30A17",
        ["NG"] = "#008751", ["JP"] = "#BC002D", ["CN"] = "#DE2910", ["KR"] = "#003478",
        ["CA"] = "#FF0000", ["AU"] = "#012169", ["NL"] = "#AE1C28", ["BY"] = "#CE1720",
        ["KZ"] = "#00ABC2", ["MX"] = "#006847", ["AR"] = "#74ACDF", ["ID"] = "#FF0000",
        ["PH"] = "#0038A8", ["SA"] = "#006C35", ["EG"] = "#CE1126", ["PK"] = "#01411C",
        ["BD"] = "#006A4E", ["VN"] = "#DA251D", ["TH"] = "#A51931", ["IR"] = "#239F40",
        ["IL"] = "#0038B8", ["SE"] = "#006AA7", ["NO"] = "#EF2B2D", ["DK"] = "#C8102E",
        ["FI"] = "#003580", ["CZ"] = "#11457E", ["PT"] = "#006600", ["GR"] = "#0D5EAF",
        ["RO"] = "#FCD116", ["BG"] = "#009B75", ["HU"] = "#477050", ["SK"] = "#0B4EA2",
        ["HR"] = "#171796", ["RS"] = "#C6363C", ["GE"] = "#FF0000", ["AM"] = "#D90012",
        ["AZ"] = "#0092BC", ["UZ"] = "#1EB53A", ["IE"] = "#169B62", ["CO"] = "#FCD116",
        ["PE"] = "#D91023", ["CL"] = "#0039A6", ["ZA"] = "#007749", ["KE"] = "#BB0000",
        ["GH"] = "#006B3F", ["MA"] = "#C1272D", ["CM"] = "#007A5E",
    };

    public static string GetColor(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "#71717a";
        return IsoToColor.TryGetValue(iso.ToUpperInvariant(), out var c) ? c : "#71717a";
    }

    /// <summary>
    /// ISO -> Russian country name dictionary. Used for full names in tooltips and bar charts.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> IsoToName = new Dictionary<string, string>
    {
        ["RU"] = "Россия",
        ["US"] = "США",
        ["UA"] = "Украина",
        ["DE"] = "Германия",
        ["FR"] = "Франция",
        ["GB"] = "Великобритания",
        ["IN"] = "Индия",
        ["BR"] = "Бразилия",
        ["IT"] = "Италия",
        ["ES"] = "Испания",
        ["PL"] = "Польша",
        ["TR"] = "Турция",
        ["NG"] = "Нигерия",
        ["JP"] = "Япония",
        ["CN"] = "Китай",
        ["KR"] = "Корея",
        ["CA"] = "Канада",
        ["AU"] = "Австралия",
        ["NL"] = "Нидерланды",
        ["BY"] = "Беларусь",
        ["KZ"] = "Казахстан",
        ["MX"] = "Мексика",
        ["AR"] = "Аргентина",
        ["ID"] = "Индонезия",
        ["PH"] = "Филиппины",
        ["SA"] = "Сауд. Аравия",
        ["EG"] = "Египет",
        ["PK"] = "Пакистан",
        ["BD"] = "Бангладеш",
        ["VN"] = "Вьетнам",
        ["TH"] = "Таиланд",
        ["IR"] = "Иран",
        ["IL"] = "Израиль",
        ["SE"] = "Швеция",
        ["NO"] = "Норвегия",
        ["DK"] = "Дания",
        ["FI"] = "Финляндия",
        ["CZ"] = "Чехия",
        ["PT"] = "Португалия",
        ["GR"] = "Греция",
        ["RO"] = "Румыния",
        ["BG"] = "Болгария",
        ["HU"] = "Венгрия",
        ["SK"] = "Словакия",
        ["HR"] = "Хорватия",
        ["RS"] = "Сербия",
        ["GE"] = "Грузия",
        ["AM"] = "Армения",
        ["AZ"] = "Азербайджан",
        ["UZ"] = "Узбекистан",
        ["IE"] = "Ирландия",
        ["CO"] = "Колумбия",
        ["PE"] = "Перу",
        ["CL"] = "Чили",
        ["ZA"] = "ЮАР",
        ["KE"] = "Кения",
        ["GH"] = "Гана",
        ["MA"] = "Марокко",
        ["CM"] = "Камерун",
    };

    /// <summary>Full Russian country name (or the ISO code if not in the map).</summary>
    public static string GetName(string iso) =>
        !string.IsNullOrEmpty(iso) && IsoToName.TryGetValue(iso, out var n) ? n : iso;
}
