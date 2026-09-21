# check-migrate.ps1 — перенос настроек прежней панели: что переносится, а что нет.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-migrate.ps1 -Exe .\app\DshTray.exe
#
# Что проверяется (панель запускается в своей временной папке: свои DSH_PANEL_DATA,
# DSH_PANEL_STATE, DSH_PANEL_SSH_DIR, DSH_TRAY_BACKUP, DSH_HOME и свой DSH_PANEL_INSTANCE;
# прежняя папка настроек подменяется переменной DSH_PANEL_LEGACY_DATA, поэтому настоящий
# %APPDATA%\DeepSeekHarness не читается вовсе; портов проверка не занимает, реестр не трогает):
#   1. файла панели нет                    — настройки и прайс переносятся из прежней папки;
#   2. файл панели есть и он СТАРШЕ прежнего — НЕ перезаписывается (это и был живой дефект:
#      прежняя панель работала месяцами, её файл новее, и настройки панели затирались);
#   3. файл панели есть и он НОВЕЕ прежнего — тоже не перезаписывается;
#   4. DSH_PANEL_NO_MIGRATE=1               — не переносится ничего;
#   5. перенос при своём каталоге ключей    — в журнале есть строка про автоматически
#      включённую упаковку ключей, а поля прежних схем в файле остаются на месте;
#   6. настоящая прежняя папка настроек     — не изменилась ни на одной проверке.
#
# Варианты: -Exe <путь> (по умолчанию ..\app\DshTray.exe), -Keep (не удалять временную папку).

param(
    [string]$Exe = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'app\DshTray.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$work = Join-Path $env:TEMP ('dsh-migrate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = @()
$failed = 0
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Слепок настоящей прежней папки настроек: проверка обязана её не трогать. Читаем только
# хеш и время правки, содержимое не показываем — оно личное.
$realLegacy = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'DeepSeekHarness\settings.json'
$realBefore = ''
if (Test-Path -LiteralPath $realLegacy) {
    $item = Get-Item -LiteralPath $realLegacy
    $realBefore = (Get-FileHash -LiteralPath $realLegacy -Algorithm SHA256).Hash + '|' + $item.LastWriteTimeUtc.Ticks
}

# Изоляция: ни настроек, ни состояния, ни ключей, ни данных DSH владельца проверка не касается.
$env:DSH_PANEL_STATE = Join-Path $work 'unused-state'
$env:DSH_PANEL_DATA = Join-Path $work 'unused-data'
$env:DSH_PANEL_SSH_DIR = Join-Path $work 'unused-ssh'
$env:DSH_TRAY_BACKUP = Join-Path $work 'unused-backups'
$env:DSH_HOME = Join-Path $work 'unused-home'
$env:DSH_PANEL_INSTANCE = 'DshMigrateCheck'
$env:DSH_PANEL_LANG = 'ru'
Remove-Item Env:DSH_PANEL_NO_MIGRATE -ErrorAction SilentlyContinue

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:results += [pscustomobject]@{
        Итог = $(if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }); Проверка = $name; Подробности = $detail
    }
    if (-not $ok) { $script:failed++ }
}

# Каждая проверка — в своей папке: иначе перенос из предыдущего случая решил бы исход следующего.
function Use-Case([string]$name) {
    $base = Join-Path $work $name
    $dirs = [ordered]@{
        Data   = Join-Path $base 'data'
        Legacy = Join-Path $base 'legacy'
        State  = Join-Path $base 'state'
        Ssh    = Join-Path $base 'ssh'
        Backup = Join-Path $base 'backups'
        Home   = Join-Path $base 'home'
    }
    foreach ($dir in $dirs.Values) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $env:DSH_PANEL_DATA = $dirs.Data
    $env:DSH_PANEL_STATE = $dirs.State
    $env:DSH_PANEL_SSH_DIR = $dirs.Ssh
    $env:DSH_TRAY_BACKUP = $dirs.Backup
    $env:DSH_HOME = $dirs.Home
    $env:DSH_PANEL_LEGACY_DATA = $dirs.Legacy
    return $dirs
}

# Файл настроек — без BOM, как его пишет сама панель.
function Write-Settings([string]$path, [string]$text, [datetime]$stamp) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::SetLastWriteTimeUtc($path, $stamp)
}

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8)
}

# Запуск панели: режим --help разбирает ключи и делает перенос настроек, но ничего не запускает —
# ни окна, ни сервера, ни порта. Панель — GUI-приложение, поэтому Start-Process с ожиданием.
function Invoke-Panel([string]$label) {
    $out = Join-Path $work ('run-' + $label + '.txt')
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
    $line = @('--help', '--out', ('"' + $out + '"')) -join ' '
    $start = Start-Process -FilePath $Exe -ArgumentList $line -PassThru
    if (-not $start.WaitForExit(120000)) { throw ('панель не ответила за 120 с: ' + $line) }
    Start-Sleep -Milliseconds 150
}

$fresh = [datetime]::UtcNow
$old = [datetime]::UtcNow.AddDays(-30)

# --- 1. Файла панели нет: переносим --------------------------------------------
Use-Case '1-no-panel' | Out-Null
Write-Settings (Join-Path $env:DSH_PANEL_LEGACY_DATA 'settings.json') '{"onboarded":true,"serverPort":3097}' $fresh
Write-Settings (Join-Path $env:DSH_PANEL_LEGACY_DATA 'pricing.json') '{"legacy":true}' $fresh
Invoke-Panel '1'

$panelSettings = Join-Path $env:DSH_PANEL_DATA 'settings.json'
$panelPricing = Join-Path $env:DSH_PANEL_DATA 'pricing.json'
Check 'Файла панели нет — настройки перенесены' (Test-Path -LiteralPath $panelSettings)
Check 'Файла панели нет — прайс перенесён' (Test-Path -LiteralPath $panelPricing)
Check 'Перенесённый порт на месте' ((Read-Text $panelSettings) -match '3097')

# --- 2. Файл панели есть и он СТАРШЕ прежнего: не перезаписываем ----------------
Use-Case '2-panel-older' | Out-Null
$panelSettings = Join-Path $env:DSH_PANEL_DATA 'settings.json'
$panelPricing = Join-Path $env:DSH_PANEL_DATA 'pricing.json'
$legacySettings = Join-Path $env:DSH_PANEL_LEGACY_DATA 'settings.json'
$legacyPricing = Join-Path $env:DSH_PANEL_LEGACY_DATA 'pricing.json'
Write-Settings $panelSettings '{"onboarded":true,"serverPort":3097,"backupWithKeys":true}' $old
Write-Settings $panelPricing '{"panel":true}' $old
Write-Settings $legacySettings '{"onboarded":false,"serverPort":3080}' $fresh
Write-Settings $legacyPricing '{"legacy":true}' $fresh

$settingsBefore = (Get-FileHash -LiteralPath $panelSettings -Algorithm SHA256).Hash
$pricingBefore = (Get-FileHash -LiteralPath $panelPricing -Algorithm SHA256).Hash
$stampBefore = (Get-Item -LiteralPath $panelSettings).LastWriteTimeUtc
Invoke-Panel '2'

$settingsAfter = (Get-FileHash -LiteralPath $panelSettings -Algorithm SHA256).Hash
$pricingAfter = (Get-FileHash -LiteralPath $panelPricing -Algorithm SHA256).Hash
Check 'Файл панели старше прежнего — НЕ перезаписан' ($settingsBefore -eq $settingsAfter) 'хеш до и после совпал'
Check 'Время правки файла панели не изменилось' ($stampBefore -eq (Get-Item -LiteralPath $panelSettings).LastWriteTimeUtc)
Check 'В файле панели остались свои значения (порт 3097)' ((Read-Text $panelSettings) -match '3097')
Check 'Прежний порт 3080 в файл панели не попал' (-not ((Read-Text $panelSettings) -match '3080'))
Check 'Прайс панели при этом тоже не перезаписан' ($pricingBefore -eq $pricingAfter)
Check 'Прежняя папка не изменена' ((Read-Text $legacySettings) -match '3080')

# --- 3. Файл панели есть и он НОВЕЕ прежнего: тоже не перезаписываем ------------
Use-Case '3-panel-newer' | Out-Null
$panelSettings = Join-Path $env:DSH_PANEL_DATA 'settings.json'
Write-Settings $panelSettings '{"onboarded":true,"serverPort":3096}' $fresh
Write-Settings (Join-Path $env:DSH_PANEL_LEGACY_DATA 'settings.json') '{"onboarded":false,"serverPort":3080}' $old

$settingsBefore = (Get-FileHash -LiteralPath $panelSettings -Algorithm SHA256).Hash
Invoke-Panel '3'
Check 'Файл панели новее прежнего — НЕ перезаписан' ($settingsBefore -eq (Get-FileHash -LiteralPath $panelSettings -Algorithm SHA256).Hash)

# --- 4. DSH_PANEL_NO_MIGRATE=1: не переносим ничего -----------------------------
Use-Case '4-no-migrate' | Out-Null
Write-Settings (Join-Path $env:DSH_PANEL_LEGACY_DATA 'settings.json') '{"onboarded":true,"serverPort":3097}' $fresh
Write-Settings (Join-Path $env:DSH_PANEL_LEGACY_DATA 'pricing.json') '{"legacy":true}' $fresh
$env:DSH_PANEL_NO_MIGRATE = '1'
Invoke-Panel '4'
Check 'DSH_PANEL_NO_MIGRATE=1 — настройки не перенесены' (-not (Test-Path -LiteralPath (Join-Path $env:DSH_PANEL_DATA 'settings.json')))
Check 'DSH_PANEL_NO_MIGRATE=1 — прайс не перенесён' (-not (Test-Path -LiteralPath (Join-Path $env:DSH_PANEL_DATA 'pricing.json')))
Remove-Item Env:DSH_PANEL_NO_MIGRATE -ErrorAction SilentlyContinue

# --- 5. Перенос с ключами: журнал и сохранность полей прежних схем --------------
Use-Case '5-keys' | Out-Null
$legacySettings = Join-Path $env:DSH_PANEL_LEGACY_DATA 'settings.json'
$unknownText = '{"onboarded":true,"serverPort":3097,"backupWorkspaceDir":"D:\\старое\\место","kitKeepCount":7}'
Write-Settings $legacySettings $unknownText $fresh
Invoke-Panel '5'

$panelSettings = Join-Path $env:DSH_PANEL_DATA 'settings.json'
$text = Read-Text $panelSettings
$json = $null
$parsed = $false
try { $json = $text | ConvertFrom-Json; $parsed = $true } catch { $parsed = $false }

Check 'Перенесённый файл настроек разбирается как JSON' $parsed
Check 'Упаковка ключей включена автоматически' ($parsed -and ($json.backupWithKeys -eq $true))
Check 'Свой каталог ключей записан' ($parsed -and ($json.backupKeyDirs -contains $env:DSH_PANEL_SSH_DIR))
Check 'Поле прежней схемы backupWorkspaceDir сохранено' ($parsed -and ($json.backupWorkspaceDir -eq 'D:\старое\место'))
Check 'Поле прежней схемы kitKeepCount сохранено' ($parsed -and ($json.kitKeepCount -eq 7))
Check 'Перенесённый порт остался на месте' ($parsed -and ($json.serverPort -eq 3097))

$logText = Read-Text (Join-Path $env:DSH_PANEL_STATE 'dsh-tray.log')
Check 'В журнале есть строка про автоматически включённую упаковку ключей' `
    ($logText -match 'настройки прежней панели перенесены: упаковка ключей включена автоматически')

# Файл настроек — без BOM: с ним его не разберёт ни панель, ни инструменты.
$bytes = [IO.File]::ReadAllBytes($panelSettings)
$hasBom = ($bytes.Length -ge 3) -and ($bytes[0] -eq 0xEF) -and ($bytes[1] -eq 0xBB) -and ($bytes[2] -eq 0xBF)
Check 'Перенесённый файл настроек — без BOM' (-not $hasBom)

# --- 6. Настоящая прежняя папка настроек не тронута -----------------------------
$realAfter = ''
if (Test-Path -LiteralPath $realLegacy) {
    $item = Get-Item -LiteralPath $realLegacy
    $realAfter = (Get-FileHash -LiteralPath $realLegacy -Algorithm SHA256).Hash + '|' + $item.LastWriteTimeUtc.Ticks
}
Check 'Настоящая прежняя папка настроек не изменилась' ($realBefore -eq $realAfter) `
    $(if ($realBefore -eq '') { 'файла на этой машине нет — проверять нечего' } else { 'хеш и время правки совпали' })

# --- уборка ---------------------------------------------------------------------
foreach ($name in @('DSH_PANEL_DATA', 'DSH_PANEL_STATE', 'DSH_PANEL_SSH_DIR', 'DSH_TRAY_BACKUP',
        'DSH_HOME', 'DSH_PANEL_INSTANCE', 'DSH_PANEL_LEGACY_DATA', 'DSH_PANEL_LANG')) {
    Remove-Item ('Env:' + $name) -ErrorAction SilentlyContinue
}
if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

$results | Format-Table -AutoSize
Write-Host ''
if ($failed -eq 0) { Write-Host ('Перенос настроек прежней панели: все проверки пройдены (' + $results.Count + ')') }
else { Write-Host ('Перенос настроек прежней панели: проблем — ' + $failed + ' из ' + $results.Count) }
if ($Keep) { Write-Host ('Временная папка: ' + $work) }
exit $(if ($failed -eq 0) { 0 } else { 1 })
