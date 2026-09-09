using System;
using System.Collections.Generic;

namespace Fakunator.Core.Server;

/// <summary>
/// Настроенный (или устанавливаемый) mail-сервер. Хранится в реестре, читается в UI-таблицу.
/// </summary>
public sealed class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";     // человекочитаемое, обычно = Domain
    public string Domain { get; set; } = "";   // основной домен, напр. w00w.site
    public string Ip { get; set; } = "";       // IPv4
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "root";
    public string SshPasswordEnc { get; set; } = ""; // DPAPI-шифрованный, base64
    public string LetsEncryptEmail { get; set; } = "";
    public string OsRelease { get; set; } = "";      // "Ubuntu 22.04.4 LTS"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? InstalledAt { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.Unknown;
    public bool IsFavorite { get; set; }
    public InstallOptions LastInstallOptions { get; set; } = new();

    // Сгенерированные при установке случайные поддомены/креды (для карточки Готово)
    public Credentials Credentials { get; set; } = new();
}

public enum ServerStatus
{
    Unknown,
    Installing,   // идёт установка
    Online,       // все ключевые сервисы active
    Degraded,     // часть сервисов упала
    Offline,      // ssh не отвечает
    Failed        // установка провалилась
}

public sealed class InstallOptions
{
    // Base (всегда true, но помечены для symmetry)
    public bool Postfix   { get; set; } = true;
    public bool Dovecot   { get; set; } = true;
    public bool MariaDB   { get; set; } = true;
    public bool OpenDkim  { get; set; } = true;
    public bool Bind9     { get; set; } = true;
    // Web
    public bool Apache    { get; set; } = true;
    public bool Certbot   { get; set; } = true;
    public bool Roundcube { get; set; } = true;
    public bool PhpMyAdmin { get; set; } = true;
    // Extras (URL-fields)
    public bool Pmta      { get; set; } = true;
    public bool Ztds      { get; set; } = true;
    public bool Mailer    { get; set; } = true;
    public bool Proxy3    { get; set; } = true;
    public bool Landing   { get; set; } = true;
    public bool AmsStats  { get; set; } = true;

    public string PmtaDebUrl { get; set; } = "";
    public string PmtadUrl { get; set; } = "";
    public string PmtaHttpdUrl { get; set; } = "";
    public string PmtaLicenseUrl { get; set; } = "";
    public string ZtdsUrl { get; set; } = "";
    public string MailerUrl { get; set; } = "";
    public string LandingUrl { get; set; } = "";
}

public sealed class Credentials
{
    public string PmtaMonitorUrl { get; set; } = "";
    public string PmtaMonitorUser { get; set; } = "";
    public string PmtaMonitorPass { get; set; } = "";

    /// <summary>Логин/пароль авторизованного SMTP-релея PMTA (source {ext-relay}, require-auth) —
    /// для скриптов-отправителей, которые шлют не с 127.0.0.1. Порт тот же, что и smtp-listener.</summary>
    public string PmtaSmtpUser { get; set; } = "";
    public string PmtaSmtpPass { get; set; } = "";
    public string PmtaSmtpPort { get; set; } = "";

    public string WebmailUrl { get; set; } = "";
    public string WebmailAdminEmail { get; set; } = "";
    public string WebmailAdminPass { get; set; } = "";
    /// <summary>abuse@/info@/sender@/mail@ и т.п. — алиасы, письма на них падают в WebmailAdminEmail.</summary>
    public List<string> MailAliases { get; set; } = new();
    /// <summary>Хостнейм из Postfix myhostname/HELO — то, что нужно прописать в PTR (reverse DNS) у хостера.</summary>
    public string PtrHostname { get; set; } = "";

    public string PhpMyAdminUrl { get; set; } = "";
    public string PhpMyAdminUser { get; set; } = "";
    public string PhpMyAdminPass { get; set; } = "";

    public string TdsUrl { get; set; } = "";
    public string TdsUser { get; set; } = "";
    public string TdsPass { get; set; } = "";

    public string MysqlRootPass { get; set; } = "";

    public string Proxy3Url { get; set; } = "";
    public string Proxy3User { get; set; } = "";
    public string Proxy3Pass { get; set; } = "";

    public string LandingUrl { get; set; } = "";
    public string DkimPublicKey { get; set; } = "";  // p= часть для DNS
    public List<DnsRecord> DnsRecords { get; set; } = new();

    public string AmsStatsUrl { get; set; } = "";    // https://domain/ams/amsweb.php
    public string AmsStatsUrlIp { get; set; } = "";  // http://ip/ams/amsweb.php
    public string AmsStatsPassword { get; set; } = "";
    public string AmsStatsPath { get; set; } = "";   // /var/www/html/domain/ams
}

public sealed class DnsRecord
{
    public string Type { get; set; } = "";   // A, MX, TXT
    public string Name { get; set; } = "";   // @, mail, default._domainkey
    public string Value { get; set; } = "";
    public int? Priority { get; set; }       // for MX
}
