# Пакует релиз Fakunator в split-архивы: code.zip + deps.zip (+ data-seed.zip).
# Использование:  .\pack-release.ps1  [-Version 2.5.1]  [-Out C:\path\to\release_stage]
# Требует: dotnet 8 в PATH, git-репозиторий рядом (для чтения версии из csproj если Version не задан).

param(
    [string]$Version = "",
    [string]$Out    = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'FakunatorWPF\FakunatorWPF.csproj'
if (-not (Test-Path $csproj)) { throw "csproj not found: $csproj" }

# ── Version: если не задан — берём из csproj ────────────────────
if ([string]::IsNullOrWhiteSpace($Version)) {
    $xml = [xml](Get-Content $csproj)
    $Version = $xml.Project.PropertyGroup.Version
    if (-not $Version) { throw "Version tag not found in csproj" }
}
Write-Host "Packing Fakunator v$Version" -ForegroundColor Green

# ── Пути ────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $env:TEMP "fakunator_release_$Version"
}
$publish = Join-Path $Out 'publish'
$codeDir = Join-Path $Out 'code'
$depsDir = Join-Path $Out 'deps'
if (Test-Path $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $publish, $codeDir, $depsDir -Force | Out-Null

# ── Publish (framework-dep, multi-file) ────────────────────────
Write-Host "`n[1/5] dotnet publish..." -ForegroundColor Cyan
& "C:\Program Files\dotnet\dotnet.exe" publish $csproj -c Release -r win-x64 `
    -p:SelfContained=false -p:PublishSingleFile=false `
    -o $publish --nologo -v q 2>&1 | Select-String -Pattern 'error|Ошибок' | Select-Object -First 3

# Удаляем xml-doc (не нужны в рантайме)
Get-ChildItem $publish -Filter '*.xml' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# ── Раскладка code / deps ──────────────────────────────────────
# code = наш выхлоп: Fakunator.exe + Fakunator.dll + .deps.json + .runtimeconfig.json
# deps = всё остальное (NuGet DLL, native libs)
Write-Host "`n[2/5] Splitting into code / deps..." -ForegroundColor Cyan

$codeFiles = @(
    'Fakunator.exe',
    'Fakunator.dll',
    'Fakunator.deps.json',
    'Fakunator.runtimeconfig.json'
)
$codeFiles | ForEach-Object {
    $src = Join-Path $publish $_
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination $codeDir -Force }
    else { Write-Warning "code file missing: $_" }
}
# Всё остальное — в deps
Get-ChildItem $publish -File | Where-Object { $codeFiles -notcontains $_.Name } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $depsDir -Force }

Write-Host "  code: $((Get-ChildItem $codeDir -File).Count) файлов, $([math]::Round((Get-ChildItem $codeDir -File | Measure-Object -Sum Length).Sum/1MB,2)) MB"
Write-Host "  deps: $((Get-ChildItem $depsDir -File).Count) файлов, $([math]::Round((Get-ChildItem $depsDir -File | Measure-Object -Sum Length).Sum/1MB,2)) MB"

# ── Zip'ы ──────────────────────────────────────────────────────
Write-Host "`n[3/5] Zipping..." -ForegroundColor Cyan
$codeZip = Join-Path $Out 'code.zip'
$depsZip = Join-Path $Out 'deps.zip'
Compress-Archive -Path (Join-Path $codeDir '*') -DestinationPath $codeZip -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $depsDir '*') -DestinationPath $depsZip -CompressionLevel Optimal -Force

# ── data-seed.zip (для FakunatorSetup.exe, только при первичной установке) ──
Write-Host "`n[4/5] Building data-seed.zip..." -ForegroundColor Cyan
$dataSeedZip = Join-Path $Out 'data-seed.zip'
$dataStage   = Join-Path $Out 'data-seed'
New-Item -ItemType Directory -Path $dataStage -Force | Out-Null

$seedSource = Join-Path $root 'Fakunator_v2.5.0_Lite\data'
if (Test-Path $seedSource) {
    # Всё из shipped data/ КРОМЕ user-owned domains.db*
    Get-ChildItem $seedSource -File | Where-Object { $_.Name -notlike 'domains.db*' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dataStage -Force }
    Compress-Archive -Path (Join-Path $dataStage '*') -DestinationPath $dataSeedZip -CompressionLevel Optimal -Force
    Write-Host "  data-seed: $((Get-ChildItem $dataStage -File).Count) файлов (без domains.db*)"
} else {
    Write-Warning "seed source not found, skipping data-seed.zip"
}

# ── latest.json ─────────────────────────────────────────────────
Write-Host "`n[5/5] Writing latest.json..." -ForegroundColor Cyan
function Sha256 { param($p) (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower() }
function Size   { param($p) (Get-Item -LiteralPath $p).Length }

$repo = 'mobikru/fakunator'
$dlBase = "https://github.com/$repo/releases/download/v$Version"

$depsHash = Sha256 $depsZip
$codeHash = Sha256 $codeZip

$manifest = [ordered]@{
    version       = $Version
    code_url      = "$dlBase/code.zip"
    code_size     = Size $codeZip
    code_sha256   = $codeHash
    deps_url      = "$dlBase/deps.zip"
    deps_size     = Size $depsZip
    deps_sha256   = $depsHash
    # deps_version = первые 12 hex символов хеша deps.zip.
    # In-app updater сравнит со своим локальным значением и качнёт deps.zip только если отличается.
    deps_version  = $depsHash.Substring(0, 12)
    data_seed_url = if (Test-Path $dataSeedZip) { "$dlBase/data-seed.zip" } else { "" }
    data_seed_size = if (Test-Path $dataSeedZip) { Size $dataSeedZip } else { 0 }
    notes         = ""
}

$manifestJson = $manifest | ConvertTo-Json -Depth 3
$manifestJson | Set-Content -LiteralPath (Join-Path $Out 'latest.json') -Encoding UTF8

# ── Summary ─────────────────────────────────────────────────────
Write-Host "`n=== RELEASE READY ===" -ForegroundColor Green
Get-ChildItem $Out -File | Sort-Object Length -Descending | ForEach-Object {
    "  {0,-24} {1,10:N0} bytes" -f $_.Name, $_.Length
}
Write-Host "`nПуть: $Out"
Write-Host "Загрузить одной командой:"
Write-Host "  gh release create v$Version `"$Out\code.zip`" `"$Out\deps.zip`" `"$Out\data-seed.zip`" `"$Out\latest.json`" --repo $repo --title `"Fakunator $Version`" --notes `"...`"" -ForegroundColor Yellow
