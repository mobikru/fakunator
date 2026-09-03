namespace Fakunator.Core.Postmaster;

/// <summary>
/// Аккаунт mail.ru, привязанный к Postmaster.
/// RefreshToken — для OAuth API (чтение статистики, verified list, troubles).
/// Password — для web-сессии постмастера (добавление домена, верификация) —
/// публичный OAuth API этого не даёт, только веб-форма с CSRF, а она требует
/// залогиненную сессию. Пароль хранится в plain в config.json — юзер согласен.
/// </summary>
public class PostmasterAccount
{
    public string Username { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Password { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
