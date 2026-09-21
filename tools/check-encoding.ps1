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
#
# Отдельно ловится ДВОЙНОЙ BOM: файл вида EF BB BF EF BB BF раньше проходил проверку —
# она смотрела только первые три байта. Лишний U+FEFF делает первую строку кодом, а не
# комментарием, и .ps1 перестаёт разбираться целиком (так tools\winget-manifest.ps1 и
# дожил до независимого ревью при зелёном конвейере). -Fix оставляет ровно один BOM;
# U+FEFF внутри текста (не в начале) только показывается — это уже правка содержимого.

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
    # Считаем ВСЕ ведущие BOM, а не только первый: файл с двумя BOM (EF BB BF EF BB BF)
    # прежняя проверка пропускала — она смотрела три байта и объявляла кодировку годной.
    # Лишний U+FEFF перед первой строкой делает её не комментарием, а кодом: у .ps1 это
    # ошибки разбора, у .iss — непонятное сообщение компилятора. Такой файл невидим, пока
    # его не начнёт читать PowerShell 5.1, поэтому проверяем и внутри текста.
    $bomCount = 0
    while ($bytes.Length -ge ($bomCount * 3) + 3 -and
           $bytes[$bomCount * 3] -eq 0xEF -and
           $bytes[$bomCount * 3 + 1] -eq 0xBB -and
           $bytes[$bomCount * 3 + 2] -eq 0xBF) {
        $bomCount++
    }

    $offset = $bomCount * 3
    $text = $utf8NoBom.GetString($bytes, $offset, $bytes.Length - $offset)
    $needsCrlf = $text -match "(?<!`r)`n"
    # U+FEFF где угодно, кроме начала: невидимый символ внутри строки или отступа.
    $strayBom = [regex]::Matches($text, '\uFEFF').Count
    $name = $file.FullName.Substring($root.Length).TrimStart('\')

    if ($bomCount -eq 0) { $bad += ($name + ' — нет BOM') }
    if ($bomCount -gt 1) { $bad += ($name + ' — ДВОЙНОЙ BOM (' + $bomCount + ' подряд)') }
    if ($strayBom -gt 0) { $bad += ($name + ' — лишний U+FEFF внутри текста: ' + $strayBom) }
    if ($needsCrlf) { $bad += ($name + ' — переводы строк не CRLF') }

    if ($Fix -and ($bomCount -ne 1 -or $needsCrlf)) {
        $text = $text -replace "`r`n", "`n"
        $text = $text -replace "`n", "`r`n"
        # Лишние ведущие BOM снимаются сами: пишем текст после них ровно с одним BOM.
        # U+FEFF внутри текста НЕ вырезаем: это правка содержимого, о ней сообщаем отдельно.
        [IO.File]::WriteAllText($file.FullName, $text, $utf8Bom)
        if ($bomCount -gt 1) { Write-Host ('  поправил (BOM было ' + $bomCount + ', стало 1): ' + $name) }
        else { Write-Host ('  поправил: ' + $name) }
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
