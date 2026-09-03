using System.Collections.Generic;

namespace Fakunator.Core;

public record SmtpVerdict(
    string Email,
    string Verdict,
    string MxHost = "",
    int ReplyCode = 0,
    string ReplyText = "",
    string ViaProxy = "",
    string Error = "",
    double ElapsedMs = 0);

public static class Verdicts
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Catchall = "catchall";
    public const string Unknown = "unknown";
    public const string Error = "error";

    public static readonly string[] Order = { Valid, Invalid, Catchall, Unknown, Error };

    public static readonly Dictionary<string, string> Labels = new()
    {
        [Valid] = "Валидные",
        [Invalid] = "Недействительные",
        [Catchall] = "Catch-all",
        [Unknown] = "Неизвестные",
        [Error] = "Ошибки",
    };

    public static readonly Dictionary<string, string> Colors = new()
    {
        [Valid] = "#22c55e",
        [Invalid] = "#ef4444",
        [Catchall] = "#eab308",
        [Unknown] = "#3b82f6",
        [Error] = "#71717a",
    };
}
