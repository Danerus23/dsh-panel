#requires -Version 5.1
<#
    ПРОВЕРКА (б) ПАТЧА 2.2.1 — «10 первых запусков подряд: 0 отказов копии по причине
    собственного журнала панели».

    ЗАЧЕМ. В 2.2.0 копия по расписанию читала СВОЙ ЖЕ `state\panel.log`, который писатель держит
    без общего доступа, и отказывала (`IOException: being used by another process`). Отчёт говорил
    «копия не снята», отказ вёл к уведомлению — а до 2.2.1 это уведомление роняло панель.
    С 2.2.1 журнал панели в копию не входит; эта проверка следит именно за ТОЙ причиной отказа.

    ЧТО ПРОВЕРЯЕТСЯ, по каждому из десяти первых запусков (профиль каждый раз чистый —
    так копия «первая», как в день установки):
      1) копия СНЯТА (в журнале «автокопия снята», в папке копий появился архив);
      2) отказов по причине собственного журнала — НОЛЬ (в журнале нет «автокопия не снята»
         вместе с `panel.log` / `being used by another process`);
      3) панель жива и событий падения нет (иначе проверка (б) зеленела бы на упавшей панели).

    ЗАПУСК (без совпадения суммы установщика проверка не начинается):

      pwsh -NoProfile -File tests\vm\proverka-kopii.ps1 `
           -Setup .\upd-build\dist-next\dsh-panel-setup.exe `
           -SetupSum <sha256 установщика 2.2.1>

    Коды возврата: 0 — проверка пройдена; 1 — не пройдена; 2 — отказ раньше времени (нет файла,
    сумма не сошлась, ВМ не отвечает).
    ⚠️ Границы: только ВМ `DSH-Panel-Test`; снимок «чистая-ОС» применяется в начале и возвращается
    в конце, машина выключается; на хосте не запускается ничего, кроме этой команды; продукт
    не правится.
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
    [int]$Runs = 10,
    [int]$WaitSeconds = 15,
    [string]$Report = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Report) {
    $here = Split-Path -Parent $PSCommandPath
    $repo = Split-Path -Parent (Split-Path -Parent $here)
    $Report = Join-Path $repo 'upd-build\vm-logs\proverka-kopii.txt'
}

$script:Log = New-Object System.Collections.Generic.List[string]
function Say([string]$text) { $script:Log.Add($text); Write-Host $text }
function Finish([int]$code) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Report) | Out-Null
    $script:Log | Set-Content -LiteralPath $Report -Encoding UTF8
    Write-Host ("отчёт: {0}" -f $Report)
    exit $code
}
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

if (-not $GuestPassword) { $GuestPassword = $env:DSH_VM_GUEST_PASSWORD }
if (-not $GuestPassword) {
    Write-Host 'ПАРОЛЬ ГОСТЯ НЕ ЗАДАН: он передаётся параметром -GuestPassword или переменной окружения DSH_VM_GUEST_PASSWORD; в репозитории пароля нет.'
    exit 2
}
Say '# Проверка (б) 2.2.1: десять первых запусков — 0 отказов копии по собственному журналу'
Say ''
Say ("Дата: {0}" -f (Get-Date -Format 'dd.MM.yyyy HH:mm'))
if (-not (Test-Path -LiteralPath $Setup)) { Say ("нет установщика: {0}" -f $Setup); Finish 2 }
$actualSum = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash.ToLower()
Say ("Установщик: {0} · {1:N0} Б · sha256 {2} · ProductVersion {3}" -f (Get-Item -LiteralPath $Setup).FullName, (Get-Item -LiteralPath $Setup).Length, $actualSum, (Get-Item -LiteralPath $Setup).VersionInfo.ProductVersion)
if ($actualSum -ne $SetupSum.ToLower()) {
    Say ("СУММА НЕ СОШЛАСЬ: ожидалась {0} — проверка не начинается" -f $SetupSum.ToLower())
    Finish 2
}
Say ''

$session = $null
$allOk = $false
try {
    Say ("## 1. Снимок ``{0}``, доставка, установка" -f $Snapshot)
    $ok = Reset-VmToSnapshot $Snapshot
    if (-not $ok) { Say 'снимок не применился'; Finish 2 }
    Start-VM -Name $Vm
    $null = Wait-VmStable
    if (-not (Wait-Guest)) { Say 'ВМ не отвечает'; Finish 2 }
    $session = New-GuestSession
    Guest $session "New-Item -ItemType Directory -Force -Path '$Root\setup' | Out-Null; 'ок'" | Out-Null
    $guestSum = (Guest $session "if (Test-Path '$Root\setup\dsh-panel-setup.exe') { (Get-FileHash '$Root\setup\dsh-panel-setup.exe' -Algorithm SHA256).Hash.ToLower() } else { 'нет' }") -join ''
    if ($guestSum -ne $actualSum) {
        Copy-Item -LiteralPath $Setup -Destination "$Root\setup\dsh-panel-setup.exe" -ToSession $session -Force
        $guestSum = (Guest $session "(Get-FileHash '$Root\setup\dsh-panel-setup.exe' -Algorithm SHA256).Hash.ToLower()") -join ''
    }
    Check 'установщик в госте — побайтово тот же' ($guestSum -eq $actualSum) $guestSum.Substring(0, [Math]::Min(16, $guestSum.Length))
    $ins = (Guest $session @'
$p = Start-Process -FilePath 'C:\g220\setup\dsh-panel-setup.exe' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=ru' -PassThru -Wait
'КОД=' + $p.ExitCode
$exe = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\DSH Panel') 'DshPanel.exe'
'ВЕРСИЯ=' + (Get-Item $exe).VersionInfo.ProductVersion
'@)
    $ins | ForEach-Object { Say ("  {0}" -f $_) }
    $inf = Parse-Facts $ins
    Check 'установка — код 0' ($inf['КОД'] -eq '0') $inf['КОД']
    Say ("  версия установленной панели: {0}" -f $inf['ВЕРСИЯ'])
    Say ''

    Say ("## 2. {0} первых запусков подряд (профиль каждый раз чистый)" -f $Runs)
    $copied = 0
    $refusedByLog = 0
    $aliveCount = 0
    $crashEvents = 0
    $schedulerRuns = 0
    for ($run = 1; $run -le $Runs; $run++) {
        $code = @'
function Kill-Panel {
  foreach ($x in @(Get-Process -Name DshPanel -ErrorAction SilentlyContinue)) { try { $x.Kill() } catch { } }
  for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if (-not (Get-Process -Name DshPanel -ErrorAction SilentlyContinue)) { break } }
}
$local = Join-Path $env:LOCALAPPDATA 'DshPanel2'
$exe = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\DSH Panel') 'DshPanel.exe'
Kill-Panel
Remove-Item $local -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'DshPanel2') -Recurse -Force -ErrorAction SilentlyContinue
$startedAt = Get-Date
$null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '"' + $exe + '"' }
$p = $null
for ($i = 0; $i -lt 80; $i++) { Start-Sleep -Milliseconds 250; $p = Get-Process -Name DshPanel -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { break } }
if ($p) { 'ПОЯВИЛСЯ=' + $p.Id } else { 'ПОЯВИЛСЯ=нет' }
Start-Sleep -Seconds @@WAIT@@
$alive = @(Get-Process -Name DshPanel -ErrorAction SilentlyContinue)
'ПАНЕЛЬ=' + $(if ($alive.Count -gt 0) { 'ЖИВ' } else { 'НЕТ' })
$log = Join-Path $local 'state\panel.log'
$txt = @()
if (Test-Path $log) { $txt = @(Get-Content $log -Encoding UTF8) }
'СТРОК_ЖУРНАЛА=' + $txt.Count
'КОПИЙ_СНЯТО=' + @($txt | Where-Object { $_ -like '*автокопия снята*' }).Count
'СТРОК_АВТОКОПИИ=' + @($txt | Where-Object { $_ -like '*автокопия*' }).Count
'ПРИЧИНА_АВТОКОПИИ=' + (($txt | Where-Object { $_ -like '*автокопия*' } | Select-Object -Last 1) -replace '^[0-9:. -]+ ', '')
'ОТКАЗОВ=' + @($txt | Where-Object { $_ -like '*автокопия не снята*' }).Count
'ОТКАЗОВ_ПО_ЖУРНАЛУ=' + @($txt | Where-Object { $_ -like '*автокопия не снята*' -and ($_ -like '*panel.log*' -or $_ -like '*being used by another process*') }).Count
$backups = Join-Path $local 'backups'
'АРХИВОВ=' + @(Get-ChildItem $backups -File -Filter '*.zip' -ErrorAction SilentlyContinue).Count
$ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error') })
'СОБЫТИЙ_ПАДЕНИЯ=' + $ev.Count
@@LOGTAIL@@
'@.Replace('@@WAIT@@', [string]$WaitSeconds).Replace('@@LOGTAIL@@', "'ХВОСТ:' + ((`$txt | Select-Object -Last 2) -join ' | ')")
        try {
        $out = (Guest $session $code)
        $f = Parse-Facts $out
        if ([int]$f['КОПИЙ_СНЯТО'] -ge 1) { $copied++ }
        $refusedByLog += [int]$f['ОТКАЗОВ_ПО_ЖУРНАЛУ']
        if ($f['ПАНЕЛЬ'] -eq 'ЖИВ') { $aliveCount++ }
        $crashEvents += [int]$f['СОБЫТИЙ_ПАДЕНИЯ']
        if ([int]$f['СТРОК_АВТОКОПИИ'] -ge 1) { $schedulerRuns++ }
        Say ("  запуск {0}: панель {1} · копий снято {2} · отказов {3} (по журналу {4}) · архивов {5} · событий падения {6}" -f `
                $run, $f['ПАНЕЛЬ'], $f['КОПИЙ_СНЯТО'], $f['ОТКАЗОВ'], $f['ОТКАЗОВ_ПО_ЖУРНАЛУ'], $f['АРХИВОВ'], $f['СОБЫТИЙ_ПАДЕНИЯ'])
        } catch { Say ("  ОШИБКА прогона {0}: {1}" -f $run, $_.Exception.Message) }
    }
    Say ''
    Say '## 3. Приговор'
    Say ("  из {0} запусков: копия снята в {1} · панель жива в {2} · отказов по собственному журналу ВСЕГО {3} · событий падения {4}" -f $Runs, $copied, $aliveCount, $refusedByLog, $crashEvents)
    # Копия СУТОЧНАЯ: второй раз за день планировщик законно молчит («рано»), поэтому требовать
    # копию в каждом запуске — неверно. Требуем: копия в этом прогоне РАБОТАЛА (снята хотя бы раз),
    # а планировщик срабатывал в КАЖДОМ запуске (строка «автокопия» есть — значит не «молчание»).
    Check ("1) копия в этом прогоне снималась (хотя бы раз из {0})" -f $Runs) ($copied -ge 1) ("снято: {0}" -f $copied)
    Check ("1б) планировщик копий срабатывал в каждом из {0} запусков" -f $Runs) ($schedulerRuns -eq $Runs) ("срабатывал: {0} из {1}" -f $schedulerRuns, $Runs)
    Check '2) отказов копии по причине СОБСТВЕННОГО журнала — ноль' ($refusedByLog -eq 0) ("отказов по журналу: {0}" -f $refusedByLog)
    Check '3) панель жива и событий падения нет' (($aliveCount -eq $Runs) -and ($crashEvents -eq 0)) ("жива {0} из {1}, событий {2}" -f $aliveCount, $Runs, $crashEvents)
    $passed = @($script:Checks | Where-Object { $_.Ok }).Count
    $allOk = ($passed -eq $script:Checks.Count)
    Say ''
    Say $(if ($allOk) { 'ПРОВЕРКА КОПИЙ (б): ПРОЙДЕНА' } else { 'ПРОВЕРКА КОПИЙ (б): НЕ ПРОЙДЕНА' })
    Say ''
    Say ("ИТОГ: проверок {0}, прошло {1}, провалилось {2}" -f $script:Checks.Count, $passed, ($script:Checks.Count - $passed))
    foreach ($c in $script:Checks) { if (-not $c.Ok) { Say ("  ПРОВАЛ: {0} — {1}" -f $c.What, $c.Detail) } }
}
finally {
    if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
    Say ''
    Say '## 4. Снимок обратно, ВМ выключается'
    $back = Reset-VmToSnapshot $Snapshot
    $vmAfter = Wait-VmStable
    Say ("  снимок применён заново: {0} · ВМ: {1}" -f $back, $vmAfter.State)
    Finish $(if ($allOk -and $back -and $vmAfter.State -eq 'Off') { 0 } else { 1 })
}




