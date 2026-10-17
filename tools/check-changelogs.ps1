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
# КОДЫ: 0 — история версий в порядке; 1 — расхождение (названо файлом и причиной); 2 — неприменимо
# (CHANGELOG в дереве нет; в дереве нет проекта панели или в нём нет <Version>, а ключ -Version не назван).
#
# ⚠️ До 17.10.2026 два обещания этой шапки НЕ ИСПОЛНЯЛИСЬ, и оба — «проверка, которая не может упасть»:
#   * ПУСТОЙ первый раздел давал ЗЕЛЁНЫЙ прогон: диапазон PowerShell «5..4» обратный, и «телом»
#     становились сам заголовок и строка перед ним (дефект найден мутацией M2 в r2);
#   * прогон на дереве данных без `-Version` падал НЕОБРАБОТАННЫМ ИСКЛЮЧЕНИЕМ с кодом 1 вместо
#     кода 2 «неприменимо» со словами (мутация M3).
# Обе беды закрыты; прогоны «до/после» и мутации — `_team\c4\r10-storozh.md`.
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
    Write-Host 'неприменимо: CHANGELOG в дереве нет — это не дерево выпуска, сверять нечего'
    exit 2
}

# Версия — из ЕДИНСТВЕННОГО места, откуда её берут установщик и описание выпуска.
if (-not $Version) {
    $project = Join-Path $Tree 'src\DshPanel\DshPanel.csproj'

    # ⚠️ ЭТО НЕ ОШИБКА ВЫЗОВА, А «НЕПРИМЕНИМО»: дерево данных (`-Tree .\release\tree`) — законное
    # дерево выпуска, но проекта панели в нём нет по устройству. Прежде здесь стоял `throw`, и прогон
    # падал необработанным исключением с кодом 1, а не кодом 2 со словами (дефект найден 17.10.2026,
    # тот же класс, что обратный диапазон ниже).
    if (-not (Test-Path -LiteralPath $project)) {
        Write-Host ('неприменимо: в дереве «' + $Tree + '» нет проекта панели (' + $project + ') — версию взять неоткуда; назовите её ключом -Version')
        exit 2
    }

    $found = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches
    if (-not $found -or $found.Count -eq 0) {
        Write-Host ('неприменимо: в ' + $project + ' нет <Version> — версию взять неоткуда; назовите её ключом -Version')
        exit 2
    }
    $Version = $found[0].Groups[1].Value
}

$version = $Version.Trim().TrimStart('v', 'V')
if (-not $version) { Write-Host ('неприменимо: версия «' + $Version + '» пустая — сверять не с чем'); exit 2 }

$problems = @()

# Тег, если он назван: номер тега и `<Version>` обязаны совпадать (правило проекта: vX.Y.Z = <Version>).
#
# ⚠️ СВЕРЯЕТСЯ НОМЕР, А НЕ НАПИСАНИЕ — и оба довода измерены 17.10.2026 (цикл V-01):
#   1. было `TrimStart('v','V')` — он снимает ВСЕ ведущие «v», а не один: репетиционный тег
#      `vtest-2.0.3-cg` превращался в «test-2.0.3-cg» и сторож краснел на ИСПРАВНОМ дереве;
#   2. решение владельца 5 (ADR-001 §5.1): до появления `v2.1.0` проверка принимается на
#      временном локальном теге `vtest-…`, который не пушится и удаляется. Значит номер версии
#      берётся из имени тега: `v2.0.3` → 2.0.3 и `vtest-2.0.3-cg` → 2.0.3.
# Тег, в имени которого номера версии нет (`vtest-empty`), остаётся КРАСНЫМ: он не выпускной.
if ($Tag) {
    $tagNumber = $null
    if ($Tag -match '(\d+\.\d+\.\d+)') { $tagNumber = $Matches[1] }
    if (-not $tagNumber) { $tagNumber = ($Tag.Trim() -replace '^[vV]', '') }
    if ($tagNumber -ne $version) {
        $problems += ('тег «' + $Tag + '» (номер «' + $tagNumber + '») не совпадает с версией проекта «' + $version + '»')
    }
}

$firstSections = @{}

foreach ($file in $files) {
    $path = Join-Path $Tree $file.Name
    if (-not (Test-Path -LiteralPath $path)) {
        # ⚠️ ЗДЕСЬ БЫЛО `if ($present.Count -eq $files.Count)` — «все три файла на месте», то есть
        # условие, которое при ОТСУТСТВУЮЩЕМ файле не выполняется никогда: пропажа одного из трёх
        # CHANGELOG давала ЗЕЛЁНЫЙ прогон (найдено 17.10.2026 при проверке соседних случаев, тот же
        # класс, что обратный диапазон). Молча пропустить отсутствующий файл нельзя: ниже цикл
        # продолжился бы, и о беде не сказал бы никто.
        $problems += ($file.Name + ' (' + $file.Title + ') — файла нет: в дереве ' + $present.Count + ' из ' + $files.Count + ' CHANGELOG')
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
    # ⚠️ ДИАПАЗОН PowerShell ОБРАТНЫЙ, когда конец меньше начала: у ПУСТОГО раздела ($end = $first + 1)
    # выражение ($first + 1)..($end - 1) даёт «5..4» — то есть сам заголовок и строку перед ним, и тело
    # пустого раздела считалось НЕПУСТЫМ. Дефект найден мутацией M2 в r2 (17.10.2026): заголовок
    # «## 2.1.0» без текста давал ЗЕЛЁНЫЙ прогон, хотя шапка обещает ловить пустой раздел.
    # Поэтому диапазон собирается только тогда, когда он прямой.
    $body = @()
    if ($end -gt $first + 1) { $body = @($lines[($first + 1)..($end - 1)] | Where-Object { $_.Trim().Length -gt 0 }) }

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
