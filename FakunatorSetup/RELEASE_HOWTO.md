# Как выпускать релиз Факунатора

## Один раз при первичной настройке

1. Заведи GitHub-репозиторий (пример: `github.com/rumobik/fakunator`).
2. Открой `FakunatorSetup/MainWindow.xaml.cs`, замени константу:
   ```csharp
   private const string GITHUB_REPO = "rumobik/fakunator";
   ```
3. Пересобери `FakunatorSetup.exe`:
   ```
   dotnet publish FakunatorSetup/FakunatorSetup.csproj -c Release -r win-x64 ^
     -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
4. Залей `FakunatorSetup.exe` (604 KB) на любой хостинг куда юзеры будут ходить его качать (сайт, Telegram-канал, что угодно). Этот файл **на всю жизнь** — его больше менять не надо.

## Каждый релиз (например 2.6.0)

1. Собери:
   - `Fakunator.exe` (single-file self-contained) — обычным publish'ем из FakunatorWPF
   - `data.zip` — упакованная папка `data/` (blocklists + domains.db + names.db)
2. Посчитай SHA-256 (опционально, для проверки целостности):
   ```
   Get-FileHash Fakunator.exe -Algorithm SHA256
   Get-FileHash data.zip      -Algorithm SHA256
   ```
3. Скопируй `latest.json.example` → `latest.json`, поправь:
   - `version`
   - `exeSize` / `dataSize` (реальные байты)
   - `exeSha256` / `dataSha256` (если посчитал)
   - `notes` (что нового — покажется в установщике)
4. На GitHub: **Releases → Draft new release**:
   - Tag: `v2.6.0`
   - Прикрепи 3 файла: `Fakunator.exe`, `data.zip`, `latest.json`
   - Publish release.

Всё. Через 30 секунд юзеры, у которых стоит уже установленный Факунатор,
увидят баннер "доступно обновление 2.6.0" (когда допишем auto-updater в самой программе).
Новые юзеры скачают ваш `FakunatorSetup.exe`, он сходит на GitHub, увидит новый релиз,
скачает и поставит 2.6.0 автоматически.

## Как GitHub находит "latest"

Установщик ходит по URL:
```
https://github.com/rumobik/fakunator/releases/latest/download/latest.json
```
GitHub автоматически редиректит `/latest/` на самый свежий Release (по дате publish).
Так что достаточно опубликовать релиз — установщик подхватит его без правок.
