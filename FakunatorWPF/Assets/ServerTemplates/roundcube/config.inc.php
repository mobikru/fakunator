<?php
/* Roundcube configuration — генерирован Fakunator */

// Общие настройки
$config = [];

// MySQL БД для Roundcube
$config['db_dsnw'] = 'mysql://roundcube:__RCPASS__@localhost/roundcubemail';

// IMAP — локальный dovecot
// ВАЖНО: default_host/default_port задаём ЯВНО — Debian-пакет roundcube-core загружает
// /etc/roundcube/defaults.inc.php ПОСЛЕ нашего config.inc.php, и там default_host='localhost',
// default_port=143 (незашифрованный). Без явных default_* Roundcube пробует 143 и падает
// с "Неудачное соединение с IMAP сервером" (у нас dovecot слушает только 993).
$config['default_host'] = 'ssl://localhost';
$config['default_port'] = 993;
$config['imap_host'] = 'ssl://localhost:993';
$config['imap_conn_options'] = [
    'ssl' => [
        'verify_peer' => false,
        'verify_peer_name' => false,
    ],
];
$config['imap_delimiter'] = '/';

// SMTP — локальный postfix через submission
$config['smtp_host'] = 'tls://localhost:587';
$config['smtp_user'] = '%u';
$config['smtp_pass'] = '%p';
$config['smtp_conn_options'] = [
    'ssl' => [
        'verify_peer' => false,
        'verify_peer_name' => false,
    ],
];

// Прочее
$config['support_url'] = '';
$config['product_name'] = 'Webmail';
$config['des_key'] = '__DESKEY__';
$config['plugins'] = ['archive', 'zipdownload'];
$config['skin'] = 'elastic';
$config['language'] = 'ru_RU';

// Отключить проверку версии Roundcube
$config['skip_first_login'] = false;
$config['session_lifetime'] = 30;
