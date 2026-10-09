# check-changelogs.ps1 — ВОРОТА ПАРИТЕТА ВЫПУСКА: одна и та же версия описана во ВСЕХ ТРЁХ
# CHANGELOG, раздел непустой и стоит первым.
#
#   pwsh -NoProfile -File .\tools\check-changelogs.ps1
#   pwsh -NoProfile -File .\tools\check-changelogs.ps1 -Tree C:\Harness\release-v2-clean-new
#
# ЗАЧЕМ ОТДЕЛЬНО. Ответ на вопрос «что не разъехалось» раньше держался на памяти сессии: три
# `CHANGELOG` (ru/en/zh) правятся руками, а версия живёт отдельно — в `src\DshPanel\DshPanel.csproj`.
# Промах виден не сразу и не здесь, а в выпуске: соберётся он по тегу, а конвейер зовёт
# `tools\release-notes.ps1`, который требует раздел `## <версия>` в КАЖДОМ из трёх файлов и падает,
# если его нет. Так уже случалось 30.09.2026 (ручной прогон 36663614401): версию брали из ветки —
# искался раздел `## main`. Здесь тот же вопрос задаётся ДО тега и называется словами.
#
# ЧТО ЭТА ПРОВЕРКА МОЖЕТ (и это надо знать, прежде чем ей верить):
#   * ловит отсутствие раздела новой версии хотя бы в одном из трёх файлов;
#   * ловит ПУСТОЙ раздел (заголовок есть, текста нет) — панель на пустом блоке молча отдаёт
#     английский, то есть человек получил бы чужой язык и не узнал об этом;
#   * ловит раздел, стоящий НЕ ПЕРВЫМ: свежая версия обязана быть сверху (решение проекта),
#     иначе описание выпуска соберётся из старого раздела;
#   * ловит расхождение версии в `DshPanel.csproj` и в теге, если тег назван ключом `-Tag`.
#
# ЧЕГО ОНА НЕ ВИДИТ: смысла написанного. Читает ли человек историю версий и правда ли то, что в ней
# сказано, — не проверяется механически, и проверка об этом говорит прямо.

param(
    [string]$Tree = '',
    [string]$Version = '',
    [string]$Tag = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Tree) { $Tree = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path -LiteralPath $Tree)) { throw ('нет дерева: ' + $Tree) }

# Три файла и то, как называть их в выводе. Порядок — от языка-оригинала (русский) к переводам.
$files = @(
    [pscustomobject]@{ Name = 'CHANGELOG.ru.md'; Title = 'русский (оригинал)' },
    [pscustomobject]@{ Name = 'CHANGELOG.md';    Title = 'английский' },
    [pscustomobject]@{ Name = 'CHANGELOG.zh.md'; Title = 'китайский' }
)

$present = @($files | Where-Object { Test-Path -LiteralPath (Join-Path $Tree $_.Name) })
if ($present.Count -eq 0) {
    Write-Host 'CHANGELOG в дереве нет — это не дерево выпуска, сверять нечего'
    exit 2
}

# Версия — из ЕДИНСТВЕННОГО места, откуда её берут установщик и описание выпуска.
if (-not $Version) {
    $project = Join-Path $Tree 'src\DshPanel\DshPanel.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw ('нет проекта панели: ' + $project) }

    $found = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches
    if (-not $found -or $found.Count -eq 0) { throw ('не нашёл <Version> в ' + $project) }
    $Version = $found[0].Groups[1].Value
}

$version = $Version.Trim().TrimStart('v', 'V')
if (-not $version) { throw ('не понял версию: ' + $Version) }

$problems = @()

# Тег, если он назван: `v2.0.3` и `<Version>2.0.3` обязаны совпадать (правило проекта).
if ($Tag) {
    $tag = $Tag.Trim().TrimStart('v', 'V')
    if ($tag -ne $version) {
        $problems += ('тег «' + $Tag + '» не совпадает с версией проекта «' + $version + '»')
    }
}

$firstSections = @{}

foreach ($file in $files) {
    $path = Join-Path $Tree $file.Name
    if (-not (Test-Path -LiteralPath $path)) {
        if ($present.Count -eq $files.Count) {
            $problems += ($file.Name + ' (' + $file.Title + ') — файла нет, а два других есть')
        }
        continue
    }

    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $lines = $text -split "`n"

    $headings = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s+') { $headings += $i }
    }

    if ($headings.Count -eq 0) {
        $problems += ($file.Name + ' (' + $file.Title + ') — ни одного раздела «## версия»')
        continue
    }

    $first = $headings[0]
    $firstTitle = $lines[$first].Trim()
    $firstSections[$file.Name] = $firstTitle

    if ($firstTitle -notmatch ('^##\s+' + [regex]::Escape($version) + '(\s|$)')) {
        $problems += ($file.Name + ' (' + $file.Title + ') — первым разделом стоит «' + $firstTitle + '», а не «## ' + $version + '»')
        continue
    }

    $end = if ($headings.Count -gt 1) { $headings[1] } else { $lines.Count }
    $body = @($lines[($first + 1)..($end - 1)] | Where-Object { $_.Trim().Length -gt 0 })

    if ($body.Count -eq 0) {
        $problems += ($file.Name + ' — раздел «' + $version + '» пуст: описание выпуска соберётся пустым, и панель покажет чужой язык')
    }
}

if ($problems.Count -gt 0) {
    Write-Host ('CHANGELOG: ' + $problems.Count + ' замечаний (версия проекта ' + $version + ', файлов найдено ' + $present.Count + ')')
    foreach ($item in $problems) { Write-Host ('  ' + $item) }
    Write-Host ''
    Write-Host 'ЧЕГО ЭТА ПРОВЕРКА НЕ ВИДИТ: смысла написанного. Правда ли то, что сказано в истории версий, читает человек.'
    exit 1
}

Write-Host ('история версий в порядке: «## ' + $version + '» первым разделом во всех трёх файлах, каждый непуст')
foreach ($file in $files) {
    if ($firstSections.ContainsKey($file.Name)) {
        Write-Host ('  ' + $file.Name + ' — ' + $firstSections[$file.Name])
    }
}
Write-Host 'ЧЕСТНО О ГРАНИЦЕ: проверка механическая. Смысл записей читает человек.'
exit 0
