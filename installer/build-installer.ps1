# build-installer.ps1
# Сборка установщика панели DSH Panel (Inno Setup).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
#
# Что делает:
#   1) собирает панель в ..\app (если не указано -SkipBuild);
#   2) берёт версию из DshTray.csproj и пишет appversion.iss;
#   3) готовит среду .NET Desktop Runtime в installer\redist (скачивает и проверяет подпись
#      Microsoft) и вкладывает её в установщик;
#   4) ищет компилятор Inno Setup (ISCC.exe) и собирает ..\dist\dsh-panel-setup.exe.
#
# Варианты:
#   -SkipBuild     не пересобирать панель, взять готовую из ..\app
#   -AppDir <путь> взять панель из другого каталога (например, из временной публикации:
#                  запущенную панель пересобрать нельзя — её .exe занят)
#   -Iscc <путь>   свой путь к ISCC.exe
#   -SkipRuntime   не вкладывать среду .NET (установщик станет ~2 МБ, но на машине без среды
#                  панель не запустится: установщик только скажет, где её взять)

param(
    [switch]$SkipBuild,
    [string]$AppDir = '',
    [string]$Iscc = '',
    [switch]$SkipRuntime
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # сам проект панели
if (-not $AppDir) { $AppDir = Join-Path $root 'app' }
$distDir = Join-Path $root 'dist'
$project = Join-Path $root 'DshTray.csproj'

if (-not $SkipBuild) {
    $build = Join-Path $root 'build.ps1'
    Write-Host ('Собираю панель: ' + $build)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -ne 0) { throw "Сборка панели завершилась с кодом $LASTEXITCODE" }
}

$exe = Join-Path $AppDir 'DshTray.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw ("Нет панели: {0}. Соберите её: .\build.ps1" -f $exe)
}

# 1. Версия — из единственного места: DshTray.csproj.
$version = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw 'Не нашёл <Version> в DshTray.csproj' }

$versionFile = Join-Path $PSScriptRoot 'appversion.iss'
$stamp = @(
    '; Файл создаётся build-installer.ps1 — не править руками.',
    ('#define AppVersion "{0}"' -f $version)
)
# С BOM, как и остальные .iss: в шапке есть кириллица, а Inno и tools\check-encoding.ps1
# ожидают у скриптов именно UTF-8 с BOM (признак кодировки у них — первые три байта).
[IO.File]::WriteAllLines($versionFile, $stamp, (New-Object Text.UTF8Encoding($true)))

# 1а. Среда .NET Desktop Runtime — вкладываем в установщик.
#
# Зачем: раньше среда качалась в момент установки, до появления окна мастера. На экране не
# оставалось ничего (только запрос прав), и человек решал, что установка сломалась; на приёмке
# в чистой Windows так и вышло. Со средой внутри установщик ничего не качает, а ставит её
# в конце, когда окно установки видно — и сам следит за результатом.
#
# Файл ~55 МБ в репозитории не хранится (installer\redist в .gitignore): скачиваем при сборке
# и обязательно проверяем подпись Microsoft — от этого зависит, что мы запускаем.
$runtimeVersion = '8.0.31'
$redistDir = Join-Path $PSScriptRoot 'redist'
$runtimeName = ('windowsdesktop-runtime-{0}-win-x64.exe' -f $runtimeVersion)
$runtimePath = Join-Path $redistDir $runtimeName

function Assert-MicrosoftSignature([string]$path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid') {
        throw ('Подпись среды недействительна (' + $signature.Status + '): ' + $path)
    }

    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
        throw ('Файл среды подписан не Microsoft: ' + $path)
    }
}

if ($SkipRuntime) {
    Write-Warning 'Среда .NET в установщик не вкладывается (-SkipRuntime): на машине без среды панель не запустится.'
}
else {
    if (-not (Test-Path -LiteralPath $runtimePath)) {
        New-Item -ItemType Directory -Path $redistDir -Force | Out-Null

        # Ссылка версионная: файл на серверах Microsoft не меняется, а версия записана в имени.
        $url = ('https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{0}/{1}' -f $runtimeVersion, $runtimeName)
        Write-Host ('Скачиваю среду .NET: ' + $url)
        try {
            Invoke-WebRequest -Uri $url -OutFile $runtimePath -UseBasicParsing
        }
        catch {
            # Запасной адрес — постоянная ссылка Microsoft на свежий выпуск 8.x. Версия файла
            # тогда может отличаться от ожидаемой, поэтому печатаем то, что получилось.
            Write-Warning ('Версионная ссылка не сработала (' + $_.Exception.Message + '), беру aka.ms')
            Invoke-WebRequest -Uri 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' -OutFile $runtimePath -UseBasicParsing
        }
    }

    Assert-MicrosoftSignature $runtimePath
    $runtimeInfo = (Get-Item -LiteralPath $runtimePath).VersionInfo
    Write-Host ('Среда .NET: {0} ({1:N1} МБ, подпись Microsoft{2})' -f $runtimeName,
        ((Get-Item -LiteralPath $runtimePath).Length / 1MB),
        $(if ($runtimeInfo.ProductVersion) { ', ' + $runtimeInfo.ProductVersion } else { '' }))
}
Write-Host ('Версия панели: {0}' -f $version)

# 2. Компилятор Inno Setup. Нужна именно седьмая версия: китайский перевод мастера
#    (Languages\ChineseSimplified.isl) появился только в 7, и .iss его включает, а
#    CreateInputOptionPage с шестью параметрами — тоже подпись Inno 7. Шестёрка падает
#    на этом файле, поэтому проверяем до сборки. Признак ровно тот, чего требует .iss:
#    файл перевода лежит рядом с компилятором. Номер версии у ISCC не спрашиваем —
#    он уходит мимо стандартного вывода, и разбирать баннер значит добавлять хрупкости.
function Test-IsccUsable([string]$path) {
    return (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $path) 'Languages\ChineseSimplified.isl'))
}

if ($Iscc) {
    $candidates = @($Iscc)
}
else {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
}

$found = @()
$Iscc = ''
foreach ($candidate in $candidates) {
    $usable = Test-IsccUsable $candidate
    $found += ('{0} ({1})' -f $candidate,
        $(if ($usable) { 'подходит: китайский перевод на месте' } else { 'не подходит: китайского перевода нет' }))
    if ($usable -and -not $Iscc) { $Iscc = $candidate }
}

if (-not $Iscc) {
    $hint = 'Поставьте седьмую версию: winget install --id JRSoftware.InnoSetup.7 -e -s winget ' +
            '— или скачайте с https://jrsoftware.org/isdl.php и укажите путь ключом -Iscc <путь>.'
    if ($found.Count -gt 0) {
        throw ("Нужен Inno Setup 7 или новее: .iss включает Languages\ChineseSimplified.isl, " +
               "а он входит только в седьмую версию. Найдено: " + ($found -join '; ') + ". " + $hint)
    }

    throw ('Не найден ISCC.exe (Inno Setup 7). ' + $hint)
}

Write-Host ('Компилятор: {0}' -f $Iscc)

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# 3. Сборка.
$iss = Join-Path $PSScriptRoot 'dsh-panel.iss'
Write-Host ('Файлы панели: ' + (Resolve-Path -LiteralPath $AppDir).Path)
Write-Host ('Собираю установщик: ' + $Iscc)

$isccArgs = @(('/DAppSource=' + (Resolve-Path -LiteralPath $AppDir).Path))
if (-not $SkipRuntime) {
    # Путь и имя файла среды передаём ключами: в .iss по ним и [Files], и ExtractTemporaryFile.
    $isccArgs += ('/DRuntimeFile=' + (Resolve-Path -LiteralPath $runtimePath).Path)
    $isccArgs += ('/DRuntimeName=' + $runtimeName)
}

& $Iscc @isccArgs $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с кодом $LASTEXITCODE" }

$setup = Join-Path $distDir 'dsh-panel-setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw ('Установщик не появился: ' + $setup) }

$info = Get-Item -LiteralPath $setup
Write-Host ''
Write-Host ('Готово: {0}' -f $setup)
Write-Host ('Размер: {0:N1} МБ, версия панели внутри: {1}' -f ($info.Length / 1MB), $version)
if ($SkipRuntime) {
    Write-Host 'Среды .NET внутри нет: установщик только подскажет, где её взять.' -ForegroundColor Yellow
}
else {
    Write-Host 'Среда .NET Desktop Runtime внутри: установка не требует интернета.'
}
