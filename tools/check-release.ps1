# check-release.ps1 — СВЕРКА ВЫПУСКА ОТДЕЛЬНЫМ ПРОГОНОМ: имя, размер и SHA-256 каждого вложения
# выпуска против собранного файла на диске, против SHA256SUMS.txt и против того, что уедет
# в манифест.
#
#   pwsh -NoProfile -File .\tools\check-release.ps1
#   pwsh -NoProfile -File .\tools\check-release.ps1 -Dir .\upd-build\release-2.0.3
#   pwsh -NoProfile -File .\tools\check-release.ps1 -Dir <папка с ассетами> -Tag v2.0.3
#   pwsh -NoProfile -File .\tools\check-release.ps1 -AgainstGitHub            # тег — из <Version>
#   pwsh -NoProfile -File .\tools\check-release.ps1 -Manifest <файл манифеста winget>
#   pwsh -NoProfile -File .\tools\check-release.ps1 -ManifestRoot <папка с манифестами версий>
#
# ЗАЧЕМ ОТДЕЛЬНО. Установщик собирает `installer\build-installer.ps1`, вложения выпуска и файл сумм
# собирает конвейер (`docs\RELEASE-V2.md` §3 п. 10), а сумму для манифеста winget берут ИЗ ВЫПУСКА,
# потому что **байты конвейера и байты машины разные** (там же, п. 11: у 2.0.2 сумма выпуска
# `2edb5c29…` против локальной `330fdfd1…`). До этого прогона «суммы сходятся» держалось на том,
# что человек руками сравнил три числа; здесь то же считает машина.
#
# ЧТО ПРОВЕРЯЕТ (и что из этого роняет прогон):
#
#   ВНУТРИ ОДНОГО ИСТОЧНИКА — обязано совпадать, расхождение = код 1:
#     1. каждое вложение ЕСТЬ в каталоге (имена не выдуманы: они взяты из одного места, где живут, —
#        `src\DshPanel\Update\UpdateAssets.cs`: ArchiveName, SetupName, SumsName);
#     2. файл сумм есть и разбирается ТЕМ ЖЕ правилом, что панель (`Update\UpdateDownload.cs`,
#        класс `UpdateSums`): BOM в начале строки, пустые строки и `#`, деление по пробелам и
#        вкладкам, хэш — ровно 64 шестнадцатеричных знака, имя — со звёздочкой и путём снимается,
#        сравнение имён без учёта регистра. Своё правило означало бы «прогон зелёный, а панель
#        отказывается обновляться»;
#     3. строка для каждого вложения в суммах есть, и её хэш совпадает с посчитанным С ФАЙЛА на
#        диске (нет строки — это не «пропустим», а беда: без суммы панель вложение не распаковывает);
#     4. манифест winget против ассетов ТОГО выпуска, на который он указывает: `InstallerUrl` ведёт
#        на имя установщика и на тег своей `PackageVersion`, `InstallerSha256` — 64 знака и равен
#        `digest` ассета выпуска;
#     5. если `SHA256SUMS.txt` на диске — файл сумм САМОГО выпуска (его digest совпал с ассетом
#        `SHA256SUMS.txt` выпуска), то и «вложения против выпуска» считается внутри одного источника:
#        расхождение размера или суммы = код 1.
#
#   МЕЖДУ ИСТОЧНИКАМИ (эта сборка против выпуска и против суммы манифеста) — НЕ падение: числа
#   печатаются рядом и подписываются словами. Так объявлено в проекте, и проверка, красная всегда,
#   ничего не стоит: 6. размер и сумма здешнего файла против выпуска; 7. сумма манифеста против
#   здешнего файла. Совпадение тоже называется — это полезный факт (значит, это те же байты).
#
# ЧЕГО НЕ ВИДИТ (и печатает это в выводе): внутрь установщика и архива не смотрит — что там за
# файлы и какой версии, показывают распаковка, `--update-selftest` и человек; подписи кода у проекта
# нет вовсе, поэтому и проверять нечего; поведение на чистой машине (права, SmartScreen, антивирус)
# и смысл описания выпуска — не здесь. И главное: «сошлось» здесь значит «числа связны», а не
# «выпуск хорош».
#
# Коды возврата, как у соседей: 0 — сошлось, 1 — расхождение, 2 — неприменимо (нет каталога
# вложений, нет ни одного вложения, не нашлись имена вложений). «Неприменимо» — не «зелено».
#
# ⚠️ Сети касаются ТОЛЬКО `-Tag` и `-AgainstGitHub`. Обычный прогон не ходит в сеть вовсе
# (это проверяется и на машине без сети), а `gh` зовётся ровно затем, чтобы взять имена, размеры
# и `digest` ассетов: их и укладывают в манифест winget.

param(
    [string]$Dir = '',
    [string]$Sums = '',
    [string]$Manifest = '',
    [string]$ManifestRoot = '',
    [string]$Root = '',
    [string]$Tag = '',
    [string]$Repo = 'Danerus23/dsh-panel',
    [switch]$AgainstGitHub
)

$ErrorActionPreference = 'Stop'

# ── Помощники вывода ─────────────────────────────────────────────────────────

function Get-Short {
    param([string]$Hash)
    if (-not $Hash) { return '—' }
    if ($Hash.Length -le 12) { return $Hash }
    return ($Hash.Substring(0, 12) + '…')
}

function Get-Number {
    param($Value)
    if ($null -eq $Value) { return '—' }
    return ([string]$Value)
}

# ── Разбор файла сумм — ТЕМ ЖЕ правилом, что у панели ────────────────────────
#
# Правило скопировано по смыслу из `src\DshPanel\Update\UpdateDownload.cs` (`UpdateSums.Parse`),
# а не придумано: прогон обязан разбирать файл так же, как его разберёт панель, иначе он скажет
# «сходится» там, где панель откажется обновляться.

function Get-SumMap {
    param([string[]]$Lines)

    $map = @{}
    if (-not $Lines) { return $map }

    foreach ($raw in $Lines) {
        # BOM в начале строки (его легко получить руками) не должен ломать первый хэш.
        $line = ([string]$raw).TrimStart([char]0xFEFF).Trim()
        if ($line.Length -eq 0) { continue }
        if ($line.StartsWith('#')) { continue }

        $parts = @($line -split '[ \t]+' | Where-Object { $_ -ne '' })
        if ($parts.Count -lt 2) { continue }

        $hash = $parts[0].Trim().ToLowerInvariant()
        if ($hash.Length -ne 64 -or $hash -notmatch '^[0-9a-f]{64}$') { continue }

        # Имя может быть помечено звёздочкой («*DshPanel.zip») или содержать путь.
        $name = $parts[$parts.Count - 1].TrimStart('*').Replace('\', '/')
        $slash = $name.LastIndexOf('/')
        if ($slash -ge 0) { $name = $name.Substring($slash + 1) }
        if ($name.Length -gt 0) { $map[$name] = $hash }
    }

    return $map
}

# ── Выпуск на GitHub: единственное место, которое ходит в сеть ───────────────

function Get-GitHubAssets {
    param([string]$Tag, [string]$Repo)

    $script:gitHubError = ''

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { $script:gitHubError = 'gh не найден в PATH'; return $null }

    $raw = & gh release view $Tag --repo $Repo --json tagName,assets 2>&1
    if ($LASTEXITCODE -ne 0) {
        $script:gitHubError = ('gh release view ' + $Tag + ' вернул код ' + $LASTEXITCODE + ': ' +
            (($raw | Out-String).Trim()))
        return $null
    }

    try { $json = (($raw | Out-String).Trim() | ConvertFrom-Json) }
    catch {
        $script:gitHubError = ('ответ gh не разобрался как JSON: ' + $_.Exception.Message)
        return $null
    }

    $map = @{}
    foreach ($asset in @($json.assets)) {
        if (-not $asset) { continue }
        $name = [string]$asset.name
        if (-not $name) { continue }
        $digest = ([string]$asset.digest) -replace '^sha256:', ''
        $map[$name] = [pscustomobject]@{
            Size = [long]$asset.size
            Hash = $digest.Trim().ToLowerInvariant()
            Url  = [string]$asset.url
        }
    }

    if ($map.Count -eq 0) { $script:gitHubError = ('в выпуске ' + $Tag + ' не видно ни одного ассета'); return $null }
    return $map
}

function Get-SingleField {
    param([string]$Text, [string]$Pattern, [string]$What)

    $found = [regex]::Matches($Text, $Pattern)
    if ($found.Count -eq 0) { $problems.Add('в манифесте нет строки ' + $What); return '' }
    if ($found.Count -gt 1) { $problems.Add('в манифесте строка ' + $What + ' встречается ' + $found.Count + ' раз'); return '' }
    return $found[0].Groups[1].Value.Trim()
}

function Write-AssetTable {
    param([object[]]$Rows, [string]$SourceTitle)

    Write-Host ''
    Write-Host ('источник колонок «в манифесте»: ' + $SourceTitle)
    Write-Host 'суммы в таблице — первые 12 знаков; целиком они в строках проверок ниже'
    Write-Host ('колонка «размер в суммах» пуста у всех, и это не пропуск: в формате ' + $sumsName +
        ' размера нет вовсе (конвейер пишет «хэш, два пробела, имя»). Размер вложений есть только у выпуска GitHub')

    $head = '{0,-22} {1,14} {2,-15} {3,-14} {4,-15} {5,17} {6,-15} {7}'
    Write-Host ''
    Write-Host ($head -f 'вложение', 'размер на диске', 'сумма на диске', 'размер в суммах',
        'сумма в суммах', 'размер в манифесте', 'сумма в манифесте', 'вердикт')

    foreach ($row in $Rows) {
        $sumInSums = '—'
        if ($row.Kind -eq 'asset') { $sumInSums = (Get-Short $row.SumHash) }

        Write-Host ($head -f $row.Name, (Get-Number $row.Size), (Get-Short $row.Hash), '—',
            $sumInSums, (Get-Number $row.ManifestSize), (Get-Short $row.ManifestHash), $row.Verdict)
    }
}

# ── Корень дерева, имена вложений, версия ────────────────────────────────────

$problems = New-Object Collections.Generic.List[string]
$differences = New-Object Collections.Generic.List[string]
$notes = New-Object Collections.Generic.List[string]
$script:gitHubError = ''

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path -LiteralPath $Root)) { throw ('нет дерева: ' + $Root) }
$root = (Resolve-Path -LiteralPath $Root).Path

Write-Host 'СВЕРКА ВЫПУСКА ОТДЕЛЬНЫМ ПРОГОНОМ (tools\check-release.ps1)'
Write-Host ('дерево: ' + $root)

# Имена вложений НЕ выдумываются: они живут одним местом — UpdateAssets.cs.
$assetsFile = Join-Path $root 'src\DshPanel\Update\UpdateAssets.cs'
if (-not (Test-Path -LiteralPath $assetsFile)) {
    Write-Host ('нет файла имён вложений: ' + $assetsFile)
    Write-Host 'имена здесь не выдумываются — сверять нечего (код 2)'
    exit 2
}
$assetsText = [IO.File]::ReadAllText($assetsFile)
$names = @{}
foreach ($key in @('ArchiveName', 'SetupName', 'SumsName')) {
    $match = [regex]::Match($assetsText, ('const\s+string\s+' + $key + '\s*=\s*"([^"]+)"'))
    if (-not $match.Success) {
        Write-Host ('в ' + $assetsFile + ' не нашлось имени ' + $key + ' — имена не выдумываются, сверять нечего (код 2)')
        exit 2
    }
    $names[$key] = $match.Groups[1].Value
}
$setupName = $names['SetupName']
$zipName = $names['ArchiveName']
$sumsName = $names['SumsName']

# Версия — из единственного места, откуда её берут установщик, конвейер и манифест.
$version = ''
$project = Join-Path $root 'src\DshPanel\DshPanel.csproj'
if (Test-Path -LiteralPath $project) {
    $found = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches
    if ($found -and $found.Count -gt 0) { $version = $found[0].Groups[1].Value.Trim() }
}
if ($version) { Write-Host ('версия проекта: ' + $version + ' (src\DshPanel\DshPanel.csproj)') }
else { Write-Host 'версия проекта: не нашлась — сверка тега манифеста с версией не делается' }

# ── Каталог вложений ─────────────────────────────────────────────────────────

if (-not $Dir) { $Dir = Join-Path $root 'dist' }
elseif (-not [IO.Path]::IsPathRooted($Dir)) { $Dir = Join-Path $root $Dir }
$dir = [IO.Path]::GetFullPath($Dir)

if (-not (Test-Path -LiteralPath $dir)) {
    Write-Host ('нет каталога с вложениями: ' + $dir)
    Write-Host 'собирать и сверять нечего (код 2)'
    exit 2
}
Write-Host ('каталог вложений: ' + $dir)

# ── Вложения: размер и сумма СЧИТАЮТСЯ С ФАЙЛА на диске ──────────────────────

$rows = New-Object Collections.Generic.List[object]
foreach ($name in @($setupName, $zipName)) {
    $path = Join-Path $dir $name
    $exists = Test-Path -LiteralPath $path
    $size = $null
    $hash = ''
    if ($exists) {
        $size = (Get-Item -LiteralPath $path).Length
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $rows.Add([pscustomobject]@{
        Kind = 'asset'; Name = $name; Path = $path; Exists = $exists; Size = $size; Hash = $hash
        SumHash = ''; ManifestSize = $null; ManifestHash = ''; ManifestCompared = $false; Verdict = ''
    })
}

$present = @($rows | Where-Object { $_.Exists })
if ($present.Count -eq 0) {
    Write-Host ('вложений выпуска в каталоге нет: искал ' + $setupName + ' и ' + $zipName)
    Write-Host 'собирать и сверять нечего (код 2)'
    exit 2
}

foreach ($row in $rows) {
    if (-not $row.Exists) {
        $problems.Add('вложение не собрано: ' + $row.Name + ' — нет файла ' + $row.Path)
    }
    else {
        Write-Host ('вложение: ' + $row.Name + ' — ' + $row.Size + ' Б, sha256 ' + $row.Hash)
    }
}

# ── Файл сумм ────────────────────────────────────────────────────────────────

if (-not $Sums) { $Sums = Join-Path $dir $sumsName }
elseif (-not [IO.Path]::IsPathRooted($Sums)) { $Sums = Join-Path $root $Sums }
$sumsPath = [IO.Path]::GetFullPath($Sums)

$sumsExists = Test-Path -LiteralPath $sumsPath
$sumsSize = $null
$sumsHash = ''
$sumMap = @{}

if ($sumsExists) {
    $sumsSize = (Get-Item -LiteralPath $sumsPath).Length
    $sumsHash = (Get-FileHash -LiteralPath $sumsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumMap = Get-SumMap -Lines ([IO.File]::ReadAllLines($sumsPath))

    Write-Host ('файл сумм: ' + $sumsPath + ' — ' + $sumsSize + ' Б, строк разобрано ' + $sumMap.Count)
    if ($sumMap.Count -eq 0) {
        $problems.Add('файл сумм не разобрался ни на одну строку: ' + $sumsPath + ' — панель на таком откажется обновляться')
    }
}
else {
    Write-Host ('файл сумм: нет файла ' + $sumsPath)
    $problems.Add('нет файла сумм: ' + $sumsPath + ' — без него панель вложение не распаковывает (' + $sumsName + ')')
}

$rows.Add([pscustomobject]@{
    Kind = 'sums'; Name = $sumsName; Path = $sumsPath; Exists = $sumsExists; Size = $sumsSize; Hash = $sumsHash
    SumHash = ''; ManifestSize = $null; ManifestHash = ''; ManifestCompared = $false
    Verdict = ''
})

# ── ВНУТРИ ОДНОГО ИСТОЧНИКА: вложения против СВОЕГО файла сумм ────────────────

foreach ($row in $rows) {
    if ($row.Kind -ne 'asset' -or -not $row.Exists) { continue }
    if (-not $sumsExists) { continue }

    if (-not $sumMap.ContainsKey($row.Name)) {
        $problems.Add('суммы нет: в ' + $sumsPath + ' нет строки для ' + $row.Name + ' — панель откажется обновляться')
        continue
    }

    $row.SumHash = $sumMap[$row.Name]
    if ($row.SumHash -ne $row.Hash) {
        $problems.Add('сумма не сошлась (' + $row.Name + '): в суммах ' + $row.SumHash +
            ', с файла на диске ' + $row.Hash)
    }
}

foreach ($key in @($sumMap.Keys)) {
    if (@($setupName, $zipName) -notcontains $key) {
        $notes.Add('в суммах есть имя, которого нет в выпуске: ' + $key + ' (панель такие строки игнорирует)')
    }
}

# ── Выпуск на GitHub (только по -Tag / -AgainstGitHub) ───────────────────────

$releaseAssets = $null
$releaseTag = ''
$wantGitHub = [bool]($AgainstGitHub -or $Tag)

if ($wantGitHub) {
    $releaseTag = $Tag
    if (-not $releaseTag) {
        if ($version) { $releaseTag = 'v' + $version }
        else { $problems.Add('не понял, какой выпуск сверять: в проекте нет <Version>, а ключ -Tag не задан') }
    }

    if ($releaseTag) {
        Write-Host ('выпуск: ' + $releaseTag + ' (' + $Repo + ', gh release view --json assets)')
        $releaseAssets = Get-GitHubAssets -Tag $releaseTag -Repo $Repo
        if ($releaseAssets) {
            Write-Host '  ассеты выпуска:'
            foreach ($key in @($releaseAssets.Keys | Sort-Object)) {
                $a = $releaseAssets[$key]
                Write-Host ('    ' + $key + ' — ' + $a.Size + ' Б, sha256 ' + $a.Hash)
            }
        }
        else {
            $problems.Add('выпуск ' + $releaseTag + ' прочитать не удалось: ' + $script:gitHubError)
        }
    }
}
else {
    Write-Host 'выпуск: не читался (обычный прогон в сеть не ходит; сверка с выпуском — ключи -Tag / -AgainstGitHub)'
}

# ── Манифест winget ──────────────────────────────────────────────────────────

$manifestName = 'Danerus23.DSHPanel.installer.yaml'
$manifestFile = ''

if ($Manifest) {
    if ([IO.Path]::IsPathRooted($Manifest)) { $manifestFile = [IO.Path]::GetFullPath($Manifest) }
    else { $manifestFile = [IO.Path]::GetFullPath((Join-Path $root $Manifest)) }
    if (-not (Test-Path -LiteralPath $manifestFile)) {
        $problems.Add('нет файла манифеста: ' + $manifestFile)
        $manifestFile = ''
    }
}
elseif ($ManifestRoot) {
    $manifestRoot = $ManifestRoot
    if (-not [IO.Path]::IsPathRooted($manifestRoot)) { $manifestRoot = Join-Path $root $manifestRoot }

    if (-not (Test-Path -LiteralPath $manifestRoot)) {
        $notes.Add('нет каталога манифестов: ' + $manifestRoot + ' — сверка с манифестом не делается')
    }
    else {
        $candidates = @()
        if ($version) { $candidates += (Join-Path $manifestRoot ($version + '\' + $manifestName)) }
        $candidates += (Join-Path $manifestRoot $manifestName)
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate) { $manifestFile = (Resolve-Path -LiteralPath $candidate).Path; break }
        }

        if (-not $manifestFile) {
            # Старшую версию манифеста НЕ выбираем сами: под нужную версию манифеста может просто
            # не быть, и «взял похожий» означало бы сверку не с тем выпуском.
            $dirs = @(Get-ChildItem -LiteralPath $manifestRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { $_.Name } | Sort-Object)
            $tail = ''
            if ($dirs.Count -gt 0) { $tail = '; в каталоге есть версии: ' + ($dirs -join ', ') }
            $notes.Add('манифеста под ' + $version + ' в ' + $manifestRoot + ' нет' + $tail +
                ' — назовите файл ключом -Manifest (сам чужую версию не беру)')
        }
    }
}
else {
    # ⚠️ ГДЕ ИСКАТЬ МАНИФЕСТ — ДВА МЕСТА, И ЭТО НЕ ЗАБЫВЧИВОСТЬ (ADR-001, 17.10.2026).
    # В ПРИВАТНОМ дереве источник выпуска лежит в `release\tree\`, и манифесты — вместе с ним:
    # `release\tree\winget\Danerus23.DSHPanel*.yaml`. В ВЫПУСКНОМ (публичном) дереве публикация
    # кладёт данные выпуска в КОРЕНЬ, и там манифест — `winget\…` в корне. До этой правки
    # сторож смотрел ТОЛЬКО в корень, поэтому в приватном дереве молча печатал «манифеста нет» и
    # сверка с манифестом не делалась вовсе (мутация CG-8: старая строка возвращена — сторож
    # перестаёт находить файл, хотя он на месте). Первым идёт путь выпуска, вторым — исторический.
    $wingetDirs = @((Join-Path $root 'release\tree\winget'), (Join-Path $root 'winget'))
    foreach ($wingetDir in $wingetDirs) {
        $candidate = Join-Path $wingetDir $manifestName
        if (Test-Path -LiteralPath $candidate) { $manifestFile = $candidate; break }
    }

    # ⚠️ ПРОПАВШИЙ ВХОД (t15, 17.10.2026): каталог манифестов ЕСТЬ, а файла в нём нет — это
    # пропавший вход, а не «сверять не с чем». Замер до правки: убрали
    # `release\tree\winget\Danerus23.DSHPanel.installer.yaml` — код 0 и замечание «манифест: файла
    # нет». После правки — код 1 с полным путём. Если каталога манифестов нет вовсе (сверка идёт
    # по одному каталогу вложений), остаётся прежнее замечание: там это не пропавший вход.
    if (-not $manifestFile) {
        $existingWingetDirs = @($wingetDirs | Where-Object { Test-Path -LiteralPath $_ })
        if ($existingWingetDirs.Count -gt 0) {
            $problems.Add('нет файла манифеста winget: ' + (Join-Path $existingWingetDirs[0] $manifestName) +
                ' (каталог манифестов есть, а файла в нём нет — это пропавший вход)')
        }
    }
}

$manifestPackageVersion = ''
$manifestUrl = ''
$manifestSum = ''
$manifestCoveredName = ''

if ($manifestFile) {
    Write-Host ('манифест: ' + $manifestFile)
    $text = [IO.File]::ReadAllText($manifestFile)

    $manifestPackageVersion = Get-SingleField -Text $text -Pattern '(?m)^PackageVersion:\s*(\S+)\s*$' -What 'PackageVersion'
    $manifestUrl = Get-SingleField -Text $text -Pattern '(?m)^\s*InstallerUrl:\s*(\S+)\s*$' -What 'InstallerUrl'
    $manifestSum = Get-SingleField -Text $text -Pattern '(?m)^\s*InstallerSha256:\s*(\S+)\s*$' -What 'InstallerSha256'
    if ($manifestSum) { $manifestSum = $manifestSum.ToLowerInvariant() }

    if ($manifestPackageVersion) { Write-Host ('  PackageVersion: ' + $manifestPackageVersion) }
    if ($manifestUrl) { Write-Host ('  InstallerUrl:   ' + $manifestUrl) }
    if ($manifestSum) { Write-Host ('  InstallerSha256: ' + $manifestSum) }

    if ($manifestUrl) {
        $match = [regex]::Match($manifestUrl, '/download/([^/]+)/([^/]+)$')
        if (-not $match.Success) {
            $problems.Add('в InstallerUrl не видно «/download/<тег>/<имя>»: ' + $manifestUrl)
        }
        else {
            $manifestUrlTag = $match.Groups[1].Value
            $manifestCoveredName = $match.Groups[2].Value

            if ($manifestCoveredName -ne $setupName) {
                $problems.Add('манифест указывает на вложение «' + $manifestCoveredName + '», а установщик выпуска называется «' + $setupName + '»')
            }
            if ($manifestPackageVersion -and $manifestUrlTag -ne ('v' + $manifestPackageVersion)) {
                $problems.Add('ссылка манифеста ведёт на ' + $manifestUrlTag + ', а PackageVersion манифеста ' + $manifestPackageVersion)
            }
        }
    }

    if ($manifestSum -and $manifestSum -notmatch '^[0-9a-f]{64}$') {
        $problems.Add('InstallerSha256 не 64 шестнадцатеричных знака: ' + $manifestSum)
    }

    if ($manifestPackageVersion -and $version -and $manifestPackageVersion -ne $version) {
        $notes.Add('манифест описывает ' + $manifestPackageVersion + ', а версия проекта ' + $version +
            ' — манифест отстаёт на выпуск, это норма (docs\RELEASE-V2.md §3 п. 11)')
    }

    # Манифест против ассетов ТОГО выпуска, на который он указывает: это падение.
    if ($manifestPackageVersion -and $manifestCoveredName -and $manifestSum) {
        $manifestTag = 'v' + $manifestPackageVersion
        $manifestRelease = $null

        if ($releaseAssets -and $releaseTag -eq $manifestTag) { $manifestRelease = $releaseAssets }
        elseif ($wantGitHub) {
            $manifestRelease = Get-GitHubAssets -Tag $manifestTag -Repo $Repo
            if (-not $manifestRelease) {
                $notes.Add('выпуск ' + $manifestTag + ' (на него указывает манифест) прочитать не удалось: ' +
                    $script:gitHubError + ' — сумма манифеста не сверена')
            }
            else {
                Write-Host ('  выпуск манифеста: ' + $manifestTag + ' прочитан отдельно (манифест указывает на него)')
            }
        }

        if ($manifestRelease) {
            if (-not $manifestRelease.ContainsKey($manifestCoveredName)) {
                $problems.Add('в выпуске ' + $manifestTag + ' нет вложения «' + $manifestCoveredName + '», на которое указывает манифест')
            }
            else {
                $asset = $manifestRelease[$manifestCoveredName]
                if ($asset.Hash -and $asset.Hash -ne $manifestSum) {
                    $problems.Add('сумма в манифесте не сошлась с выпуском ' + $manifestTag + ': в манифесте ' +
                        $manifestSum + ', в выпуске ' + $asset.Hash)
                }
                elseif ($asset.Hash -and $asset.Hash -eq $manifestSum) {
                    Write-Host ('  сумма манифеста = digest ассета выпуска ' + $manifestTag + ' — сошлось')
                }
            }
        }
    }
}
else {
    Write-Host 'манифест: файла нет и ключи -Manifest / -ManifestRoot не названы'
}

# ── Источник колонок «в манифесте» и род сверки ──────────────────────────────

$manifestSourceTitle = 'нет источника (ни файла манифеста, ни выпуска)'
if ($releaseAssets) {
    $manifestSourceTitle = 'выпуск ' + $releaseTag + ' — манифест winget заполняют именно из него'
}
elseif ($manifestFile) {
    $manifestSourceTitle = 'файл манифеста ' + $manifestFile + ' (размера вложений в формате winget нет)'
}

# Один источник или два? Если файл сумм на диске — это файл сумм САМОГО выпуска (его digest совпал
# с ассетом SHA256SUMS.txt выпуска), то «вложения против выпуска» — сверка внутри одного источника,
# и расхождение обязано ронять прогон. Если файл сумм свой — это другая сборка, и расхождение
# называется числами, но не роняет.
$sameSource = $false
if ($releaseAssets -and $sumsExists -and $releaseAssets.ContainsKey($sumsName)) {
    $releaseSums = $releaseAssets[$sumsName]
    if ($releaseSums.Hash -and $releaseSums.Hash -eq $sumsHash) { $sameSource = $true }
}

if ($releaseAssets) {
    if ($sameSource) {
        Write-Host 'род сверки: SHA256SUMS.txt на диске — файл сумм САМОГО выпуска (digest сошёлся):'
        Write-Host '  вложения против выпуска считаются ВНУТРИ одного источника — расхождение = код 1'
    }
    else {
        Write-Host 'род сверки: SHA256SUMS.txt на диске — НЕ файл сумм выпуска (это другая сборка):'
        Write-Host '  вложения против выпуска считаются МЕЖДУ источниками — расхождение называется числами, но не роняет'
        Write-Host '  (байты конвейера и байты машины разные: docs\RELEASE-V2.md §3 п. 11)'
    }
}

# ── Выпуск против диска: внутри источника — падение, между — расхождение ─────

if ($releaseAssets) {
    foreach ($row in $rows) {
        if (-not $row.Exists) { continue }

        if (-not $releaseAssets.ContainsKey($row.Name)) {
            $problems.Add('в выпуске ' + $releaseTag + ' нет вложения ' + $row.Name)
            continue
        }

        $asset = $releaseAssets[$row.Name]
        $row.ManifestCompared = $true
        $row.ManifestSize = $asset.Size
        $row.ManifestHash = $asset.Hash

        if (-not $asset.Hash) {
            $notes.Add('у ассета ' + $row.Name + ' выпуска нет digest — сумма выпуска не сверена')
        }

        if ($sameSource) {
            if ($null -ne $row.Size -and $asset.Size -ne $row.Size) {
                $problems.Add('размер не сошёлся (' + $row.Name + '): в выпуске ' + $releaseTag + ' ' +
                    $asset.Size + ' Б, на диске ' + $row.Size + ' Б')
            }
            if ($asset.Hash -and $asset.Hash -ne $row.Hash) {
                $problems.Add('сумма не сошлась (' + $row.Name + '): в выпуске ' + $releaseTag + ' ' +
                    $asset.Hash + ', на диске ' + $row.Hash)
            }
        }
        else {
            if ($null -ne $row.Size -and $asset.Size -ne $row.Size) {
                $differences.Add('размер (' + $row.Name + '): выпуск ' + $releaseTag + ' ' + $asset.Size +
                    ' Б, эта сборка ' + $row.Size + ' Б')
            }
            if ($asset.Hash -and $asset.Hash -ne $row.Hash) {
                $differences.Add('сумма (' + $row.Name + '): выпуск ' + $releaseTag + ' ' + $asset.Hash +
                    ', эта сборка ' + $row.Hash)
            }
        }
    }
}
elseif ($manifestFile -and $manifestCoveredName -and $manifestSum) {
    # Выпуск не читался — колонки «в манифесте» берём из файла: там только установщик и только сумма.
    foreach ($row in $rows) {
        if ($row.Name -eq $manifestCoveredName) {
            $row.ManifestCompared = $true
            $row.ManifestHash = $manifestSum
        }
        elseif ($row.Kind -eq 'asset' -and $row.Exists) {
            $notes.Add('вложения ' + $row.Name + ' в манифесте winget нет (там только установщик) — сверять не с чем')
        }
    }
}

# ── Вердикт по каждой строке ─────────────────────────────────────────────────

foreach ($row in $rows) {
    $parts = @()

    if (-not $row.Exists) {
        $parts += 'НЕТ НА ДИСКЕ'
    }
    else {
        if ($row.Kind -eq 'asset') {
            if (-not $sumsExists) { $parts += 'нет файла сумм' }
            elseif (-not $row.SumHash) { $parts += 'суммы нет' }
            elseif ($row.SumHash -ne $row.Hash) { $parts += 'сумма в суммах ≠ файлу' }
        }

        if ($row.ManifestCompared) {
            $sameSum = ($row.ManifestHash -and $row.ManifestHash -eq $row.Hash)
            $sameSize = ($null -eq $row.ManifestSize -or $row.ManifestSize -eq $row.Size)

            if ($sameSum -and $sameSize) { $parts += 'совпало с выпуском' }
            else {
                $bad = @()
                if ($null -ne $row.ManifestSize -and $row.ManifestSize -ne $row.Size) { $bad += 'размер' }
                if ($row.ManifestHash -and $row.ManifestHash -ne $row.Hash) { $bad += 'сумма' }

                if ($bad.Count -gt 0) {
                    if ($sameSource) { $parts += (($bad -join ' и ') + ' ≠ выпуску') }
                    else { $parts += (($bad -join ' и ') + ' ≠ выпуску (ожидаемо: разные сборки)') }
                }
            }
        }
    }

    if ($parts.Count -eq 0) { $parts += 'сошлось' }
    $row.Verdict = ($parts -join '; ')
}

# ── Вывод ────────────────────────────────────────────────────────────────────

Write-AssetTable -Rows $rows -SourceTitle $manifestSourceTitle

# ── Итог ─────────────────────────────────────────────────────────────────────

Write-Host ''

if ($problems.Count -gt 0) {
    Write-Host ('РАСХОЖДЕНИЙ (внутри одного источника — это падение): ' + $problems.Count)
    for ($i = 0; $i -lt $problems.Count; $i++) { Write-Host ('  ' + ($i + 1) + '. ' + $problems[$i]) }
}

if ($differences.Count -gt 0) {
    Write-Host ('РАСХОЖДЕНИЯ МЕЖДУ ИСТОЧНИКАМИ (не падение: байты конвейера и машины разные,' +
        ' docs\RELEASE-V2.md §3 п. 11): ' + $differences.Count)
    for ($i = 0; $i -lt $differences.Count; $i++) { Write-Host ('  ' + ($i + 1) + '. ' + $differences[$i]) }
    Write-Host '  это ожидаемо, если каталог — здешняя сборка, а выпуск собирал конвейер;'
    Write-Host '  и это же значит, что в манифест winget пойдёт сумма ВЫПУСКА, а не эта.'
}

if ($notes.Count -gt 0) {
    Write-Host ('ЗАМЕЧАНИЯ (вердикт не роняют): ' + $notes.Count)
    foreach ($note in $notes) { Write-Host ('  ' + $note) }
}

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host 'ЧЕГО ЭТОТ ПРОГОН НЕ ВИДИТ: содержимого установщика и архива, подписи кода (её у проекта нет),'
    Write-Host 'поведения на чистой машине (права, SmartScreen, антивирус) и смысла описания выпуска.'
    Write-Host ('ИТОГ: расхождений ' + $problems.Count + ' — сверка выпуска НЕ прошла (код 1)')
    exit 1
}

Write-Host 'ЧЕСТНО О ГРАНИЦЕ: «сошлось» здесь значит «числа связны», а не «выпуск хорош». Не видно'
Write-Host 'содержимого установщика и архива, подписи кода (её у проекта нет), поведения на чистой'
Write-Host 'машине (права, SmartScreen, антивирус) и смысла описания выпуска — это смотрит человек.'
Write-Host ('ИТОГ: сверка выпуска прошла, расхождений внутри источников нет (код 0)')
exit 0
