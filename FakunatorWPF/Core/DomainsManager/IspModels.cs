namespace Fakunator.Core.DomainsManager;

/// <summary>
/// Аккаунт панели ISPmanager (или совместимой DNSmanager).
/// Хост может быть с портом (`panel.host.ru:1501`) — тогда порт не дописывается.
/// </summary>
public class IspAccount
{
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Provider { get; set; } = "ispmanager";
    /// <summary>Тип авторизации: "password" (обычный логин через API) или "firstvds" (только через WebView2, сессия не восстанавливается автоматически).</summary>
    public string AuthType { get; set; } = "password";
    /// <summary>Для AuthType=firstvds — id сервера в кабинете my.firstvds.ru (для повторного бесшовного входа).</summary>
    public string FirstVdsServerId { get; set; } = "";
    /// <summary>Кэш sessionId — не сериализуется, обновляется в рантайме после WebView2-логина.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CachedSessionId { get; set; }
}

/// <summary>Домен в панели ISPmanager (что возвращает `func=domain`).</summary>
public record IspDomain(
    string Name,
    string DomainType,
    string? IpAddress,
    string? Owner);

/// <summary>DNS-запись (что возвращает `func=domain.record&amp;elid=`).</summary>
public record DnsRecord(
    string Type,
    string Name,
    string Value,
    int Ttl,
    int? Priority,
    string? Rkey);
