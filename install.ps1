# install.ps1
# Ярлыки приложения DSH Panel: рабочий стол и меню «Пуск».
#
# Запуск (один раз, после build.ps1):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
#
# Обычному человеку ярлыки ставит установщик (installer\dsh-panel-setup.exe);
# этот скрипт — для того, кто собрал панель сам и хочет те же ярлыки без установщика.
# Место и имена ярлыков обязаны совпадать с установщиком (его раздел [Icons]), иначе на
# одну программу их будет два: установщик кладёт ярлык меню «Пуск» в подпапку группы
# («Программы\DSH Panel\DSH Panel.lnk»), потому что у него DisableProgramGroupPage=yes
# и DefaultGroupName=DSH Panel.
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

# Убрать ярлык, если он НАШ. Чужое не трогаем: сверяем цель с нашей копией ($exe), а не только
# имя файла — на машине может стоять другая копия DshTray.exe (прежний запуск с другим -AppDir,
# установленная панель), и её ярлык не наш. Сравнение без учёта регистра, как принято в Windows.
# (Установщик для ярлыка прежней генерации проверяет мягче — по имени цели: у него другая задача,
# убрать ярлык прежней генерации, чей путь сегодня уже не существует.)
# Нужно это для ярлыка, который прежняя версия ЭТОГО скрипта клала в корень «Программ»: после
# переезда в подпапку группы он остался бы вторым ярлыком на ту же программу.
function Remove-OwnShortcut {
    param([string]$Path, [string]$Target)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }

    $shell = New-Object -ComObject WScript.Shell
    try { $current = $shell.CreateShortcut($Path).TargetPath } catch { return $false }
    if (-not $current) { return $false }
    if ($current -ne $Target) {
        # Write-Host, а не строка в поток вывода: иначе сообщение попало бы в результат функции.
        Write-Host ('Ярлык ведёт на другую копию — оставлен: {0}' -f $Path)
        return $false
    }

    Remove-Item -LiteralPath $Path -Force
    return $true
}

$created = @()

if (-not $NoDesktop) {
    $created += New-Shortcut -Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Panel.lnk') `
        -Target $exe -Icon $icon -Description 'DSH Panel — панель управления локальным сервером DeepSeek Harness'
}

if (-not $NoStartMenu) {
    # Та же подпапка, что у установщика ({group} = «Программы\DSH Panel»).
    $programsDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Panel'
    $created += New-Shortcut -Path (Join-Path $programsDir 'DSH Panel.lnk') `
        -Target $exe -Icon $icon -Description 'DSH Panel — панель управления локальным сервером DeepSeek Harness'

    # Ярлык прежнего места (корень «Программ») убираем — иначе на одну программу их два.
    $stale = Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Panel.lnk'
    if (Remove-OwnShortcut -Path $stale -Target $exe) { 'Убран ярлык прежнего места: {0}' -f $stale }
}

'Готово. Создано:'
foreach ($item in $created) { '  {0}' -f $item }
'Приложение: {0}' -f $exe
'Автозапуск при входе включается галочкой в самой панели (раздел «Запускать при входе в Windows»).'
