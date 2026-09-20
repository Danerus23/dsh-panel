# check-encoding.ps1 — сторожок кодировки скриптов.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-encoding.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-encoding.ps1 -Fix
#
# Зачем: Windows PowerShell 5.1 читает файл без BOM в кодировке консоли (на русской
# Windows — 866), и скрипт с кириллицей падает с невнятной ошибкой разбора внутри строки.
# Один такой случай уже был: installer\build-installer.ps1 после правки редактором без BOM.
# Поэтому .ps1 и .iss держим в UTF-8 с BOM и проверяем это шагом сборки.
#
# Трогаем ТОЛЬКО .ps1 и .iss: проверка кодировки не имеет права переписывать двоичные
# файлы (значки, снимки) и данные. Фильтр по расширению сделан в PowerShell, а не ключом
# -Include: -Include вместе с -LiteralPath игнорируется и возвращает все файлы подряд —
# так однажды были испорчены снимки в docs\screenshots и значок dsh-tray.ico.
#
# -Fix дописывает BOM (содержимое не меняется) и переводит переводы строк в CRLF.

param([switch]$Fix)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$files = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Extension -in '.ps1', '.iss' -and
        $_.FullName -notmatch '\\(app|dist|bin|obj|_build|\.git|\.appdata|\.dotnet-home|\.nuget)\\'
    }

if ($files.Count -eq 0) { throw 'не нашёл ни одного .ps1 или .iss — проверять нечего' }

$utf8Bom = New-Object Text.UTF8Encoding($true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$bad = @()

foreach ($file in $files) {
    # Строб на всякий случай: этот скрипт переписывает файлы, и расширение здесь — не мелочь.
    if ($file.Extension -notin '.ps1', '.iss') { throw ('отказываюсь трогать: ' + $file.FullName) }

    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = $utf8NoBom.GetString($bytes, $offset, $bytes.Length - $offset)
    $needsCrlf = $text -match "(?<!`r)`n"
    $name = $file.FullName.Substring($root.Length).TrimStart('\')

    if (-not $hasBom) { $bad += ($name + ' — нет BOM') }
    if ($needsCrlf) { $bad += ($name + ' — переводы строк не CRLF') }

    if ($Fix -and (-not $hasBom -or $needsCrlf)) {
        $text = $text -replace "`r`n", "`n"
        $text = $text -replace "`n", "`r`n"
        [IO.File]::WriteAllText($file.FullName, $text, $utf8Bom)
        Write-Host ('  поправил: ' + $name)
    }
}

if ($Fix) {
    Write-Host ('проверено файлов: {0}' -f $files.Count)
    exit 0
}

if ($bad.Count -gt 0) {
    Write-Host 'кодировка не та (перезапустите с -Fix):'
    foreach ($item in $bad) { Write-Host ('  ' + $item) }
    exit 1
}

Write-Host ('кодировка в порядке: UTF-8 с BOM, CRLF — {0} файлов' -f $files.Count)
exit 0
