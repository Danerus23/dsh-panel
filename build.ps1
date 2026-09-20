# build.ps1
# Сборка приложения DSH Panel (панель управления сервером DSH с треем).
#
# Обычная сборка и публикация рядом с исходниками:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
#
# Варианты:
#   -OutDir <путь>     куда положить готовое приложение (по умолчанию .\app)
#   -SelfContained     положить в комплект сам .NET (папка станет ~160 МБ,
#                      зато приложение заработает и на машине без .NET;
#                      для этого нужен доступ в интернет — качаются runtime-паки)
#   -SingleFile        собрать одним .exe (тоже требует интернет: пакет ILLink)
#   -NoPublish         только собрать (bin\Release), без публикации

param(
    [string]$OutDir = '',
    [switch]$SelfContained,
    [switch]$SingleFile,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'app' }

# Сборку видно в консоли, и туда же пишет dotnet (он отдаёт UTF-8): переводим
# PowerShell на UTF-8, иначе кириллица вперемешку с выводом dotnet рассыпается.
try {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    $OutputEncoding = New-Object Text.UTF8Encoding($false)
}
catch { }

# Сборка пишет служебные файлы в профиль; в песочнице это может быть запрещено,
# поэтому уводим их в рабочую папку — на обычной машине это ничего не меняет.
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:APPDATA = Join-Path $root '.appdata'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
if (-not (Test-Path -LiteralPath $env:APPDATA)) { New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null }

$project = Join-Path $root 'DshTray.csproj'

# Папку публикации чистим: иначе удалённый из проекта файл (или оставшийся рядом с .exe
# settings.json) молча уедет и в архив, и в установщик.
if (-not $NoPublish -and (Test-Path -LiteralPath $OutDir)) {
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}

$command = @('build', $project, '-c', 'Release')
if (-not $NoPublish) {
    $command = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $OutDir)

    if ($SingleFile) {
        $command += '-p:PublishSingleFile=true'
    }

    if ($SelfContained) {
        $command += '--self-contained'
        $command += 'true'
    } else {
        $command += '--self-contained'
        $command += 'false'
    }

    # Самодостаточной сборке нужны runtime-паки, а сборке одним файлом — ILLink, но
    # NuGet.config очищает список источников, а кэш пакетов уведён в пустую папку проекта.
    # Без явного источника обе эти сборки падают с NU1100, по которому причину не видно.
    if ($SelfContained -or $SingleFile) {
        $command += '--source'
        $command += 'https://api.nuget.org/v3/index.json'
    }
}

Write-Host ('dotnet ' + ($command -join ' '))
& dotnet @command
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE" }

if (-not $NoPublish) {
    $exe = Join-Path $OutDir 'DshTray.exe'
    Write-Host ''
    Write-Host ('Готово: {0}' -f $exe)

    $exeInfo = Get-Item -LiteralPath $exe

    # Штамп сборки: по нему панель («Резервные копии», --status) и сборка установщика
    # понимают, какая именно сборка лежит в папке и что вложено в установщик.
    $stampPath = Join-Path $OutDir 'build.txt'
    $stamp = @(
        '# Штамп сборки панели. Читается панелью и сборкой установщика. Формат: ключ=значение.',
        ('built=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
        ('version=' + $exeInfo.VersionInfo.ProductVersion),
        ('source=' + $root)
    )
    [IO.File]::WriteAllLines($stampPath, $stamp, (New-Object Text.UTF8Encoding($true)))
    Write-Host ('Штамп сборки — {0}' -f $stampPath)

    # Сторожок истории версий: напоминаем, если для текущей версии нет записи в CHANGELOG.md.
    $version = $exeInfo.VersionInfo.ProductVersion
    $changelog = Join-Path $root 'CHANGELOG.md'
    if (Test-Path -LiteralPath $changelog) {
        $hasEntry = Select-String -LiteralPath $changelog -Pattern ('^##\s+' + [regex]::Escape($version) + '(\s|$)') -Quiet -ErrorAction SilentlyContinue
        if ($hasEntry) {
            Write-Host ('История версий: запись для {0} есть' -f $version)
        }
        else {
            Write-Warning ('В CHANGELOG.md нет раздела для версии {0} — добавьте «## {0}».' -f $version)
        }
    }

    # Самопроверка идёт в своём профиле: иначе панель работает с настоящими %APPDATA%\DshPanel,
    # а перенос из %APPDATA%\DeepSeekHarness подсунул бы ей настройки того, кто собирает
    # (вплоть до включения упаковки ключей в копии). Профиль — временный, в конце убирается.
    $check = Join-Path ([IO.Path]::GetTempPath()) ('dsh-build-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $check | Out-Null
    $env:DSH_PANEL_DATA = Join-Path $check 'data'
    $env:DSH_PANEL_STATE = Join-Path $check 'state'
    $env:DSH_PANEL_SSH_DIR = Join-Path $check 'ssh'
    $env:DSH_TRAY_BACKUP = Join-Path $check 'backups'
    # Домашняя папка dsh — тоже своя: иначе проверка читала бы настоящий ключ модели
    # и на каждой сборке ходила бы на сервер за балансом.
    $env:DSH_HOME = Join-Path $check 'dsh-home'
    $env:DSH_PANEL_NO_MIGRATE = '1'
    $env:DSH_PANEL_LANG = 'ru'

    # Панель — GUI-приложение, PowerShell её не дожидается: без WaitForExit
    # самопроверка заканчивалась раньше, чем панель успевала записать отчёт,
    # и status.txt появлялся в публикации уже после уборки.
    $statusFile = Join-Path $OutDir 'status.txt'
    try {
        # Кавычки вокруг пути обязательны: Start-Process клеит элементы списка пробелами,
        # и «--out C:\My Apps\status.txt» доехало бы до CLI тремя словами.
        $statusProcess = Start-Process -FilePath $exe -ArgumentList @('--status', '--out', ('"' + $statusFile + '"')) -PassThru
        if ($statusProcess) { $null = $statusProcess.WaitForExit(60000) }
    }
    finally {
        Remove-Item -LiteralPath $check -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item Env:DSH_PANEL_NO_MIGRATE -ErrorAction SilentlyContinue
    }
    Write-Host ('Проверка состояния — {0}' -f $statusFile)
}
