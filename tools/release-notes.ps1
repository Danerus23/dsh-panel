# release-notes.ps1 — собрать тело релиза из трёх историй версий.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-notes.ps1 `
#       -Version 1.21.0 -Out dist\release-notes.md
#
# Зачем отдельным скриптом: тот же текст нужен локальной сборке (make-release.ps1)
# и GitHub Actions при выпуске по тегу — заметки к выпуску показывает окно обновлений
# в панели, поэтому они должны собираться одинаково в обоих местах.
#
# Откуда тексты: раздел текущей версии («## <версия>») из трёх файлов истории —
# CHANGELOG.md (русский, источник истины), CHANGELOG.en.md, CHANGELOG.zh.md.
# Английская и китайская истории ведутся с 1.21.0, история до неё есть только в CHANGELOG.md:
# для версии, которой в них нет, скрипт подставляет русский раздел и ГОВОРИТ об этом —
# пустой блок в теле релиза был бы хуже.
#
# Тело несёт пары машинных маркеров, по которым панель выбирает язык (UpdateService.PickNotes):
# английский блок первым, затем русский, затем китайский. Единственный служебный элемент в
# теле — сам маркер; никакой другой разметки у него нет.
#
# ПОЧЕМУ БЕЗ HTML. Панели СТАРЕЕ 1.21.0 не умеют выбирать блок: они сохраняют и показывают
# тело выпуска КАК ЕСТЬ, вместе со служебными строками. Поэтому <details>/<summary> из тела
# убраны совсем: старый панельный показ — это просто текст, и он не должен нести тегов, которые
# человек увидит как есть. Каждый язык начинается обычным заголовком «## English» — его видно и
# на странице выпуска, и в старой панели.
#
# Версия по умолчанию берётся из DshTray.csproj. Если раздела для неё нет в русском
# CHANGELOG.md — ошибка: выпуск без заметок человек увидит как «нет истории изменений».

param(
    [string]$Version = '',
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $project = Join-Path $root 'DshTray.csproj'
    $match = Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>'
    if (-not $match) { throw ('В ' + $project + ' нет <Version>') }
    $Version = $match.Matches[0].Groups[1].Value
}

# Язык — имя блока в теле релиза; файл — откуда берётся раздел. Русский здесь первый:
# именно он источник истины и он же запасной вариант для версий, которых нет в переводах.
$sources = @(
    [pscustomobject]@{ Language = 'ru'; File = 'CHANGELOG.md' },
    [pscustomobject]@{ Language = 'en'; File = 'CHANGELOG.en.md' },
    [pscustomobject]@{ Language = 'zh'; File = 'CHANGELOG.zh.md' }
)

function Read-Section([string]$path, [string]$version) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }

    $lines = Get-Content -LiteralPath $path -Encoding UTF8
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match ('^##\s+' + [regex]::Escape($version) + '(\s|$)')) { $start = $index; break }
    }
    if ($start -lt 0) { return '' }

    $notes = @()
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^##\s') { break }
        $notes += $lines[$index]
    }

    return (($notes -join [Environment]::NewLine).Trim())
}

$sections = @{}
foreach ($source in $sources) {
    $path = Join-Path $root $source.File
    $sections[$source.Language] = Read-Section $path $Version
}

if (-not $sections['ru']) {
    throw ('Ни в CHANGELOG.md, ни в переводах нет раздела для версии ' + $Version)
}

# Русский раздел — источник истины, поэтому он подставляется в пустой перевод, а не наоборот.
foreach ($source in $sources) {
    $text = $sections[$source.Language]
    if ($text) { continue }
    $sections[$source.Language] = $sections['ru']
    Write-Host ('  перевод отсутствует: ' + $source.File + ' — в блок «' + $source.Language +
                '» подставлен раздел из CHANGELOG.md')
}

$newLine = [Environment]::NewLine
$body = @()
# Каждый язык — обычный заголовок и пара маркеров. Заголовок человекочитаемый: панели до
# 1.21.0 показывают тело целиком, и человек должен видеть, где кончается один язык.
foreach ($item in @(
        [pscustomobject]@{ Language = 'en'; Title = 'English' },
        [pscustomobject]@{ Language = 'ru'; Title = 'Русский' },
        [pscustomobject]@{ Language = 'zh'; Title = '中文' })) {
    $body += ('## ' + $item.Title)
    $body += ''
    $body += ('<!-- dsh-notes:' + $item.Language + ' -->')
    $body += @($sections[$item.Language].Split("`n") | ForEach-Object { $_.TrimEnd("`r") })
    $body += ('<!-- /dsh-notes:' + $item.Language + ' -->')
    $body += ''
}

$destinationDir = Split-Path -Parent $Out
if ($destinationDir -and -not (Test-Path -LiteralPath $destinationDir)) {
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
}

$text = ('# DSH Panel ' + $Version + $newLine + $newLine) +
        (($body -join $newLine).Trim()) + $newLine
[IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))

$total = ($sections['ru'].Split("`n")).Count
Write-Host ('Заметки к выпуску {0}: {1} (строк в русском разделе: {2}, всего строк: {3})' -f
    $Version, $Out, $total, $body.Count)
