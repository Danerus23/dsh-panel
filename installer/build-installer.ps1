# build-installer.ps1 — сборка установщика панели DSH Panel 2.0 (Inno Setup 7).
#
#   pwsh -NoProfile -File .\installer\build-installer.ps1
#   pwsh -NoProfile -File .\installer\build-installer.ps1 -SkipBuild
#   pwsh -NoProfile -File .\installer\build-installer.ps1 -AppDir <папка публикации>
#
# Что делает:
#   1) публикует панель одним exe (если не указано -SkipBuild);
#   2) берёт версию из ЕДИНСТВЕННОГО места — src\DshPanel\DshPanel.csproj — и пишет
#      installer\appversion.iss (его правит этот скрипт, руками не правят);
#   3) ищет компилятор Inno Setup (ISCC.exe) и собирает dist\dsh-panel-setup.exe.
#
# ЧЕГО ЭТОТ УСТАНОВЩИК НЕ ДЕЛАЕТ, и это не забывчивость, а решение (docs\ROADMAP.md):
#   * не несёт среду .NET — 2.0 самодостаточна (.NET 10 внутри неё), поэтому нет ни
#     скачивания среды, ни проверки подписи Microsoft, ни ожидания её установки;
#   * не ставит Node и pnpm — их ставит сама панель из мастера;
#   * не возвращает копию данных при установке — копию возвращает сама панель
#     (решение владельца 28.09.2026).
#
# ⚠️ ПАНЕЛЬ НЕЛЬЗЯ ПЕРЕСОБРАТЬ, ПОКА ОНА ЗАПУЩЕНА: Windows держит её exe. Для этого и есть
# -AppDir — публикация в другую папку внутри репозитория (путь вывода ВНЕ дерева ломает
# проверки, которые ищут корень по DshPanel.slnx).
#
# ⚠️ РАЗДАЧУ ИЗ ЭТОГО СКРИПТА НЕ РАЗДАВАТЬ: он кладёт установщик в тот же dist, где лежит
# панель, и НЕ публикует выпуск. Выпуск собирается отдельным шагом (см. docs\ROADMAP.md).

param(
    [switch]$SkipBuild,
    [string]$AppDir = '',
    [string]$Iscc = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$root = Split-Path -Parent $installerDir
$project = Join-Path $root 'src\DshPanel\DshPanel.csproj'
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

# ⚠️ ОБА пути приводятся к АБСОЛЮТНЫМ, и это не придирка: Inno Setup разрешает относительные пути
# `Source:` от каталога СВОЕГО файла (`installer\`), а не от корня репозитория. С относительным
# `-AppDir upd-build\rel-dist` он искал `installer\upd-build\rel-dist\*` и падал
# («No files found matching …») — проверено 29.09.2026.
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)

# ⚠️ -AppDir трогаем ТОЛЬКО когда он задан: ПУСТОЙ означает «взять из -OutDir», и это решается ниже.
# Здесь 30.09.2026 была ошибка: `Join-Path $root ''` на пустом -AppDir давал КОРЕНЬ репозитория,
# панель искалась в корне, и сборка падала «нет панели: <корень>\DshPanel.exe». Локально это
# не видно (я всегда передавал -AppDir), а в CI, где зовут только `-SkipBuild`, видно сразу.
if ($AppDir) {
    if (-not [IO.Path]::IsPathRooted($AppDir)) { $AppDir = Join-Path $root $AppDir }
    $AppDir = [IO.Path]::GetFullPath($AppDir)
}

if (-not (Test-Path -LiteralPath $project)) {
    throw ('не нашёл проект панели: ' + $project)
}

# --- 1. Панель ---------------------------------------------------------------

if (-not $AppDir) { $AppDir = $OutDir }

if (-not $SkipBuild) {
    Write-Host ('публикую панель в ' + $AppDir)

    & dotnet publish $project -c Release -p:PublishProfile=win-x64 -o $AppDir
    if ($LASTEXITCODE -ne 0) {
        throw ('публикация панели завершилась кодом ' + $LASTEXITCODE)
    }
}

$panelExe = Join-Path $AppDir 'DshPanel.exe'
if (-not (Test-Path -LiteralPath $panelExe)) {
    throw ('нет панели: ' + $panelExe + '. Соберите её или укажите -AppDir')
}

# --- 2. Версия ---------------------------------------------------------------

$version = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw ('не нашёл <Version> в ' + $project) }

$versionFile = Join-Path $installerDir 'appversion.iss'
$stamp = @(
    '; Файл создаётся build-installer.ps1 — не править руками.',
    ('#define AppVersion "{0}"' -f $version)
)

# С BOM: .iss читает Inno Setup, и header с кириллицей без BOM он разберёт неверно
# (то же правило, что у .ps1 — см. tools\check-encoding.ps1).
[IO.File]::WriteAllLines($versionFile, $stamp, (New-Object Text.UTF8Encoding($true)))
Write-Host ('версия: ' + $version)

# --- 3. Компилятор -----------------------------------------------------------
#
# ⚠️ ЗДЕСЬ 30.09.2026 ПАДАЛ ВЫПУСК (шаг CI «Собрать установщик», тег v2.0.1), и виноват был
# не установщик: на раннере GitHub уже стоит Inno Setup 6 (в «Program Files (x86)»), а шаг CI
# ставит Inno Setup 7 (в «Program Files»). Прежний список искал шестёрку РАНЬШЕ семёрки, сборка
# брала её — а в шестёрке НЕТ китайского языка (`Languages\ChineseSimplified.isl`). Панель обещает
# три языка, .iss требует этот файл, и Inno падал на строке 104 «Couldn't open include file»:
# снаружи это выглядело как «установщик не собрался» без причины. Локально не воспроизводилось —
# на машине владельца семёрка лежит там, где её ищут первым.
#
# Отсюда два правила, и оба проверяемы:
#   1) все пути Inno Setup 7 идут ПЕРЕД любым Inno Setup 6;
#   2) компилятор обязан уметь собрать ЭТОТ .iss — его языковые файлы проверяются ДО запуска.

$iss = Join-Path $installerDir 'dsh-panel.iss'

# Языки берутся из самого .iss: добавили в него язык — скрипт потребует и файл для него.
function Get-IssLanguageFile {
    param([string]$IssPath)

    $files = New-Object Collections.Generic.List[string]
    foreach ($line in (Get-Content -LiteralPath $IssPath)) {
        foreach ($match in [regex]::Matches($line, 'MessagesFile:\s*"compiler:([^"]+)"')) {
            $files.Add(($match.Groups[1].Value -replace '/', '\'))
        }
    }
    return $files.ToArray()
}

# Пустой ответ — компилятор годится; непустой — список недостающих у него файлов.
function Get-MissingLanguageFile {
    param([string]$IsccPath, [string[]]$Needed)

    $dir = Split-Path -Parent $IsccPath
    $missing = New-Object Collections.Generic.List[string]
    foreach ($file in $Needed) {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $file))) { $missing.Add($file) }
    }
    return $missing.ToArray()
}

$languageFiles = Get-IssLanguageFile -IssPath $iss
if (-not $languageFiles -or $languageFiles.Count -eq 0) {
    throw ('в ' + $iss + ' не нашлось ни одного MessagesFile — посмотрите раздел [Languages]')
}

if (-not $Iscc) {
    # Порядок — это предпочтение: сперва седьмая версия во всех своих местах, потом шестая.
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    )

    $unfit = New-Object Collections.Generic.List[string]
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }

        $missing = Get-MissingLanguageFile -IsccPath $candidate -Needed $languageFiles
        if ($missing.Count -gt 0) {
            $unfit.Add('   ' + $candidate + ' — нет: ' + ($missing -join ', '))
            continue
        }

        $Iscc = $candidate
        break
    }

    if (-not $Iscc -and $unfit.Count -gt 0) {
        throw ('ни один компилятор Inno Setup на этой машине не соберёт установщик с этими языками:' +
            [Environment]::NewLine + ($unfit -join [Environment]::NewLine) + [Environment]::NewLine +
            'Нужен Inno Setup 7: китайского языка (' + ($languageFiles -join ', ') + ') в шестёрке нет.')
    }
}

if (-not $Iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Iscc = $command.Source }
}

if (-not $Iscc) {
    throw ('не нашёл компилятор Inno Setup (ISCC.exe). Поставьте его: ' +
        'winget install --id JRSoftware.InnoSetup --exact --silent — или укажите -Iscc <путь>')
}

# Последняя дверь: и -Iscc, и ISCC.exe из PATH проходят ту же проверку, что и поиск.
$missingInChosen = Get-MissingLanguageFile -IsccPath $Iscc -Needed $languageFiles
if ($missingInChosen.Count -gt 0) {
    throw ('компилятор ' + $Iscc + ' не соберёт этот установщик: нет файлов ' +
        ($missingInChosen -join ', ') + ' (нужен Inno Setup 7 — в шестёрке китайского языка нет)')
}

$isccVersion = (Get-Item -LiteralPath $Iscc).VersionInfo.FileVersion
Write-Host ('компилятор: ' + $Iscc + ' (' + $isccVersion + ')')

# --- 4. Сборка установщика ---------------------------------------------------

# $iss найден выше — в разделе 3, потому что оттуда же берутся языки компилятора.
$setup = Join-Path $OutDir 'dsh-panel-setup.exe'
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }

Write-Host ('собираю установщик: ' + $iss)
& $Iscc ('/DAppSource=' + $AppDir) ('/DOutputDir=' + $OutDir) $iss
if ($LASTEXITCODE -ne 0) {
    throw ('Inno Setup вернул код ' + $LASTEXITCODE + ' — установщик не собран')
}

if (-not (Test-Path -LiteralPath $setup)) {
    throw ('установщик не появился: ' + $setup)
}

$setupFile = Get-Item -LiteralPath $setup
Write-Host ('установщик: {0} ({1:N1} МБ)' -f $setupFile.FullName, ($setupFile.Length / 1MB))
Write-Host ('сумма SHA-256: ' + (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant())
