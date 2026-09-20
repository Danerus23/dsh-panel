# release-notes.ps1 — вырезать из CHANGELOG.md раздел нужной версии.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-notes.ps1 `
#       -Version 1.20.0 -Out dist\release-notes.md
#
# Зачем отдельным скриптом: тот же текст нужен локальной сборке (make-release.ps1)
# и GitHub Actions при выпуске по тегу — заметки к выпуску показывает окно обновлений
# в панели, поэтому они должны собираться одинаково в обоих местах.
#
# Версия по умолчанию берётся из DshTray.csproj. Если раздела для неё нет — ошибка:
# выпуск без заметок человек увидит как «нет истории изменений».

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

$changelog = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $changelog)) { throw ('Нет истории версий: ' + $changelog) }

$lines = Get-Content -LiteralPath $changelog -Encoding UTF8
$start = -1
for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match ('^##\s+' + [regex]::Escape($Version) + '(\s|$)')) { $start = $index; break }
}
if ($start -lt 0) { throw ('В CHANGELOG.md нет раздела для версии ' + $Version) }

$notes = @()
for ($index = $start + 1; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^##\s') { break }
    $notes += $lines[$index]
}

$destinationDir = Split-Path -Parent $Out
if ($destinationDir -and -not (Test-Path -LiteralPath $destinationDir)) {
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
}

$text = ('# DSH Panel ' + $Version + [Environment]::NewLine + [Environment]::NewLine) +
        (($notes -join [Environment]::NewLine).Trim()) + [Environment]::NewLine
[IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))

Write-Host ('Заметки к выпуску {0}: {1} (строк {2})' -f $Version, $Out, $notes.Count)
