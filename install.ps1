# install.ps1
# Ярлыки приложения DSH Panel: рабочий стол и меню «Пуск».
#
# Запуск (один раз, после build.ps1):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
#
# Обычному человеку ярлыки ставит установщик (installer\dsh-panel-setup.exe);
# этот скрипт — для того, кто собрал панель сам и хочет те же ярлыки без установщика.
# Имена ярлыков совпадают с установщиком, иначе на одну программу их будет два.
#
# Варианты:
#   -AppDir <путь>   где лежит DshTray.exe (по умолчанию .\app)
#   -NoDesktop       не создавать ярлык на рабочем столе
#   -NoStartMenu     не создавать ярлык в меню «Пуск»

param(
    [string]$AppDir = '',
    [switch]$NoDesktop,
    [switch]$NoStartMenu
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $AppDir) { $AppDir = Join-Path $root 'app' }

$exe = Join-Path $AppDir 'DshTray.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw ("Не найден {0}. Сначала соберите приложение: .\build.ps1" -f $exe)
}

# Иконка для ярлыка: своя рядом с приложением, иначе берём из исходников.
$icon = Join-Path $AppDir 'dsh-tray.ico'
if (-not (Test-Path -LiteralPath $icon)) { $icon = Join-Path $root 'dsh-tray.ico' }

function New-Shortcut {
    param([string]$Path, [string]$Target, [string]$Icon, [string]$Description)

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = (Split-Path -Parent $Target)
    if (Test-Path -LiteralPath $Icon) { $shortcut.IconLocation = ('{0},0' -f $Icon) }
    $shortcut.Description = $Description
    $shortcut.Save()
    return $Path
}

$created = @()

if (-not $NoDesktop) {
    $created += New-Shortcut -Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Panel.lnk') `
        -Target $exe -Icon $icon -Description 'DSH Panel — панель управления локальным сервером DeepSeek Harness'
}

if (-not $NoStartMenu) {
    $created += New-Shortcut -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Panel.lnk') `
        -Target $exe -Icon $icon -Description 'DSH Panel — панель управления локальным сервером DeepSeek Harness'
}

'Готово. Создано:'
foreach ($item in $created) { '  {0}' -f $item }
'Приложение: {0}' -f $exe
'Автозапуск при входе включается галочкой в самой панели (раздел «Запускать при входе в Windows»).'
