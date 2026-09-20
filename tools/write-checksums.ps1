# write-checksums.ps1 — собрать файл контрольных сумм выпуска (SHA256SUMS.txt).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\write-checksums.ps1 -Dist dist
#
# Зачем: панель обновляет себя, скачивая DshPanel.zip из выпуска на GitHub. Своего
# сертификата у проекта нет, поэтому единственная доступная проверка скачанного — сверка
# с опубликованной контрольной суммой. Без SHA256SUMS.txt в выпуске обновление
# останавливается с понятным сообщением (см. UpdateService.Prepare), а этот скрипт
# гарантирует, что файл есть и в нём перечислены ровно те сборки, которые уехали в выпуск.
#
# Формат — как у sha256sum: «хэш  имя файла» (два пробела), UTF-8 БЕЗ BOM:
# с BOM первый хэш перестал бы быть хэшем, и разбор в панели его пропустил бы.

param(
    [string]$Dist = '',
    [string[]]$Files = @('dsh-panel-setup.exe', 'DshPanel.zip', 'DshPanel-selfcontained.zip')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dist) { $Dist = Join-Path $root 'dist' }

if (-not (Test-Path -LiteralPath $Dist)) { throw ('Нет папки выпуска: ' + $Dist) }

$lines = New-Object System.Collections.Generic.List[string]
$missing = New-Object System.Collections.Generic.List[string]

foreach ($name in $Files) {
    $path = Join-Path $Dist $name
    if (-not (Test-Path -LiteralPath $path)) {
        # Отсутствие файла — не беда этого скрипта: с -SkipInstaller установщика и не должно
        # быть. Что все нужные сборки на месте, проверяет tools\check-versions.ps1.
        $missing.Add($name)
        continue
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add(($hash + '  ' + $name))
    Write-Host ('  {0}…  {1}' -f $hash.Substring(0, 16), $name)
}

if ($missing.Count -gt 0) {
    Write-Warning ('В выпуске нет этих файлов, в суммы они не попали: ' + ($missing -join ', '))
}

if ($lines.Count -eq 0) {
    throw ('В ' + $Dist + ' не нашлось ни одной сборки выпуска — контрольные суммы собирать не из чего.')
}

$out = Join-Path $Dist 'SHA256SUMS.txt'
$text = (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
[IO.File]::WriteAllText($out, $text, (New-Object Text.UTF8Encoding($false)))

Write-Host ('Контрольные суммы: {0} (строк {1})' -f $out, $lines.Count)
