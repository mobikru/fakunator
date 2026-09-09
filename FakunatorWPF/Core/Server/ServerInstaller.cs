using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Fakunator.Core.Server;

/// <summary>
/// Полное развёртывание mail-сервера: apt пакеты, MySQL, конфиги, OpenDKIM ключи,
/// Bind9 зона, Apache vhosts, Certbot SSL, BYO PMTA/zTDS/mailer/landing.
/// </summary>
public sealed class ServerInstaller : IDisposable
{
    public event Action<InstallEvent>? Event;

    private readonly ServerConfig _srv;
    private readonly InstallOptions _opts;
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private readonly CancellationTokenSource _cts = new();

    // Сгенерированные при установке — сохраняются в srv.Credentials
    private string _mysqlRootPass = "";
    private string _mysqlMailPass = "";
    private string _mysqlServerPass = "";
    private string _mysqlRoundcubePass = "";
    private string _webmailAdminPass = "";
    private string _webmailSub = "";     // случайный поддомен для webmail
    private string _tdsSub = "";         // случайный поддомен для TDS
    private string _dkimPublicKey = "";  // для DNS TXT

    public ServerInstaller(ServerConfig srv, InstallOptions opts)
    {
        _srv = srv;
        _opts = opts;
    }

    public void Cancel() => _cts.Cancel();

    public async Task<bool> RunAsync()
    {
        _webmailSub = RandomSub(7);
        _tdsSub = RandomSub(7);
        _mysqlRootPass = RandomPass(20);
        _mysqlMailPass = RandomPass(20);
        _mysqlServerPass = RandomPass(20);
        _mysqlRoundcubePass = RandomPass(20);
        _webmailAdminPass = RandomPass(14);

        // Сохраняем креды СРАЗУ — даже если установка упадёт, пароли не потеряются
        _srv.Credentials = new Credentials
        {
            MysqlRootPass = _mysqlRootPass,
            WebmailAdminEmail = $"admin@{_srv.Domain}",
            WebmailAdminPass = _webmailAdminPass,
            WebmailUrl = $"https://{_webmailSub}.{_srv.Domain}/webmail/",
            PhpMyAdminUrl = $"https://{_srv.Domain}/phpMyAdmin",
            PhpMyAdminUser = "root",
            PhpMyAdminPass = _mysqlRootPass,
            TdsUrl = $"https://{_tdsSub}.{_srv.Domain}/TDS/admin.php",
            TdsUser = "admin",
            TdsPass = _webmailAdminPass,
            LandingUrl = $"https://{_srv.Domain}/",
            MailAliases = MailAliasLocalParts.Select(a => $"{a}@{_srv.Domain}").ToList(),
            PtrHostname = $"{_webmailSub}.{_srv.Domain}",
        };

        try
        {
            var pass = ServerRegistry.DecryptPassword(_srv.SshPasswordEnc);
            _ssh = new SshClient(_srv.Ip, _srv.SshPort, _srv.SshUser, pass);
            _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            _ssh.Connect();
            _sftp = new SftpClient(_srv.Ip, _srv.SshPort, _srv.SshUser, pass);
            _sftp.Connect();
            Emit(EventLevel.Ok, $"SSH+SFTP подключение установлено ({_srv.SshUser}@{_srv.Ip}:{_srv.SshPort})");

            // Сначала чиним любые сломанные пакеты от предыдущих установок
            await Phase("apt update", "dpkg --configure -a 2>/dev/null; apt-get -f install -y 2>/dev/null; apt update -y && DEBIAN_FRONTEND=noninteractive apt upgrade -y");

            await Phase("apt install базовых пакетов",
                "DEBIAN_FRONTEND=noninteractive apt install -y " +
                "postfix postfix-mysql dovecot-core dovecot-imapd dovecot-lmtpd dovecot-pop3d dovecot-mysql " +
                "mariadb-server opendkim opendkim-tools bind9 bind9-utils " +
                "apache2 libapache2-mod-php php php-mysql php-mbstring php-curl php-imap php-gd php-xml " +
                "php-sqlite3 " + // zTDS требует SQLite3 extension для админ БД
                "certbot python3-certbot-apache wget curl unzip bsd-mailx");

            await MySqlSetupPhase();
            await ApplyPostfixConfigs();
            await ApplyDovecotConfigs();
            await ApplyOpenDkimAndGenKey();
            await ApplyBind9Zone();
            await ApplyApacheVhosts();
            if (_opts.PhpMyAdmin) await PhpMyAdminPhase();
            if (_opts.Roundcube) await RoundcubePhase();
            if (_opts.Certbot) await CertbotPhase();
            if (_opts.Pmta) await PmtaByoPhase();
            if (_opts.Pmta) await PmtaConfigAndStartPhase();
            if (_opts.Ztds) await ZtdsByoPhase();
            if (_opts.Mailer) await MailerByoPhase();
            if (_opts.Landing) await LandingByoPhase();
            if (_opts.Proxy3) await Apply3ProxyPhase();
            if (_opts.AmsStats) await AmsStatsPhase();

            // Финализация — дозаполняем поля которые появились только в процессе установки
            _srv.Credentials.DkimPublicKey = _dkimPublicKey;
            _srv.Credentials.PmtaMonitorUrl = _opts.Pmta ? $"http://{_srv.Ip}:8080" : "";
            _srv.Credentials.PmtaMonitorUser = _opts.Pmta ? "admin" : "";
            _srv.Credentials.PmtaMonitorPass = _opts.Pmta ? _mysqlMailPass : "";
            _srv.Credentials.DnsRecords = new List<DnsRecord>
            {
                new() { Type="A",   Name="@",     Value=_srv.Ip },
                new() { Type="A",   Name="mail",  Value=_srv.Ip },
                new() { Type="A",   Name=_webmailSub, Value=_srv.Ip },
                new() { Type="A",   Name=_tdsSub, Value=_srv.Ip },
                new() { Type="MX",  Name="@",     Value="mail." + _srv.Domain, Priority=10 },
                new() { Type="TXT", Name="@",     Value=$"v=spf1 ip4:{_srv.Ip} ~all" },
                new() { Type="TXT", Name="default._domainkey", Value=_dkimPublicKey },
                new() { Type="TXT", Name="_dmarc", Value=$"v=DMARC1; p=quarantine; rua=mailto:info@{_srv.Domain}" },
            };
            _srv.InstalledAt = DateTime.UtcNow;
            _srv.Status = ServerStatus.Online;
            Emit(EventLevel.Ok, "🎉 Установка завершена успешно");
            return true;
        }
        catch (OperationCanceledException)
        {
            Emit(EventLevel.Warn, "Отменено пользователем");
            _srv.Status = ServerStatus.Failed;
            return false;
        }
        catch (Exception ex)
        {
            Emit(EventLevel.Error, $"Установка провалилась: {ex.Message}");
            _srv.Status = ServerStatus.Failed;
            return false;
        }
        finally
        {
            try { _sftp?.Disconnect(); _sftp?.Dispose(); } catch { }
            try { _ssh?.Disconnect();  _ssh?.Dispose();  } catch { }
        }
    }

    // ── ФАЗЫ ────────────────────────────────────────────────

    private async Task MySqlSetupPhase()
    {
        Emit(EventLevel.Phase, "MySQL setup");
        // MariaDB на Ubuntu 22 использует unix_socket для root@localhost. Правильный способ
        // переключить на password auth: IDENTIFIED VIA mysql_native_password USING PASSWORD(...).
        // Сначала работаем через unix_socket (mysql без -u -p), потом пишем /root/.my.cnf для
        // всех последующих команд (mysql без параметров подхватит).
        var setPass = $@"
set -e
mysql <<'EOF'
ALTER USER 'root'@'localhost' IDENTIFIED VIA mysql_native_password USING PASSWORD('{_mysqlRootPass}');
FLUSH PRIVILEGES;
EOF
cat > /root/.my.cnf <<'EOF'
[client]
user=root
password={_mysqlRootPass}
EOF
chmod 600 /root/.my.cnf
";
        await ExecScript(setPass);
        Emit(EventLevel.Ok, "✓ MySQL: root пароль установлен, /root/.my.cnf создан");

        // Дальше mysql без параметров работает через /root/.my.cnf
        var setup = $@"
set -e
mysql -e ""DELETE FROM mysql.user WHERE User=''; DROP DATABASE IF EXISTS test; FLUSH PRIVILEGES;""
mysql -e ""CREATE DATABASE IF NOT EXISTS postfix; CREATE DATABASE IF NOT EXISTS server; CREATE DATABASE IF NOT EXISTS roundcubemail;""
mysql -e ""CREATE USER IF NOT EXISTS 'admin'@'localhost' IDENTIFIED BY '{_mysqlMailPass}'; ALTER USER 'admin'@'localhost' IDENTIFIED BY '{_mysqlMailPass}'; GRANT ALL PRIVILEGES ON postfix.* TO 'admin'@'localhost'; GRANT ALL PRIVILEGES ON server.* TO 'admin'@'localhost'; FLUSH PRIVILEGES;""
mysql -e ""CREATE USER IF NOT EXISTS 'roundcube'@'localhost' IDENTIFIED BY '{_mysqlRoundcubePass}'; ALTER USER 'roundcube'@'localhost' IDENTIFIED BY '{_mysqlRoundcubePass}'; GRANT ALL ON roundcubemail.* TO 'roundcube'@'localhost'; FLUSH PRIVILEGES;""
";
        await ExecScript(setup);
        Emit(EventLevel.Ok, "✓ MySQL: 3 databases + admin/roundcube users готовы");

        // Импорт schema postfix (через /root/.my.cnf — без пароля-параметра)
        var postfixSchema = TemplateEngine.Render("sql/postfix_schema.sql", new Dictionary<string, string>());
        await UploadFile("/tmp/postfix_schema.sql", postfixSchema);
        await ExecScript("mysql postfix < /tmp/postfix_schema.sql");

        var serverSchema = TemplateEngine.Render("sql/server_schema.sql", new Dictionary<string, string>());
        await UploadFile("/tmp/server_schema.sql", serverSchema);
        await ExecScript("mysql server < /tmp/server_schema.sql");

        // Хэшируем пароль admin юзера через doveadm pw (dovecot уже установлен)
        var pwHashRaw = await ExecCapture($"doveadm pw -s SHA512-CRYPT -p '{_webmailAdminPass}'");
        var pwHash = pwHashRaw.Trim().Replace("'", "''");
        var domainSql = _srv.Domain.Replace("'", "''");

        // ВАЖНО: пишем SQL в файл через SFTP, а не inline в mysql -e "..." —
        // хэш вида $6$salt$body внутри двойных кавычек bash подставляет как переменные,
        // из-за чего в БД попадёт обрезанный хэш и dovecot auth будет вечно fail.
        var seedSql =
            $"INSERT IGNORE INTO virtual_domains(name) VALUES('{domainSql}');\n" +
            $"INSERT IGNORE INTO virtual_users(domain_id, email, password) " +
            $"SELECT id, 'admin@{domainSql}', '{pwHash}' FROM virtual_domains " +
            $"WHERE name='{domainSql}' LIMIT 1;\n";
        foreach (var alias in MailAliasLocalParts)
            seedSql +=
                $"INSERT IGNORE INTO virtual_aliases(domain_id, source, destination) " +
                $"SELECT id, '{alias}@{domainSql}', 'admin@{domainSql}' FROM virtual_domains " +
                $"WHERE name='{domainSql}' LIMIT 1;\n";
        await UploadFile("/tmp/postfix_seed.sql", seedSql);
        await ExecScript("mysql postfix < /tmp/postfix_seed.sql && rm -f /tmp/postfix_seed.sql");
        Emit(EventLevel.Ok, $"✓ MySQL: schemas + admin@domain юзер + {MailAliasLocalParts.Length} алиасов ({string.Join("/", MailAliasLocalParts)}) созданы");
    }

    /// <summary>Локальные части алиасов, которые заводятся на admin@domain при установке
    /// (abuse@ — для FBL/жалоб, остальные — общие адреса для отправки/приёма).</summary>
    private static readonly string[] MailAliasLocalParts = { "abuse", "info", "sender", "mail" };

    private async Task ApplyPostfixConfigs()
    {
        Emit(EventLevel.Phase, "Postfix configs");
        var vars = SubVars();
        foreach (var (local, remote) in new[]
        {
            ("postfix/main.cf",   "/etc/postfix/main.cf"),
            ("postfix/master.cf", "/etc/postfix/master.cf"),
            ("postfix/mysql-virtual-alias-maps.cf",    "/etc/postfix/mysql-virtual-alias-maps.cf"),
            ("postfix/mysql-virtual-mailbox-domains.cf","/etc/postfix/mysql-virtual-mailbox-domains.cf"),
            ("postfix/mysql-virtual-mailbox-maps.cf",   "/etc/postfix/mysql-virtual-mailbox-maps.cf"),
        })
        {
            var text = TemplateEngine.Render(local, vars);
            await UploadFile(remote, text);
        }
        await ExecScript("chmod 640 /etc/postfix/mysql-virtual-*.cf && chown root:postfix /etc/postfix/mysql-virtual-*.cf && systemctl restart postfix");
        Emit(EventLevel.Ok, "✓ Postfix: конфиги применены, сервис перезапущен");
    }

    private async Task ApplyDovecotConfigs()
    {
        Emit(EventLevel.Phase, "Dovecot configs");
        var vars = SubVars();
        foreach (var (local, remote) in new[]
        {
            ("dovecot/dovecot.conf",             "/etc/dovecot/dovecot.conf"),
            ("dovecot/dovecot-sql.conf.ext",     "/etc/dovecot/dovecot-sql.conf.ext"),
            ("dovecot/conf.d/10-auth.conf",      "/etc/dovecot/conf.d/10-auth.conf"),
            ("dovecot/conf.d/10-mail.conf",      "/etc/dovecot/conf.d/10-mail.conf"),
            ("dovecot/conf.d/10-master.conf",    "/etc/dovecot/conf.d/10-master.conf"),
            ("dovecot/conf.d/10-ssl.conf",       "/etc/dovecot/conf.d/10-ssl.conf"),
        })
        {
            var text = TemplateEngine.Render(local, vars);
            await UploadFile(remote, text);
        }
        // vhosts directory + static userdb + snakeoil SSL fallback (до certbot)
        await ExecScript($@"
mkdir -p /var/mail/vhosts
groupadd -f -g 5000 vmail
id -u vmail >/dev/null 2>&1 || useradd -g vmail -u 5000 vmail -d /var/mail/vhosts -s /bin/false
chown -R vmail:vmail /var/mail/vhosts
chmod 600 /etc/dovecot/dovecot-sql.conf.ext
chown root:root /etc/dovecot/dovecot-sql.conf.ext

# Static userdb — SQL passdb + static userdb (uid/gid из vmail, home = /var/mail/vhosts/DOMAIN/USER)
if ! grep -q 'driver = static' /etc/dovecot/dovecot.conf; then
cat >> /etc/dovecot/dovecot.conf <<'EOF'

# Static userdb для virtual mailboxes (vmail user, uid=5000)
userdb {{
  driver = static
  args = uid=5000 gid=5000 home=/var/mail/vhosts/%d/%n
}}
EOF
fi

# Snakeoil SSL fallback — dovecot 10-ssl.conf требует LE cert, но certbot ещё не отработал
mkdir -p /etc/letsencrypt/live/{_webmailSub}.{_srv.Domain}
[ -e /etc/letsencrypt/live/{_webmailSub}.{_srv.Domain}/fullchain.pem ] || ln -sf /etc/ssl/certs/ssl-cert-snakeoil.pem /etc/letsencrypt/live/{_webmailSub}.{_srv.Domain}/fullchain.pem
[ -e /etc/letsencrypt/live/{_webmailSub}.{_srv.Domain}/privkey.pem ]   || ln -sf /etc/ssl/private/ssl-cert-snakeoil.key /etc/letsencrypt/live/{_webmailSub}.{_srv.Domain}/privkey.pem

systemctl restart dovecot
sleep 1
systemctl is-active dovecot
");
        Emit(EventLevel.Ok, "✓ Dovecot: конфиги + vmail юзер + static userdb + SSL fallback");
    }

    private async Task ApplyOpenDkimAndGenKey()
    {
        Emit(EventLevel.Phase, "OpenDKIM ключ + конфиг");
        var vars = SubVars();
        var opendkimConf = TemplateEngine.Render("opendkim/opendkim.conf", vars);
        await UploadFile("/etc/opendkim.conf", opendkimConf);
        var opendkimDefault = TemplateEngine.Render("opendkim/opendkim.default", vars);
        await UploadFile("/etc/default/opendkim", opendkimDefault);

        var setup = $@"
mkdir -p /etc/opendkim/keys/{_srv.Domain}
cd /etc/opendkim/keys/{_srv.Domain} && opendkim-genkey -s default -d {_srv.Domain}
chown -R opendkim:opendkim /etc/opendkim
echo 'default._domainkey.{_srv.Domain} {_srv.Domain}:default:/etc/opendkim/keys/{_srv.Domain}/default.private' > /etc/opendkim/KeyTable
echo '*@{_srv.Domain} default._domainkey.{_srv.Domain}' > /etc/opendkim/SigningTable
echo '127.0.0.1
localhost
*.{_srv.Domain}
{_srv.Domain}' > /etc/opendkim/TrustedHosts
adduser postfix opendkim 2>/dev/null || true
systemctl restart opendkim
";
        await ExecScript(setup);

        // Читаем публичную часть ключа для DNS
        var pubKey = await ExecCapture($"cat /etc/opendkim/keys/{_srv.Domain}/default.txt");
        _dkimPublicKey = ExtractDkimPValue(pubKey);
        Emit(EventLevel.Ok, "✓ OpenDKIM: ключ сгенерирован, публичный сохранён для DNS");
    }

    private async Task ApplyBind9Zone()
    {
        Emit(EventLevel.Phase, "Bind9 zone");
        var vars = SubVars();
        var zone = TemplateEngine.Render("bind/db.domain.template", vars);
        await UploadFile($"/etc/bind/db.{_srv.Domain}", zone);

        var namedLocal = $@"
zone ""{_srv.Domain}"" {{
    type master;
    file ""/etc/bind/db.{_srv.Domain}"";
}};
";
        await UploadFile("/etc/bind/named.conf.local", namedLocal);
        await ExecScript("systemctl restart bind9");
        Emit(EventLevel.Ok, "✓ Bind9: zone для домена загружен");
    }

    private async Task ApplyApacheVhosts()
    {
        Emit(EventLevel.Phase, "Apache vhosts");
        var vars = SubVars();
        var domainVhost = TemplateEngine.Render("apache/domain.conf", vars);
        await UploadFile($"/etc/apache2/sites-available/{_srv.Domain}.conf", domainVhost);

        var webmailVhost = TemplateEngine.Render("apache/subdomain.conf",
            new Dictionary<string, string>(vars) { ["nserren.w00w.site"] = $"{_webmailSub}.{_srv.Domain}" });
        await UploadFile($"/etc/apache2/sites-available/{_webmailSub}.{_srv.Domain}.conf", webmailVhost);

        // TDS vhost (для 3-го поддомена)
        var tdsVhost = TemplateEngine.Render("apache/subdomain.conf",
            new Dictionary<string, string>(vars) { ["nserren.w00w.site"] = $"{_tdsSub}.{_srv.Domain}" });
        await UploadFile($"/etc/apache2/sites-available/{_tdsSub}.{_srv.Domain}.conf", tdsVhost);

        var setup = $@"
mkdir -p /var/www/html/{_srv.Domain} /var/www/html/{_webmailSub}.{_srv.Domain} /var/www/html/{_tdsSub}.{_srv.Domain}
echo '<h1>{_srv.Domain}</h1>' > /var/www/html/{_srv.Domain}/index.html
chown -R www-data:www-data /var/www/html
a2enmod ssl rewrite headers
a2ensite {_srv.Domain}.conf {_webmailSub}.{_srv.Domain}.conf {_tdsSub}.{_srv.Domain}.conf
a2dissite 000-default.conf 2>/dev/null || true
systemctl enable apache2
systemctl restart apache2
";
        await ExecScript(setup);
        Emit(EventLevel.Ok, "✓ Apache: 3 vhosts (main + webmail + tds), ssl/rewrite/headers включены");
    }

    private async Task CertbotPhase()
    {
        Emit(EventLevel.Phase, "Certbot SSL");
        var email = string.IsNullOrEmpty(_srv.LetsEncryptEmail) ? $"info@{_srv.Domain}" : _srv.LetsEncryptEmail;
        var hosts = new[] { _srv.Domain, $"{_webmailSub}.{_srv.Domain}", $"{_tdsSub}.{_srv.Domain}" };

        // Пробуем один общий SAN cert — работает если у всех доменов есть DNS (wildcard A)
        var allDs = string.Join(" ", hosts.Select(h => $"-d {h}"));
        var oneShot = $"certbot --apache --non-interactive --agree-tos --email {email} {allDs} --expand 2>&1";
        try
        {
            await ExecScript(oneShot);
            Emit(EventLevel.Ok, $"✓ Certbot: SSL выдан для всех 3 доменов одним SAN cert");
            return;
        }
        catch (Exception ex)
        {
            Emit(EventLevel.Warn, $"⚠ Общий cert fail ({ex.Message.Split('\n').First()}). Пробую по одному…");
        }

        // Fallback — по одному, если общий SAN cert не прошёл (у одного домена нет DNS)
        int ok = 0;
        foreach (var host in hosts)
        {
            try
            {
                await ExecScript($"certbot --apache --non-interactive --agree-tos --email {email} -d {host} 2>&1");
                Emit(EventLevel.Ok, $"  ✓ SSL для {host}");
                ok++;
            }
            catch (Exception ex)
            {
                Emit(EventLevel.Warn, $"  ⚠ {host} — {ex.Message.Split('\n').First()}");
            }
        }
        Emit(ok > 0 ? EventLevel.Ok : EventLevel.Warn,
            ok > 0 ? $"✓ Certbot: {ok}/{hosts.Length} доменов получили SSL" : "Certbot: ни одного SSL не выдано (нет DNS?)");
    }

    private async Task PmtaByoPhase()
    {
        Emit(EventLevel.Phase, "PMTA (BYO)");
        if (string.IsNullOrWhiteSpace(_opts.PmtaDebUrl) ||
            string.IsNullOrWhiteSpace(_opts.PmtadUrl) ||
            string.IsNullOrWhiteSpace(_opts.PmtaHttpdUrl) ||
            string.IsNullOrWhiteSpace(_opts.PmtaLicenseUrl))
        {
            Emit(EventLevel.Warn, "PMTA URLs пустые — пропускаем");
            return;
        }
        var cmd = $@"
set -e
# ВАЖНО: создаём user pmta ДО dpkg -i — postinst скрипт .deb хочет chown pmta:pmta
id pmta >/dev/null 2>&1 || useradd -r -s /bin/false pmta
mkdir -p /tmp/pmta
cd /tmp/pmta
wget -q '{_opts.PmtaDebUrl}' -O pmta.deb
wget -q '{_opts.PmtadUrl}' -O pmtad
wget -q '{_opts.PmtaHttpdUrl}' -O pmtahttpd
wget -q '{_opts.PmtaLicenseUrl}' -O license
DEBIAN_FRONTEND=noninteractive dpkg -i /tmp/pmta/pmta.deb || apt-get install -f -y
rm -f /usr/sbin/pmtad /usr/sbin/pmtahttpd
cp /tmp/pmta/pmtad /usr/sbin/pmtad
cp /tmp/pmta/pmtahttpd /usr/sbin/pmtahttpd
mkdir -p /etc/pmta
cp /tmp/pmta/license /etc/pmta/license
chmod 755 /usr/sbin/pmtad /usr/sbin/pmtahttpd
rm -rf /tmp/pmta
";
        await ExecScript(cmd);
        Emit(EventLevel.Ok, "✓ PMTA: .deb + cracked бинари + license установлены");
    }

    private async Task PhpMyAdminPhase()
    {
        Emit(EventLevel.Phase, "phpMyAdmin");
        // Preseed чтобы избежать interactive prompt
        var install = $@"
set -e
DEBIAN_FRONTEND=noninteractive
export DEBIAN_FRONTEND
# Preseed для phpmyadmin: не настраивать через dbconfig-common (у нас свой MySQL)
echo 'phpmyadmin phpmyadmin/dbconfig-install boolean false' | debconf-set-selections
echo 'phpmyadmin phpmyadmin/reconfigure-webserver multiselect' | debconf-set-selections
apt-get install -y phpmyadmin
";
        await ExecScript(install);

        var vhostConf = TemplateEngine.Render("apache/phpmyadmin.conf", new Dictionary<string, string>());
        await UploadFile("/etc/apache2/conf-available/phpmyadmin.conf", vhostConf);
        await ExecScript(@"a2enconf phpmyadmin && systemctl reload apache2");
        Emit(EventLevel.Ok, "✓ phpMyAdmin: установлен + alias /phpMyAdmin активирован");
    }

    private async Task RoundcubePhase()
    {
        Emit(EventLevel.Phase, "Roundcube webmail");
        var install = $@"
set -e
DEBIAN_FRONTEND=noninteractive
export DEBIAN_FRONTEND
echo 'roundcube-core roundcube/dbconfig-install boolean false' | debconf-set-selections
echo 'roundcube-core roundcube/database-type select mysql' | debconf-set-selections
apt-get install -y roundcube roundcube-mysql roundcube-plugins
";
        await ExecScript(install);

        // Импорт schema Roundcube в roundcubemail БД
        await ExecScript("mysql roundcubemail < /usr/share/dbconfig-common/data/roundcube/install/mysql");

        // Config file
        var desKey = RandomPass(24);
        var config = TemplateEngine.Render("roundcube/config.inc.php", new Dictionary<string, string>
        {
            ["__RCPASS__"] = _mysqlRoundcubePass,
            ["__DESKEY__"] = desKey,
        });
        await UploadFile("/etc/roundcube/config.inc.php", config);

        // Apache alias config
        var apacheConf = TemplateEngine.Render("apache/roundcube.conf", new Dictionary<string, string>());
        await UploadFile("/etc/apache2/conf-available/roundcube.conf", apacheConf);
        await ExecScript(@"a2enconf roundcube 2>/dev/null || true
chown -R www-data:www-data /var/lib/roundcube
systemctl reload apache2");
        Emit(EventLevel.Ok, "✓ Roundcube: установлен + БД импортирована + config применён");
    }

    private async Task PmtaConfigAndStartPhase()
    {
        Emit(EventLevel.Phase, "PMTA config + start");
        // PMTA v5 web-monitor не имеет отдельной password auth в этом config —
        // доступ по IP через http-access (__IP__ admin, 0.0.0.0/0 monitor).
        _srv.Credentials.PmtaMonitorUser = "—";
        _srv.Credentials.PmtaMonitorPass = "IP-based (см. http-access в /etc/pmta/config)";

        // Авторизованный SMTP-релей (source {ext-relay}, require-auth true) — для скриптов,
        // которые шлют не с 127.0.0.1. Тот же порт, что и общий smtp-listener (2525).
        _srv.Credentials.PmtaSmtpUser = "pmtauser";
        _srv.Credentials.PmtaSmtpPass = _mysqlMailPass;
        _srv.Credentials.PmtaSmtpPort = "2525";

        var config = TemplateEngine.Render("pmta/config", new Dictionary<string, string>
        {
            ["__IP__"] = _srv.Ip,
            ["__DOMAIN__"] = _srv.Domain,
            ["__PMTASMTPUSER__"] = _srv.Credentials.PmtaSmtpUser,
            ["__PMTASMTPPASS__"] = _srv.Credentials.PmtaSmtpPass,
        });
        await UploadFile("/etc/pmta/config", config);

        // Создаём systemd unit для pmtahttpd (нет в дефолтной поставке .deb).
        // Type=simple + ExecStart — systemd управляет процессом полностью,
        // никаких проблем с SSH channel fd inheritance.
        var pmtaHttpdUnit = @"[Unit]
Description=PowerMTA HTTP web-monitor
After=pmta.service
Requires=pmta.service

[Service]
Type=simple
ExecStart=/usr/sbin/pmtahttpd
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
";
        await UploadFile("/etc/systemd/system/pmtahttpd.service", pmtaHttpdUnit);

        var setup = @"
set -e
mkdir -p /var/spool/pmta /var/log/pmta
id pmta >/dev/null 2>&1 || useradd -r -s /bin/false pmta
chown -R pmta:pmta /var/spool/pmta /var/log/pmta /etc/pmta
chmod 755 /usr/sbin/pmtad /usr/sbin/pmtahttpd

systemctl daemon-reload
# pmtad (unit от .deb пакета)
systemctl enable pmta 2>/dev/null || true
systemctl restart pmta 2>/dev/null || true
# pmtahttpd (наш собственный unit)
systemctl enable pmtahttpd 2>/dev/null || true
systemctl restart pmtahttpd 2>/dev/null || true
sleep 2
systemctl is-active pmta || true
systemctl is-active pmtahttpd || true
ss -tlnp | grep -E ':8080|:2525' || true
";
        await ExecScript(setup);
        Emit(EventLevel.Ok, "✓ PMTA: config + pmtad + pmtahttpd (через systemd) запущены (порты 2525 / 8080)");
    }

    private async Task ZtdsByoPhase()
    {
        Emit(EventLevel.Phase, "zTDS (BYO)");
        if (string.IsNullOrWhiteSpace(_opts.ZtdsUrl)) { Emit(EventLevel.Warn, "URL пустой — пропускаем"); return; }
        var dst = $"/var/www/html/{_tdsSub}.{_srv.Domain}/TDS";
        // MD5 пароля — zTDS хранит admin_pass в md5 (см. config.php плейсхолдер [PASSWORD])
        var pwMd5 = System.Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(_webmailAdminPass))).ToLowerInvariant();
        var cookieName = RandomPass(12);
        var cmd = $@"
mkdir -p {dst}
cd /tmp && wget -q '{_opts.ZtdsUrl}' -O ztds.zip
unzip -o -q /tmp/ztds.zip -d {dst}
# Дистрибутив zTDS имеет плейсхолдеры в config.php — заменяем перед первым запуском
sed -i 's|\[PASSWORD\]|{pwMd5}|g' {dst}/config.php
sed -i 's|\[RANDOM\]|{cookieName}|g' {dst}/config.php
chown -R www-data:www-data /var/www/html/{_tdsSub}.{_srv.Domain}
rm -f /tmp/ztds.zip
";
        await ExecScript(cmd);
        Emit(EventLevel.Ok, $"✓ zTDS распакован в {dst}, admin/pass прописан в config.php");
    }

    private async Task MailerByoPhase()
    {
        Emit(EventLevel.Phase, "Mailer-скрипты (BYO)");
        if (string.IsNullOrWhiteSpace(_opts.MailerUrl)) { Emit(EventLevel.Warn, "URL пустой — пропускаем"); return; }
        var dst = $"/var/www/html/{_srv.Domain}/mailer";
        var cmd = $@"
mkdir -p {dst}
cd /tmp && wget -q '{_opts.MailerUrl}' -O mailer.zip
unzip -o -q /tmp/mailer.zip -d {dst}
chown -R www-data:www-data {dst}
rm -f /tmp/mailer.zip
";
        await ExecScript(cmd);
        Emit(EventLevel.Ok, $"✓ Mailer распакован в {dst}");
    }

    private async Task Apply3ProxyPhase()
    {
        Emit(EventLevel.Phase, "3proxy");
        var proxyPass = RandomPass(16);
        var proxyUser = "admin";
        _srv.Credentials.Proxy3User = proxyUser;
        _srv.Credentials.Proxy3Pass = proxyPass;
        _srv.Credentials.Proxy3Url = $"socks5://{proxyUser}:{proxyPass}@{_srv.Ip}:3221";

        // 1) Установка бинаря — в Ubuntu 22.04 default repos нет пакета 3proxy,
        //    качаем официальный .deb с github release
        var install = @"
set -e
cd /tmp
wget -q --timeout=30 https://github.com/3proxy/3proxy/releases/download/0.9.5/3proxy-0.9.5.x86_64.deb -O 3proxy.deb
DEBIAN_FRONTEND=noninteractive dpkg -i --force-confnew 3proxy.deb 2>&1 | tail -3
rm -f /tmp/3proxy.deb
mkdir -p /etc/3proxy /var/log/3proxy
chown -R nobody:nogroup /var/log/3proxy 2>/dev/null || true
";
        await ExecScript(install);

        // 2) Upload config (fakir placeholder больше не используется —
        //    новый формат с $/etc/3proxy/passwd, юзер+пароль пишем ниже отдельным файлом)
        var config = TemplateEngine.Render("3proxy/3proxy.cfg", new Dictionary<string, string>());
        await UploadFile("/etc/3proxy/3proxy.cfg", config);

        // 2b) Файл с юзерами — github .deb-бинарь ищет их только через $ префикс из файла
        await UploadFile("/etc/3proxy/passwd", $"{proxyUser}:CL:{proxyPass}\n");
        await ExecScript("chmod 644 /etc/3proxy/passwd");

        // 3) systemd unit — Ubuntu apt-пакет не создаёт его, делаем сами
        var proxyUnit = @"[Unit]
Description=3proxy tiny proxy
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/3proxy /etc/3proxy/3proxy.cfg
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
";
        await UploadFile("/etc/systemd/system/3proxy.service", proxyUnit);

        var startup = @"
systemctl daemon-reload
systemctl enable 3proxy 2>/dev/null || true
systemctl restart 3proxy 2>/dev/null || true
sleep 2
systemctl is-active 3proxy || true
ss -tlnp | grep 3221 || true
";
        await ExecScript(startup);
        Emit(EventLevel.Ok, $"✓ 3proxy: SOCKS5 на порту 3221 через systemd (user: {proxyUser})");
    }

    private async Task LandingByoPhase()
    {
        Emit(EventLevel.Phase, "Landing");
        var dst = $"/var/www/html/{_srv.Domain}";
        var vars = new Dictionary<string, string> { ["__DOMAIN__"] = _srv.Domain };

        // Локальная папка Landing/ рядом с Fakunator.exe — юзер может править
        var localRoot = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "Landing");

        if (!System.IO.Directory.Exists(localRoot))
        {
            Emit(EventLevel.Warn, $"Папка Landing/ не найдена рядом с exe — пропускаем");
            return;
        }

        await ExecScript($"mkdir -p {dst}");

        // Текстовые файлы — с подстановкой __DOMAIN__. Бинарные (картинки/шрифты) — как есть.
        var textExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".css", ".js", ".txt", ".xml", ".json", ".svg", ".md" };

        int uploaded = 0;
        int textCount = 0, binaryCount = 0;
        foreach (var file in System.IO.Directory.GetFiles(localRoot, "*", System.IO.SearchOption.AllDirectories))
        {
            var relPath = System.IO.Path.GetRelativePath(localRoot, file).Replace('\\', '/');
            var remotePath = $"{dst}/{relPath}";
            var remoteDir = System.IO.Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(remoteDir) && remoteDir != dst)
                await ExecScript($"mkdir -p {remoteDir}");

            if (textExt.Contains(System.IO.Path.GetExtension(file)))
            {
                var text = System.IO.File.ReadAllText(file);
                var rendered = TemplateEngine.Substitute(text, vars);
                await UploadFile(remotePath, rendered);
                textCount++;
            }
            else
            {
                await UploadBinaryFile(remotePath, System.IO.File.ReadAllBytes(file));
                binaryCount++;
            }
            uploaded++;
        }

        await ExecScript($"chown -R www-data:www-data {dst}");
        Emit(EventLevel.Ok, $"✓ Landing загружен ({textCount} текстовых + {binaryCount} бинарных = {uploaded} всего) в {dst}");
    }

    private async Task AmsStatsPhase()
    {
        Emit(EventLevel.Phase, "AMS RealTime статистика");
        var dst = $"/var/www/html/{_srv.Domain}/ams";
        var amsPass = RandomPass(16);

        await ExecScript($"mkdir -p {dst}");

        var htaccess = TemplateEngine.Render("ams/.htaccess", new Dictionary<string, string>());
        await UploadFile($"{dst}/.htaccess", htaccess);

        var php = TemplateEngine.Render("ams/amsweb.php", new Dictionary<string, string> { ["__AMSPASS__"] = amsPass });
        await UploadFile($"{dst}/amsweb.php", php);

        await ExecScript($"chown -R www-data:www-data {dst}");

        _srv.Credentials.AmsStatsUrl = $"https://{_srv.Domain}/ams/amsweb.php";
        _srv.Credentials.AmsStatsUrlIp = $"http://{_srv.Ip}/ams/amsweb.php";
        _srv.Credentials.AmsStatsPassword = amsPass;
        _srv.Credentials.AmsStatsPath = dst;

        Emit(EventLevel.Ok, $"✓ AMS RealTime статистика загружена в {dst} (доступна по домену и по IP)");
    }

    private async Task UploadBinaryFile(string remotePath, byte[] content)
    {
        Emit(EventLevel.Command, "> " + remotePath);
        await Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP not connected");
            using var ms = new System.IO.MemoryStream(content);
            _sftp.UploadFile(ms, remotePath, true);
        });
    }

    // ── ХЕЛПЕРЫ ─────────────────────────────────────────────

    private Dictionary<string, string> SubVars() => new()
    {
        // Порядок применения — от длинных ключей к коротким (см. TemplateEngine.Substitute)
        // Пример: "nserren.w00w.site" заменится ДО "w00w.site"
        ["nserren.w00w.site"] = $"{_webmailSub}.{_srv.Domain}",
        ["tencprod.w00w.site"] = $"{_tdsSub}.{_srv.Domain}",
        ["w00w.site"] = _srv.Domain,
        ["fakir"] = _mysqlMailPass, // MySQL admin пароль в mysql-virtual-*.cf
        ["146.103.115.104"] = _srv.Ip,
    };

    private async Task Phase(string name, string command)
    {
        _cts.Token.ThrowIfCancellationRequested();
        Emit(EventLevel.Phase, name);
        Emit(EventLevel.Command, "# " + command.Split('\n').First());
        var r = await ExecCaptureResult(command);
        foreach (var line in (r.Stdout ?? "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(30))
            Emit(EventLevel.Stdout, line.TrimEnd('\r'));
        if (r.ExitCode == 0) Emit(EventLevel.Ok, $"✓ {name}");
        else                 throw new Exception($"{name} упал exit={r.ExitCode}: {r.Error}");
    }

    private async Task<string> ExecCapture(string cmd)
    {
        var r = await ExecCaptureResult(cmd);
        return r.Stdout ?? "";
    }

    private async Task ExecScript(string script)
    {
        Emit(EventLevel.Command, script.Split('\n').First().Trim());
        var r = await ExecCaptureResult(script);
        if (r.ExitCode != 0)
            throw new Exception($"Script failed exit={r.ExitCode}: {r.Error}\nStdout: {r.Stdout}");
    }

    private Task<ExecResult> ExecCaptureResult(string cmd)
    {
        return Task.Run(() =>
        {
            if (_ssh == null) throw new InvalidOperationException("SSH not connected");
            var scmd = _ssh.CreateCommand(cmd);
            scmd.CommandTimeout = TimeSpan.FromMinutes(15);
            var stdout = scmd.Execute();
            return new ExecResult(scmd.ExitStatus ?? 0, stdout ?? "", scmd.Error ?? "");
        });
    }

    private async Task UploadFile(string remotePath, string content)
    {
        Emit(EventLevel.Command, "> " + remotePath);
        await Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP not connected");
            using var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(content));
            _sftp.UploadFile(ms, remotePath, true);
        });
    }

    private void Emit(EventLevel level, string text)
        => Event?.Invoke(new InstallEvent(DateTime.Now, level, text));

    public void Dispose()
    {
        _cts.Cancel();
        try { _sftp?.Dispose(); } catch { }
        try { _ssh?.Dispose();  } catch { }
        _cts.Dispose();
    }

    // ── УТИЛИТЫ ─────────────────────────────────────────────

    private static string RandomSub(int len)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz";
        var rnd = RandomNumberGenerator.Create();
        var buf = new byte[len];
        rnd.GetBytes(buf);
        return new string(buf.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static string RandomPass(int len)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var rnd = RandomNumberGenerator.Create();
        var buf = new byte[len];
        rnd.GetBytes(buf);
        return new string(buf.Select(b => chars[b % chars.Length]).ToArray());
    }

    /// <summary>Из содержимого default.txt (opendkim-genkey) вытащить p= значение.</summary>
    private static string ExtractDkimPValue(string dkimTxt)
    {
        // Формат: default._domainkey  IN  TXT  ( "v=DKIM1; h=sha256; k=rsa; "
        //           "p=MIGf...QAB" ) ; -- ...
        var sb = new StringBuilder();
        bool inValue = false;
        foreach (var line in dkimTxt.Split('\n'))
        {
            var trim = line.Trim();
            int q1 = 0;
            while (true)
            {
                int qStart = trim.IndexOf('"', q1);
                if (qStart < 0) break;
                int qEnd = trim.IndexOf('"', qStart + 1);
                if (qEnd < 0) break;
                sb.Append(trim.AsSpan(qStart + 1, qEnd - qStart - 1));
                q1 = qEnd + 1;
                inValue = true;
            }
        }
        return inValue ? sb.ToString().Trim() : dkimTxt.Trim();
    }
}

public sealed record InstallEvent(DateTime Time, EventLevel Level, string Text);
public sealed record ExecResult(int ExitCode, string Stdout, string Error);

public enum EventLevel
{
    Phase, Command, Stdout, Ok, Warn, Error, Info
}
