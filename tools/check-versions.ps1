# check-versions.ps1 — сверка версий в собранном комплекте.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-versions.ps1
#   powershell ... -File .\tools\check-versions.ps1 -Dist dist -Backups <папка с копиями>
#
# Зачем: номер версии живёт в четырёх местах — DshTray.csproj, штамп build.txt, номер
# установщика (appversion.iss и сам dsh-panel-setup.exe) и версия собранной панели.
# Раньше за этим следил отдельный файл KitStatus (свежесть установщика); при сборке
# комплекта для проверки на чистой машине важно, чтобы в раздатку не попали установщик
# от прошлой сборки и копия от другой версии движка.
#
# Что проверяется:
#   1. DshTray.csproj <Version> = базовый номер;
#   2. панель в dist\panel (FileVersion/ProductVersion) совпадает с csproj;
#   3. штамп dist\panel\build.txt совпадает с csproj;
#   4. installer\appversion.iss совпадает с csproj;
#   5. версия dsh-panel-setup.exe совпадает с csproj;
#   6. версии внутри архивов (build.txt в DshPanel.zip) совпадают с csproj;
#   7. если задана папка копий — в каждой копии опись читается и её версии записаны.
#
# Код возврата 0 — всё сходится; 1 — расхождение (печатает, где именно).

param(
    [string]$Project = '',
    [string]$Dist = '',
    [string]$Backups = '',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Project) { $Project = Join-Path $root 'DshTray.csproj' }
if (-not $Dist) { $Dist = Join-Path $root 'dist' }

$problems = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    $lines.Add($text)
    if (-not $Quiet) { Write-Host $text }
}

function Assert-Same([string]$what, [string]$found, [string]$expected) {
    if ($found -eq $expected) {
        Say ('  ок   ' + $what + ': ' + $found)
        return
    }

    Say ('  БЕДА ' + $what + ': ' + $found + ', ожидалось ' + $expected)
    $problems.Add($what)
}

# --- 1. базовый номер ---------------------------------------------------------

$match = Select-String -LiteralPath $Project -Pattern '<Version>([^<]+)</Version>'
if (-not $match) { throw ('В ' + $Project + ' нет <Version>') }
$version = $match.Matches[0].Groups[1].Value.Trim()
Say ('Версия в проекте: ' + $version)

# --- 2. собранная панель ------------------------------------------------------

$panelExe = Join-Path $Dist 'panel\DshTray.exe'
if (Test-Path -LiteralPath $panelExe) {
    $info = (Get-Item -LiteralPath $panelExe).VersionInfo
    Assert-Same 'панель (ProductVersion)' $info.ProductVersion $version
    Assert-Same 'панель (FileVersion)' ($info.FileVersion -replace '\.0$', '') $version
    $dll = Join-Path $Dist 'panel\DshTray.dll'
    if (Test-Path -LiteralPath $dll) {
        $dllVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -replace '\.0$', ''
        Assert-Same 'панель (DshTray.dll)' $dllVersion $version
    }
}
else {
    Say '  --   панели в dist\panel нет — шаг пропущен'
}

# --- 3. штамп сборки ----------------------------------------------------------

$stamp = Join-Path $Dist 'panel\build.txt'
if (Test-Path -LiteralPath $stamp) {
    $stampVersion = (Select-String -LiteralPath $stamp -Pattern '^version=(.+)$').Matches[0].Groups[1].Value.Trim()
    Assert-Same 'штамп build.txt' $stampVersion $version
    $built = (Select-String -LiteralPath $stamp -Pattern '^built=(.+)$').Matches[0].Groups[1].Value.Trim()
    Say ('       собран: ' + $built)
}
else {
    Say '  --   штампа build.txt нет — шаг пропущен'
}

# --- 4. номер установщика в скрипте -------------------------------------------

$issVersion = Join-Path $root 'installer\appversion.iss'
if (Test-Path -LiteralPath $issVersion) {
    $fromIss = (Select-String -LiteralPath $issVersion -Pattern '#define AppVersion "([^"]+)"').Matches[0].Groups[1].Value.Trim()
    Assert-Same 'installer\appversion.iss' $fromIss $version
}
else {
    Say '  --   installer\appversion.iss нет — шаг пропущен'
}

# --- 5. сам установщик --------------------------------------------------------

$setup = Join-Path $Dist 'dsh-panel-setup.exe'
if (Test-Path -LiteralPath $setup) {
    # Inno Setup дописывает номер пробелами до фиксированной ширины — сравниваем без них.
    $setupVersion = (Get-Item -LiteralPath $setup).VersionInfo.ProductVersion.Trim()
    Assert-Same 'dsh-panel-setup.exe' $setupVersion $version
    Say ('       размер: {0:N1} МБ, собран: {1}' -f ((Get-Item -LiteralPath $setup).Length / 1MB), (Get-Item -LiteralPath $setup).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
}
else {
    Say '  --   установщика в dist нет — шаг пропущен'
}

# --- 6. версии внутри переносимого архива -------------------------------------

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
$portable = Join-Path $Dist 'DshPanel.zip'
if (Test-Path -LiteralPath $portable) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($portable)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'build.txt' } | Select-Object -First 1
        if ($entry) {
            $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $fromZip = ([regex]::Match($text, '(?m)^version=(.+)$')).Groups[1].Value.Trim()
            Assert-Same 'DshPanel.zip (build.txt)' $fromZip $version
            if ($text -match '(?m)^source=') { $problems.Add('DshPanel.zip: штамп уносит путь владельца'); Say '  БЕДА DshPanel.zip: штамп уносит путь владельца' }
        }
        else {
            Say '  --   в DshPanel.zip нет build.txt — шаг пропущен'
        }
    }
    finally { $zip.Dispose() }
}
else {
    Say '  --   DshPanel.zip нет — шаг пропущен'
}

# --- 7. версии в резервных копиях (для комплекта на чистую машину) -------------

if ($Backups -and (Test-Path -LiteralPath $Backups)) {
    $archives = Get-ChildItem -LiteralPath $Backups -Filter *.zip | Sort-Object Name
    if ($archives.Count -eq 0) { Say '  --   копий в папке нет' }

    foreach ($archive in $archives) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq 'manifest.json' } | Select-Object -First 1
            if (-not $entry) {
                $problems.Add($archive.Name + ': нет описи')
                Say ('  БЕДА ' + $archive.Name + ': нет описи manifest.json')
                continue
            }

            $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
            try { $manifest = ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }

            $parts = @()
            if ($manifest.AppVersion) { $parts += ('панель ' + $manifest.AppVersion) }
            if ($manifest.Versions.dsh) { $parts += ('dsh ' + $manifest.Versions.dsh) }
            if ($manifest.Versions.node) { $parts += ('node ' + $manifest.Versions.node) }
            $kind = if ($manifest.WithEngine) { 'полная' } else { 'тонкая' }
            $hasKeys = @($zip.Entries | Where-Object { $_.FullName -like 'keys/*' }).Count -gt 0
            Say ('  ок   ' + $archive.Name + ': ' + $kind + ', ' + ($parts -join ', ') + ', файлов ' + $manifest.TotalFiles + $(if ($hasKeys) { ', С КЛЮЧАМИ' } else { '' }))

            if (-not $manifest.AppVersion) {
                $problems.Add($archive.Name + ': в описи нет версии панели')
                Say ('  БЕДА ' + $archive.Name + ': в описи нет версии панели')
            }
        }
        finally { $zip.Dispose() }
    }
}

# --- итог ---------------------------------------------------------------------

Say ''
if ($problems.Count -gt 0) {
    Say ('РАСХОЖДЕНИЯ: ' + $problems.Count + ' — ' + ($problems -join '; '))
    exit 1
}

Say 'версии сходятся'
exit 0
