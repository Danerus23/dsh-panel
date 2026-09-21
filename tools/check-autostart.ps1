# check-autostart.ps1 — автозапуск: запись сверяется с текущей копией и чинится.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-autostart.ps1 -Exe .\app\DshTray.exe
#
# Что проверяется (запись автозапуска живёт в HKCU\...\Run, поэтому проверка уводит её
# в СВОЮ ветку реестра через DSH_PANEL_RUN_KEY и в конце убеждается, что настоящий Run не
# изменился; ни одного порта не занимает, процессов после себя не оставляет):
#   1. записи нет            — починка ничего не создаёт (автозапуск сам не включаем);
#   2. прежнее имя, цель — наша панель без --tray — запись переносится в DSHPanel, --tray дописан;
#   3. прежнее имя, цель — чужой файл без версии — запись переводится на нашу копию;
#   4. запись ведёт на отсутствующий файл   — переводится на нашу копию;
#   5. запись ведёт на существующий НЕ наш файл — переводится на нашу копию;
#   6. запись ведёт на живую копию НАШЕЙ версии — не трогается, о ней лишь сообщают;
#   7. обе записи сразу      — остаётся одна (прежнее имя убрано);
#   8. значение без кавычек с пробелом в пути — путь разбирается верно (видно в --status);
#   9. настоящий HKCU\...\Run не изменился ни на одной проверке.
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

$branch = 'Software\DshPanel-Check\' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$testKey = 'HKCU:\' + $branch
$realRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$work = Join-Path $env:TEMP ('dsh-autostart-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = @()
$failed = 0

New-Item -ItemType Directory -Force -Path $work | Out-Null

# Изоляция: проверка не должна прочитать или записать ни настройки, ни состояние, ни ключи,
# ни данные DSH владельца. Пути те же, что описаны в docs\DEVELOPMENT.md.
$env:DSH_PANEL_RUN_KEY = $branch
$env:DSH_PANEL_NO_MIGRATE = '1'
# Язык фиксируем: проверка сверяет строку состояния автозапуска, а Loc берёт язык настройки,
# которой в изолированном профиле нет, — то есть язык системы. На английской Windows сверка
# русской подписи дала бы ложную «ПРОБЛЕМУ» на исправном коде.
$env:DSH_PANEL_LANG = 'ru'
$env:DSH_PANEL_DATA = Join-Path $work 'data'
$env:DSH_PANEL_STATE = Join-Path $work 'state'
$env:DSH_PANEL_SSH_DIR = Join-Path $work 'ssh'
$env:DSH_TRAY_BACKUP = Join-Path $work 'backups'
$env:DSH_HOME = Join-Path $work 'home'
$env:DSH_PANEL_INSTANCE = 'DshAutostartCheck'
foreach ($dir in @($env:DSH_PANEL_DATA, $env:DSH_PANEL_STATE, $env:DSH_PANEL_SSH_DIR,
        $env:DSH_TRAY_BACKUP, $env:DSH_HOME)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{
        Итог = $(if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }); Проверка = $name; Подробности = $detail
    }
    if (-not $ok) { $script:failed++ }
}

function Run([string[]]$arguments, [string]$outFile) {
    Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    # Пути с пробелом Start-Process склеивает без кавычек: «--out C:\Users\John Doe\...»
    # доезжает до панели тремя словами, и проверка показала бы «чисто» зря.
    $line = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $start = Start-Process -FilePath $Exe -ArgumentList $line -PassThru
    if (-not $start.WaitForExit(120000)) { throw ('панель не ответила за 120 с: ' + $line) }
    Start-Sleep -Milliseconds 150
    if (Test-Path -LiteralPath $outFile) { return (Get-Content -LiteralPath $outFile -Encoding UTF8) }
    return @()
}

function SetEntry([string]$name, [string]$value) {
    # New-Item -Force на существующей ветке реестра пересоздаёт её и стирает значения —
    # поэтому ветку создаём только когда её ещё нет (иначе вторая запись затирала первую).
    if (-not (Test-Path -LiteralPath $testKey)) { $null = New-Item -Path $testKey -Force }
    if ($null -eq $value) { Remove-ItemProperty -Path $testKey -Name $name -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $testKey -Name $name -Value $value -Type String }
}

function GetEntry([string]$name) {
    if (-not (Test-Path -LiteralPath $testKey)) { return $null }
    $item = Get-ItemProperty -Path $testKey -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return [string]$item.$name
}

function ClearEntries { Remove-Item -Path $testKey -Recurse -Force -ErrorAction SilentlyContinue }

function FixReport([string]$name) {
    $out = Join-Path $work ('fix-' + $name + '.txt')
    return (Run @('--autostart-fix', '--out', $out) $out)
}

function Action([string[]]$lines) {
    # Машинная строка «action=…», а не русская подпись «Действие:»: подпись зависит от языка
    # и от формулировки, а действие — нет (см. AutostartFixReport).
    $line = $lines | Where-Object { $_ -like 'action=*' } | Select-Object -First 1
    if (-not $line) { return '' }
    $value = ($line -replace '^action=', '').Trim()
    if ($value -eq 'none') { return '' }
    return $value
}

# Слепок НАСТОЯЩЕГО автозапуска: он не должен измениться ни на одной проверке. Сверяем все
# значения ветки целиком, а не только наши два имени — проверка не имеет права менять и чужое.
function RealRunSnapshot {
    $map = @{}
    $key = Get-Item -Path $realRun -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $map }
    foreach ($name in $key.Property) {
        $map[$name] = [string](Get-ItemProperty -Path $realRun -Name $name -ErrorAction SilentlyContinue).$name
    }
    return $map
}

function SameRealRun($left, $right) {
    if ($left.Count -ne $right.Count) { return $false }
    foreach ($name in $left.Keys) {
        if (-not $right.ContainsKey($name)) { return $false }
        if ([string]$left[$name] -ne [string]$right[$name]) { return $false }
    }
    return $true
}

function Cleanup {
    Remove-Item -Path $testKey -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

# Уборка обязана случиться и при исключении: иначе останутся наша ветка реестра, временная папка
# и, возможно, процесс панели.
trap { Cleanup; exit 1 }

$realBefore = RealRunSnapshot

# Сверка настоящего автозапуска ДО всех пишущих кейсов, а не только в конце: если изоляция
# сломается (`DSH_PANEL_RUN_KEY` перестанет её уводить), настоящую запись владельца нельзя
# успеть испортить — проверка обязана остановиться на первом же расхождении.
function AssertRealRun([string]$where) {
    $now = RealRunSnapshot
    if (SameRealRun $script:realBefore $now) { return }

    Write-Host ('ПРОВЕРКА ОСТАНОВЛЕНА: настоящий HKCU\...\Run изменён после ' + $where)
    foreach ($name in $script:realBefore.Keys) {
        $was = [string]$script:realBefore[$name]
        $became = if ($now.ContainsKey($name)) { [string]$now[$name] } else { '(значения нет)' }
        if ($was -ne $became) { Write-Host ('  ' + $name + ': было «' + $was + '», стало «' + $became + '»') }
    }
    Cleanup
    exit 1
}

$selfValue = '"' + $Exe + '" --tray'
$sibling = Join-Path $work 'sibling'
New-Item -ItemType Directory -Force -Path $sibling | Out-Null
Copy-Item -LiteralPath $Exe -Destination (Join-Path $sibling 'DshTray.exe') -Force
$siblingExe = Join-Path $sibling 'DshTray.exe'

$foreign = Join-Path $work 'foreign'
New-Item -ItemType Directory -Force -Path $foreign | Out-Null
$foreignExe = Join-Path $foreign 'DshTray.exe'
Set-Content -LiteralPath $foreignExe -Value 'не исполняемый файл: у него нет ресурса версии' -Encoding UTF8

$spaced = Join-Path $work 'копия панели'
New-Item -ItemType Directory -Force -Path $spaced | Out-Null
Copy-Item -LiteralPath $Exe -Destination (Join-Path $spaced 'DshTray.exe') -Force
$spacedExe = Join-Path $spaced 'DshTray.exe'

# --- 1. Записи нет -------------------------------------------------------------
ClearEntries
$lines = FixReport 'empty'
$action = Action $lines
Check 'Записи нет — починка ничего не создаёт' `
    (($null -eq (GetEntry 'DSHPanel')) -and ($null -eq (GetEntry 'DeepSeekHarness')) -and ($action -eq '')) `
    ('действие: ' + $action)
AssertRealRun 'первого кейса'

# --- 2. Прежнее имя, цель — наша панель без --tray ----------------------------
ClearEntries
SetEntry 'DeepSeekHarness' ('"' + $Exe + '"')
$lines = FixReport 'legacy-self'
$action = Action $lines
Check 'Прежнее имя на нашу панель — перенесено в DSHPanel и дописан --tray' `
    ((GetEntry 'DSHPanel') -eq $selfValue -and ($null -eq (GetEntry 'DeepSeekHarness')) -and ($action -eq 'migrated')) `
    ('действие: ' + $action + '; DSHPanel: ' + (GetEntry 'DSHPanel'))

# --- 3. Прежнее имя, цель — чужой файл без версии ------------------------------
ClearEntries
SetEntry 'DeepSeekHarness' ('"' + $foreignExe + '"')
$lines = FixReport 'legacy-foreign'
$action = Action $lines
Check 'Прежнее имя на чужой файл — запись переведена на нашу копию' `
    ((GetEntry 'DSHPanel') -eq $selfValue -and ($null -eq (GetEntry 'DeepSeekHarness')) -and ($action -eq 'migrated')) `
    ('действие: ' + $action)

# --- 4. Запись ведёт на отсутствующий файл -------------------------------------
ClearEntries
$gone = Join-Path $work 'нет-такой-папки\DshTray.exe'
SetEntry 'DSHPanel' ('"' + $gone + '" --tray')
$lines = FixReport 'missing'
$action = Action $lines
Check 'Запись на отсутствующий файл — переведена на нашу копию' `
    ((GetEntry 'DSHPanel') -eq $selfValue -and ($action -eq 'missing')) `
    ('действие: ' + $action)

# --- 5. Запись ведёт на существующий НЕ наш файл -------------------------------
ClearEntries
SetEntry 'DSHPanel' ('"' + $foreignExe + '" --tray')
$lines = FixReport 'foreign'
$action = Action $lines
Check 'Запись на чужой файл — переведена на нашу копию' `
    ((GetEntry 'DSHPanel') -eq $selfValue -and ($action -in @('missing', 'older'))) `
    ('действие: ' + $action)

# --- 6. Запись ведёт на живую копию НАШЕЙ версии: не трогаем -------------------
ClearEntries
SetEntry 'DSHPanel' ('"' + $siblingExe + '" --tray')
$lines = FixReport 'sibling'
$action = Action $lines
$kept = (GetEntry 'DSHPanel') -eq ('"' + $siblingExe + '" --tray')
$statusOut = Join-Path $work 'status-sibling.txt'
$status = Run @('--status', '--out', $statusOut) $statusOut
$statusLine = $status | Where-Object { $_ -like 'Автозапуск при входе:*' } | Select-Object -First 1
Check 'Живая копия той же версии — запись не тронута, о ней сообщено' `
    ($kept -and ($action -eq '') -and ($statusLine -like ('*' + $siblingExe + '*'))) `
    ('действие: ' + $action + '; отчёт: ' + $statusLine)

# --- 7. Обе записи сразу -------------------------------------------------------
ClearEntries
SetEntry 'DSHPanel' $selfValue
SetEntry 'DeepSeekHarness' ('"' + $gone + '" --tray')
$lines = FixReport 'both'
$action = Action $lines
Check 'Обе записи сразу — остаётся одна (прежнее имя убрано)' `
    ((GetEntry 'DSHPanel') -eq $selfValue -and ($null -eq (GetEntry 'DeepSeekHarness')) -and ($action -eq 'dedup')) `
    ('действие: ' + $action)

# --- 8. Значение без кавычек с пробелом в пути ---------------------------------
ClearEntries
SetEntry 'DSHPanel' ($spacedExe + ' --tray')
$statusOut = Join-Path $work 'status-spaced.txt'
$status = Run @('--status', '--out', $statusOut) $statusOut
$statusLine = $status | Where-Object { $_ -like 'Автозапуск при входе:*' } | Select-Object -First 1
# Сверяем не «встречается ли путь», а что распознан ИМЕННО он: иначе утверждение прошло бы и на
# обрезанном по пробелу пути (`...\копия` вместо `...\копия панели\DshTray.exe`) — а это ровно
# тот дефект, который кейс и должен ловить. Путь берём как последний фрагмент строки после «: »
# (в самом пути тоже есть двоеточие диска, поэтому делим по двоеточию с пробелом).
$reported = if ($statusLine) { ($statusLine -split ':\s+')[-1].Trim() } else { '' }
Check 'Путь без кавычек с пробелом разобран верно' `
    ($reported -eq $spacedExe) `
    ('отчёт: ' + $statusLine)

# --- 9. Настоящий автозапуск не тронут -----------------------------------------
$realAfter = RealRunSnapshot
$same = SameRealRun $realBefore $realAfter
Check 'Настоящий HKCU\...\Run не изменился' $same `
    ('значений в ветке: ' + $realAfter.Count + ' (было ' + $realBefore.Count + ')')

# --- уборка --------------------------------------------------------------------
Cleanup

$results | Format-Table -AutoSize
Write-Host ''
if ($failed -eq 0) { Write-Host ('Автозапуск: все проверки пройдены (' + $results.Count + ')') }
else { Write-Host ('Автозапуск: проблем — ' + $failed + ' из ' + $results.Count) }
if ($Keep) { Write-Host ('Временная папка: ' + $work) }
exit $(if ($failed -eq 0) { 0 } else { 1 })
