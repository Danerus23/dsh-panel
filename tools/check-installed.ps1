# check-installed.ps1 — проверка УСТАНОВЛЕННОЙ панели (для приёмки на чистой машине).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-installed.ps1
#
# Запускать там, где панель поставлена установщиком: проверяет, что она на месте, что запись
# об удалении есть, и прогоняет по ней приёмку «как чужой» (tools\acceptance.ps1).
# Сама панель при этом не пересобирается и живой сервер не трогается.
#
# Варианты: -AppDir <путь> (по умолчанию %LOCALAPPDATA%\Programs\DSH Panel).

param(
    [string]$AppDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = @()
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Итог = $(if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }); Проверка = $name; Подробности = $detail }
    if (-not $ok) { $script:failed++ }
}

if (-not $AppDir) { $AppDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH Panel' }

$exe = Join-Path $AppDir 'DshTray.exe'
$uninstaller = Join-Path $AppDir 'unins000.exe'
Check 'Панель установлена' (Test-Path -LiteralPath $exe) $exe
Check 'Есть удаление' (Test-Path -LiteralPath $uninstaller) $uninstaller

# Запись в «Программах и компонентах»: AppId — постоянный GUID приложения.
$uninstallKey = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like '*7F3C6A21*' } | Select-Object -First 1
$display = if ($uninstallKey) { (Get-ItemProperty $uninstallKey.PSPath).DisplayName } else { '' }
Check 'Видна в «Программах и компонентах»' ([bool]$uninstallKey) $display

# Командные режимы установленной панели.
if (Test-Path -LiteralPath $exe) {
    $work = Join-Path $env:TEMP ('dsh-installed-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    function Installed([string[]]$arguments, [string]$outFile) {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        # Кавычки вокруг путей с пробелом: Start-Process клеит список аргументов пробелами,
        # и «--out C:\Users\John Doe\…» уехало бы в CLI тремя словами.
        $line = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
        $start = Start-Process -FilePath $exe -ArgumentList $line -PassThru
        if (-not $start.WaitForExit(120000)) { throw ('панель не ответила за 120 с: ' + $line) }
        Start-Sleep -Milliseconds 200
        if (Test-Path -LiteralPath $outFile) { return (Get-Content -LiteralPath $outFile -Encoding UTF8) }
        return @()
    }

    $lang = Installed @('--lang-check', '--out', (Join-Path $work 'lang.txt')) (Join-Path $work 'lang.txt')
    $langText = $lang -join "`n"
    Check 'Установленная панель: словари на трёх языках' (($langText -match '\[ru\]') -and ($langText -match '\[en\]') -and ($langText -match '\[zh\]') -and ($langText -notmatch 'НЕТ КЛЮЧЕЙ')) 'ru, en, zh без пропусков'

    $env_check = Installed @('--env-check', '--out', (Join-Path $work 'env.txt')) (Join-Path $work 'env.txt')
    $envText = $env_check -join "`n"
    Check 'Установленная панель: проверка окружения отвечает' ($envText.Length -gt 0) (($env_check | Select-Object -First 2) -join ' | ')

    $layout = Installed @('--layout-check', '--out', (Join-Path $work 'layout.txt')) (Join-Path $work 'layout.txt')
    Check 'Установленная панель: вёрстка не обрезана' ((($layout -join "`n") -match 'вёрстка чистая')) (($layout | Where-Object { $_ -match 'вёрстка|обрезанный' }) -join ' ')

    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# Приёмка «как чужой» по установленной панели.
if (Test-Path -LiteralPath (Join-Path $root 'tools\acceptance.ps1')) {
    Write-Host ''
    Write-Host '=== Приёмка «как чужой» по установленной панели' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\acceptance.ps1') -Exe $exe -Port 3098
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ('Проверок: {0}, проблем: {1}' -f $results.Count, $failed)
exit $(if ($failed -eq 0) { 0 } else { 1 })
