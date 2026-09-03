# Факунатор — полное описание проекта

> Windows desktop приложение для маркетингового email-workflow:
> очистка/валидация баз → SMTP-проверка через прокси → AI-определение пол/страна
> → сканирование доменов → управление DNS через ISPmanager → полностью автоматическая
> регистрация и верификация домена в mail.ru Postmaster.
>
> **Версия:** 2.0.0 (WPF · .NET 8 · C#) — single-file self-contained exe.
> **Автор:** rumobik@gmail.com. Личный инструмент, не для публикации.
> **Дата ревизии README:** 2026-08-29.

Этот файл — единая точка правды для новой сессии Claude Code. Держим его в
актуальном состоянии — если что-то в коде разошлось, фикси README в тот же
момент.

---

## 1. Что за проект и его архитектура

### Назначение

Инструмент, покрывающий весь путь email-marketing owner'а:

| Этап | Что делает |
|---|---|
| **Cleanup** | Классификация email-адресов по 9 категориям (clean / duplicate / invalid / reserved / trap / role / disposable / corporate / typo) и раскладка по файлам. Стрим-обработка миллионов строк. |
| **SMTP** | RFC-5321 zoll-mail probing через SOCKS5-прокси (EHLO/MAIL FROM/RCPT TO), никаких писем не шлёт. Rotation, retry, recovery-задержка. |
| **Analyze** | Определение пол/страна: сначала локальная name-БД (~19K имён), затем OpenAI/Anthropic API батчами + optional pass 2 для low-confidence. |
| **Domain Scanner** | Прогон whois-provider'ов через `domainsmonitor.com` API + локальный DNS resolve, поиск свободных зон под fake-домены с нужным NS. |
| **Domains Manager** | ISPmanager (Zomro DNSmanager) — список доменов, DNS-записи, add/edit/delete, upsert TXT для верификации. |
| **Mail.ru Postmaster** | Полностью автоматическая регистрация домена + DNS-верификация (без браузера, через прямую web-сессию mail.ru). Плюс OAuth-стата по verified-доменам. |

### Архитектура высокоуровнево

```
┌─ App (WPF entry) ─────────────────────────────────────────────────┐
│  ┌─ MainWindow ────────────────────────────────────────────────┐  │
│  │  ┌─ Sidebar (per-tab context)                             │  │
│  │  ┌─ Tabs: Cleanup | SMTP | Analyze | Scanner | Domains    │  │
│  │  │       ├─ Views (XAML + code-behind)                    │  │
│  │  │       └─ ViewModels (MVVM, INotifyPropertyChanged)     │  │
│  │  └─ Modal dialogs: Settings, AddDomain, ConnectMailRu,    │  │
│  │                    MailRuVerify, PostmasterDomainsWindow  │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                   │
│  Core (без UI-зависимостей)                                       │
│  ├─ Config (singleton, JSON, debounced save)                      │
│  ├─ Paths (ExeDir через Environment.ProcessPath, output/data)     │
│  ├─ Blocklists, Classifier, EmailHelpers, TypoFixer               │
│  ├─ CleanupWorker (Task-based)                                    │
│  ├─ SmtpProber, MxResolver, SmtpValidateWorker                    │
│  ├─ AiClient (OpenAI/Anthropic), AnalyzeCache, AnalyzeWorker      │
│  ├─ NameDb (SQLite read-only)                                     │
│  ├─ DomainScanner: DmApiClient / NsResolver / DomainDb / Worker   │
│  ├─ DomainsManager: IspApiClient                                  │
│  └─ Postmaster: MailRuOAuthClient / PostmasterApiClient           │
│                 / MailRuWebSession                                │
└───────────────────────────────────────────────────────────────────┘
```

**Паттерн:** MVVM. ViewModels подписаны на `Config.Changed` — изменения в Settings
диалоге тут же прилетают во все табы без перезапуска. Все длинные операции —
`Task.Run` с `IProgress<T>` для UI-обновлений, cancel через `CancellationToken`.

**Тема:** `App.SwitchTheme("dark"|"light")` меняет второй словарь в
`MergedDictionaries` (первый — SharedStyles). Custom-controls (ParticleBackground,
SparklineChart, ToggleSwitch) подписаны на `App.ThemeChanged` — вызывают
`InvalidateVisual()` при смене.

### Deploy-модель

**Single-file self-contained exe** (~73 MB): `dotnet publish -c Release -r win-x64
--self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true`.
Все зависимости внутри, .NET 8 runtime не нужен на машине. `config.json` и папка
`data/` — рядом с exe.

---

## 2. Структура каталогов

```
Clear Base/                                    ← project root
│
├── README.md                                  ← этот файл (единая точка правды)
│
├── FakunatorWPF/                              ← ★ АКТУАЛЬНЫЙ ИСХОДНИК (WPF C# .NET 8)
│   ├── FakunatorWPF.csproj                    ← project file
│   ├── App.xaml + App.xaml.cs                 ← entry + theme switcher
│   ├── MainWindow.xaml + .cs                  ← оболочка, tab-switch, DataContext'ы
│   ├── DebugTest.cs                           ← --test режим (smoke-тест)
│   ├── config.json                            ← DEV-конфиг (не деплойный)
│   │
│   ├── Core/                                  ← бизнес-логика (без Qt/WPF типов)
│   │   ├── Config.cs                          ← singleton, JSON persist
│   │   ├── Paths.cs                           ← ExeDir/OutputRoot/DataDir helpers
│   │   ├── Tokens.cs                          ← цветовая палитра (dark/light)
│   │   │
│   │   ├── Blocklists.cs                      ← disposable/role/spam-trap lists
│   │   ├── BlocklistUpdater.cs                ← pull из GitHub-репо
│   │   ├── Classifier.cs                      ← 9 категорий → routing
│   │   ├── EmailHelpers.cs                    ← RFC-5321 нормализация
│   │   ├── TypoFixer.cs                       ← gmial → gmail и т.п.
│   │   ├── CleanupWorker.cs                   ← Task-стрим-обработка
│   │   ├── SnapshotBatch.cs                   ← UI-прогресс структура
│   │   │
│   │   ├── SmtpProber.cs                      ← RFC-5321 через SOCKS5
│   │   ├── SmtpValidateWorker.cs              ← rotation + retry orchestrator
│   │   ├── SmtpSnapshotBatch.cs               ← UI-прогресс
│   │   ├── SmtpVerdict.cs                     ← enum vaild/invalid/greylist/…
│   │   ├── MxResolver.cs                      ← DNS MX кэш (DnsClient)
│   │   ├── Verdict.cs                         ← общий enum
│   │   │
│   │   ├── AiClient.cs                        ← OpenAI + Anthropic HTTP
│   │   ├── AnalyzeWorker.cs                   ← DB-lookup + AI-pipeline
│   │   ├── AnalyzeCache.cs                    ← кэш AI-ответов (SHA-fingerprint)
│   │   ├── AnalyzeSnapshot.cs                 ← UI-прогресс
│   │   ├── AnalysisResult.cs                  ← record с полями gender/iso/…
│   │   ├── NameDb.cs                          ← names.db (SQLite read-only)
│   │   ├── CountryFlags.cs                    ← ISO → эмодзи/PNG
│   │   ├── ProviderRegistry.cs                ← известные почтовые провайдеры
│   │   │
│   │   ├── DomainScanner/
│   │   │   ├── DmApiClient.cs                 ← domainsmonitor.com REST
│   │   │   ├── NsResolver.cs                  ← DNS NS/A lookups (DnsClient)
│   │   │   ├── NsProviderDetector.cs          ← NS-hosts → provider name
│   │   │   ├── DomainDb.cs                    ← SQLite: domains + scan_history
│   │   │   ├── DomainScanModels.cs            ← records (DomainRecord и т.д.)
│   │   │   └── DomainScanWorker.cs            ← producer/consumer orchestration
│   │   │
│   │   ├── DomainsManager/                    ← ISPmanager DNSmanager
│   │   │   ├── IspApiClient.cs                ← XML/JSON API клиент
│   │   │   └── IspModels.cs                   ← IspAccount, IspDomain, DnsRecord
│   │   │
│   │   └── Postmaster/                        ← mail.ru
│   │       ├── PostmasterAccount.cs           ← Username/Password/RefreshToken
│   │       ├── MailRuOAuthClient.cs           ← refresh_token → access_token
│   │       ├── PostmasterApiClient.cs         ← reg-list/troubles/stat + rate-limit
│   │       └── MailRuWebSession.cs            ← ★ web login + add + verify (DNS)
│   │
│   ├── ViewModels/
│   │   ├── CleanupViewModel.cs                ← state machine + прогресс
│   │   ├── SmtpViewModel.cs
│   │   ├── AnalyzeViewModel.cs
│   │   ├── DomainScannerViewModel.cs
│   │   ├── DomainsManagerViewModel.cs         ← ISPmanager список + DNS UI
│   │   ├── StateToVisibilityConverter.cs      ← общий XAML-конвертер (idle/running/done)
│   │   ├── SmtpStateToVisibilityConverter.cs
│   │   └── AnalyzeStateToVisibilityConverter.cs
│   │
│   ├── Views/                                 ← Views (XAML + .xaml.cs) и Dialogs (чистый C#)
│   │   ├── CleanupIdleView / LoadedView / RunningView / DoneView
│   │   ├── SmtpIdleView / RunningView / DoneView
│   │   ├── AnalyzeIdleView / RunningView / DoneView
│   │   ├── DomainScannerView + SidebarDomainScannerView
│   │   ├── DomainsManagerView + SidebarDomainsManagerView
│   │   ├── SidebarCleanupView / SidebarSmtpView / SidebarAnalyzeView
│   │   ├── AddDomainDialog.cs                 ← модалка для DomainsManager
│   │   ├── AddIspAccountDialog.cs             ← подключить ISPmanager
│   │   ├── EditDnsRecordDialog.cs             ← правка DNS-записи
│   │   ├── DnsRecordsWindow.cs                ← просмотр всех DNS для домена
│   │   ├── ConnectMailRuDialog.cs             ← подключить mail.ru (login+pass+refresh)
│   │   ├── PostmasterDomainsWindow.cs         ← дашборд verified-доменов + troubles + stat
│   │   ├── MailRuVerifyDialog.cs              ← ★ авто-верификация домена
│   │   ├── ProxyCheckDialog.cs                ← массовая проверка прокси
│   │   └── SettingsDialog.cs                  ← 3 страницы: AI / Приложение / Продвинутое
│   │
│   ├── Controls/
│   │   ├── ParticleBackground.cs              ← сеть точек фон (idle-scene)
│   │   ├── SparklineChart.cs                  ← график скорости в Running
│   │   └── ToggleSwitch.cs                    ← кастомный toggle
│   │
│   ├── Converters/
│   │   ├── IsoToFlagConverter.cs              ← ISO2 → emoji
│   │   ├── IsoToFlagImageConverter.cs         ← ISO2 → PNG path
│   │   └── DomainScannerConverters.cs         ← statuses/times/humans
│   │
│   ├── Themes/
│   │   ├── SharedStyles.xaml                  ← кнопки, инпуты, скроллбары
│   │   ├── DarkTheme.xaml                     ← brushes темной темы
│   │   └── LightTheme.xaml                    ← brushes светлой темы
│   │
│   └── Assets/
│       ├── icon.ico                           ← иконка exe/taskbar
│       └── Flags/                             ← 90 PNG-флагов ISO-стран
│
├── Fakunator_v2.0.0_SelfContained/            ← ★ АКТУАЛЬНАЯ РАЗДАЧА (сюда деплоим)
│   ├── Fakunator.exe                          ← 73 MB single-file self-contained
│   ├── config.json                            ← ★ USER DATA — НИКОГДА не перезаписывать
│   ├── data/                                  ← names.db + blocklists + domains.db (создаётся)
│   └── output/                                ← результаты Cleanup/SMTP/Analyze (создаётся)
│
├── Fakunator_v2.0.0_Lite/                     ← framework-dependent (устаревшая, не используем)
│
└── (LEGACY — Python/PySide6, НЕ ТРОГАТЬ без явного запроса)
    ├── fakunator/                             ← старый пакет
    ├── run_fakunator.py, fakunator.spec
    ├── scripts/                               ← старые dev-утилиты
    ├── Release/, Release_v1.5.0/, Release_v1.5.0.zip
    ├── name_modul/, design_handoff/, design_smtp/
```

**Правило:** любой новый код — в `FakunatorWPF/`. Никаких изменений в
`fakunator/` / `scripts/` / `Release*/` без явной просьбы юзера.

---

## 3. Стек

| Слой | Технология | Комментарий |
|---|---|---|
| Runtime | **.NET 8** (`net8.0-windows`) | LTS до 2026-11 |
| UI | **WPF** (Windows Presentation Foundation) | XAML + MVVM, code-behind где проще |
| Язык | **C# 12** | `<Nullable>enable</Nullable>` |
| Пакеты | **Microsoft.Data.Sqlite 8.0.0** | names.db + domains.db |
| | **DnsClient 1.7.0** | все MX/NS/A resolve'ы |
| HTTP | Встроенный **HttpClient** | никаких Refit/RestSharp — везде вручную |
| JSON | Встроенный **System.Text.Json** | camelCase policy глобально |
| Прокси | Встроенный **Socks5Proxy** (в SmtpProber) | без сторонних SOCKS libs |
| Сборка | `dotnet publish` single-file | ~30-60 сек на итерацию |
| SDK | `C:\Program Files\dotnet\` (x64) | ⚠ x86-stub в `C:\Program Files (x86)\dotnet\` — БЕЗ SDK, обходим явным путём |
| IDE | Не привязан — редактируем через VSCode + Claude Code | XAML designer не нужен, всё правится текстом |

**Что явно НЕ подключено (и не будем):** ORM (EF Core — избыточно), CommunityToolkit.Mvvm, ReactiveUI, Serilog, DI-контейнер. Всё вручную — проект компактный, зависимости плодят проблемы с single-file publish.

---

## 4. БД и важные таблицы

### 4.1. `data/names.db` — база имён (bundled, read-only)

SQLite-файл ~19 MB, поставляется в раздаче (`Fakunator_v2.0.0_SelfContained/data/`).
Собран из BehindTheName + ручных дополнений (legacy `scripts/build_names_db.py`).

**Таблицы:**

| Таблица | Поля | Назначение |
|---|---|---|
| `names` | `id`, `name`, `name_lower`, `gender` (M/F/N), `tier` (1-3, качество) | Основной lookup — сюда бьём email local-part |
| `name_cultures` | `name_id`, `culture_clean` (ISO-подобный, e.g. "russian", "english") | one-to-many культур на имя, используется в `AllCultures[]` результата |
| `name_variants` | `name_id`, `variant` | Варианты написания (Alex/Alexey/Aleksei → все ведут на Alexander) |

**Как читаем:** `NameDb` при старте `Load` тянет ВСЁ в память (два Dictionary:
`_exactMap` + `_variantMap`). ~19K имён, ~50 MB RAM. Дальше lookup — O(1)
in-memory, дисковых чтений после инициализации нет. Файл открывается
`Mode=ReadOnly`.

**Расположение:** `Paths.DataDir / "names.db"` (то есть `<exe_dir>/data/names.db`).

### 4.2. `data/domains.db` — кэш DomainScanner'а (генерируется)

SQLite ~50-150 MB для миллиона доменов. Создаётся при первом запуске сканирования.

**Таблицы:**

```sql
CREATE TABLE domains (
    domain      TEXT PRIMARY KEY,
    zone        TEXT NOT NULL,           -- ru, com, …
    ns_provider TEXT,                    -- 'Cloudflare', 'Selectel', 'unknown', …
    ns_hosts    TEXT,                    -- JSON-array NS-хостов
    status      TEXT,                    -- 'ok', 'lame_delegation', 'nxdomain', …
    has_a       INTEGER,                 -- 0/1
    a_status    TEXT,
    checked_at  INTEGER                  -- unix timestamp
);
CREATE INDEX idx_zone;
CREATE INDEX idx_provider;
CREATE INDEX idx_zone_provider;
CREATE INDEX idx_status;

CREATE TABLE scan_history (
    id INTEGER PRIMARY KEY,
    zone TEXT, started_at INTEGER, finished_at INTEGER,
    total_fetched INTEGER, total_saved INTEGER
);
```

**Pragmas:** `journal_mode=WAL`, `synchronous=NORMAL` — быстрые батч-записи.
**Writer-конкурентность:** одна writer-очередь (`BlockingCollection`), N reader'ов
пишут в неё, один loop флашит батчами в SQLite. WAL+один writer = максимум
throughput SQLite'а.

### 4.3. `data/analyze_cache.db` (опционально, создаётся)

Кэш AI-ответов по SHA-фингерпринту `(system_prompt + provider + model + email)`.
Одинаковый запрос из следующего прогона возвращается моментально из кэша,
экономит деньги.

### 4.4. `config.json` — не БД, но critical persistence

Полный список полей — в разделе **5.1 Config.cs** ниже. Валится в
корневую директорию **где лежит exe**, поиск через `Environment.ProcessPath`
(см. секцию 6 — «Архитектурные решения»).

### 4.5. Внешние API (не хранилища, но важно помнить)

- **`domainsmonitor.com`** — используем для получения списка ру-доменов зоны, whois-инфы. Ключ юзера в config, лимитов не проверяем — платный тариф.
- **`mail.ru/postmaster/api/v1/*`** — OAuth Bearer, лимит 10 запросов/минуту на пару (домен, аккаунт).
- **ISPmanager API** — session-based, session-id живёт до logout / TTL. Без rate-limit.

---

## 5. Ключевые файлы и за что отвечают

### 5.1. `Core/Config.cs` (307 строк)

Singleton-конфиг. Все поля с camelCase JSON-сериализацией.

**Секции конфига:**

| Секция | Поля |
|---|---|
| App | `theme`, `outputDir`, `gmailStrict`, `enabledFilters[]`, `updateBlocklistsOnStart` |
| SMTP | `proxies`, `smtpBatchSize`, `smtpWorkersPerProxy`, `smtpTimeoutSec`, `smtpMaxConsecErrors`, `smtpRetries`, `smtpSessionDelaySec`, `smtpRecoveryMin`, `smtpHelo`, `smtpMailFrom`, `aliveProxies[]` |
| AI | `openAiKey`, `anthropicKey`, `aiProvider`, `aiModel`, `googleKey`, `googleModel` |
| Analyze | `analyzeSpendCap`, `analyzeAiBatchSize`, `analyzeMaxConcurrent`, `analyzeTwoPass`, `analyzeUseAi`, `analyzeSkipJunk`, `pass2Provider`, `analyzeBatchDelay`, `analyzeSkipRoleBased`, `privacyMode` |
| Additional | `useSpamhausDbl` |
| DomainsManager | `ispAccounts[]` — `{host, username, password, displayName, provider}` |
| Postmaster | `postmasterAccounts[]` — `{username, password, refreshToken, displayName}` |
| DomainScanner | `domainsMonitorApiKey`, `domainScanConcurrency`, `domainDnsTimeoutSec`, `domainScanZones`, `domainScanTargetNs` |
| Window | `windowLeft`, `windowTop`, `windowWidth`, `windowHeight`, `windowMaximized` |

**Механизмы:**
- `Config.Current` — глобальный singleton, thread-safe (`lock`)
- `Save()` — **debounced 500ms** через `DispatcherTimer` (много быстрых изменений → одна запись)
- `SaveNow()` — немедленный флаш + событие `Changed` (ViewModels подписаны)
- `ResetToDefaults()` — сброс, но `OpenAiKey`, `AnthropicKey`, `GoogleKey`, `Proxies` сохраняются
- `ConfigPath` — `Environment.ProcessPath` → dir + `/config.json` (**см. секцию 6, ловушка single-file exe**)

### 5.2. `Core/Paths.cs` (52 строки)

Все пути наружу:
- `Paths.ExeDir` — папка exe (кэшируется, через `Environment.ProcessPath`)
- `Paths.OutputRoot` = `<ExeDir>/output/`
- `Paths.CleanupRoot` / `SmtpRoot` / `AnalyzeRoot` — подпапки
- `Paths.DataDir` = `<ExeDir>/data/` (создаётся автоматом)

Никогда не пишем в `%APPDATA%` или `%TEMP%` — всё рядом с exe, пользователь не ищет по системе.

### 5.3. Cleanup pipeline

- **`Core/Blocklists.cs`** (211) — загрузка `disposable_domains.txt`, `free_providers.txt`, `role_prefixes.txt`, `spamtrap_patterns.txt`, `allow_domains.txt`. Поиск через HashSet.
- **`Core/BlocklistUpdater.cs`** (143) — pull из GitHub-репо (disposable-email-domains и др.). Опционально запускается на старте.
- **`Core/Classifier.cs`** (258) — принимает email, возвращает `Verdict` (clean/duplicate/…). Проверки в порядке: RFC-syntax → typo-fix → disposable → role → trap → …
- **`Core/EmailHelpers.cs`** (212) — normalize (lower, gmail-strict удаляет "+"-теги и точки в local для gmail.com), split, syntax-check.
- **`Core/TypoFixer.cs`** (296) — heuristic для `gmial.com → gmail.com`, `yhaoo → yahoo`. Levenshtein на топ-10 популярных доменов.
- **`Core/CleanupWorker.cs`** (335) — стрим-чтение файла, батч N адресов, классификация, запись в 9 файлов (`clean.txt`, `rejected_*.txt`) в `<OutputRoot>/cleanup/<basename>/`. Отчёт в `SnapshotBatch` для UI.

### 5.4. SMTP pipeline

- **`Core/MxResolver.cs`** (246) — DNS MX lookup, кэш в памяти (LRU по времени).
- **`Core/SmtpProber.cs`** (684) — сам SOCKS5 + SMTP handshake:
  1. Открываем TCP через SOCKS5 к `mx.host:25`
  2. Читаем баннер, EHLO
  3. `MAIL FROM: <smtpMailFrom>` (или пусто)
  4. `RCPT TO: <target>` — читаем код 250 (ok) / 550 (invalid) / 421 (greylist) / 4xx (temp)
  5. `RSET` + `QUIT`
  6. Маппинг в `SmtpVerdict` (Valid / Invalid / Greylist / ConnectFail / …)
- **`Core/SmtpValidateWorker.cs`** (471) — orchestrator: N воркеров на прокси, session-delay, recovery на N-consec-errors, retry, финальная запись `smtp_valid.txt` / `smtp_invalid.txt` / `smtp_greylist.txt`.

### 5.5. Analyze pipeline

- **`Core/NameDb.cs`** (135) — SQLite → RAM (`_exactMap` + `_variantMap`), lookup `Get(local_part)` → `NameLookupResult { Name, Gender, Culture, Tier, AllCultures[] }`.
- **`Core/AiClient.cs`** (391) — OpenAI (`/v1/chat/completions`) и Anthropic (`/v1/messages`), общий `SystemPrompt` для gender+country классификации, JSON-структурированный ответ, retry/backoff на 429/5xx.
- **`Core/AnalyzeCache.cs`** (159) — SQLite-кэш ответов по SHA1(prompt+provider+model+email).
- **`Core/AnalyzeWorker.cs`** (788) — pipeline:
  1. **DB-lookup**: пробегает все emails через `NameDb`. Экономит до 60% AI-запросов на gmail-базах.
  2. **AI pass 1**: непокрытые батчами `analyzeAiBatchSize` (default 20 email/req), до `analyzeMaxConcurrent` (default 8) параллельно.
  3. **AI pass 2** (если `analyzeTwoPass=true`): low-confidence из pass1 переотправляются к Anthropic Haiku (провайдер настраивается `pass2Provider`), serial dispatch с `analyzeBatchDelay` (default 4s) — обход rate-limit'а Anthropic (10K output tokens/min).
  4. **ISO-scoring**: TLD-домен + AllCultures → финальный `country`.
  5. Финальные CSV/JSON в `<OutputRoot>/analyze/<basename>/`.

### 5.6. DomainScanner

- **`Core/DomainScanner/DmApiClient.cs`** (85) — `domainsmonitor.com` REST-клиент, стрим доменов зоны.
- **`Core/DomainScanner/NsResolver.cs`** (232) — DNS NS и A lookups через `DnsClient` (batch + timeout).
- **`Core/DomainScanner/NsProviderDetector.cs`** (69) — mapping NS-хостов → provider name (`selectel.ru → 'Selectel'`, `cloudflare.com → 'Cloudflare'`).
- **`Core/DomainScanner/DomainDb.cs`** (406) — SQLite `domains.db`: producer/consumer, WAL, батч-writer через очередь. `SearchAsync`, статистика по провайдерам, `LatestScan`.
- **`Core/DomainScanner/DomainScanWorker.cs`** (241) — orchestrator: стрим из DmApi → N-parallel resolve → provider-detect → фильтр по `targetNs`/`targetProvider` → батч в БД. Прогресс через `IProgress<ScanProgress>`.

### 5.7. DomainsManager (ISPmanager / Zomro)

- **`Core/DomainsManager/IspModels.cs`** (30) — `IspAccount { Host, Username, Password, DisplayName, Provider }`, `IspDomain { Name, Ip, Owner }`, `DnsRecord { Name, Type, Value, Key, Ttl, Priority }`.
- **`Core/DomainsManager/IspApiClient.cs`** (364) — XML/JSON API клиент:
  - `AuthAsync(username, password) → sessionId`
  - `ListDomainsAsync(sessionId) → List<IspDomain>`
  - `GetRecordsAsync(sessionId, domain) → List<DnsRecord>`
  - `AddDomainAsync(sessionId, domain, dtype = "master", …)`
  - `UpsertRecordAsync(sessionId, domain, rtype, name, value, ttl, priority=null)` — key idea: если запись есть, edit по rkey; иначе add
  - `DeleteRecordAsync(sessionId, domain, rkey)`
  - `DeleteDomainAsync(sessionId, domain)`
  - Base URL: `https://{host}[:1500]/dnsmgr?func=…&out=xml` (порт 1500 стандартный, кастомный можно указать в hostname)
  - SSL: `ServerCertificateCustomValidationCallback = _ => true` (самоподписанные сертификаты у панелей)

### 5.8. Postmaster (mail.ru)

- **`Core/Postmaster/PostmasterAccount.cs`** (16) — `{ Username, Password, RefreshToken, DisplayName }`. **Пароль в plain — согласовано юзером** ([memory `feedback_preserve_user_config`](../.claude/…/memory/feedback_preserve_user_config.md)).
- **`Core/Postmaster/MailRuOAuthClient.cs`** (135) — `RefreshAccessAsync(refreshToken) → { AccessToken, RefreshToken (может обновиться), ExpiresIn }`. Endpoint `https://o2.mail.ru/token`, `grant_type=refresh_token`, `client_id=postmaster_api_client`. Для получения первичного refresh_token юзер проходит **Manual OAuth** в браузере (URL в `ConnectMailRuDialog`).
- **`Core/Postmaster/PostmasterApiClient.cs`** (255) — Bearer-API:
  - `RegListAsync() → List<PostmasterDomain>` (`/ext-api/reg-list/`)
  - `TroublesListAsync(domain) → List<PostmasterTrouble>` (`/ext-api/troubles-list/`) — **фильтр `code==0` пропускаем** (это info-entries, не проблемы)
  - `StatListAsync(domain, from, to) → …` (`/ext-api/stat-list/`)
  - Rate-limit клиента: 10 req/min per (domain, account)
- **`Core/Postmaster/MailRuWebSession.cs`** (277) ★ — web-сессия для действий, которых нет в публичном OAuth API:
  - `LoginAsync(password)`:
    1. `GET account.mail.ru/login` (базовые cookies)
    2. `POST auth.mail.ru/cgi-bin/auth` с `{Login, Password, Domain, saveauth=1, FailPage=""}` → cookie `Mpop`
    3. `GET postmaster.mail.ru/add` (активация сессии постмастера, csrftoken)
  - `AddDomainAndGetTxtAsync(domain)`:
    1. `GET /add` → парсим `csrfmiddlewaretoken` из HTML
    2. `POST /add/` с `{name=domain, csrfmiddlewaretoken}` → 302 → `/DOMAIN/verify/`
    3. Regex `mailru-verification[""'\s:]+([0-9a-fA-F]{16,})` → возвращаем HEX
  - `RequestVerifyAsync(domain) → VerifyRequestResult` ★:
    1. `GET /DOMAIN/verify/` (refresh csrftoken)
    2. `POST /DOMAIN/verify/` form=`verifier=3` (DNS-режим) + header `X-CSRFToken: <cookie>` + `X-Requested-With: XMLHttpRequest` → `{status:pending, task_id, message}`
    3. **Polling** (12 попыток × 10 сек) `POST /DOMAIN/verify/` с `task_id=X` → `{status:success|failed, message}`
    4. Возвращаем `VerifyRequestResult { Status, Message, TaskId, HttpStatus, BodySnippet, IsSuccess, IsFailed }`

### 5.9. Views (ключевые окна)

- **`MainWindow.xaml.cs`** — оболочка, 5 tab'ов, tab-specific sidebars, keyboard shortcuts (Ctrl+O = open, Ctrl+Enter = start).
- **`SettingsDialog.cs`** (742) — 3 страницы:
  1. **AI-ключи** — OpenAI/Anthropic/Google keys, активный провайдер, кнопки «Тест подключения»
  2. **Приложение** — тема, папка вывода, тумблеры блоклистов, сброс окна
  3. **Продвинутое** — открыть config.json, папку логов, сбросить настройки (кроме ключей/прокси), about-инфа с путём к `names.db`
- **`AddIspAccountDialog.cs`** (129) — форма подключения ISPmanager (host/user/pass/name), проверка `AuthAsync` перед сохранением
- **`DnsRecordsWindow.cs`** (237) — список DNS-записей домена, кнопки Add/Edit/Delete, quick-add SPF/DKIM/DMARC preset
- **`EditDnsRecordDialog.cs`** (149) — редактирование одной записи (name/type/value/ttl/priority)
- **`ConnectMailRuDialog.cs`** (261) — форма подключения mail.ru: login/pass (обязательно) + refresh_token (опционально, через Manual OAuth в браузере), тест `LoginAsync` + `RefreshAccessAsync` перед сохранением
- **`PostmasterDomainsWindow.cs`** (496) — дашборд verified-доменов, слева список, справа детали (troubles, stat за 30 дней)
- **`MailRuVerifyDialog.cs`** (368) ★ — авто-верификация домена:
  - Кнопка **«▶ Верифицировать полностью автоматически»** (5 шагов: login → add → TXT в ISP → 45s пауза → RequestVerifyAsync)
  - Кнопка **«🔄 Только проверить сейчас»** (2 шага: login → RequestVerifyAsync без пересоздания TXT — для случая когда «TXT already exists»)
  - Try/catch на `already exists / уже существует` при UpsertRecordAsync — переиспользуем существующую запись
  - `HandleVerifyResult(vr)`: `success` → «✅ Домен подтверждён» + подтянуть `TroublesListAsync`; `failed` → «❌ TXT не найдена или не совпадает» с советом
- **`ProxyCheckDialog.cs`** (324) — массовая проверка SOCKS5-прокси через ping-connection

---

## 6. Уже принятые архитектурные решения

### 6.1. `Environment.ProcessPath` вместо `AppContext.BaseDirectory` для путей рядом с exe

**Почему:** для single-file self-contained exe `AppContext.BaseDirectory` в некоторых случаях возвращает temp-папку с распакованным native-контентом, а не реальную папку exe. Из-за этого `Config.cs` не находил `config.json` рядом с exe и создавал новый пустой в fallback-пути — «пропадали» все ключи.

**Что: везде** используем:
```csharp
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
```
См. `Core/Config.cs:134`, `Core/Paths.cs:24`.

### 6.2. Пароли и API-ключи в plain-JSON

**Решение:** пароли (mail.ru, ISPmanager), API-ключи (OpenAI, Anthropic), refresh-токены — **всё в открытом виде в `config.json`**.

**Обоснование (юзер):** «Это моя личная машина. Windows credential manager, keychain, шифрование мастер-паролем — не хочу, только тратить время. Все на диске в одном файле — устраивает.»

**Следствие:** никогда не заливать `config.json` в git / архивы раздачи / сторонние сервисы. Копируем в раздачу только `Fakunator.exe`.

### 6.3. `config.json` = user data — никогда не перезаписывать

При деплое копируем **только** `Fakunator.exe`. `config.json` рядом — не трогать. Если случайно пересобрали пустой config — восстанавливаем вручную (данные ценные, ключи и пароли теряются).

### 6.4. Debounced-save конфига (500 ms)

При изменении настроек `Config.Save()` не пишет сразу — ставит `DispatcherTimer` на 500ms. Много быстрых изменений (щёлкание чекбоксов) = одна запись файла.

Immediate flush — только `SaveNow()` (при exit-e окна, ResetToDefaults, и т.д.).

### 6.5. Mail.ru: **web-сессия + OAuth API**, а не только OAuth

**Публичный OAuth API mail.ru НЕ умеет:**
- добавлять домен в постмастер
- запускать проверку DNS-верификации

Всё это работает **только через веб-формы** (Django + CSRF), которые требуют залогиненную сессию. Поэтому:
- `MailRuOAuthClient` + `PostmasterApiClient` — для чтения статистики (reg-list, troubles, stat)
- `MailRuWebSession` — для write-операций (add domain, verify DNS)

Пароль нужен для web-сессии. Refresh_token — для OAuth API (без него write-часть работает, чтение — нет).

### 6.6. Mail.ru verify: `verifier=3` + `X-CSRFToken` + polling

Полный правильный flow — см. `[reference_mailru_postmaster_verify.md]` в memory. Кратко:
1. `POST /DOMAIN/verify/` form `verifier=3` + header `X-CSRFToken: <cookie csrftoken>` → `{status:pending, task_id}`
2. Wait 10s, `POST /DOMAIN/verify/` form `task_id=X` → `{status:success|failed}`
3. Polling до 12 попыток × 10 сек.

Без `verifier` mail.ru молча отдаёт `{status:failed}` без объяснения — легко подумать что endpoint не тот. **CSRF работает через cookie + header** (Django-style), не через form-field.

### 6.7. Single-file self-contained exe

**Почему:** одна раздача (`Fakunator.exe`) без установки .NET runtime у юзера, без dll-соседей.

**Trade-off:** ~73 MB (весь Runtime + WPF + SDK). Полностью автономно.

**Компиляция:** `-p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true`.

### 6.8. WPF, а не WinUI 3 / Avalonia / MAUI

- WinUI 3: молодой, packaging headache, MSIX-ограничения
- Avalonia: cross-platform не нужен
- MAUI: слишком много абстракций для desktop-only

WPF в .NET 8 = stable, богатый control-set, XAML понятный, полно материалов, single-file publish работает без плясок.

### 6.9. Никакого DI-контейнера

Проект компактный, singleton'ы (`Config.Current`, `NameDb`) достаточны. DI-фреймворки + reflection = проблемы с trimming и single-file.

### 6.10. Тема: swap ResourceDictionary, не rebuild control tree

`App.SwitchTheme("dark"|"light")` меняет `Application.Current.Resources.MergedDictionaries[1]`. Все brushes/styles резолвятся по `SetResourceReference` → автоматом обновляются. Custom-controls (`ParticleBackground`, `SparklineChart`) слушают `App.ThemeChanged` и делают `InvalidateVisual()` для перерендера cached-picture.

---

## 7. Что пробовали и от чего отказались

| Отказались от | Причина |
|---|---|
| **Python/PySide6 версия** (fakunator/) | Deploy-геморрой (PyInstaller onefile ~99 MB, антивирусы ругаются), медленно на 10M-строчных CSV, тяжело работать со SQLite thread-safely. Оставлено как legacy — не трогаем. |
| **WebView2 (Chromium в окне)** для авто-верификации mail.ru | «Не хочу никакой встроенный браузер — обещал автоматику полную». Полная HTTP-эмуляция сессии — правильный путь. |
| **Password grant type** для OAuth mail.ru (`grant_type=password`) | Возвращает `bad client`. Реально работает только Manual OAuth flow (authorization_code через браузер). |
| **API-endpoint `/ext-api/verify/`** для запуска верификации | Endpoint существует, но требует web-сессию (Bearer не хватает). Проще сразу через web-сессию всё делать. |
| **Отправка пароля mail.ru через keychain** | Юзер согласовал plain в config.json. |
| **Ручной ввод HTML-source страницы верификации** | Слишком костыльно. Заменили автоматическим `AddDomainAndGetTxtAsync` — парсим `mailru-verification` из ответа. |
| **X-CSRFToken как единственный CSRF-механизм** для `/add/` | На `/add/` работает `csrfmiddlewaretoken` из form. На `/verify/` — только `X-CSRFToken` в header. Оба варианта в коде. |
| **QListWidget с item-widgets + HTML `<table>` в QLabel** (legacy Python) | Qt HTML не поддерживает многоколоночные ряды нормально → `QHBoxLayout+widgets`. |
| **UI cleanup до disconnect сигналов** при teardown воркера (legacy Python) | SIGABRT в `QListWidget` с item-widgets. Правильно: сначала disconnect, потом clear. |
| **Auto-архивирование раздачи в zip после каждой сборки** | Медленно и бессмысленно — итерируем через bin/publish + copy exe. Zip только под релизный tag. |

---

## 8. Текущие проблемы (что известно / незакрыто)

| Проблема | Статус | Комментарий |
|---|---|---|
| Warnings при сборке (`CS0108`, `CS0168`) в `AddDomainDialog`, `EditDnsRecordDialog`, `DomainsManagerViewModel` | ⚠ несмертельно, косметика | `Style(TextBox)` скрывает `FrameworkElement.Style` — переименовать в `_style` или `new`; `catch (Exception ex)` без использования — убрать `ex` |
| Debug-логирование в `MailRuVerifyDialog` (`AppendLog` с `body[0..400]`) — оставлено для диагностики | 🟡 подчистить перед следующим релизом | Заменить на компактное сообщение |
| `PollVerifiedAsync` в MailRuVerifyDialog оставлен но не вызывается (заменён на HandleVerifyResult) | 🟡 dead code | Можно удалить или оставить как задел |
| Domain scanner: если `domainsMonitorApiKey` пуст — падает молча в UI | 🟡 UX | Показать явное сообщение «нужен ключ, идёт в Настройки → AI-ключи» |
| PostmasterDomainsWindow при большом списке (100+ доменов) может тормозить на рендере | 🟡 не проверено | Реальных данных для теста нет, откладываем |
| ISPmanager: не проверены edge-cases с не-стандартным портом (не `:1500`) | 🟡 | В коде поддержано, но реальных панелей с другим портом у юзера нет |
| `Fakunator_v2.0.0_Lite/` — устаревшая framework-dependent раздача | 🟢 не используется | Можно удалить, но никому не мешает |

---

## 9. Что уже реализовано (feature-status)

### 🟢 Полностью работает

- ✅ **Cleanup** — 9 категорий, стрим-обработка, gmail-strict-mode, disposable/role/typo detection
- ✅ **Blocklist auto-update** из GitHub (тумблер в Settings)
- ✅ **SMTP-валидация** через SOCKS5 (EHLO/RCPT TO/RSET/QUIT), rotation, retry, recovery, MX-cache
- ✅ **Analyze** — DB-lookup + OpenAI/Anthropic AI pass 1 + optional pass 2 + ISO-scoring, CSV/JSON экспорт, кэш ответов
- ✅ **Domain Scanner** — стрим из domainsmonitor.com + parallel NS/A resolve + provider-detect + SQLite persist + фильтры
- ✅ **Domains Manager (ISPmanager)** — список доменов, DNS-записи, add/edit/delete, upsert TXT
- ✅ **Mail.ru Postmaster** — подключение (login+pass+refresh), просмотр verified-доменов (`PostmasterDomainsWindow`), troubles (с фильтром `code==0`), stat
- ✅ **Автоматическая DNS-верификация домена в mail.ru** — полный flow (add в постмастере → TXT в ISP → 45s → verify через web-session) + «только проверить» без пересоздания TXT
- ✅ **Настройки** — 3 страницы, все опции работают
- ✅ **Тёмная/светлая тема** — swap ResourceDictionary
- ✅ **Window geometry persist** — восстанавливается на старте
- ✅ **Config debounced save** (500ms)
- ✅ **Single-file self-contained publish**
- ✅ **Path resolution** через `Environment.ProcessPath` — работает и для dev и для single-file exe

### 🟡 Частично / без критической проверки

- 🟡 **Spamhaus DBL** тумблер в UI — интеграция не проверена на боевых данных
- 🟡 **AI pass 2** (двухпроходный анализ) — работает, но rate-limit Anthropic очень чувствительный, редко тестируем
- 🟡 **Analyze cache** SHA-fingerprint — на второй прогон корректно попадает, но UX кэш-хитов не показывается

### 🔴 Не реализовано (не нужно / отложено)

- 🔴 Backup config'а перед изменениями (юзер: «не надо, руками копирую»)
- 🔴 Мультиязычность UI (только русский)
- 🔴 Cross-platform (Windows-only навсегда)
- 🔴 Автообновление exe (юзер: «сам скачаю»)
- 🔴 Отправка почты (SMTP-проверка = only probe, не отправка кампаний)

---

## 10. Что сейчас в работе (открытые треды)

**На момент 2026-08-29:**

- ✅ ЗАВЕРШЕНО в этой сессии: полностью автоматическая mail.ru DNS-верификация (endpoint найден, `verifier=3` + `X-CSRFToken` + task_id polling; протестирован на реальном домене `2025-lord-smotrim.ru` → success).
- ✅ ЗАВЕРШЕНО: фикс single-file exe path → config («пропали ключи» → исправлено `Environment.ProcessPath`).
- ✅ ЗАВЕРШЕНО: фильтр `code==0` в `TroublesListAsync` (info-entries больше не считаются проблемами).
- ✅ ЗАВЕРШЕНО: try/catch на «TXT already exists» в `MailRuVerifyDialog` — переиспользуем существующую запись при retry.
- ✅ ЗАВЕРШЕНО: кнопка «🔄 Только проверить сейчас» в диалоге верификации.

**Активных незакрытых тредов нет** — на момент готовности этого README всё, что запрашивал юзер в текущей сессии, сделано и задеплоено (PID Fakunator.exe в `Fakunator_v2.0.0_SelfContained/`).

---

## 11. Важные ограничения и требования юзера

Это правила, которые я нарушал/забывал в прошлом — они дублируются в persistent
memory (`~/.claude/projects/…/memory/`), но для новой сессии лучше видеть их
сразу в README.

| # | Правило | Почему возникло |
|---|---|---|
| 1 | **Никогда не перетирай `config.json` юзера** | Там хранятся прокси / API-ключи / пароли / ISP-аккаунт / mail.ru-refresh. Ежели пересобираешь раздачу — копируй только `Fakunator.exe`, `config.json` рядом не трогай. |
| 2 | **Не пересобирай zip-архивы каждую итерацию** | Zip только под релиз. Для итерации: `dotnet publish` → `Copy-Item exe` → run. |
| 3 | **Build workflow: сначала фича, exe в конце** | Пока не готово — гоняй логику в scratchpad-`.csproj` (Console app с ссылкой на нужные `.cs`). Компилировать WPF exe при каждом изменении — медленно и маскирует баги. |
| 4 | **Verify перед деплоем** | Не публиковать exe пока сам не проверил (тестовый скрипт / API-probe / клик в UI). Не можешь проверить — говори явно, не деплой молча. |
| 5 | **Не перезапускай exe без разрешения юзера** | Если что-то крутится в UI — это работа юзера, а не dev-сессия. |
| 6 | **GUI запускать detached** | Для WPF `Start-Process` OK (окно живёт после завершения PowerShell-хост-процесса). Для Python-legacy — `cmd /c start "" python …`. |
| 7 | **dotnet publish — только через полный путь** | `Get-Command dotnet` находит x86-stub без SDK. Использовать `"C:\Program Files\dotnet\dotnet.exe"`. |
| 8 | **`Path.GetDirectoryName(Environment.ProcessPath)`** для путей рядом с exe | В single-file self-contained `AppContext.BaseDirectory` может указывать на temp. |
| 9 | **Не изобретай костыли** | Юзер согласовал plain-пароль mail.ru → так и хранится. Не предлагай keychain/шифрование/master-password если не просили. |
| 10 | **Не хардкодь API-ключи в код** | Всё через `Config.Current` — иначе ключи попадают в exe и во все раздачи. |
| 11 | **Кредо юзера уже в config** — не запрашивать повторно | `batalyora02@mail.ru / Win113322@` (mail.ru), Zomro `user5995281 / U5WA5vZ5Idwp` — ищи в config.json деплоя, не спрашивай. |
| 12 | **Legacy Python/PySide6 не трогаем** | `fakunator/`, `run_fakunator.py`, `scripts/`, `Release_v1.5.0*/` — история и потенциальный fallback. Все новые фичи только в `FakunatorWPF/`. |

**Legacy Qt-правила** (были актуальны в Python-фазе, теперь неактуальны, но
оставлены для истории на случай возврата):
- HTML `<table>` в `QLabel` не работает → `QHBoxLayout + widgets`
- QThread signal race при teardown → сначала disconnect сигналов, потом UI cleanup

---

## 12. Следующие задачи (backlog)

Открытых задач от юзера в текущей сессии не осталось — всё
запрошенное сделано. Ниже — потенциальные направления, не приоритизированные:

**UX-полировка:**
- Убрать debug-логирование в `MailRuVerifyDialog` (сейчас показывает `body[0..400]` — оставлено для диагностики)
- Fix warnings CS0108 (Style-shadow) и CS0168 (unused ex) — 3 файла
- Domain scanner: показать явное сообщение при пустом `domainsMonitorApiKey` вместо тихого падения
- Удалить dead-code `PollVerifiedAsync` в `MailRuVerifyDialog` (не используется)

**Feature-возможности (если попросит юзер):**
- Autofill DKIM/SPF/DMARC пресетов после успешной mail.ru верификации (сейчас юзер добавляет вручную через DnsRecordsWindow)
- Массовая mail.ru регистрация N доменов одним нажатием (сейчас по одному через диалог)
- Экспорт списка verified доменов + их troubles в CSV
- История scan'ов в DomainScanner UI (у нас есть таблица `scan_history`, но не показана)

**Технический долг:**
- Убрать `Fakunator_v2.0.0_Lite/` если совсем никому не нужна
- Разобрать `bin/`, `obj/` — не в git, но заваливают файловую систему

**Приоритет:** ждём указаний юзера. Пока задач нет — не начинаем инициативно.

---

## Приложение A — команды сборки и деплоя (шпаргалка)

```powershell
# ── Полный цикл: build + deploy + launch ─────────────────────────────

$env:PATH = "C:\Program Files\dotnet;" + $env:PATH

# 1. Kill running instance
Get-Process Fakunator -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. Build single-file self-contained
cd "c:\Users\Александр\Desktop\Clear Base\FakunatorWPF"
& "C:\Program Files\dotnet\dotnet.exe" publish `
    -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -o "c:\Users\Александр\Desktop\Clear Base\FakunatorWPF\bin\publish"

# 3. Copy ONLY exe (config.json не трогаем!)
Copy-Item -Force `
  "c:\Users\Александр\Desktop\Clear Base\FakunatorWPF\bin\publish\Fakunator.exe" `
  "c:\Users\Александр\Desktop\Clear Base\Fakunator_v2.0.0_SelfContained\Fakunator.exe"

# 4. Launch (detached, окно живёт)
Start-Process `
  -FilePath "c:\Users\Александр\Desktop\Clear Base\Fakunator_v2.0.0_SelfContained\Fakunator.exe" `
  -WorkingDirectory "c:\Users\Александр\Desktop\Clear Base\Fakunator_v2.0.0_SelfContained"

# 5. Verify
Get-CimInstance Win32_Process -Filter "Name='Fakunator.exe'" | Select-Object ProcessId, ExecutablePath
```

## Приложение B — быстрый тест интеграции без пересборки WPF

Для проверки логики Core-модулей (напр. `MailRuWebSession`, `IspApiClient`)
без 60-секундного `dotnet publish` — временный Console-проект в scratchpad:

```powershell
# 1. Создать csproj с ссылкой на нужные .cs
mkdir C:\Temp\test1; cd C:\Temp\test1
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <Compile Include="c:/Users/Александр/Desktop/Clear Base/FakunatorWPF/Core/Postmaster/MailRuWebSession.cs"/>
  </ItemGroup>
</Project>
'@ | Set-Content test1.csproj

# 2. Program.cs с логикой теста
# 3. Запустить:
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH
$env:MR_LOGIN = "batalyora02@mail.ru"; $env:MR_PASS = "Win113322@"
& "C:\Program Files\dotnet\dotnet.exe" run
```

Так проверил mail.ru verify flow за 15 секунд вместо ждать пересборки WPF exe.

---

## Приложение C — как продолжить работу в новой сессии Claude

1. **Прочитай этот README целиком** — он единственная точка правды.
2. **Прочитай `~/.claude/projects/c--Users-----------Desktop-Clear-Base/memory/MEMORY.md`** — там индекс правил. Все связанные `.md` файлы линкуются с описанием.
3. **Не пытайся угадать состояние** — если что-то из README расходится с кодом, читай код и правь README на месте.
4. **Кредо юзера** уже в `Fakunator_v2.0.0_SelfContained\config.json` — не спрашивай пароли повторно.
5. **Первое что открой** после Read этого файла: `Core/Config.cs`, `Views/MailRuVerifyDialog.cs`, `Core/Postmaster/MailRuWebSession.cs` — там сконцентрирована самая свежая логика.
6. **Не начинай инициативно рефакторить или чинить warnings** — юзер даст задачу, тогда действуй.

Всё. README актуален на **2026-08-29**. Если что-то поменялось — фикси в тот же
момент, не проходи мимо.
