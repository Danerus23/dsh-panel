# release-notes.ps1 — собрать ОПИСАНИЕ ВЫПУСКА из трёх CHANGELOG ровно в том виде, какой
# разбирает панель.
#
# Зачем. Панель показывает заметки к выпуску на СВОЁМ языке (`Update\UpdateDecisions.PickNotes`):
# она ищет машинные метки `<!-- dsh-notes:xx -->` (они СИЛЬНЕЕ человеческих заголовков `## RU`)
# и берёт блок нужного языка. Значит у выпуска на GitHub обязаны быть три размеченных блока —
# иначе человек увидит чужой язык или сами служебные метки.
#
# ⚠️ Почему инструмент переехал в `tools\` (30.09.2026). Выпуски `v2.0.0` и `v2.0.1` собирались
# вручную, и описание к ним ставили руками — поэтому дыры не было видно. В конвейере же стояло
# `gh release create --generate-notes`: это английский текст GitHub БЕЗ меток, и панель показала бы
# его на всех трёх языках. Дыру нашли при разборе выпускного пути (в CI выпуск не создавался ни
# разу: оба тега падали на шаге установщика).
#
# Запуск:  pwsh -NoProfile -File .\tools\release-notes.ps1 -Version v2.0.1 -Out dist\release-notes.md
#          Без -Tree берётся корень репозитория — так зовёт конвейер (шаг «Собрать описание выпуска»).

param(
    [string]$Tree = '',
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'

if (-not $Tree) { $Tree = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path -LiteralPath $Tree)) { throw ('нет дерева выпуска: ' + $Tree) }

# Метка приходит из конвейера как `v2.0.1`, а раздел в CHANGELOG называется `## 2.0.1`.
$version = $Version.TrimStart('v', 'V')
if (-not $version) { throw ('не понял версию: ' + $Version) }

function Get-Section([string]$Path, [string]$Version) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $lines = $text -split "`n"

    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^##\s+' + [regex]::Escape($Version) + '(\s|$)')) { $start = $i + 1; break }
    }
    if ($start -lt 0) { throw ('в ' + $Path + ' нет раздела ## ' + $Version) }

    $end = $lines.Count
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s') { $end = $i; break }
    }

    # Хвостовые пустые строки убираем: они попали бы в блок и «съели» закрывающую метку.
    # ⚠️ Длину СЧИТАЕМ, а не режем срезом `$body[0..($body.Count - 2)]`: на ОДНОЙ пустой строке
    # такой срез даёт `$body[0..-1]`, а это в PowerShell два последних элемента — цикл не кончается
    # НИКОГДА. Нашла мутация 30.09.2026 («раздел пуст»): прогон не падал, а **завис**, то есть
    # проверка, обязанная падать, висела; в конвейере это был бы отказ выпуска без причины.
    $body = $lines[$start..($end - 1)]
    $keep = $body.Count
    while ($keep -gt 0 -and [string]::IsNullOrWhiteSpace($body[$keep - 1])) { $keep-- }
    $body = if ($keep -gt 0) { $body[0..($keep - 1)] } else { @() }
    if ($body.Count -eq 0) { throw ('раздел ## ' + $Version + ' в ' + $Path + ' пуст') }

    return ($body -join "`n")
}

$blocks = @(
    [pscustomobject]@{ Marker = 'en'; Heading = '## English'; File = 'CHANGELOG.md' },
    [pscustomobject]@{ Marker = 'ru'; Heading = '## Русский'; File = 'CHANGELOG.ru.md' },
    [pscustomobject]@{ Marker = 'zh'; Heading = '## 中文';    File = 'CHANGELOG.zh.md' }
)

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# DSH Panel ' + $version)
[void]$sb.AppendLine()
[void]$sb.AppendLine('The panel speaks three languages. Russian is the original; **English and Chinese are')
[void]$sb.AppendLine('machine translations** — the meaning is right, but some wording may not sound native.')
[void]$sb.AppendLine()
[void]$sb.AppendLine('---')

foreach ($b in $blocks) {
    $path = Join-Path $Tree $b.File
    if (-not (Test-Path -LiteralPath $path)) { throw ('нет файла: ' + $path) }

    $body = Get-Section $path $version

    [void]$sb.AppendLine()
    [void]$sb.AppendLine($b.Heading)
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('<!-- dsh-notes:' + $b.Marker + ' -->')
    [void]$sb.AppendLine($body)
    [void]$sb.AppendLine('<!-- /dsh-notes:' + $b.Marker + ' -->')
}

$notes = $sb.ToString()

# ⚠️ Самопроверка: три метки, каждая пара на месте и ни один блок не пуст. Без неё «описание
# собралось» означало бы только «файл появился», а панель на пустом блоке молча отдаёт английский
# (ловушка `Loc.TIn`) — то есть человек получил бы чужой язык и не узнал об этом.
$check = [System.Text.StringBuilder]::new()
foreach ($b in $blocks) {
    $open = '<!-- dsh-notes:' + $b.Marker + ' -->'
    $close = '<!-- /dsh-notes:' + $b.Marker + ' -->'
    $openAt = $notes.IndexOf($open, [StringComparison]::Ordinal)
    $closeAt = $notes.IndexOf($close, [StringComparison]::Ordinal)
    if ($openAt -lt 0 -or $closeAt -lt 0) { throw ('блок ' + $b.Marker + ' не собрался: нет меток') }
    if ($closeAt -lt $openAt) { throw ('блок ' + $b.Marker + ': закрывающая метка раньше открывающей') }
    if (($closeAt - $openAt) -le ($open.Length + 2)) { throw ('блок ' + $b.Marker + ' пуст') }
    [void]$check.Append($b.Marker)
}

[IO.File]::WriteAllText($Out, $notes, (New-Object Text.UTF8Encoding($false)))
Write-Host ('описание выпуска ' + $version + ': ' + $Out + ' (' + (Get-Item $Out).Length + ' Б), блоки: ' + $check.ToString())
