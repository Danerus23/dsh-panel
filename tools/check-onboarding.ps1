# check-onboarding.ps1 — проверить, что мастер первой настройки открывается ОДНИМ окном.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-onboarding.ps1
#
# Зачем: на приёмке в виртуальной машине мастер открывался дважды — его показывал и Program,
# и таймер старта TrayHost (модальное окно не мешает таймеру сработать). Проверка запускает
# панель с ключом --onboard в отдельном профиле, считает окна мастера через EnumWindows
# и закрывает только свой процесс.
#
# Живой панели и сервера проверка не касается: свой профиль, свой порт, свой процесс.
# Нужен рабочий стол (интерактивный сеанс) — в CI без экрана она не запустится.

param(
    [string]$Exe = '',
    [int]$Port = 3913,
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'
if ($Port -eq 3080) { throw 'порт 3080 не трогаем' }

$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'dist\panel\DshTray.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw ('нет панели: ' + $Exe) }

$work = Join-Path ([IO.Path]::GetTempPath()) ('dsh-onboarding-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
foreach ($dir in @('data', 'state', 'ssh', 'backups', 'dsh-home')) {
    New-Item -ItemType Directory -Path (Join-Path $work $dir) -Force | Out-Null
}

# Перечисление окон: у панели несколько окон в одном процессе, и Get-Process видит только одно.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowList
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);

    private delegate bool EnumProc(IntPtr handle, IntPtr param);

    public static List<string> Titles(uint processId)
    {
        var found = new List<string>();
        EnumWindows((handle, param) =>
        {
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (pid == processId && IsWindowVisible(handle))
            {
                var length = GetWindowTextLength(handle);
                if (length > 0)
                {
                    var text = new StringBuilder(length + 1);
                    GetWindowText(handle, text, text.Capacity);
                    found.Add(text.ToString());
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@ -Language CSharp | Out-Null

$env:DSH_PANEL_DATA = Join-Path $work 'data'
$env:DSH_PANEL_STATE = Join-Path $work 'state'
$env:DSH_PANEL_SSH_DIR = Join-Path $work 'ssh'
$env:DSH_TRAY_BACKUP = Join-Path $work 'backups'
$env:DSH_HOME = Join-Path $work 'dsh-home'
$env:DSH_PANEL_NO_MIGRATE = '1'
$env:DSH_PANEL_LANG = 'ru'
# Своё имя экземпляра: иначе второй запуск просто просит живую панель показать окно.
$env:DSH_PANEL_INSTANCE = 'Local\DshTray.Check-' + (Get-Random)

Write-Host ('Свой профиль: ' + $work)
$process = Start-Process -FilePath $Exe -ArgumentList @('--onboard', '--tray', '--port', $Port) -PassThru
try {
    # Ждём: панель поднимает окно мастера через 60 мс после старта, но окну нужно время появиться.
    Start-Sleep -Seconds $WaitSeconds

    if ($process.HasExited) {
        # Панель уже запущена: у приложения один экземпляр на сеанс (мьютекс), и второй
        # только просит первый показать окно, а сам выходит. Проверить мастер можно лишь там,
        # где панель не запущена (например, в тестовой ВМ).
        $others = @(Get-Process -Name 'DshTray' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $process.Id })
        if ($others.Count -gt 0) {
            Write-Host ('ПРОПУЩЕНО: запущена другая панель (PID ' + (($others | ForEach-Object { $_.Id }) -join ', ') + '), второй экземпляр выходит сразу.')
            Write-Host 'Закройте её или запустите эту проверку в тестовой ВМ.'
            exit 2
        }

        Write-Host ('панель завершилась сама с кодом ' + $process.ExitCode + ' — проверка не удалась')
        exit 1
    }

    $titles = @([WindowList]::Titles([uint32]$process.Id))
    Write-Host ('Окна процесса (PID ' + $process.Id + '):')
    foreach ($title in $titles) { Write-Host ('  «' + $title + '»') }

    $wizards = @($titles | Where-Object { $_ -match 'Первая настройка' })
    Write-Host ''
    if ($wizards.Count -eq 1) {
        Write-Host 'мастер первой настройки: одно окно — как и должно быть'
        exit 0
    }

    if ($wizards.Count -eq 0) {
        Write-Host 'мастер первой настройки не открылся вовсе — это тоже ошибка'
        exit 1
    }

    Write-Host ('ОКОН МАСТЕРА: ' + $wizards.Count + ' — должно быть одно')
    exit 1
}
finally {
    # Закрываем только свой процесс: живая панель владельца не трогается.
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
