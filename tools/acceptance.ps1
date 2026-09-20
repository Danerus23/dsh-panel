# acceptance.ps1 — приёмка панели «как чужой»: на отдельном профиле, не трогая рабочую панель.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\acceptance.ps1
#
# Что проверяется (всё на изолированных DSH_PANEL_DATA / DSH_PANEL_STATE / DSH_HOME
# и на своём порту, поэтому живой сервер и настройки владельца не затрагиваются):
#   1. первый запуск: мастер ещё не пройден;
#   2. окружение: node и пакет dsh найдены;
#   3. словари: три языка, без пропусков;
#   4. вёрстка: текст не обрезан на трёх языках;
#   5. ответы мастера (порт, рабочая папка, язык) применяются;
#   6. сервер: пуск и остановка (движок подменяется заглушкой preview\stub-dsh.js);
#   7. копия без ключей: внутри нет ни keys/, ни workspace/;
#   8. копия с ключами: keys/ появляется (свой каталог ключей задаётся DSH_PANEL_SSH_DIR);
#   9. опись и контрольные суммы копии читаются, копия признана годной;
#  10. накат копии: испорченные данные вернулись, ключи вернулись, есть копия «до наката»;
#  11. в копию не утекли чужие пути (AndroidKeys и т. п.).
#
# Варианты: -Exe <путь> (по умолчанию ..\app\DshTray.exe), -Port <порт> (по умолчанию 3097),
#           -Keep (не удалять временный профиль — для разбора).

param(
    [string]$Exe = '',
    [int]$Port = 3097,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'app\DshTray.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$work = Join-Path $env:TEMP ('dsh-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = @()
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Итог = $(if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }); Проверка = $name; Подробности = $detail }
    if (-not $ok) { $script:failed++ }
}

function Run([string[]]$arguments, [string]$outFile) {
    Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    # Пути с пробелом Start-Process склеивает без кавычек, и «--out C:\Users\John Doe\…»
    # доезжает до панели тремя словами: отчёт уедет не туда, а проверка покажет «чисто» зря.
    $line = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $start = Start-Process -FilePath $Exe -ArgumentList $line -PassThru
    if (-not $start.WaitForExit(120000)) { throw ('панель не ответила за 120 с: ' + $line) }
    Start-Sleep -Milliseconds 200
    if (Test-Path -LiteralPath $outFile) { return (Get-Content -LiteralPath $outFile -Encoding UTF8) }
    return @()
}

function Sections([string]$zipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try { return ($archive.Entries | ForEach-Object { ($_.FullName -split '/')[0] } | Sort-Object -Unique) }
    finally { $archive.Dispose() }
}

New-Item -ItemType Directory -Force -Path $work | Out-Null
$data = Join-Path $work 'appdata'
$state = Join-Path $work 'state'
$dshHome = Join-Path $work 'dsh'
$backups = Join-Path $work 'backups'
$work_dir = Join-Path $work 'work'
$ssh = Join-Path $work 'ssh'
foreach ($path in @($data, $state, $dshHome, $backups, $work_dir, $ssh)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
Set-Content -LiteralPath (Join-Path $dshHome 'settings.yaml') -Value 'probe: yes' -Encoding UTF8

# Свой каталог ключей: иначе проверка «копия с ключами» зависела бы от того, есть ли у человека
# ~/.ssh, а накат закрывал бы права на его настоящих ключах.
[IO.File]::WriteAllText((Join-Path $ssh 'id_test'), 'test-key', (New-Object Text.UTF8Encoding($false)))

# Изоляция: своя папка настроек, своё состояние, своя папка данных DSH, свои копии.
$env:DSH_PANEL_DATA = $data
$env:DSH_PANEL_STATE = $state
$env:DSH_HOME = $dshHome
$env:DSH_TRAY_BACKUP = $backups
$env:DSH_PANEL_SSH_DIR = $ssh
Remove-Item Env:\DSH_PANEL_LANG -ErrorAction SilentlyContinue

try {
    # 1. Первый запуск: мастер ещё не пройден.
    $status = Run @('--status', '--out', (Join-Path $work 'status.txt')) (Join-Path $work 'status.txt')
    $settings = Join-Path $data 'settings.json'
    $onboarded = $false
    if (Test-Path -LiteralPath $settings) {
        $onboarded = (Get-Content -LiteralPath $settings -Raw -Encoding UTF8) -match '"onboarded":\s*true'
    }
    Check 'Первый запуск: мастер ещё не пройден' (-not $onboarded) 'onboarded отсутствует или false'

    # 2. Окружение.
    $env_check = Run @('--env-check', '--out', (Join-Path $work 'env.txt')) (Join-Path $work 'env.txt')
    $envText = $env_check -join "`n"
    Check 'Окружение: node найден' ($envText -match 'node\.exe') 'строка про node без «не найден»'
    Check 'Окружение: пакет dsh найден' ($envText -match 'bin\.js') 'строка про lib\bin.js'

    # 3. Словари.
    $lang = Run @('--lang-check', '--out', (Join-Path $work 'lang.txt')) (Join-Path $work 'lang.txt')
    $langText = $lang -join "`n"
    Check 'Словари: три языка без пропусков' (($langText -match '\[ru\]') -and ($langText -match '\[en\]') -and ($langText -match '\[zh\]') -and ($langText -notmatch 'НЕТ КЛЮЧЕЙ')) 'ru, en, zh; пропусков нет'

    # 4. Самопроверка: сравнение версий (предрелизы) и разбор файла контрольных сумм —
    #    то, на чём держатся проверка обновлений и её целостность.
    $self = Run @('--selftest', '--out', (Join-Path $work 'selftest.txt')) (Join-Path $work 'selftest.txt')
    $selfText = $self -join "`n"
    Check 'Самопроверка: версии и контрольные суммы' ($selfText -match 'SELFTEST OK' -and $selfText -notmatch 'БЕДА') 'SELFTEST OK без строк БЕДА'

    # 5. Вёрстка.
    $layoutOk = $true
    $layoutDetail = @()
    foreach ($code in 'ru', 'en', 'zh') {
        $path = Join-Path $work ("layout-$code.txt")
        $lines = Run @('--lang', $code, '--layout-check', '--out', $path) $path
        $line = ($lines | Where-Object { $_ -match 'вёрстка|обрезанный' }) -join ' '
        if ($line -notmatch 'чистая') { $layoutOk = $false }
        $layoutDetail += ($code + ': ' + $line)
    }
    Check 'Вёрстка: текст не обрезан на трёх языках' $layoutOk ($layoutDetail -join '; ')

    # 6. Ответы мастера применяются.
    $answers = [ordered]@{
        language          = 'en'
        onboarded         = $true
        serverPort        = $Port
        serverWorkingDir  = $work_dir
        backupWithKeys    = $false
        balanceAutoRefresh = $false
        peakAutoCheck     = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText($settings, $answers, (New-Object Text.UTF8Encoding($false)))
    $status = Run @('--status', '--out', (Join-Path $work 'status2.txt')) (Join-Path $work 'status2.txt')
    $statusText = $status -join "`n"
    Check 'Ответы мастера: порт применён' ($statusText -match ("Port:\s*$Port")) "ожидался порт $Port"
    $env2 = Run @('--env-check', '--out', (Join-Path $work 'env2.txt')) (Join-Path $work 'env2.txt')
    Check 'Ответы мастера: рабочая папка применена' (($env2 -join "`n") -match [regex]::Escape($work_dir)) $work_dir

    # 7. Сервер через заглушку: пуск и остановка. Движок подменяем только здесь.
    $env:DSH_TRAY_BIN = Join-Path $root 'preview\stub-dsh.js'
    $startLines = Run @('--server-start', '--out', (Join-Path $work 'start.txt')) (Join-Path $work 'start.txt')
    $startText = $startLines -join "`n"
    $started = $startText -match 'Server started'
    $urlFile = Join-Path $state 'web-url.txt'
    Check 'Сервер: панель подняла его и записала ссылку' ($started -and (Test-Path -LiteralPath $urlFile)) ('порт ' + $Port)
    $stopLines = Run @('--server-stop', '--out', (Join-Path $work 'stop.txt')) (Join-Path $work 'stop.txt')
    Start-Sleep -Seconds 1
    $stillListening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    Check 'Сервер: панель остановила его' ((-not $stillListening)) 'порт свободен'

    # 8. Копия без ключей.
    Get-ChildItem -LiteralPath $backups -Filter '*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
    $backupLines = Run @('--backup', '--out', (Join-Path $work 'backup.txt')) (Join-Path $work 'backup.txt')
    $zip = Get-ChildItem -LiteralPath $backups -Filter '*.zip' | Sort-Object LastWriteTime | Select-Object -Last 1
    $sections = Sections $zip.FullName
    Check 'Копия без ключей: нет keys/ и workspace/' ((-not ($sections -contains 'keys')) -and (-not ($sections -contains 'workspace'))) ($sections -join ', ')
    Check 'Копия без ключей: данные DSH на месте' ($sections -contains 'dsh-home') ($sections -join ', ')

    # 9. Копия с ключами.
    Get-ChildItem -LiteralPath $backups -Filter '*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
    $withKeys = [ordered]@{ language = 'en'; onboarded = $true; serverPort = $Port; backupWithKeys = $true; balanceAutoRefresh = $false; peakAutoCheck = $false } | ConvertTo-Json
    [IO.File]::WriteAllText($settings, $withKeys, (New-Object Text.UTF8Encoding($false)))
    $null = Run @('--backup', '--out', (Join-Path $work 'backup2.txt')) (Join-Path $work 'backup2.txt')
    $zip2 = Get-ChildItem -LiteralPath $backups -Filter '*.zip' | Sort-Object LastWriteTime | Select-Object -Last 1
    $sections2 = Sections $zip2.FullName
    Check 'Копия с ключами: keys/ появился' ($sections2 -contains 'keys') ($sections2 -join ', ')

    # 10. Проверка копии.
    $checkLines = Run @('--backup-check', '--from', $zip2.FullName, '--out', (Join-Path $work 'check.txt')) (Join-Path $work 'check.txt')
    $checkText = $checkLines -join "`n"
    Check 'Проверка копии: признана годной' ($checkText -match 'OK' -and $checkText -notmatch 'NOT USABLE') 'по описи и контрольным суммам'

    # 11. Накат копии: портим данные и возвращаем их из архива.
    $probe = Join-Path $dshHome 'settings.yaml'
    Set-Content -LiteralPath $probe -Value 'испорчено' -Encoding UTF8
    $restoreLines = Run @('--restore', '--from', $zip2.FullName, '--keys', '--out', (Join-Path $work 'restore.txt')) (Join-Path $work 'restore.txt')
    $restoreText = $restoreLines -join "`n"
    Check 'Накат копии: данные вернулись' ((Get-Content -LiteralPath $probe -Raw -Encoding UTF8) -match 'probe: yes') $restoreText
    Check 'Накат копии: ключи вернулись' (Test-Path -LiteralPath (Join-Path $ssh 'id_test')) 'ключ из своего каталога'
    Check 'Накат копии: предохранительная копия сделана' ((Get-ChildItem -LiteralPath $backups -Filter 'dsh-before-restore-*.zip' -ErrorAction SilentlyContinue).Count -ge 1) 'копия «до наката» лежит рядом'

    # 12. Вёрстка окна наката, когда копия есть: подписи с датой, машиной и составом длиннее прочих.
    $restoreLayoutOk = $true
    $restoreLayoutDetail = @()
    foreach ($code in 'ru', 'en', 'zh') {
        $path = Join-Path $work ("layout-restore-$code.txt")
        $lines = Run @('--lang', $code, '--layout-check', '--out', $path) $path
        $line = ($lines | Where-Object { $_ -match 'вёрстка|обрезанный' }) -join ' '
        $bad = ($lines | Where-Object { $_ -match 'накат:' }) -join '; '
        if ($line -notmatch 'чистая') { $restoreLayoutOk = $false }
        $restoreLayoutDetail += ($code + ': ' + $line + $(if ($bad) { ' | ' + $bad } else { '' }))
    }
    Check 'Вёрстка окна наката с готовой копией: текст не обрезан' $restoreLayoutOk ($restoreLayoutDetail -join '; ')

    # 13. В копию не утекли чужие пути.
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip2.FullName)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'manifest.json' } | Select-Object -First 1
        $reader = New-Object IO.StreamReader($entry.Open())
        $manifest = $reader.ReadToEnd()
        $reader.Close()
    }
    finally { $archive.Dispose() }
    Check 'В копию не утекли чужие пути' (($manifest -notmatch 'AndroidKeys') -and ($manifest -notmatch '\.ssh')) 'ни AndroidKeys, ни .ssh'
}
finally {
    Remove-Item Env:\DSH_PANEL_DATA, Env:\DSH_PANEL_STATE, Env:\DSH_HOME, Env:\DSH_TRAY_BACKUP, Env:\DSH_TRAY_BIN, Env:\DSH_PANEL_LANG, Env:\DSH_PANEL_SSH_DIR -ErrorAction SilentlyContinue
    if (-not $Keep) {
        # Панель создаёт в домашней папке dsh общий node_modules движка — junction на установку.
        # Сначала снимаем саму ссылку: рекурсивное удаление песочницы по ссылке не пойдёт, но
        # так надёжнее и понятнее (rmdir у junction убирает только ссылку, не цель).
        $link = Join-Path $work 'dsh-home\profiles\node_modules'
        if (Test-Path -LiteralPath $link) { & cmd /c rmdir "$link" 2>&1 | Out-Null }

        # Каталог ключей после наката закрыт на владельца — перед уборкой возвращаем себе права.
        icacls $ssh /grant "$($env:USERNAME):(OI)(CI)F" /T /C /Q 2>&1 | Out-Null
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($Keep) { Write-Host ('Временный профиль оставлен: ' + $work) }
Write-Host ('Проверок: {0}, проблем: {1}' -f $results.Count, $failed)
exit $(if ($failed -eq 0) { 0 } else { 1 })
