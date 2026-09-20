# pack-panel.ps1 — упаковать папку панели в раздаваемый архив.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\pack-panel.ps1 `
#       -Source dist\panel -Destination dist\DshPanel.zip
#
# Зачем отдельным скриптом: тот же путь нужен и локальной сборке (make-release.ps1),
# и CI, иначе артефакт из CI отличается от проверенного локально — в него уезжают
# DshTray.pdb, status.txt и путь сборщика в штампе build.txt.
#
# Что делает:
#   1) копирует папку панели в staging рядом с архивом (саму папку не портит);
#   2) убирает служебное: status.txt, settings.json, web-url.txt, *.pdb, *.log,
#      и вычёркивает из build.txt строку source= с путём того, кто собирал;
#   3) пакует и проверяет готовый архив — служебного и личных путей внутри нет.
#
# Код возврата: 0 — архив собран и чист, 1 — что-то не так (годно для сборки и CI).

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'

function Clear-ServiceFiles([string]$folder) {
    foreach ($name in 'status.txt', 'settings.json', 'web-url.txt') {
        Remove-Item -LiteralPath (Join-Path $folder $name) -Force -ErrorAction SilentlyContinue
    }

    # Фильтр по расширению делаем в PowerShell, а не ключом -Include: в Windows PowerShell 5.1
    # -Include вместе с -LiteralPath фильтр игнорирует и возвращает все файлы подряд — уборка
    # однажды стёрла всю копию папки, и архив собирался пустым.
    Get-ChildItem -LiteralPath $folder -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.pdb', '.log' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $stamp = Join-Path $folder 'build.txt'
    if (Test-Path -LiteralPath $stamp) {
        $kept = Get-Content -LiteralPath $stamp -Encoding UTF8 | Where-Object { $_ -notmatch '^source=' }
        [IO.File]::WriteAllLines($stamp, $kept, (New-Object Text.UTF8Encoding($true)))
    }
}

function Assert-CleanArchive([string]$archive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $bad = @($zip.Entries | Where-Object {
                $_.FullName -match 'status\.txt$' -or $_.FullName -match '\.pdb$' -or
                $_.FullName -match 'settings\.json$' -or $_.FullName -match 'web-url' -or $_.FullName -match '\.log$'
            } | ForEach-Object { $_.FullName })
        if ($bad.Count -gt 0) {
            throw ('в ' + (Split-Path $archive -Leaf) + ' попали служебные файлы: ' + ($bad -join ', '))
        }

        $stamp = $zip.Entries | Where-Object { $_.FullName -eq 'build.txt' } | Select-Object -First 1
        if ($stamp) {
            $reader = New-Object IO.StreamReader($stamp.Open(), [Text.Encoding]::UTF8)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($text -match '^source=') { throw ('в ' + (Split-Path $archive -Leaf) + ' штамп сборки уносит путь владельца') }
        }

        Write-Host ('  {0}: чисто, файлов {1}' -f (Split-Path $archive -Leaf), $zip.Entries.Count)
    }
    finally { $zip.Dispose() }
}

if (-not (Test-Path -LiteralPath $Source)) { throw ('Нет папки панели: ' + $Source) }

$destinationDir = Split-Path -Parent $Destination
if ($destinationDir -and -not (Test-Path -LiteralPath $destinationDir)) {
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
}

# Копия нужна, чтобы убрать служебные файлы и не портить саму папку сборки: её читает
# сборка установщика, а из CI ту же папку забирает следующий шаг.
$staging = Join-Path $destinationDir ('.pack-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Copy-Item -LiteralPath $Source -Destination $staging -Recurse
    Clear-ServiceFiles $staging

    $count = (Get-ChildItem -LiteralPath $staging -Recurse -File).Count
    Write-Host ('  файлов в раздаче: ' + $count)

    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $Destination -Force
    Write-Host ('  собрано: {0} ({1:N1} МБ)' -f (Split-Path $Destination -Leaf), ((Get-Item -LiteralPath $Destination).Length / 1MB))

    Assert-CleanArchive $Destination
}
catch {
    Write-Host ('ОШИБКА упаковки: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0
