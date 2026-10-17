# winget-manifest.ps1 — ЗАПОЛНЕНИЕ МАНИФЕСТОВ WINGET ДЛЯ ВЫПУСКА.
#
#   pwsh -NoProfile -File .\tools\winget-manifest.ps1 -Sums .\upd-build\dist-next\SHA256SUMS.txt
#   pwsh -NoProfile -File .\tools\winget-manifest.ps1 -Sums .\upd-build\dist-next\SHA256SUMS.txt -ReleaseDate 2026-10-17
#   pwsh -NoProfile -File .\tools\winget-manifest.ps1 -Sums <файл сумм> -Check      # только сказать, что разойдётся
#
# ЧТО ЗАПОЛНЯЕТ (и откуда берёт числа):
#   * `PackageVersion` во всех трёх файлах — из `<Version>` `src\DshPanel\DshPanel.csproj`
#     (единственный источник версии в проекте: из него же её берут установщик и сторож выпуска);
#   * тег в `InstallerUrl` (installer) и `ReleaseNotesUrl` (locale) — вместе с версией: `check-release`
#     падает кодом 1, если ссылка ведёт на один тег, а `PackageVersion` называет другой;
#   * `InstallerSha256` (installer) — ИЗ ФАЙЛА СУММ, а не из локальной пересборки: байты выпуска
#     и байты машины разные (`docs\RELEASE-V2.md` §3 п.11), а манифест публикуется под выпуск;
#   * `ReleaseDate` — ТОЛЬКО параметром `-ReleaseDate`: день выпуска не выводится ни из чего.
#     Не назван — строка остаётся как была, и об этом говорится прямо.
#
# ЧЕГО НЕ ДЕЛАЕТ: в сеть не ходит вовсе; ничего не скачивает и не проверяет на GitHub; не ставит
# тег, не публикует; не правит прочие поля манифестов (описания, теги, ProductCode).
#
# ИДЕМПОТЕНТНОСТЬ: второй прогон на тех же данных не меняет ни одного байта и печатает
# «нечего менять» — проверяется прогоном дважды подряд.
#
# КОДЫ: 0 — записано (или менять нечего); 1 — при `-Check` есть расхождения (файлы не тронуты);
# 2 — неприменимо (нет проекта, нет файла сумм, нет манифестов, нет строки установщика в суммах).
#
# ⚠️ Файл манифеста пишется без BOM и в его же переводах строк: он уезжает в microsoft/winget-pkgs
# как есть, а BOM и смена концов строк — это чужие диффы в публичном пулл-реквесте.

param(
    [Parameter(Mandatory = $true)][string]$Sums,
    [string]$ReleaseDate = '',
    [string]$Root = '',
    [string]$ManifestRoot = '',
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host ('неприменимо: нет дерева ' + $Root)
    exit 2
}
$root = (Resolve-Path -LiteralPath $Root).Path

function Stop-NotApplicable([string]$reason) {
    Write-Host ('неприменимо: ' + $reason)
    exit 2
}

# ── Версия: единственный источник ───────────────────────────────────────────────────────────────
$project = Join-Path $root 'src\DshPanel\DshPanel.csproj'
if (-not (Test-Path -LiteralPath $project)) { Stop-NotApplicable ('нет проекта панели: ' + $project) }
$found = [regex]::Matches([IO.File]::ReadAllText($project), '<Version>([^<]+)</Version>')
if ($found.Count -eq 0) { Stop-NotApplicable ('в ' + $project + ' нет <Version>') }
$version = $found[0].Groups[1].Value.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { Stop-NotApplicable ('версия проекта не вида X.Y.Z: «' + $version + '»') }
$tag = 'v' + $version

# ── Имена вложений: тоже одно место ─────────────────────────────────────────────────────────────
$assetsFile = Join-Path $root 'src\DshPanel\Update\UpdateAssets.cs'
if (-not (Test-Path -LiteralPath $assetsFile)) { Stop-NotApplicable ('нет файла имён вложений: ' + $assetsFile) }
$assetsText = [IO.File]::ReadAllText($assetsFile)
$setupMatch = [regex]::Match($assetsText, 'const\s+string\s+SetupName\s*=\s*"([^"]+)"')
if (-not $setupMatch.Success) { Stop-NotApplicable 'в UpdateAssets.cs не нашлось SetupName' }
$setupName = $setupMatch.Groups[1].Value

# ── Файл сумм: оттуда берётся сумма установщика ─────────────────────────────────────────────────
if (-not [IO.Path]::IsPathRooted($Sums)) { $Sums = Join-Path $root $Sums }
$sumsPath = [IO.Path]::GetFullPath($Sums)
if (-not (Test-Path -LiteralPath $sumsPath)) { Stop-NotApplicable ('нет файла сумм: ' + $sumsPath) }

$sumMap = @{}
foreach ($raw in [IO.File]::ReadAllLines($sumsPath)) {
    $line = ([string]$raw).TrimStart([char]0xFEFF).Trim()
    if ($line.Length -eq 0) { continue }
    if ($line.StartsWith('#')) { continue }
    $parts = @($line -split '[ \t]+' | Where-Object { $_ -ne '' })
    if ($parts.Count -lt 2) { continue }
    $hash = $parts[0].Trim().ToLowerInvariant()
    if ($hash.Length -ne 64 -or $hash -notmatch '^[0-9a-f]{64}$') { continue }
    $name = $parts[$parts.Count - 1].TrimStart('*').Replace('\', '/')
    $slash = $name.LastIndexOf('/')
    if ($slash -ge 0) { $name = $name.Substring($slash + 1) }
    if ($name.Length -gt 0) { $sumMap[$name] = $hash }
}

$setupHash = ''
foreach ($key in @($sumMap.Keys)) {
    if ($key -eq $setupName) { $setupHash = $sumMap[$key] }
}
if (-not $setupHash) {
    Stop-NotApplicable ('в файле сумм ' + $sumsPath + ' нет строки для установщика «' + $setupName + '»')
}

if ($ReleaseDate -and $ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
    Write-Host ('непонятно: -ReleaseDate «' + $ReleaseDate + '» — ожидался вид ГГГГ-ММ-ДД')
    exit 2
}

# ── Манифесты: ищутся по своему `ManifestType`, а не по имени файла ────────────────────────────
if (-not $ManifestRoot) { $ManifestRoot = Join-Path $root 'release\tree\winget' }
if (-not (Test-Path -LiteralPath $ManifestRoot)) { Stop-NotApplicable ('нет каталога манифестов: ' + $ManifestRoot) }
$files = @(Get-ChildItem -LiteralPath $ManifestRoot -Filter '*.yaml' -File)
if ($files.Count -eq 0) { Stop-NotApplicable ('в ' + $ManifestRoot + ' нет ни одного .yaml') }

Write-Host ('версия проекта: ' + $version + ' (src\DshPanel\DshPanel.csproj); тег: ' + $tag)
Write-Host ('файл сумм:      ' + $sumsPath)
Write-Host ('сумма установщика «' + $setupName + '»: ' + $setupHash)
if ($ReleaseDate) { Write-Host ('дата выпуска:   ' + $ReleaseDate) } else { Write-Host 'дата выпуска:   не названа (-ReleaseDate) — строка останется как была' }
Write-Host ''

$changes = New-Object Collections.Generic.List[string]
$touched = 0

foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $typeMatch = [regex]::Match($text, '(?m)^ManifestType:\s*(\S+)\s*$')
    if (-not $typeMatch.Success) { Write-Host ('  пропущен (нет ManifestType): ' + $file.Name); continue }
    $type = $typeMatch.Groups[1].Value
    $before = $text

    # PackageVersion — во всех трёх
    $text = [regex]::Replace($text, '(?m)^PackageVersion:\s*\S+\s*$', ('PackageVersion: ' + $version))

    if ($type -eq 'installer') {
        # ссылка на установщик: тег обязан совпадать с версией, имя — с SetupName
        $text = [regex]::Replace(
            $text,
            '(?m)^(\s*InstallerUrl:\s*https://github\.com/[^/\s]+/[^/\s]+/releases/download/)v[^/\s]+/(\S+)\s*$',
            ('${1}' + $tag + '/${2}'))
        # сумма — ИЗ ФАЙЛА СУММ (не из локальной пересборки)
        $text = [regex]::Replace(
            $text,
            '(?m)^(\s*InstallerSha256:\s*)[0-9A-Fa-f]{64}\s*$',
            ('${1}' + $setupHash.ToUpperInvariant()))
        if ($ReleaseDate) {
            $text = [regex]::Replace($text, '(?m)^ReleaseDate:.*$', ('ReleaseDate: ' + $ReleaseDate))
        }
    }
    elseif ($type -eq 'defaultLocale') {
        # ссылка на заметки выпуска живёт тем же тегом, что и версия
        $text = [regex]::Replace(
            $text,
            '(?m)^(ReleaseNotesUrl:\s*https://github\.com/[^/\s]+/[^/\s]+/releases/tag/)v\S+\s*$',
            ('${1}' + $tag))
    }

    if ($text -ceq $before) {
        Write-Host ('  ' + $file.Name + ' (' + $type + '): нечего менять')
        continue
    }

    # что именно поменялось — по строкам, чтобы отчёт был числами, а не «поправил»
    $oldLines = $before -split "`r?`n"
    $newLines = $text -split "`r?`n"
    for ($i = 0; $i -lt [Math]::Min($oldLines.Count, $newLines.Count); $i++) {
        if ($oldLines[$i] -cne $newLines[$i]) {
            $changes.Add($file.Name + ': «' + $oldLines[$i].Trim() + '» → «' + $newLines[$i].Trim() + '»')
        }
    }

    $touched++
    if (-not $Check) { [IO.File]::WriteAllText($file.FullName, $text, (New-Object Text.UTF8Encoding($false))) }
    Write-Host ('  ' + $file.Name + ' (' + $type + '): правок ' + $changes.Count)
}

Write-Host ''
if ($changes.Count -eq 0) {
    Write-Host 'ИТОГ: менять нечего — манифесты уже описывают эту версию, сумму и дату (код 0)'
    exit 0
}

Write-Host ('РАСХОЖДЕНИЙ: ' + $changes.Count)
foreach ($line in $changes) { Write-Host ('  ' + $line) }

if ($Check) {
    Write-Host ''
    Write-Host 'ИТОГ: это -Check — файлы НЕ тронуты (код 1)'
    exit 1
}

Write-Host ''
Write-Host ('ИТОГ: правлено файлов ' + $touched + '; повторный прогон на тех же данных ничего не изменит (код 0)')
exit 0
