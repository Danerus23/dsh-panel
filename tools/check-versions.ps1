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
#   7. манифест winget согласован САМ С СОБОЙ (см. ниже);
#   8. если задана папка копий — в каждой копии опись читается и её версии записаны.
#
# Про пункт 7 отдельно. Версия манифеста НЕ обязана совпадать с <Version> проекта
# в момент сборки: манифест обновляют ПОСЛЕ публикации выпуска, когда CI уже собрал свой
# установщик и его SHA-256 отличается от локального. Поэтому здесь ловится не расхождение
# с проектом, а противоречия ВНУТРИ манифеста:
#   * PackageVersion в трёх файлах одна и та же;
#   * InstallerUrl указывает на тот же тег v<PackageVersion>, а InstallerSha256 — 64 hex;
#   * ReleaseNotesUrl ведёт на тег v<PackageVersion>;
#   * все три файла называют один PackageIdentifier и правильные ManifestType.
# Такую ошибку (версия от одного выпуска, сумма от другого) иначе видно только тогда,
# когда пакет уже не ставится у людей.
#
# Код возврата 0 — всё сходится; 1 — расхождение (печатает, где именно).

param(
    [string]$Project = '',
    [string]$Dist = '',
    [string]$Backups = '',
    [string]$Winget = '',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Project) { $Project = Join-Path $root 'DshTray.csproj' }
if (-not $Dist) { $Dist = Join-Path $root 'dist' }
if (-not $Winget) { $Winget = Join-Path $root 'winget' }

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

# Читает одно поле манифеста и сообщает, если строки нет или их несколько.
function Get-ManifestField([string]$name, [string]$path, [string]$pattern, [string]$what) {
    $matches = [regex]::Matches((Get-Content -LiteralPath $path -Raw -Encoding UTF8), $pattern)
    if ($matches.Count -eq 0) {
        Say ('  БЕДА ' + $name + ': нет строки ' + $what)
        $problems.Add($name + ': нет ' + $what)
        return ''
    }
    if ($matches.Count -gt 1) {
        Say ('  БЕДА ' + $name + ': строка ' + $what + ' встречается ' + $matches.Count + ' раз')
        $problems.Add($name + ': ' + $what + ' повторяется')
    }
    return $matches[0].Groups[1].Value.Trim()
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
    # ProductVersion несёт «+хеш коммита» после git init — сравниваем числовую часть.
    Assert-Same 'панель (ProductVersion)' ($info.ProductVersion -split '\+')[0].Trim() $version
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

# --- 7. согласованность манифеста winget --------------------------------------
#
# Манифест — данные для winget, и он правится отдельным шагом ПОСЛЕ выпуска. Поэтому
# сверяем его сам с собой, а не с версией проекта. Папки winget может не быть вовсе
# (её нет в старых копиях) — тогда шаг пропускается, а не валит проверку.

if (-not (Test-Path -LiteralPath $Winget)) {
    Say ('  --   папки ' + $Winget + ' нет — шаг манифеста пропущен')
}
else {
    $manifestNames = @(
        'Danerus23.DSHPanel.yaml',
        'Danerus23.DSHPanel.installer.yaml',
        'Danerus23.DSHPanel.locale.en-US.yaml'
    )
    $manifests = @{}

    foreach ($name in $manifestNames) {
        $path = Join-Path $Winget $name
        if (-not (Test-Path -LiteralPath $path)) {
            Say ('  БЕДА манифест winget: нет файла ' + $name)
            $problems.Add('манифест winget: нет ' + $name)
            continue
        }

        $manifests[$name] = @{
            Id = Get-ManifestField $name $path '(?m)^PackageIdentifier:\s*(\S+)\s*$' 'PackageIdentifier'
            Version = Get-ManifestField $name $path '(?m)^PackageVersion:\s*(\S+)\s*$' 'PackageVersion'
            Type = Get-ManifestField $name $path '(?m)^ManifestType:\s*(\S+)\s*$' 'ManifestType'
        }
    }

    $versionManifest = 'Danerus23.DSHPanel.yaml'
    $installerManifest = 'Danerus23.DSHPanel.installer.yaml'
    $localeManifest = 'Danerus23.DSHPanel.locale.en-US.yaml'

    if ($manifests.ContainsKey($versionManifest)) {
        $manifestVersion = $manifests[$versionManifest].Version
        $manifestId = $manifests[$versionManifest].Id
        Say ('  ок   манифест winget: ' + $manifestId + ' ' + $manifestVersion)

        # Внутри одного манифеста версия обязана встречаться ровно один раз и быть номером выпуска.
        if ($manifestVersion -notmatch '^\d+\.\d+\.\d+') {
            Say ('  БЕДА манифест winget: PackageVersion не похожа на номер выпуска: ' + $manifestVersion)
            $problems.Add('манифест winget: PackageVersion')
        }

        foreach ($name in @($installerManifest, $localeManifest)) {
            if (-not $manifests.ContainsKey($name)) { continue }
            Assert-Same ('манифест ' + $name + ' (PackageVersion)') $manifests[$name].Version $manifestVersion
            Assert-Same ('манифест ' + $name + ' (PackageIdentifier)') $manifests[$name].Id $manifestId
        }

        Assert-Same 'манифест version (ManifestType)' $manifests[$versionManifest].Type 'version'
        if ($manifests.ContainsKey($installerManifest)) {
            Assert-Same 'манифест installer (ManifestType)' $manifests[$installerManifest].Type 'installer'
        }
        if ($manifests.ContainsKey($localeManifest)) {
            Assert-Same 'манифест defaultLocale (ManifestType)' $manifests[$localeManifest].Type 'defaultLocale'
        }

        if ($manifests.ContainsKey($installerManifest)) {
            $installerPath = Join-Path $Winget $installerManifest
            $url = Get-ManifestField $installerManifest $installerPath '(?m)^\s*InstallerUrl:\s*(\S+)\s*$' 'InstallerUrl'
            $sum = Get-ManifestField $installerManifest $installerPath '(?m)^\s*InstallerSha256:\s*(\S+)\s*$' 'InstallerSha256'

            if ($sum -and $sum -notmatch '^[0-9A-Fa-f]{64}$') {
                Say ('  БЕДА манифест winget: InstallerSha256 не 64 hex: ' + $sum)
                $problems.Add('манифест winget: InstallerSha256')
            }
            elseif ($sum) {
                Say ('  ок   манифест winget: InstallerSha256 — 64 hex')
            }

            if ($url -and -not ($url -match ('/download/v' + [regex]::Escape($manifestVersion) + '/'))) {
                Say ('  БЕДА манифест winget: InstallerUrl указывает не на тег v' + $manifestVersion + ': ' + $url)
                $problems.Add('манифест winget: InstallerUrl')
            }
            elseif ($url) {
                Say ('  ок   манифест winget: InstallerUrl — тег v' + $manifestVersion)
            }

            if ($url -and -not ($url -match '/dsh-panel-setup\.exe$')) {
                Say ('  БЕДА манифест winget: InstallerUrl указывает не на dsh-panel-setup.exe: ' + $url)
                $problems.Add('манифест winget: имя файла в InstallerUrl')
            }
        }

        if ($manifests.ContainsKey($localeManifest)) {
            $localePath = Join-Path $Winget $localeManifest
            $notes = Get-ManifestField $localeManifest $localePath '(?m)^ReleaseNotesUrl:\s*(\S+)\s*$' 'ReleaseNotesUrl'
            if ($notes -and -not ($notes -match ('/releases/tag/v' + [regex]::Escape($manifestVersion) + '$'))) {
                Say ('  БЕДА манифест winget: ReleaseNotesUrl указывает не на тег v' + $manifestVersion + ': ' + $notes)
                $problems.Add('манифест winget: ReleaseNotesUrl')
            }
            elseif ($notes) {
                Say ('  ок   манифест winget: ReleaseNotesUrl — тег v' + $manifestVersion)
            }
        }
    }
}

# --- 8. версии в резервных копиях (для комплекта на чистую машину) -------------

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
