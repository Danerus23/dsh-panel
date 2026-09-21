# winget-manifest.ps1 — подставить версию и сумму установщика в манифесты winget.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\winget-manifest.ps1 -ReleaseTag v1.20.3
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\winget-manifest.ps1 -Validate
#
# Зачем: в трёх файлах winget\ (version, installer, defaultLocale) одно и то же значение
# встречается по несколько раз, и руками его правят с ошибками. Версия берётся из
# <Version> в DshTray.csproj — там же, где и у всей остальной сборки.
#
# ВАЖНО про сумму. Установщик выпуска собирает CI по тегу, и байты его установщика
# ОТЛИЧАЮТСЯ от локальной сборки в dist\: у 1.20.3 локальный установщик дал
# D3FDED02…, а в выпуске лежит 44B6B4FE… (тот же SHA256SUMS.txt, что и в выпуске).
# Поэтому для настоящего манифеста сумму надо брать из выпуска:
#   -ReleaseTag v1.20.3   — скачать SHA256SUMS.txt выпуска и взять строку установщика
#   -Sha256 <хеш>         — если сумма выпуска уже известна и её приносят с собой
# Оба ключа вместе НЕ задаются: при -ReleaseTag сумма обязана быть прочитана из выпуска, и
# подменить её ключом -Sha256 значило бы уехать в манифест с суммой другого файла мимо сверки.
# Происхождение суммы из -Sha256 скрипт не проверяет (выпуск не читается) — это ключ для того,
# кто сумму выпуска уже видел; поэтому о нём и говорится в строке «Сумма: …».
# Локальная сумма (по dist\dsh-panel-setup.exe) в манифест НЕ пишется: winget откажется
# ставить пакет по такой сумме. Если она всё же нужна (отладка скрипта, не выпуск),
# её надо запросить явным ключом -AllowLocalSum.
#
# Тег выпуска и версия проекта обязаны совпадать: без этой сверки -ReleaseTag v1.20.3
# при <Version>1.21.0 дал бы ссылку на v1.21.0 с суммой от v1.20.3 — манифест, который
# не поставится ни у кого и никогда.
#
# -Validate дополнительно прогоняет winget validate по папке манифестов.

param(
    [string]$Version = '',
    [string]$Sha256 = '',
    [string]$ReleaseTag = '',
    [string]$SetupPath = '',
    [string]$ManifestDir = '',
    [switch]$AllowLocalSum,
    [switch]$Validate,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $ManifestDir) { $ManifestDir = Join-Path $root 'winget' }
if (-not $SetupPath) { $SetupPath = Join-Path $root 'dist\dsh-panel-setup.exe' }

$repository = 'Danerus23/dsh-panel'
$assetName = 'dsh-panel-setup.exe'
$utf8NoBom = New-Object Text.UTF8Encoding($false)

# ── Ожидаемое содержимое манифестов (ключ — имя файла) ───────────────────────
# Живёт рядом с правкой, чтобы «что записали» и «что потом проверяем» не разъехались.

$expected = @{}

# ── Версия ───────────────────────────────────────────────────────────────────
if (-not $Version) {
    $project = Join-Path $root 'DshTray.csproj'
    $match = Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>'
    if (-not $match) { throw ('В ' + $project + ' нет <Version>') }
    $Version = $match.Matches[0].Groups[1].Value
}
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+') { throw ('версия не похожа на номер выпуска: ' + $Version) }

$expectedVersion = 'v' + $Version

# ── Сверка тега выпуска с версией проекта (Н4) ───────────────────────────────
# Тег задаёт, ОТКУДА берётся сумма, а версия — КУДА указывает ссылка. Если они
# разошлись, манифест получит сумму одного выпуска и адрес другого.

if ($ReleaseTag) {
    $tag = $ReleaseTag.Trim()
    if ($tag.TrimStart('v', 'V') -ne $Version) {
        throw ('-ReleaseTag ' + $tag + ' не совпадает с версией проекта ' + $Version +
               ': сумма была бы взята из одного выпуска, а ссылка вела бы на другой. ' +
               'Поднимите <Version> в DshTray.csproj или укажите тег этого выпуска.')
    }
}
else {
    $tag = $expectedVersion
}

# ── Сумма ────────────────────────────────────────────────────────────────────
# Источник суммы называется явно и ровно один. -Sha256 и -ReleaseTag вместе не задаются: при
# -ReleaseTag сумма обязана быть прочитана из SHA256SUMS.txt выпуска, а ключ -Sha256 подменил бы
# её (опечатка, старая сумма) мимо этой сверки — и манифест уехал бы с суммой чужого файла при
# зелёных проверках: Test-Manifest и -Validate смотрят формат и тег, но не происхождение суммы.
$source = ''
$hash = ''
$localHash = ''

if ($Sha256 -and $ReleaseTag) {
    throw ('-Sha256 и -ReleaseTag заданы вместе: сумма обязана быть прочитана из SHA256SUMS.txt выпуска ' +
           $ReleaseTag.Trim() + ', а -Sha256 её подменяет. Оставьте что-то одно: -ReleaseTag ' +
           $ReleaseTag.Trim() + ' (сумма из выпуска) или -Sha256 <сумма> (готовая сумма без выпуска).')
}

if ($Sha256) {
    $hash = $Sha256.Trim().ToUpperInvariant()
    $source = 'задана ключом -Sha256 (выпуск не читался, происхождение не проверено)'
}
elseif ($ReleaseTag) {
    $url = 'https://github.com/' + $repository + '/releases/download/' + $tag + '/SHA256SUMS.txt'
    Write-Host ('Читаю суммы выпуска: ' + $url)
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 120
    }
    catch {
        throw ('не удалось прочитать SHA256SUMS.txt выпуска ' + $tag + ': ' + $_.Exception.Message +
               '. Проверьте, что выпуск ' + $tag + ' опубликован и содержит ' + $assetName + '.')
    }
    # PowerShell 7 отдаёт двоичный ответ массивом байт: декодируем сами, иначе разбор
    # увидел бы числа, а не строки с суммами.
    $content = $response.Content
    if ($content -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($content) }
    else { $body = [string]$content }
    foreach ($line in ($body -split "`n")) {
        $parts = ($line.Trim() -split '\s+')
        if ($parts.Count -ge 2 -and $parts[1] -eq $assetName) { $hash = $parts[0].ToUpperInvariant() }
    }
    if (-not $hash) { throw ('в SHA256SUMS.txt выпуска ' + $tag + ' нет строки для ' + $assetName) }
    $source = 'взята из выпуска ' + $tag
}
else {
    # Локальная сумма — это сумма ЛОКАЛЬНОЙ сборки. Выпуск собирает CI по тегу, байты там
    # другие, поэтому такой манифест не поставится: winget сверит SHA-256 и откажется.
    # Пишем её только по явному требованию и никогда — в настоящий манифест.
    if (-not $AllowLocalSum) {
        throw ('сумма не задана: укажите -ReleaseTag v' + $Version + ' (сумма из выпуска) или -Sha256 <сумма>. ' +
               'Локальная сумма по ' + $SetupPath + ' в манифест не годится — выпуск собирает CI, и байты ' +
               'его установщика другие. Если она нужна осознанно (отладка, не выпуск), добавьте -AllowLocalSum.')
    }
    if (-not (Test-Path -LiteralPath $SetupPath)) {
        throw ('нет файла ' + $SetupPath + ' — укажите -SetupPath, -Sha256 или -ReleaseTag')
    }
    $hash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $source = 'посчитана по локальному файлу (-AllowLocalSum)'
}

if ($hash -notmatch '^[0-9A-F]{64}$') { throw ('сумма не похожа на SHA-256: ' + $hash) }

# Локальная сумма — только для сравнения: в манифест она попадать не должна.
if ($ReleaseTag -and (Test-Path -LiteralPath $SetupPath)) {
    $localHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToUpperInvariant()
}

$url = 'https://github.com/' + $repository + '/releases/download/' + $expectedVersion + '/' + $assetName
$notesUrl = 'https://github.com/' + $repository + '/releases/tag/' + $expectedVersion

Write-Host ''
Write-Host ('Версия:   ' + $Version)
Write-Host ('Сумма:    ' + $hash + '  (' + $source + ')')
Write-Host ('Ссылка:   ' + $url)

if ($localHash) {
    if ($localHash -eq $hash) {
        Write-Host 'Локальная сборка совпала с выпуском (так бывает, когда установщик собрали тем же CI).'
    }
    else {
        Write-Host ('Локальная сборка установщика даёт другую сумму: ' + $localHash)
        Write-Host 'Это нормально: выпуск собирает CI по тегу, байты отличаются. В манифест идёт сумма выпуска.'
    }
}

if ($AllowLocalSum -and -not $ReleaseTag -and -not $Sha256) {
    Write-Host ''
    Write-Host '!! Сумма посчитана по ЛОКАЛЬНОЙ сборке (-AllowLocalSum). Выпуск собирает CI, и его'
    Write-Host '!! установщик — другие байты, поэтому winget откажется ставить пакет по такой сумме.'
    Write-Host '!! Перед подачей манифеста перезапустите с -ReleaseTag v<версия> или -Sha256 <сумма>.'
}

# ── Правка трёх манифестов ───────────────────────────────────────────────────
$edits = @(
    @{ File = 'Danerus23.DSHPanel.yaml'; Pattern = '(?m)^PackageVersion: .*$'; Value = 'PackageVersion: ' + $Version },
    @{ File = 'Danerus23.DSHPanel.installer.yaml'; Pattern = '(?m)^PackageVersion: .*$'; Value = 'PackageVersion: ' + $Version },
    @{ File = 'Danerus23.DSHPanel.installer.yaml'; Pattern = '(?m)^\s*InstallerUrl: .*$'; Value = '  InstallerUrl: ' + $url },
    @{ File = 'Danerus23.DSHPanel.installer.yaml'; Pattern = '(?m)^\s*InstallerSha256: .*$'; Value = '  InstallerSha256: ' + $hash },
    @{ File = 'Danerus23.DSHPanel.locale.en-US.yaml'; Pattern = '(?m)^PackageVersion: .*$'; Value = 'PackageVersion: ' + $Version },
    @{ File = 'Danerus23.DSHPanel.locale.en-US.yaml'; Pattern = '(?m)^ReleaseNotesUrl: .*$'; Value = 'ReleaseNotesUrl: ' + $notesUrl }
)

$changed = 0
foreach ($group in ($edits | Group-Object { $_.File })) {
    $path = Join-Path $ManifestDir $group.Name
    if (-not (Test-Path -LiteralPath $path)) { throw ('нет файла манифеста: ' + $path) }

    $text = [IO.File]::ReadAllText($path)
    $newline = "`n"
    if ($text -match "`r`n") { $newline = "`r`n" }
    $hits = 0

    foreach ($item in $group.Group) {
        $regex = New-Object Text.RegularExpressions.Regex($item.Pattern)
        $found = $regex.Matches($text).Count
        if ($found -eq 0) { throw ('в ' + $group.Name + ' не нашёл строку ' + $item.Pattern) }
        $hits += $found
        # Значение подставляем оценщиком совпадений, а не строкой замены: в строке замены
        # .NET сам разбирает $ и \ , и путь с обратными слэшами или $ в нём поехал бы.
        $value = $item.Value
        $text = $regex.Replace($text, { param($match) $value })
    }

    if ($newline -eq "`r`n") {
        $text = $text -replace "`r`n", "`n"
        $text = $text -replace "`n", "`r`n"
    }

    if ($WhatIf) {
        Write-Host ('  [что было бы] ' + $group.Name + ' — строк заменено: ' + $hits)
    }
    else {
        [IO.File]::WriteAllText($path, $text, $utf8NoBom)
        Write-Host ('  ' + $group.Name + ' — строк заменено: ' + $hits)
        $changed++
    }
}

if (-not $WhatIf) { Write-Host ('Обновлено файлов: ' + $changed + ' в ' + $ManifestDir) }

# ── Проверка внутренней согласованности манифеста (Н4) ───────────────────────

# Достаёт единственное значение поля и жалуется, если строки нет или их несколько.
function Get-ManifestField {
    param(
        [string]$What,
        [string]$Pattern,
        [string]$Text,
        [System.Collections.Generic.List[string]]$Problems
    )

    $found = [regex]::Matches($Text, $Pattern)
    if ($found.Count -eq 0) { $Problems.Add('нет строки ' + $What); return '' }
    if ($found.Count -gt 1) { $Problems.Add('строка ' + $What + ' встречается ' + $found.Count + ' раз') }
    return $found[0].Groups[1].Value.Trim()
}

function Test-Manifest {
    param([string]$Label, [string]$Text, [string]$ExpectedVersion)

    $problems = New-Object System.Collections.Generic.List[string]

    $version = Get-ManifestField 'PackageVersion' '(?m)^PackageVersion:\s*(\S+)\s*$' $Text $problems
    Get-ManifestField 'ManifestType' '(?m)^ManifestType:\s*(\S+)\s*$' $Text $problems | Out-Null

    if ($version -and $version -notmatch '^\d+\.\d+\.\d+') {
        $problems.Add('PackageVersion не похожа на номер выпуска: ' + $version)
    }

    if ($Label -eq 'Danerus23.DSHPanel.installer.yaml') {
        $url = Get-ManifestField 'InstallerUrl' '(?m)^\s*InstallerUrl:\s*(\S+)\s*$' $Text $problems
        $sum = Get-ManifestField 'InstallerSha256' '(?m)^\s*InstallerSha256:\s*(\S+)\s*$' $Text $problems

        if ($sum -and $sum -notmatch '^[0-9A-Fa-f]{64}$') {
            $problems.Add('InstallerSha256 не 64 шестнадцатеричных знака: ' + $sum)
        }
        if ($url -and $version -and
            $url -notmatch ('/download/v' + [regex]::Escape($version) + '/')) {
            $problems.Add('InstallerUrl указывает не на тег v' + $version + ': ' + $url)
        }
    }

    if ($Label -eq 'Danerus23.DSHPanel.locale.en-US.yaml') {
        $notes = Get-ManifestField 'ReleaseNotesUrl' '(?m)^ReleaseNotesUrl:\s*(\S+)\s*$' $Text $problems
        if ($notes -and $version -and
            $notes -notmatch ('/releases/tag/v' + [regex]::Escape($version) + '$')) {
            $problems.Add('ReleaseNotesUrl указывает не на тег v' + $version + ': ' + $notes)
        }
    }

    if ($ExpectedVersion -and $version -and $version -ne $ExpectedVersion) {
        $problems.Add('PackageVersion ' + $version + ' не совпадает с версией правки ' + $ExpectedVersion +
                      ' — видимо, файл остался от прежнего прогона')
    }

    return ,$problems
}

$manifestNames = @($edits | Group-Object { $_.File } | ForEach-Object { $_.Name })
$checkFailed = $false

foreach ($name in $manifestNames) {
    $path = Join-Path $ManifestDir $name
    if (-not (Test-Path -LiteralPath $path)) { throw ('нет файла манифеста: ' + $path) }

    # Проверяем то, что лежит на диске. При -WhatIf файл никто не менял, и проверка
    # говорит о состоянии манифеста до правки — это и нужно: противоречие видно сразу.
    $problems = Test-Manifest -Label $name -Text ([IO.File]::ReadAllText($path)) -ExpectedVersion $Version
    if ($problems.Count -gt 0) {
        $checkFailed = $true
        Write-Host ('  БЕДА ' + $name + ': ' + ($problems -join '; '))
    }
    else {
        Write-Host ('  ок   ' + $name)
    }
}

if ($checkFailed) {
    Write-Host 'манифест несогласован — см. БЕДА выше'
    exit 1
}

if ($WhatIf) {
    Write-Host 'Ничего не записано (-WhatIf); проверка выше смотрела файлы на диске.'
    exit 0
}

# ── Проверка самим winget ────────────────────────────────────────────────────
if ($Validate) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Host 'winget не найден — проверку манифеста выполнить нечем'
        exit 1
    }
    Write-Host ''
    & winget validate --manifest $ManifestDir
    $code = $LASTEXITCODE
    Write-Host ('winget validate: код ' + $code)
    exit $code
}

exit 0
