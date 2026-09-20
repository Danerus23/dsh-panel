# check-update.ps1 — проверить путь обновления, не публикуя выпуск.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-update.ps1
#
# Зачем: панель обновляет себя с GitHub, и главная защита там — сверка скачанного архива
# с опубликованными контрольными суммами (своего сертификата у проекта нет). Проверить это
# «на живом» до публикации нельзя, поэтому здесь поднимается локальная заглушка GitHub API,
# а панель проходит НАСТОЯЩИЙ путь обновления: --update-prepare скачивает выпуск, сверяет
# суммы, распаковывает архив и готовит сценарий замены. Панель при этом не закрывается,
# сценарий не запускается, живой сервер не трогается.
#
# Три случая:
#   1) суммы верны            → обновление подготовлено, в папке обновления лежит новая панель;
#   2) сумма испорчена        → отказ с сообщением о несовпадении, за собой убрано;
#   3) файла сумм нет         → отказ с сообщением, что проверять целостность нечем.
#
# Нужен только Node.js (для заглушки) и собранная панель. Интернет не нужен.

param(
    [string]$Exe = '',
    [string]$Work = '',
    [int]$Port = 3912,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
if ($Port -eq 3080) { throw 'порт 3080 занят живой панелью — проверка обновления его не трогает' }

$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'dist\panel\DshTray.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw ('нет панели: ' + $Exe) }

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'нужен Node.js для заглушки GitHub' }

if ([string]::IsNullOrWhiteSpace($Work)) {
    $Work = Join-Path ([IO.Path]::GetTempPath()) ('dsh-update-check-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$work = [IO.Path]::GetFullPath($Work)
if ($work.TrimEnd('\').Length -le 2) { throw ('отказываюсь работать с корнем диска: ' + $work) }

$release = Join-Path $work 'release'
$files = Join-Path $release 'files'
$data = Join-Path $work 'panel-data'
$state = Join-Path $work 'panel-state'
$report = Join-Path $work 'report.txt'

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
foreach ($dir in @($release, $files, $data, $state, (Join-Path $work 'ssh'), (Join-Path $work 'backups'), (Join-Path $work 'dsh-home'))) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host ('  ок   ' + $name); return }
    Write-Host ('  БЕДА ' + $name + $(if ($detail) { ' — ' + $detail } else { '' }))
    $failures.Add($name)
}

Write-Host ('Песочница: ' + $work)

# --- 1. «Выпуск»: та же сборка панели, но разложенная как ассеты GitHub ----------
Write-Host '1) собираю выпуск из уже собранной панели'
$panelDir = Split-Path -Parent $Exe
& (Join-Path $PSScriptRoot 'pack-panel.ps1') -Source $panelDir -Destination (Join-Path $files 'DshPanel.zip') | Out-Null
Copy-Item -LiteralPath $Exe -Destination (Join-Path $files 'dsh-panel-setup.exe') -Force

# После git init .NET вписывает в ProductVersion «+хеш коммита»: «1.20.0+2c1156d…». Панель
# сравнивает версии по числовой части (UpdateService.Clean), поэтому и проверка должна.
$version = ((Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion -split '\+')[0].Trim()
& (Join-Path $PSScriptRoot 'write-checksums.ps1') -Dist $files -Files @('DshPanel.zip', 'dsh-panel-setup.exe') | Out-Null

$goodSums = Join-Path $work 'SHA256SUMS-good.txt'
$badSums = Join-Path $work 'SHA256SUMS-broken.txt'
# Суммы держим вне папки выпуска: файл в выпуске подменяется перед каждым случаем,
# поэтому «правильный» и «испорченный» варианты лежат отдельно от того, что отдаёт заглушка.
Move-Item -LiteralPath (Join-Path $files 'SHA256SUMS.txt') -Destination $goodSums -Force
# Портим именно сумму архива: иначе проверка прошла бы по совпадению.
$broken = (Get-Content -LiteralPath $goodSums -Encoding UTF8) -replace '^[0-9a-f]{64}(\s+DshPanel\.zip)$', ('0' * 64 + '$1')
[IO.File]::WriteAllLines($badSums, $broken, (New-Object Text.UTF8Encoding($false)))

# --- 2. Заглушка GitHub API ------------------------------------------------------
$stub = Join-Path $work 'stub-github.js'
$stubSource = @'
// Заглушка GitHub API: отдаёт «последний выпуск» и файлы ассетов из своей папки.
// Конфигурация перечитывается на каждый запрос, поэтому проверка меняет её без перезапуска.
const http = require("http");
const fs = require("fs");
const path = require("path");

const dir = process.argv[2];
const port = Number(process.argv[3]);
const configPath = path.join(dir, "config.json");

http.createServer((request, response) => {
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  const url = request.url.split("?")[0];

  if (url === "/repos/" + config.repo + "/releases/latest") {
    const body = JSON.stringify({
      tag_name: config.tag,
      name: config.tag,
      body: config.notes || "test release",
      published_at: new Date().toISOString(),
      html_url: "http://127.0.0.1:" + port + "/release",
      assets: config.assets.map((name) => ({
        name: name,
        browser_download_url: "http://127.0.0.1:" + port + "/files/" + encodeURIComponent(name),
      })),
    });
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(body);
    return;
  }

  if (url.startsWith("/files/")) {
    const name = decodeURIComponent(url.slice("/files/".length));
    const file = path.join(config.dir, name);
    if (!fs.existsSync(file)) { response.writeHead(404); response.end("not found"); return; }
    response.writeHead(200, { "Content-Type": "application/octet-stream" });
    fs.createReadStream(file).pipe(response);
    return;
  }

  response.writeHead(404);
  response.end("not found");
}).listen(port, "127.0.0.1");
'@
[IO.File]::WriteAllText($stub, $stubSource, (New-Object Text.UTF8Encoding($false)))

# Конфигурацию пишем до запуска: заглушка читает её на каждый запрос, и без файла
# первый же запрос падает, а проверка выглядит как «заглушка не отвечает».
$defaultConfig = @{
    repo = 'test/dsh-panel'
    tag = $version
    dir = $files
    notes = 'проверка пути обновления'
    assets = @('DshPanel.zip', 'dsh-panel-setup.exe', 'SHA256SUMS.txt')
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $work 'config.json'), $defaultConfig, (New-Object Text.UTF8Encoding($false)))

$stubOut = Join-Path $work 'stub-out.txt'
$stubErr = Join-Path $work 'stub-err.txt'
$stubProcess = Start-Process -FilePath 'node' -ArgumentList @($stub, $work, $Port) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $stubOut -RedirectStandardError $stubErr
Start-Sleep -Milliseconds 900

try {
    $ok = $false
    try {
        $probe = Invoke-WebRequest -Uri ("http://127.0.0.1:" + $Port + "/repos/test/dsh-panel/releases/latest") -UseBasicParsing -TimeoutSec 5
        $ok = $probe.StatusCode -eq 200
    }
    catch { }
    if (-not $ok) {
        $why = ((Get-Content -LiteralPath $stubErr -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 3) -join ' ')
        throw ('заглушка GitHub не отвечает (порт ' + $Port + ' занят?): ' + $why)
    }

    $env:DSH_PANEL_REPO = 'test/dsh-panel'
    $env:DSH_PANEL_API = 'http://127.0.0.1:' + $Port
    $env:DSH_PANEL_DATA = $data
    $env:DSH_PANEL_STATE = $state
    $env:DSH_PANEL_SSH_DIR = Join-Path $work 'ssh'
    $env:DSH_TRAY_BACKUP = Join-Path $work 'backups'
    $env:DSH_HOME = Join-Path $work 'dsh-home'
    $env:DSH_PANEL_NO_MIGRATE = '1'
    $env:DSH_PANEL_LANG = 'ru'

    function Set-Release([string[]]$assets, [string]$sumsContent) {
        # Файл сумм подменяем перед каждым случаем: заглушка отдаёт файлы из этой папки.
        $target = Join-Path $files 'SHA256SUMS.txt'
        if ($sumsContent -eq 'good') {
            Copy-Item -LiteralPath $goodSums -Destination $target -Force
        }
        elseif ($sumsContent -eq 'broken') {
            Copy-Item -LiteralPath $badSums -Destination $target -Force
        }
        else {
            # Файла в выпуске нет — так выглядит выпуск без контрольных сумм.
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        }

        $config = @{
            repo = 'test/dsh-panel'
            tag = $version
            dir = $files
            notes = 'проверка пути обновления'
            assets = $assets
        } | ConvertTo-Json -Depth 4
        [IO.File]::WriteAllText((Join-Path $work 'config.json'), $config, (New-Object Text.UTF8Encoding($false)))
    }

    function Invoke-Prepare([string]$name) {
        Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
        # --force разрешает выпуск той же версии: проверяем путь обновления, а не сравнение версий.
        $process = Start-Process -FilePath $Exe -ArgumentList @('--update-prepare', '--force', '--out', ('"' + $report + '"')) -PassThru
        if (-not $process.WaitForExit(180000)) { throw ('панель не ответила за 180 с: ' + $name) }
        Start-Sleep -Milliseconds 300
        if (-not (Test-Path -LiteralPath $report)) { throw ('нет отчёта: ' + $name) }
        return (Get-Content -LiteralPath $report -Raw -Encoding UTF8)
    }

    $stage = Join-Path $state 'update'

    # --- случай 1: суммы верны ----------------------------------------------------
    Write-Host '2) суммы верны: обновление должно подготовиться'
    Set-Release @('DshPanel.zip', 'dsh-panel-setup.exe', 'SHA256SUMS.txt') 'good'
    $text = Invoke-Prepare 'good'
    Check 'суммы верны: обновление подготовлено' ($text -match 'обновление подготовлено') ($text -replace "`r?`n", ' | ' | Select-Object -First 1)
    $staged = Get-ChildItem -LiteralPath $stage -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $version } | Select-Object -First 1
    Check 'новая панель распакована в папку обновления' ($null -ne $staged -and (Test-Path -LiteralPath (Join-Path $staged.FullName 'DshTray.exe')))
    Check 'сценарий замены подготовлен' ($text -match 'сценарий' -or (Test-Path -LiteralPath (Join-Path $stage 'update.cmd')))

    # --- случай 2: сумма испорчена ------------------------------------------------
    Write-Host '3) сумма испорчена: обновление должно остановиться'
    Set-Release @('DshPanel.zip', 'dsh-panel-setup.exe', 'SHA256SUMS.txt') 'broken'
    $text = Invoke-Prepare 'broken'
    Check 'испорченная сумма: отказ с объяснением' (($text -match 'не совпала') -and ($text -match 'DshPanel\.zip'))
    Check 'испорченная сумма: панель не распакована' (-not ($text -match 'обновление подготовлено'))
    $left = @(Get-ChildItem -LiteralPath $stage -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $version })
    Check 'испорченная сумма: за собой убрано' ($left.Count -eq 0)

    # --- случай 3: файла сумм в выпуске нет ---------------------------------------
    Write-Host '4) файла сумм нет: обновление должно отказаться'
    Set-Release @('DshPanel.zip', 'dsh-panel-setup.exe') 'none'
    $text = Invoke-Prepare 'no-sums'
    Check 'нет файла сумм: отказ с объяснением' ($text -match 'контрольных сумм')
    Check 'нет файла сумм: панель не распакована' (-not ($text -match 'обновление подготовлено'))
}
finally {
    if ($stubProcess -and -not $stubProcess.HasExited) { Stop-Process -Id $stubProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:DSH_PANEL_API -ErrorAction SilentlyContinue
    Remove-Item Env:DSH_PANEL_REPO -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ('ПРОВАЛЕНО проверок: ' + $failures.Count + ' — ' + ($failures -join '; '))
    if ($Keep) { Write-Host ('песочница осталась здесь: ' + $work) } else { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}

Write-Host 'путь обновления: все проверки пройдены'
if ($Keep) { Write-Host ('песочница осталась здесь: ' + $work) } else { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
exit 0
