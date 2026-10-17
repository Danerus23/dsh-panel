# check-encoding.ps1 — сторожок кодировки. Перенесён из панели 1.x (tools\check-encoding.ps1)
# и приспособлен к раскладке 2.0.
#
#   pwsh -NoProfile -File .\tools\check-encoding.ps1
#   pwsh -NoProfile -File .\tools\check-encoding.ps1 -Fix
#
# ЗАЧЕМ. Windows PowerShell 5.1 читает файл без BOM в кодировке консоли (на русской Windows —
# 866), и .ps1 с кириллицей падает с невнятной ошибкой разбора внутри строки. Обратная беда
# не мягче: BOM в .json ломает разбор (System.Text.Json читает BOM как мусор перед значением).
# Поэтому правило ДВУХ родов, и оно следует за тем, КТО файл читает:
#
#   * .ps1, .iss  — UTF-8 **с** BOM и переводы строк CRLF (их читают PowerShell 5.1 и Inno);
#   * .cmd, .bat  — переводы строк CRLF (их читает cmd.exe);
#   * .json, .csproj, .props, .targets, .slnx, .config — UTF-8 **без** BOM и без ведущего U+FEFF
#     (их читают .NET и System.Text.Json: «The JSON value could not be converted» из-за
#     невидимых трёх байт — ровно та поломка, которую ищет эта проверка).
#   * ⚠️ **.json — ЕЩЁ И CRLF** (правило введено 12.10.2026 вместе с `*.json text eol=crlf`
#     в `.gitattributes`). От какого случая: три словаря жили с LF, а `.gitattributes` их
#     переводы строк не закреплял, поэтому правка ШЕСТИ строк в них показала коммит
#     в 2446 строк — `git diff --ignore-cr-at-eol` давал +424/−191 против +4084/−3851.
#     Переводы строк файла данных — такая же часть формата, как отсутствие BOM: пока их
#     не закрепляет никто, их меняет любой инструмент правки, и дифф перестаёт что-либо
#     доказывать. **Сторож этого не видел вовсе** — поэтому .json и попал в список CRLF.
#
# ⚠️ ЧЕСТНО О ГРАНИЦАХ, и это не оговорка, а измеренный факт:
#   * запись через [IO.File]::WriteAllText(путь, текст) **БЕЗ** явной кодировки пишет UTF-8
#     БЕЗ BOM (замерено: 208,191,209 — это «пр», а не «EF BB BF»); Set-Content -Encoding UTF8
#     в PowerShell 7 — тоже без BOM. То есть инструмент теряет BOM молча, и восстановить его
#     может только явная запись `New-Object Text.UTF8Encoding($true)` — это и делает -Fix;
#   * проверка НЕ смотрит внутрь PNG, ICO и ZIP: расширения в списке нет, а решение по таким
#     файлам не выносится вовсе (иначе вердикт читался бы шире, чем он есть);
#   * «переводы строк не CRLF» и «нет BOM» — это ПРАВКИ, а «U+FEFF внутри текста» — уже
#     правка содержимого: -Fix его НЕ вырезает, а только показывает (иначе сторожок молча
#     менял бы текст, который кто-то написал намеренно).
#
# Коды возврата, как у прочих проверок проекта: 0 — в порядке, 1 — не в порядке, 2 — нечего
# проверять (неприменимо; «неприменимо» — не «зелено»).

param(
    [switch]$Fix,
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

# Корень — параметром, а не только «на папку выше»: без этого сторожок нельзя прогнать на
# песочнице, а проверка, которую нельзя уронить нарочно, ничего не стоит (её и роняют так).
if (-not $Root) {
    $Root = Split-Path -Parent $PSScriptRoot
}
# ⚠️ ПРОПАВШИЙ ВХОД = ОТКАЗ, А НЕ АВАРИЯ (t15, 17.10.2026). Было: `-Root <нет такого>` роняло
# Resolve-Path — исключение и код 1 вместо названного отказа. Замер до правки это подтвердил.
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host ('неприменимо: под корнем «' + $Root + '» нет каталога — проверять нечего')
    exit 2
}
$root = (Resolve-Path -LiteralPath $Root).Path

# Папки сборки и лаборатории: там лежат копии исходников, и вердикт по ним ничего не значит.
$excluded = '\\(bin|obj|dist|upd-build|_lab[^\\]*|\.git|\.vs|\.nuget)\\'
$withBom = @('.ps1', '.iss')
$withoutBom = @('.json', '.csproj', '.props', '.targets', '.slnx', '.config')
$crlfOnly = @('.ps1', '.iss', '.cmd', '.bat', '.json')

$candidates = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excluded })

$files = @($candidates | Where-Object {
    $withBom -contains $_.Extension.ToLowerInvariant() -or
    $withoutBom -contains $_.Extension.ToLowerInvariant() -or
    $crlfOnly -contains $_.Extension.ToLowerInvariant()
})

if ($files.Count -eq 0) {
    Write-Host ('нечего проверять: под корнем «' + $root + '» нет ни одного файла из списка' +
        ' (.ps1/.iss/.json/.csproj/.props/.targets/.slnx/.config/.cmd/.bat)')
    exit 2
}

# ⚠️ ПРОПАВШИЙ ВХОД: ОБХОД ПАПКИ ВИДИТ ТОЛЬКО ТО, ЧТО ЛЕЖИТ (t15, 17.10.2026). Файл, который есть
# в git, но исчез с диска, для обхода не существует — и сторож молчал: замер до правки — убран
# `tools\check-personal.ps1`, код 0, «кодировка в порядке: 27 файлов» вместо 28. Перечень входа
# знает git, поэтому сверяемся и с ним; без git (или в собранном дереве) остаётся обход.
$checkedExtensions = @($withBom + $withoutBom + $crlfOnly | Select-Object -Unique)
$missingTracked = @()
if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $trackedFiles = @(& git -C $root ls-files --cached 2>$null)
    if ($LASTEXITCODE -eq 0) {
        foreach ($rel in $trackedFiles) {
            if ($checkedExtensions -notcontains [IO.Path]::GetExtension($rel).ToLowerInvariant()) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $root ($rel -replace '/', '\')))) { $missingTracked += $rel }
        }
    }
}

if ($missingTracked.Count -gt 0) {
    Write-Host ('ПРОПАВШИЙ ВХОД: в git файл есть, на диске нет — ' + $missingTracked.Count)
    foreach ($item in $missingTracked) { Write-Host ('  ' + $item) }
    Write-Host 'эти файлы сторож обязан был проверить, а обход папки их не видит вовсе'
    exit 1
}

$utf8Bom = New-Object Text.UTF8Encoding($true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$bad = @()
$fixed = 0
$counts = @{}

foreach ($file in $files) {
    $extension = $file.Extension.ToLowerInvariant()
    if ($counts.ContainsKey($extension)) { $counts[$extension]++ } else { $counts[$extension] = 1 }

    $bytes = [IO.File]::ReadAllBytes($file.FullName)

    # Считаем ВСЕ ведущие BOM, а не только первый: файл EF BB BF EF BB BF прежняя проверка
    # 1.x пропускала (смотрела три байта) — лишний U+FEFF делает первую строку кодом, а не
    # комментарием, и .ps1 перестаёт разбираться целиком.
    $bomCount = 0
    while ($bytes.Length -ge ($bomCount * 3) + 3 -and
           $bytes[$bomCount * 3] -eq 0xEF -and
           $bytes[$bomCount * 3 + 1] -eq 0xBB -and
           $bytes[$bomCount * 3 + 2] -eq 0xBF) {
        $bomCount++
    }

    $offset = $bomCount * 3
    $text = $utf8NoBom.GetString($bytes, $offset, $bytes.Length - $offset)
    $needsCrlf = ($text -match "(?<!`r)`n") -and ($crlfOnly -contains $extension)
    $strayBom = [regex]::Matches($text, '\uFEFF').Count
    $name = $file.FullName.Substring($root.Length).TrimStart('\')

    if ($withBom -contains $extension) {
        if ($bomCount -eq 0) { $bad += ($name + ' — нет BOM (его читает PowerShell 5.1 / Inno)') }
        if ($bomCount -gt 1) { $bad += ($name + ' — ДВОЙНОЙ BOM (' + $bomCount + ' подряд)') }
    }
    else {
        # Файл обязан быть чистым: BOM в .json/.csproj/.slnx/.config — это поломка разбора.
        if ($bomCount -gt 0) { $bad += ($name + ' — BOM там, где его быть не должно (' + $bomCount + ')') }
    }

    if ($strayBom -gt 0) { $bad += ($name + ' — лишний U+FEFF внутри текста: ' + $strayBom) }
    if ($needsCrlf) { $bad += ($name + ' — переводы строк не CRLF (файл читает cmd.exe или PowerShell 5.1)') }

    # ASCII-требование к сценариям замены файлов: cmd.exe читает .cmd в кодировке консоли
    # (на русской Windows — cp866), и не-ASCII знак делает путь несуществующим. Это правило
    # проекта (UpdateScript), поэтому сторожок говорит о нём прямо, а не «на глаз».
    if ($extension -in '.cmd', '.bat') {
        for ($i = 0; $i -lt $text.Length; $i++) {
            if ([int]$text[$i] -ge 128) {
                $bad += ($name + ' — не-ASCII знак в сценарии: код ' + [int]$text[$i] + ' на позиции ' + $i)
                break
            }
        }
    }

    if ($Fix) {
        $changed = $false
        $target = $text

        if ($needsCrlf) {
            $target = ($target -replace "`r`n", "`n") -replace "`n", "`r`n"
            $changed = $true
        }

        $wantBom = $withBom -contains $extension
        if ($bomCount -ne ($(if ($wantBom) { 1 } else { 0 }))) { $changed = $true }

        if ($changed) {
            # Лишние ведущие BOM снимаются сами: пишем текст после них, ровно с нужной шапкой.
            # U+FEFF внутри текста НЕ вырезаем: это правка содержимого, о ней сказано отдельно.
            [IO.File]::WriteAllText($file.FullName, $target, $(if ($wantBom) { $utf8Bom } else { $utf8NoBom }))
            $fixed++
            Write-Host ('  поправил: ' + $name)
        }
    }
}

$summary = ($counts.Keys | Sort-Object | ForEach-Object { $_ + '=' + $counts[$_] }) -join ', '

if ($Fix) {
    Write-Host ('проверено файлов: {0} ({1}); поправлено: {2}' -f $files.Count, $summary, $fixed)
    exit 0
}

if ($bad.Count -gt 0) {
    Write-Host ('кодировка не та ({0} замечаний; перезапустите с -Fix, если речь о BOM и CRLF):' -f $bad.Count)
    foreach ($item in $bad) { Write-Host ('  ' + $item) }
    exit 1
}

Write-Host ('кодировка в порядке: {0} файлов ({1})' -f $files.Count, $summary)
exit 0
