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
    [int]$Port = 3915,
    # Порт, на который смотрит песочный профиль панели. 0 — подобрать свободный самим: значение
    # по умолчанию в свежем settings.json — 3080, то есть порт владельца, и прогон проверки не
    # имеет права даже смотреть в его сторону. Ключ --port тут не поможет: update.cmd поднимает
    # панель без аргументов (UpdateService), поэтому порт задаётся настройкой песочницы.
    [int]$ServerPort = 0
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
if ($ServerPort -eq 3080) { throw 'порт 3080 занят живой панелью — проверка его не трогает' }
if (-not (Test-Path -LiteralPath $Exe)) { throw ('не нашёл панель: ' + $Exe) }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'нужен Node.js для заглушки GitHub' }

if (-not $Work) { $Work = Join-Path $env:TEMP ('dsh-update-apply-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $Work -Force | Out-Null

# Свободный порт для песочного профиля панели: слушаем нулевой порт, забираем выданный номер и
# отпускаем его. Так проверка не зависит ни от занятости 3097 (его берёт приёмка), ни от порядка
# запуска проверок в конвейере.
if ($ServerPort -eq 0) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $ServerPort = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
    $probe.Stop()
}
if ($ServerPort -eq $Port) { throw 'песочный порт панели и порт заглушки GitHub должны быть разными' }

$version = (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion
if ($version.IndexOf('+') -gt 0) { $version = $version.Substring(0, $version.IndexOf('+')) }
$version = $version.TrimStart('v')

Write-Host ('Песочница: ' + $Work)
Write-Host ('Версия панели: ' + $version)
Write-Host ('Песочный порт панели: ' + $ServerPort)

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

$data = Join-Path $Work 'data'
$state = Join-Path $Work 'state'
# Не $home: так называется встроенная переменная PowerShell, и присваивание ей падает.
$dshHome = Join-Path $Work 'dsh-home'
$legacy = Join-Path $Work 'legacy-appdata'
New-Item -ItemType Directory -Path $data, $state, (Join-Path $Work 'ssh'),
    (Join-Path $Work 'backups'), $dshHome, $legacy -Force | Out-Null

# Песочный профиль панели пишем ДО первого запуска. Без этого свежий settings.json получал
# serverPort = 3080 (значение по умолчанию), и панель проверки смотрела на живой порт владельца,
# а её собственный автозапуск сервера — тот, что глушит TrayHost.ShouldAutoStartServer, — целился
# бы в него же. Язык и мастер заданы явно: профиль должен быть похож на обычный, чтобы проверка
# ловила именно изоляцию, а не пустой профиль.
$settingsPath = Join-Path $data 'settings.json'
$sandboxSettings = [ordered]@{
    language           = 'ru'
    onboarded          = $true
    openBrowserOnStart = $true
    serverPort         = $ServerPort
    balanceAutoRefresh = $false
    peakAutoCheck      = $false
} | ConvertTo-Json
[IO.File]::WriteAllText($settingsPath, $sandboxSettings, (New-Object Text.UTF8Encoding($false)))

$keepData = $env:DSH_PANEL_DATA
$keepState = $env:DSH_PANEL_STATE
$keepSsh = $env:DSH_PANEL_SSH_DIR
$keepBackup = $env:DSH_TRAY_BACKUP
$keepApi = $env:DSH_PANEL_API
$keepInstance = $env:DSH_PANEL_INSTANCE
$keepLang = $env:DSH_PANEL_LANG
$keepMigrate = $env:DSH_PANEL_NO_MIGRATE
$keepHome = $env:DSH_HOME
$keepLegacy = $env:DSH_PANEL_LEGACY_DATA

# Полный набор подмен из красной линии №2 (AGENTS.md) и таблицы docs\DEVELOPMENT.md: своя папка
# настроек, своё состояние, свои ключи, свои копии, своя домашняя папка DSH (в ней лежит ключ —
# без неё панель читала настоящий %USERPROFILE%\.dsh и показывала баланс владельца) и своя папка
# настроек прежней генерации.
#
# DSH_PANEL_RUN_KEY здесь НЕ выставляется намеренно: без него Autostart.IsCheckRun видит прогон
# проверки (подменены DSH_PANEL_DATA/STATE/INSTANCE) и не пишет в реестр вообще ничего — это
# строже, чем увести запись в свою ветку, которую пришлось бы потом убирать.
$env:DSH_PANEL_DATA = $data
$env:DSH_PANEL_STATE = $state
$env:DSH_PANEL_SSH_DIR = Join-Path $Work 'ssh'
$env:DSH_TRAY_BACKUP = Join-Path $Work 'backups'
$env:DSH_HOME = $dshHome
$env:DSH_PANEL_LEGACY_DATA = $legacy
$env:DSH_PANEL_API = 'http://127.0.0.1:' + $Port
$env:DSH_PANEL_INSTANCE = 'dsh-update-apply'
$env:DSH_PANEL_LANG = 'ru'
$env:DSH_PANEL_NO_MIGRATE = '1'

try {
    # Заглушка поднимается ВНУТРИ try: иначе ранняя ошибка подготовки песочницы оставляла бы её
    # процесс жить (так и вышло один раз — осиротевший node держал порт, а следующий прогон молча
    # скачал сборку прошлого).
    $stubProcess = Start-Process -FilePath 'node' -ArgumentList @($stub, $files, $Port, $version) -PassThru -WindowStyle Hidden

    # И она обязана встать ИМЕННО на свой порт со СВОЕЙ сборкой: порт мог остаться занят прошлой
    # (или чужой) заглушкой, и панель тогда скачала бы чужой архив, а проверка этого не заметила.
    # Сверяем не «отвечает ли кто-нибудь», а контрольную сумму: у нашей сборки — своя.
    $stubReady = $false
    $stubError = ''
    for ($i = 0; $i -lt 20; $i++) {
        if ($stubProcess.HasExited) { break }
        try {
            # WebClient, а не Invoke-WebRequest: заглушка отдаёт файлы как octet-stream, и
            # Invoke-WebRequest вернул бы байты, а не текст — сверка суммы молча не сработала бы.
            $web = New-Object System.Net.WebClient
            try {
                $served = $web.DownloadString('http://127.0.0.1:' + $Port + '/files/SHA256SUMS.txt')
            }
            finally { $web.Dispose() }
            if ($served -match $hash) { $stubReady = $true; break }
            $stubError = 'на порту отвечает чужая заглушка'
        }
        catch { $stubError = $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    if (-not $stubReady) {
        throw ('заглушка GitHub не встала на порт ' + $Port + ' со своей сборкой — порт занят прошлым или чужим процессом: ' + $stubError)
    }

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

    # Панель, которую поднял сценарий замены, дописывает журнал не сразу: строки подавления и
    # строку счёта пишет таймер старта. Ждём их появления, а не спим угаданное время.
    $stateLog = Join-Path $state 'dsh-tray.log'
    $stateLogText = ''
    for ($i = 0; $i -lt 30; $i++) {
        if (Test-Path -LiteralPath $stateLog) {
            $stateLogText = Get-Content -LiteralPath $stateLog -Raw -Encoding UTF8
            if (($stateLogText -match 'сервер при старте не поднят') -and
                ($stateLogText -match 'счёт \(при запуске\)')) { break }
        }
        Start-Sleep -Milliseconds 500
    }

    # Изоляция песочницы проверяется не словами, а тремя утверждениями о том, что прогон
    # НЕ трогал владельца: настройки несут песочный порт, журнал не знает про 3080 и не содержит
    # баланса из настоящего профиля, а сервер сам не поднялся (его глушит ShouldAutoStartServer,
    # иначе проверка заняла бы порт владельца и оставила бы осиротевший node).
    $settingsNow = if (Test-Path -LiteralPath $settingsPath) {
        Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
    } else { '' }
    $portOk = ($settingsNow -match ('"serverPort":\s*' + $ServerPort)) -and
              ($settingsNow -notmatch '"serverPort":\s*3080')
    Check 'Песочный профиль: порт панели — песочный, а не 3080' $portOk ('serverPort = ' + $ServerPort)

    $noRealPort = ($stateLogText.Length -gt 0) -and ($stateLogText -notmatch '3080')
    Check 'Журнал песочницы не упоминает порт 3080' $noRealPort 'панель проверки не смотрела на живой порт'

    $balanceLine = ($stateLogText -split "`r?`n" | Where-Object { $_ -match 'счёт \(при запуске\)' } |
        Select-Object -First 1)
    $balanceIsolated = [bool]$balanceLine -and ($balanceLine -match 'не получен')
    Check 'Баланс владельца не читается: ключ в песочницу не попал' $balanceIsolated `
        ($balanceLine -replace '^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s*', '')

    $urlFile = Join-Path $state 'web-url.txt'
    $listening = [bool](Get-NetTCPConnection -LocalPort $ServerPort -State Listen -ErrorAction SilentlyContinue)
    $suppressed = $stateLogText -match 'сервер при старте не поднят \(изолированный прогон\)'
    Check 'Сервер сам не поднялся: изоляция заглушила автозапуск' `
        ((-not (Test-Path -LiteralPath $urlFile)) -and (-not $listening) -and $suppressed) `
        ('порт ' + $ServerPort + ' свободен, ссылки входа нет')

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
    $env:DSH_HOME = $keepHome
    $env:DSH_PANEL_LEGACY_DATA = $keepLegacy

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