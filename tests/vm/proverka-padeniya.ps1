#requires -Version 5.1
<#
    ПРОВЕРКА ПАДЕНИЯ ПАНЕЛИ — отдельный запускаемый прогон (заготовлен в t18, для 2.2.1).

    ЗАЧЕМ. В t17/t18 найдено падение опубликованной 2.2.0: `App.Notify` зовёт
    `TrayIconHost.ShowNotification` без защиты, та бросает, и необработанное исключение роняет
    панель (`InvalidOperationException: значок ещё не поставлен: сначала Show()`). Триггер —
    неудавшаяся стартовая автокопия, после которой панель пытается показать «Копия не снята».

    ЭТА ПРОВЕРКА — ПЕРВАЯ, КОТОРАЯ ДОЛЖНА ПРОЙТИ НА 2.2.1, и проверяет она НЕ «не упало», а три
    вещи сразу:
      1) СЦЕНАРИЙ СОСТОЯЛСЯ: копия по расписанию действительно провалилась, и панель записала
         отказ строкой «автокопия не снята …» (иначе «жива» ничего не значит — падение просто
         не было спровоцировано);
      2) ПАНЕЛЬ ВЫЖИЛА: процесс тот же и жив через N секунд после запуска;
      3) ПАДЕНИЯ НЕ ЗАПИСАНО: событий `Application Error`/`.NET Runtime` по `DshPanel` с момента
         запуска — 0, новых WER-дампов — 0.
    Если (1) не выполнено — проверка честно говорит «сценарий не состоялся» и ПАДАЕТ: значит
    ловушка перестала ловить, и прогон ничего не доказывает.

    ПОЧЕМУ ЛОВУШКА НЕ ЗАВИСИТ ОТ ВТОРОГО РЕМОНТА. Папка копий указывается ВНУТРЬ ОБЫЧНОГО ФАЙЛА
    (`<Root>\blocker\backups`), поэтому копия не снимется ни при каком ремонте гонки журнала:
    сценарий «копия провалилась → панель обязана сказать об этом и выжить» воспроизводится всегда.

    ЧТО ЗАПУСКАТЬ:  pwsh -NoProfile -File tests\vm\proverka-padeniya.ps1 `
                        -Setup <путь к установщику 2.2.1> -SetupSum <его sha256>
    Для 2.2.0 (опубликованной) проверка обязана давать ПРОВАЛ — это её способность падать;
    в t18 это подтверждено: панель умерла, событие `.NET Runtime` 1026 записано.

    Границы: только ВМ; снимок «чистая-ОС» применяется в начале и возвращается в конце, машина
    выключается; на хосте не запускается ничего, кроме этой команды; продукт не правится.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$SetupSum,
    [string]$Vm = 'DSH-Panel-Test',
    [string]$Snapshot = 'чистая-ОС',
    [string]$Root = 'C:\g220',
    [string]$Guest = 'dshlab',
    [string]$GuestPassword = '',
    [int]$WaitSeconds = 25,
    [string]$Report = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:Log = New-Object System.Collections.Generic.List[string]
function Say([string]$text) { $script:Log.Add($text); Write-Host $text }
function New-GuestSession {
    $secure = ConvertTo-SecureString $GuestPassword -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential($Guest, $secure)
    New-PSSession -VMName $Vm -Credential $cred
}
function Guest([System.Management.Automation.Runspaces.PSSession]$Session, [string]$Code) {
    Invoke-Command -Session $Session -ScriptBlock ([ScriptBlock]::Create($Code))
}
function Wait-Guest([int]$Tries = 60) {
    for ($i = 0; $i -lt $Tries; $i++) {
        Start-Sleep -Seconds 5
        try {
            $probe = New-GuestSession
            $answer = Guest $probe '"готов"'
            Remove-PSSession $probe
            if ($answer -contains 'готов') { return $true }
        } catch { }
    }
    return $false
}
function Wait-VmStable([int]$Tries = 60) {
    for ($i = 0; $i -lt $Tries; $i++) {
        $cur = Get-VM -Name $Vm
        $busy = ($cur.State -in @('Starting', 'Stopping', 'Saving', 'Restoring', 'Pausing', 'Resuming')) -or ($cur.Status -eq 'Обслуживается')
        if (-not $busy) { return $cur }
        Start-Sleep -Seconds 5
    }
    return (Get-VM -Name $Vm)
}
function Reset-VmToSnapshot([string]$Target) {
    for ($i = 1; $i -le 6; $i++) {
        try {
            $cur = Wait-VmStable
            if ($cur.State -ne 'Off') {
                Stop-VM -Name $Vm -Force -Confirm:$false -ErrorAction Stop
                for ($k = 0; $k -lt 60; $k++) { Start-Sleep -Seconds 2; if ((Get-VM -Name $Vm).State -eq 'Off') { break } }
            }
            Restore-VMSnapshot -VMName $Vm -Name $Target -Confirm:$false -ErrorAction Stop
            return $true
        } catch {
            Say ("  попытка {0} отката снимка не прошла: {1}" -f $i, $_.Exception.Message.Split("`n")[0])
            Start-Sleep -Seconds 10
        }
    }
    return $false
}
function Parse-Facts($lines) {
    $h = @{}
    foreach ($line in @($lines)) { $i = $line.IndexOf('='); if ($i -gt 0) { $h[$line.Substring(0, $i)] = $line.Substring($i + 1) } }
    return $h
}
$script:Checks = New-Object System.Collections.Generic.List[object]
function Check([string]$What, [bool]$Ok, [string]$Detail = '') {
    $script:Checks.Add([pscustomobject]@{ What = $What; Ok = $Ok; Detail = $Detail })
    Say ("  [{0}] {1}{2}" -f $(if ($Ok) { 'ок' } else { 'ПРОВАЛ' }), $What, $(if ($Detail) { " — $Detail" } else { '' }))
}

if (-not $Report) {
    $here = Split-Path -Parent $PSCommandPath
    $repo = Split-Path -Parent (Split-Path -Parent $here)
    $Report = Join-Path $repo 'upd-build\vm-logs\proverka-padeniya.txt'
}

if (-not $GuestPassword) { $GuestPassword = $env:DSH_VM_GUEST_PASSWORD }
if (-not $GuestPassword) {
    Write-Host 'ПАРОЛЬ ГОСТЯ НЕ ЗАДАН: он передаётся параметром -GuestPassword или переменной окружения DSH_VM_GUEST_PASSWORD; в репозитории пароля нет.'
    exit 2
}
Say '# Проверка падения панели (ловушка: копия по расписанию обязана провалиться)'
Say ''
Say ("Дата: {0}" -f (Get-Date -Format 'dd.MM.yyyy HH:mm'))
if (-not (Test-Path -LiteralPath $Setup)) { Write-Host ("нет установщика: {0}" -f $Setup); exit 2 }
$actualSum = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash.ToLower()
$setupInfo = Get-Item -LiteralPath $Setup
Say ("Установщик: {0} · {1:N0} Б · sha256 {2} · ProductVersion {3}" -f $setupInfo.FullName, $setupInfo.Length, $actualSum, $setupInfo.VersionInfo.ProductVersion)
Check 'установщик — тот, который назвали (sha256 сошёлся)' ($actualSum -eq $SetupSum.ToLower()) $actualSum.Substring(0, 16)
if ($actualSum -ne $SetupSum.ToLower()) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Report) | Out-Null
    $script:Log | Set-Content -LiteralPath $Report -Encoding UTF8
    Write-Host 'СУММА НЕ СОШЛАСЬ — проверка не начинается'
    exit 2
}
Say ''

$session = $null
try {
    Say ("## 1. Снимок ``{0}``, доставка, установка" -f $Snapshot)
    $ok = Reset-VmToSnapshot $Snapshot
    Check 'снимок применён' $ok
    if (-not $ok) { throw 'снимок не применился' }
    Start-VM -Name $Vm
    $null = Wait-VmStable
    $ready = Wait-Guest
    Check 'ВМ отвечает' $ready
    if (-not $ready) { throw 'ВМ не отвечает' }
    $session = New-GuestSession
    Guest $session "New-Item -ItemType Directory -Force -Path '$Root\setup' | Out-Null; 'ок'" | Out-Null
    $guestSum = (Guest $session "if (Test-Path '$Root\setup\dsh-panel-setup.exe') { (Get-FileHash '$Root\setup\dsh-panel-setup.exe' -Algorithm SHA256).Hash.ToLower() } else { 'нет' }") -join ''
    if ($guestSum -ne $actualSum) {
        Copy-Item -LiteralPath $Setup -Destination "$Root\setup\dsh-panel-setup.exe" -ToSession $session -Force
        $guestSum = (Guest $session "(Get-FileHash '$Root\setup\dsh-panel-setup.exe' -Algorithm SHA256).Hash.ToLower()") -join ''
    }
    Check 'установщик в госте — побайтово тот же' ($guestSum -eq $actualSum) $guestSum.Substring(0, [Math]::Min(16, $guestSum.Length))
    $ins = (Guest $session @'
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath 'C:\g220\setup\dsh-panel-setup.exe' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=ru' -PassThru -Wait
$sw.Stop()
'КОД=' + $p.ExitCode
'МС=' + [int]$sw.Elapsed.TotalMilliseconds
$exe = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\DSH Panel') 'DshPanel.exe'
'ЕСТЬ=' + (Test-Path $exe)
if (Test-Path $exe) { 'ВЕРСИЯ=' + (Get-Item $exe).VersionInfo.ProductVersion }
'@)
    $ins | ForEach-Object { Say ("  {0}" -f $_) }
    $inf = Parse-Facts $ins
    Check 'установка — код 0' ($inf['КОД'] -eq '0') $inf['КОД']
    Check 'панель установлена' ($inf['ЕСТЬ'] -eq 'True') $inf['ВЕРСИЯ']
    Say ''

    Say '## 2. Ловушка падения включена: WER LocalDumps (Full) для DshPanel.exe'
    $traps = (Guest $session @'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\DshPanel.exe'
New-Item -ItemType Directory -Force -Path 'C:\dumps' | Out-Null
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name 'DumpFolder' -Value 'C:\dumps' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $key -Name 'DumpType' -Value 2 -PropertyType DWord -Force | Out-Null
$r = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
'ЛОВУШКА: DumpFolder=' + $r.DumpFolder + ' DumpType=' + $r.DumpType
'@)
    $traps | ForEach-Object { Say ("  {0}" -f $_) }
    Say ''

    Say '## 3. Сценарий: папка копий внутри файла → копия по расписанию обязана провалиться'
    $template = @'
function Kill-Panel {
  foreach ($x in @(Get-Process -Name DshPanel -ErrorAction SilentlyContinue)) { try { $x.Kill() } catch { } }
  for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if (-not (Get-Process -Name DshPanel -ErrorAction SilentlyContinue)) { break } }
}
$local = Join-Path $env:LOCALAPPDATA 'DshPanel2'
$exe = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\DSH Panel') 'DshPanel.exe'
$startedAt = Get-Date
Kill-Panel
Remove-Item $local -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'DshPanel2') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $local | Out-Null
Set-Content -LiteralPath 'C:\g220\blocker' -Value 'not a directory' -Encoding ASCII -Force
[IO.File]::WriteAllText((Join-Path $local 'settings.json'), '{ "backupFolder": "C:\\g220\\blocker\\backups" }', (New-Object Text.UTF8Encoding($false)))
'НАСТРОЙКА=' + (Get-Content (Join-Path $local 'settings.json') -Raw)
Remove-Item 'C:\dumps\*' -Force -ErrorAction SilentlyContinue
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '"' + $exe + '"' }
'ЗАПУСК_ВОЗВРАТ=' + $r.ReturnValue
$p = $null
for ($i = 0; $i -lt 80; $i++) { Start-Sleep -Milliseconds 250; $p = Get-Process -Name DshPanel -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { break } }
if ($p) { 'ПОЯВИЛСЯ=' + $p.Id + '/' + $p.StartTime.ToString('HH:mm:ss') } else { 'ПОЯВИЛСЯ=нет' }
Start-Sleep -Seconds @@WAIT@@
$alive = @(Get-Process -Name DshPanel -ErrorAction SilentlyContinue)
'СОСТОЯНИЕ=' + $(if ($alive.Count -gt 0) { 'ЖИВ(' + $alive.Count + ')' } else { 'НЕТ' })
foreach ($x in $alive) { 'ЖИВОЙ=' + $x.Id + '/' + $x.StartTime.ToString('HH:mm:ss') }
$log = Join-Path $local 'state\panel.log'
'ЖУРНАЛ=' + (Test-Path $log)
if (Test-Path $log) {
  $txt = @(Get-Content $log -Encoding UTF8)
  'ЖУРНАЛ_СТРОК=' + $txt.Count
  'ОТКАЗ_КОПИИ=' + @($txt | Where-Object { $_ -like '*автокопия не снята*' }).Count
  'УВЕДОМЛЕНИЕ=' + @($txt | Where-Object { $_ -like '*уведомление*' }).Count
  ($txt | Where-Object { $_ -like '*автокопия*' -or $_ -like '*уведомление*' } | Select-Object -Last 6 | ForEach-Object { 'ЛОГ| ' + $_ })
}
$ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error') })
'СОБЫТИЙ_ПАДЕНИЯ=' + $ev.Count
foreach ($e in $ev) { 'СОБЫТИЕ=' + $e.TimeCreated.ToString('HH:mm:ss') + ' ' + $e.ProviderName + ' id=' + $e.Id }
$d = @(Get-ChildItem 'C:\dumps' -File -ErrorAction SilentlyContinue)
'ДАМПОВ=' + $d.Count
foreach ($x in $d) { 'ДАМП=' + $x.Name + ' ' + $x.Length }
'@
    $run = (Guest $session $template.Replace('@@WAIT@@', [string]$WaitSeconds))
    $run | ForEach-Object { Say ("  {0}" -f $_) }
    $rf = Parse-Facts $run
    Say ''

    Say '## 4. Приговор'
    $occurred = ([int]$rf['ОТКАЗ_КОПИИ'] -ge 1)
    $aliveOk = ($rf['СОСТОЯНИЕ'] -like 'ЖИВ*')
    $crashFree = ([int]$rf['СОБЫТИЙ_ПАДЕНИЯ'] -eq 0)
    $dumpFree = ([int]$rf['ДАМПОВ'] -eq 0)
    Check '1) сценарий СОСТОЯЛСЯ: копия провалилась, панель записала отказ' $occurred ('строк «автокопия не снята»: ' + $rf['ОТКАЗ_КОПИИ'])
    Check '2) панель ВЫЖИЛА после отказа копии' $aliveOk ('состояние: ' + $rf['СОСТОЯНИЕ'])
    Check '3) падения не записано (событий Application Error/.NET Runtime — 0)' $crashFree ('событий: ' + $rf['СОБЫТИЙ_ПАДЕНИЯ'])
    Check '4) новых WER-дампов нет' $dumpFree ('дампов: ' + $rf['ДАМПОВ'] + ' ' + (($run | Where-Object { $_ -like 'ДАМП=*' }) -join ' '))
    $passedHere = ($occurred -and $aliveOk -and $crashFree -and $dumpFree)
    Say ''
    if ($passedHere) { Say 'ПРОВЕРКА ПАДЕНИЯ: ПРОЙДЕНА — панель выжила и сказала об отказе строкой' }
    else { Say 'ПРОВЕРКА ПАДЕНИЯ: НЕ ПРОЙДЕНА — см. пункты выше (и «сценарий не состоялся» тоже провал)' }
    Say ''
}
finally {
    if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
    Say '## 5. Снимок обратно, ВМ выключается'
    $back = Reset-VmToSnapshot $Snapshot
    $vmAfter = Wait-VmStable
    Say ("  снимок применён заново: {0} · ВМ: {1}" -f $back, $vmAfter.State)
    Check 'снимок применён заново' $back
    Check 'ВМ выключена' ($vmAfter.State -eq 'Off') $vmAfter.State

    $passed = @($script:Checks | Where-Object { $_.Ok }).Count
    Say ''
    Say ("ИТОГ: проверок {0}, прошло {1}, провалилось {2}" -f $script:Checks.Count, $passed, ($script:Checks.Count - $passed))
    foreach ($c in $script:Checks) { if (-not $c.Ok) { Say ("  ПРОВАЛ: {0} — {1}" -f $c.What, $c.Detail) } }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Report) | Out-Null
    $script:Log | Set-Content -LiteralPath $Report -Encoding UTF8
    Write-Host ("отчёт: {0}" -f $Report)
    if ($passed -ne $script:Checks.Count) { exit 1 }
    exit 0
}




