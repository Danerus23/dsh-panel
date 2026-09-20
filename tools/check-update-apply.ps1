# check-update-apply.ps1 — проверка НАСТОЯЩЕЙ замены файлов при обновлении панели.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-update-apply.ps1 -Exe <панель>
#
# Зачем отдельно от check-update.ps1: тот проверяет только подготовку (скачать, сверить суммы,
# распаковать). А ломается обычно сама замена: панель установлена в «%LOCALAPPDATA%\Programs\DSH
# Panel» — путь с пробелом и хвостовым разделителем, и если передать его в сценарий как есть,
# robocopy получает кавычки вместе с ключами («DSH Panel" \E \NFL…») и отказывается копировать.
# Обновление молча откатывается, и панель поднимается прежней версии — ровно это и случилось
# на живой проверке.
#
# Здесь повторяется весь путь целиком: копируем панель в папку С ПРОБЕЛОМ, готовим обновление на
# локальной заглушке GitHub и выполняем готовую команду замены. Признак успеха — строка
# «files replaced» в журнале обновления и отсутствие ошибки robocopy.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Work = '',
    [int]$Port = 3915
)

$ErrorActionPreference = 'Stop'

# Тот же вид отчёта, что у остальных проверок: строка «ок/БЕДА» и таблица в конце.
$results = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { 'ок' } else { 'БЕДА' }
    Write-Host ('  ' + $mark.PadRight(5) + $name.PadRight(44) + ' ' + $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    $script:results += [pscustomobject]@{ 'Итог' = $mark; 'Проверка' = $name; 'Подробности' = $detail }
}

if ($Port -eq 3080) { throw 'порт 3080 занят живой панелью — проверка его не трогает' }
if (-not (Test-Path -LiteralPath $Exe)) { throw ('не нашёл панель: ' + $Exe) }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'нужен Node.js для заглушки GitHub' }

if (-not $Work) { $Work = Join-Path $env:TEMP ('dsh-update-apply-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $Work -Force | Out-Null

$version = (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion
if ($version.IndexOf('+') -gt 0) { $version = $version.Substring(0, $version.IndexOf('+')) }
$version = $version.TrimStart('v')

Write-Host ('Песочница: ' + $Work)
Write-Host ('Версия панели: ' + $version)

# 1. Панель в папке с пробелом — как настоящая установка
$target = Join-Path $Work 'DSH Panel'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path (Split-Path -Parent $Exe) '*') -Destination $target -Recurse -Force

# 2. Заглушка GitHub отдаёт этот же выпуск (--force разрешает обновиться на ту же версию)
$files = Join-Path $Work 'release'
New-Item -ItemType Directory -Path $files -Force | Out-Null
$zip = Join-Path $files 'DshPanel.zip'
& (Join-Path $PSScriptRoot 'pack-panel.ps1') -Source (Split-Path -Parent $Exe) -Destination $zip | Out-Null
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()
Set-Content -LiteralPath (Join-Path $files 'SHA256SUMS.txt') -Value ($hash + '  DshPanel.zip') -Encoding ASCII

$stub = Join-Path $Work 'stub-github.js'
$stubSource = @'
const http = require("http");
const fs = require("fs");
const path = require("path");
const dir = process.argv[2];
const port = Number(process.argv[3]);
const version = process.argv[4];
http.createServer((request, response) => {
  const url = new URL(request.url, "http://127.0.0.1:" + port);
  if (url.pathname.endsWith("/releases/latest")) {
    const assets = ["DshPanel.zip", "SHA256SUMS.txt"].filter((name) => fs.existsSync(path.join(dir, name))).map((name) => ({
      name,
      browser_download_url: "http://127.0.0.1:" + port + "/files/" + encodeURIComponent(name),
    }));
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify({
      tag_name: "v" + version,
      name: "DSH Panel " + version,
      body: "Проверка замены файлов.",
      published_at: new Date().toISOString(),
      html_url: "http://127.0.0.1:" + port + "/release",
      assets,
    }));
    return;
  }
  if (url.pathname.startsWith("/files/")) {
    const name = decodeURIComponent(url.pathname.slice("/files/".length));
    const file = path.join(dir, name);
    if (!fs.existsSync(file)) { response.writeHead(404); response.end("not found"); return; }
    response.writeHead(200, { "Content-Type": "application/octet-stream" });
    response.end(fs.readFileSync(file));
    return;
  }
  response.writeHead(404);
  response.end("not found");
}).listen(port, "127.0.0.1");
'@
[IO.File]::WriteAllText($stub, $stubSource, (New-Object Text.UTF8Encoding($false)))

$stubProcess = Start-Process -FilePath 'node' -ArgumentList @($stub, $files, $Port, $version) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

$data = Join-Path $Work 'data'
$state = Join-Path $Work 'state'
New-Item -ItemType Directory -Path $data, $state, (Join-Path $Work 'ssh'), (Join-Path $Work 'backups') -Force | Out-Null

$keepData = $env:DSH_PANEL_DATA
$keepState = $env:DSH_PANEL_STATE
$keepSsh = $env:DSH_PANEL_SSH_DIR
$keepBackup = $env:DSH_TRAY_BACKUP
$keepApi = $env:DSH_PANEL_API
$keepInstance = $env:DSH_PANEL_INSTANCE
$keepLang = $env:DSH_PANEL_LANG
$keepMigrate = $env:DSH_PANEL_NO_MIGRATE

$env:DSH_PANEL_DATA = $data
$env:DSH_PANEL_STATE = $state
$env:DSH_PANEL_SSH_DIR = Join-Path $Work 'ssh'
$env:DSH_TRAY_BACKUP = Join-Path $Work 'backups'
$env:DSH_PANEL_API = 'http://127.0.0.1:' + $Port
$env:DSH_PANEL_INSTANCE = 'dsh-update-apply'
$env:DSH_PANEL_LANG = 'ru'
$env:DSH_PANEL_NO_MIGRATE = '1'

try {
    # 3. Готовим обновление
    $out = Join-Path $Work 'prepare.txt'
    & (Join-Path $target 'DshTray.exe') --update-prepare --force --out $out | Out-Null
    $report = Get-Content -LiteralPath $out -Encoding UTF8

    $prepared = [bool]($report -match 'обновление подготовлено')
    $line = ($report | Where-Object { $_ -match 'update\.cmd' } | Select-Object -Last 1)
    $hasCommand = [bool]$line

    # 4. Выполняем ровно ту команду, которую выполнила бы панель
    $applied = $false
    if ($prepared -and $hasCommand) {
        $command = $line.Trim()
        # Ждём сам сценарий, а не его потомков: панель, которую он поднимает, живёт дальше,
        # и Start-Process -Wait ждал бы её бесконечно.
        $replacement = Start-Process -FilePath 'cmd.exe' -ArgumentList ('/c ' + $command) -PassThru -WindowStyle Hidden
        if (-not $replacement.WaitForExit(180000)) { try { $replacement.Kill() } catch { } }
        Start-Sleep -Seconds 5

        $log = Join-Path $state 'update\update.log'
        $logText = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw -Encoding UTF8) } else { '' }
        $applied = ($logText -match 'files replaced') -and ($logText -notmatch 'ОШИБКА 123|ERROR 123|update failed')
    }

    Check 'Обновление подготовлено' $prepared 'скачивание, суммы и распаковка'
    Check 'Команда замены напечатана' $hasCommand 'панель показала, чем будет заменять файлы'
    Check 'Файлы заменены в папке с пробелом' $applied 'robocopy отработал, откат не потребовался'

    if ($prepared -and $hasCommand -and -not $applied) {
        Write-Host '  журнал обновления:' -ForegroundColor Yellow
        Get-Content -LiteralPath (Join-Path $state 'update\update.log') -Encoding UTF8 -ErrorAction SilentlyContinue |
            Select-Object -Last 12 | ForEach-Object { Write-Host ('    ' + $_) }
    }
}
finally {
    $env:DSH_PANEL_DATA = $keepData
    $env:DSH_PANEL_STATE = $keepState
    $env:DSH_PANEL_SSH_DIR = $keepSsh
    $env:DSH_TRAY_BACKUP = $keepBackup
    $env:DSH_PANEL_API = $keepApi
    $env:DSH_PANEL_INSTANCE = $keepInstance
    $env:DSH_PANEL_LANG = $keepLang
    $env:DSH_PANEL_NO_MIGRATE = $keepMigrate

    if ($stubProcess -and -not $stubProcess.HasExited) { try { $stubProcess.Kill() } catch { } }

    # Панель, которую поднял сценарий замены, снимаем: это тестовый экземпляр.
    Get-CimInstance Win32_Process -Filter "Name = 'DshTray.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -like ($Work + '*') } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
}
Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object { $_.'Итог' -ne 'ок' })
if ($failed.Count -gt 0) {
    Write-Host ('ПРОВАЛЕНО проверок: ' + $failed.Count + ' — ' + ($failed[0].'Проверка')) -ForegroundColor Red
    exit 1
}

Write-Host 'замена файлов при обновлении: все проверки пройдены' -ForegroundColor Green
exit 0