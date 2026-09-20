# verify-release.ps1 — проверка опубликованного выпуска: скачать ассеты и сверить с суммами.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <папка репозитория>\tools\verify-release.ps1
#
# Проверяет именно то, что получит человек со страницы выпуска:
#   1. контрольные суммы скачанных файлов совпадают с SHA256SUMS.txt;
#   2. установщик несёт версию выпуска;
#   3. архив панели распаковывается и внутри DshTray.exe той же версии.

param(
    [string]$Repo = 'Danerus23/dsh-panel',
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$version = $Tag.TrimStart('v')
if (-not $Out) { $Out = Join-Path $env:TEMP ('dsh-release-check-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

Write-Host ('Скачиваю ассеты выпуска ' + $Tag) -ForegroundColor Green
& gh release download $Tag --repo $Repo --dir $Out
if ($LASTEXITCODE -ne 0) { throw 'не удалось скачать ассеты выпуска' }

Get-ChildItem -LiteralPath $Out | ForEach-Object { '  {0,-26} {1,9:N2} МБ' -f $_.Name, ($_.Length / 1MB) }

# 1. Контрольные суммы
$sums = Join-Path $Out 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sums)) { throw 'в выпуске нет SHA256SUMS.txt — панель откажется обновляться' }

$bad = @()
foreach ($line in Get-Content -LiteralPath $sums -Encoding UTF8) {
    if (-not $line.Trim()) { continue }
    $parts = $line.Trim() -split '\s+', 2
    if ($parts.Count -lt 2) { continue }
    $expected = $parts[0].ToLower()
    $file = Join-Path $Out $parts[1].Trim()
    if (-not (Test-Path -LiteralPath $file)) { $bad += ('нет файла ' + $parts[1]); continue }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) { $bad += ('сумма не сходится: ' + $parts[1]) }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host ('БЕДА  ' + $_) -ForegroundColor Red }; throw 'контрольные суммы не сходятся' }
Write-Host 'Суммы сходятся' -ForegroundColor Green

# 2. Версия установщика
$setup = Join-Path $Out 'dsh-panel-setup.exe'
if (Test-Path -LiteralPath $setup) {
    $setupVersion = (Get-Item -LiteralPath $setup).VersionInfo.ProductVersion
    '  версия установщика: ' + $setupVersion
    if ($setupVersion -notlike ($version + '*')) { throw ('версия установщика ' + $setupVersion + ' не ' + $version) }
}

# 3. Панель внутри архива
$zip = Join-Path $Out 'DshPanel.zip'
if (Test-Path -LiteralPath $zip) {
    $unpack = Join-Path $Out 'unpacked'
    Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
    $exe = Join-Path $unpack 'DshTray.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'в архиве нет DshTray.exe' }
    $panelVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    '  версия панели в архиве: ' + $panelVersion
    if ($panelVersion -notlike ($version + '*')) { throw ('версия панели ' + $panelVersion + ' не ' + $version) }
}

Write-Host ''
Write-Host ('Выпуск ' + $Tag + ' проверен: суммы, установщик и архив совпадают по версии.') -ForegroundColor Green
