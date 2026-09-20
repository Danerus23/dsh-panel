# Проверка наката резервной копии: копия -> порча данных -> накат -> сверка.
#
# Работает в отдельной песочнице: свои DSH_HOME, DSH_PANEL_DATA, DSH_PANEL_STATE
# и папка копий, а также свой порт. Живая панель и её данные не трогаются: накат
# останавливает сервер на своём порту, и порт 3080 в этой проверке запрещён.
#
# Запуск:
#   pwsh -File tools\check-restore.ps1                 # приложение берётся из ..\_build\app
#   pwsh -File tools\check-restore.ps1 -Dll <путь>     # своя сборка
[CmdletBinding()]
param(
    [string]$Dll = "",
    [string]$Work = "",
    [int]$Port = 3099,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
if ($Port -eq 3080) { throw 'порт 3080 занят живой панелью — проверка наката его не трогает' }

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Dll)) {
    $Dll = Join-Path (Split-Path -Parent $root) '_build\app\DshTray.dll'
}
if (-not (Test-Path -LiteralPath $Dll)) { throw "не нашёл сборку: $Dll" }

# $Work приходит параметром, а дальше по нему идёт рекурсивная выдача прав и удаление.
# Пустая строка, корень диска, домашний каталог и профиль — то, что затирать нельзя,
# поэтому проверяем путь до первой разрушительной операции, а не после.
function Assert-SandboxPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw 'пустой путь песочницы' }

    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if ($full.Length -le 2) { throw ('отказываюсь работать с корнем диска: ' + $full) }

    $forbidden = @(
        [Environment]::GetFolderPath('UserProfile'),
        [Environment]::GetFolderPath('ApplicationData'),
        [Environment]::GetFolderPath('LocalApplicationData'),
        (Split-Path -Parent ([Environment]::GetFolderPath('UserProfile')))
    )
    foreach ($item in $forbidden) {
        if ($item -and $full -eq ([IO.Path]::GetFullPath($item).TrimEnd('\'))) {
            throw ('отказываюсь работать с этим каталогом: ' + $full)
        }
    }

    return $full
}

if ([string]::IsNullOrWhiteSpace($Work)) {
    $Work = Join-Path (Split-Path -Parent $root) '_build\restore-test'
}
$Work = Assert-SandboxPath $Work

# Убираем песочницу в одном месте: каталоги ключей после наката закрыты на владельца,
# поэтому сначала возвращаем себе права. Уборка нужна и при провале проверки —
# иначе песочница остаётся с изменёнными правами доступа.
function Exit-With([int]$code) {
    if (-not $Keep) {
        icacls "$Work" /grant "$($env:USERNAME):(OI)(CI)F" /T /C /Q 2>&1 | Out-Null
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host ("песочница осталась здесь: " + $Work)
    }

    exit $code
}

# Непредвиденная ошибка не должна оставлять песочницу с изменёнными правами:
# уборку делает тот же выход, что и при обычном провале проверки.
trap {
    Write-Host ('ОШИБКА проверки: ' + $_.Exception.Message) -ForegroundColor Red
    Exit-With 1
}

if (Test-Path -LiteralPath $Work) {
    # Каталоги ключей после прошлой проверки закрыты на владельца: сначала возвращаем себе права.
    icacls "$Work" /grant "$($env:USERNAME):(OI)(CI)F" /T /C /Q 2>&1 | Out-Null
    Remove-Item -LiteralPath $Work -Recurse -Force
}
$dshHome = Join-Path $Work 'dsh-home'
$data = Join-Path $Work 'panel-data'
$state = Join-Path $Work 'panel-state'
$keys = Join-Path $Work 'my-keys'
# Имя каталога ключей SSH важно: по нему копия помечает группу как .ssh (keys\1-ssh),
# и при переезде на другую машину ключи возвращаются в свой .ssh нового пользователя.
$ssh = Join-Path $Work '.ssh'
$backups = Join-Path $Work 'backups'
foreach ($dir in @($dshHome, $data, $state, $keys, $ssh, $backups)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) {
        Write-Host ("  ок   " + $name)
        return
    }

    $line = "  БЕДА " + $name
    if ($detail) { $line = $line + " — " + $detail }
    Write-Host $line
    $failures.Add($name)
}

Write-Host "Песочница: $Work"

# --- что «лежит у человека» до копии ---------------------------------------
$files = [ordered]@{
    'settings.json'               = '{"model":"deepseek-flash","nested":true}'
    'skills\моя-навычка\SKILL.md' = "# Навык`nтекст с кириллицей и 汉字"
    'profiles\web\package.json'   = '{"name":"web","dependencies":{}}'
    'sessions\session-0001.json'  = '{"turns":[1,2,3]}'
    '.credentials.yaml'           = 'token: test-token'
}
foreach ($pair in $files.GetEnumerator()) {
    $path = Join-Path $dshHome $pair.Key
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [System.IO.File]::WriteAllText($path, $pair.Value, (New-Object System.Text.UTF8Encoding($false)))
}
[System.IO.File]::WriteAllText((Join-Path $keys 'id_ed25519'), 'PRIVATE-KEY-TEST', (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText((Join-Path $ssh 'id_ed25519'), 'SSH-KEY-TEST', (New-Object System.Text.UTF8Encoding($false)))

$settings = @{
    onboarded = $true
    language = 'ru'
    serverPort = $Port
    backupWithEngine = $false
    backupWithSessions = $true
    backupWithKeys = $true
    backupKeyDirs = @($keys)
    backupKeepCount = 3
} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settings, (New-Object System.Text.UTF8Encoding($false)))

$env:DSH_HOME = $dshHome
$env:DSH_PANEL_DATA = $data
$env:DSH_PANEL_STATE = $state
$env:DSH_TRAY_BACKUP = $backups
$env:DSH_PANEL_LANG = 'ru'

# Свой каталог ключей: без этого накат закрывал бы права на настоящем ~/.ssh владельца машины.
$env:DSH_PANEL_SSH_DIR = $ssh

function Run([string[]]$arguments, [string]$reportName) {
    $out = Join-Path $Work $reportName
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
    & dotnet exec $Dll @arguments '--port' $Port '--out' $out 2>&1 | Out-Null
    Start-Sleep -Milliseconds 900
    if (Test-Path -LiteralPath $out) { return (Get-Content -LiteralPath $out -Raw -Encoding UTF8) }
    return ''
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# --- 1. копия ---------------------------------------------------------------
Write-Host "1) делаю копию"
$report = Run @('--backup') 'backup.txt'
$zip = Get-ChildItem -LiteralPath $backups -Filter 'dsh-backup-*.zip' | Select-Object -First 1
Check 'копия создана' ($null -ne $zip) $report
if (-not $zip) { Exit-With 1 }

function EntryNames([string]$path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try { return ($archive.Entries | ForEach-Object { $_.FullName }) } finally { $archive.Dispose() }
}

function Manifest([string]$path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $archive.GetEntry('manifest.json')
        $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

$names = EntryNames $zip.FullName
Check 'данные DSH в архиве' (($names | Where-Object { $_ -eq 'dsh-home/settings.json' }).Count -eq 1)
Check 'навык с кириллицей в архиве' (($names | Where-Object { $_ -like '*SKILL.md' }).Count -eq 1)
Check 'настройки панели в архиве' (($names | Where-Object { $_ -eq 'appdata/DshPanel/settings.json' }).Count -eq 1)
Check 'ключи в архиве (галочка стояла)' (($names | Where-Object { $_ -like 'keys/*' }).Count -ge 1)
Check 'движка в тонкой копии нет' (($names | Where-Object { $_ -like 'engine/*' }).Count -eq 0)

$manifest = Manifest $zip.FullName
$recorded = @($manifest.Paths.PSObject.Properties | Where-Object { $_.Value -eq $keys })
Check 'опись помнит, откуда взяты ключи' ($recorded.Count -ge 1) (($manifest.Paths.PSObject.Properties | ForEach-Object { $_.Name + '=' + $_.Value }) -join ', ')
Check 'опись знает версию панели' ($manifest.AppVersion.Length -gt 0)

# --- 2. злой архив: запись, которая хочет выйти наружу ----------------------
Write-Host "2) добавляю в копию запись с недопустимым путём"
$evil = Join-Path $backups 'dsh-backup-evil.zip'
Copy-Item $zip.FullName $evil
$archive = [System.IO.Compression.ZipFile]::Open($evil, 'Update')
$entry = $archive.CreateEntry('dsh-home/../../evil.txt')
$writer = New-Object System.IO.StreamWriter($entry.Open())
$writer.Write('я не должен появиться')
$writer.Dispose()
$archive.Dispose()

# --- 3. порча данных --------------------------------------------------------
Write-Host "3) порчу данные перед накатом"
Remove-Item (Join-Path $dshHome 'skills') -Recurse -Force
[System.IO.File]::WriteAllText((Join-Path $dshHome 'settings.json'), '{"сломано":true}', (New-Object System.Text.UTF8Encoding($false)))
Remove-Item (Join-Path $data 'settings.json') -Force
Remove-Item (Join-Path $keys 'id_ed25519') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $ssh 'id_ed25519') -Force -ErrorAction SilentlyContinue
$evilTarget = Join-Path $Work 'evil.txt'
Check 'данные действительно испорчены' (-not (Test-Path (Join-Path $dshHome 'skills')))

# --- 4. накат ---------------------------------------------------------------
Write-Host "4) накатываю копию (с ключами)"
$report = Run @('--restore', '--from', $evil, '--keys') 'restore.txt'
Write-Host $report.Trim()

foreach ($pair in $files.GetEnumerator()) {
    $path = Join-Path $dshHome $pair.Key
    $ok = (Test-Path $path)
    if ($ok) { $ok = ((Get-Content -Raw -Encoding UTF8 $path) -eq $pair.Value) }
    Check ("вернулся " + $pair.Key) $ok
}
Check 'ключ из своего каталога вернулся' (Test-Path (Join-Path $keys 'id_ed25519'))
Check 'ключ из каталога .ssh вернулся' (Test-Path (Join-Path $ssh 'id_ed25519'))
Check 'настройки панели вернулись' (Test-Path (Join-Path $data 'settings.json'))
Check 'предохранительная копия сделана' ((Get-ChildItem -Path $backups -Filter 'dsh-before-restore-*.zip').Count -ge 1)
Check 'злая запись не вышла наружу' (-not (Test-Path $evilTarget))
Check 'в отчёте сказано про пропущенную запись' ($report -match 'недопустимым путём')
Check 'в отчёте есть группы' ($report -match 'данные DSH')
Check 'в отчёте видно число файлов' ($report -match 'Восстановлено')

# --- 5. ключи закрыты на владельца -----------------------------------------
Write-Host "5) смотрю права на каталоге ключей"
# Сравниваем по номерам (SID): имя SYSTEM на русской Windows пишется словами.
$allowed = @([System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18')
$sids = ((Get-Acl -LiteralPath $keys).Access + (Get-Acl -LiteralPath $ssh).Access) | ForEach-Object {
    try { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
    catch { $_.IdentityReference.Value }
}
$aliens = @($sids | Where-Object { $allowed -notcontains $_ })
Check 'в правах только владелец и SYSTEM' ($aliens.Count -eq 0) ($aliens -join ', ')
# Читаем только если файл на месте: иначе провал выше превратился бы в исключение,
# и проверка обрывалась бы, не показав остальные.
$keyFile = Join-Path $keys 'id_ed25519'
Check 'ключ читается после наката' ((Test-Path -LiteralPath $keyFile) -and ((Get-Content -LiteralPath $keyFile -Raw -Encoding UTF8) -eq 'PRIVATE-KEY-TEST'))

# --- 6. накат без ключей ничего не трогает ---------------------------------
Write-Host "6) накатываю без ключей"
Remove-Item -LiteralPath (Join-Path $keys 'id_ed25519') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ssh 'id_ed25519') -Force -ErrorAction SilentlyContinue
$report = Run @('--restore', '--from', $zip.FullName) 'restore-nokeys.txt'
Check 'без --keys ключи не вернулись' ((-not (Test-Path -LiteralPath (Join-Path $keys 'id_ed25519'))) -and (-not (Test-Path -LiteralPath (Join-Path $ssh 'id_ed25519'))))
Check 'данные при этом вернулись' (Test-Path -LiteralPath (Join-Path $dshHome 'skills\моя-навычка\SKILL.md'))

# --- 7. чужая копия не накатывается ----------------------------------------
Write-Host "7) пробую накатить чужой архив"
$alien = Join-Path $backups 'чужой.zip'
$foreign = [System.IO.Compression.ZipFile]::Open($alien, 'Create')
$foreignEntry = $foreign.CreateEntry('что-то.txt')
$foreignWriter = New-Object System.IO.StreamWriter($foreignEntry.Open())
$foreignWriter.Write('не наша копия')
$foreignWriter.Dispose()
$foreign.Dispose()
$report = Run @('--restore', '--from', $alien) 'restore-alien.txt'
Check 'чужой архив отвергнут' ($report -match 'сделана не панелью')

# --- 8. копия с другой машины не кладёт ключи куда попало -------------------
# Путь к ключам в описи — путь машины-источника. Для чужой копии он не наш, поэтому
# ключи из незнакомого каталога восстанавливаться не должны: иначе переезд «в гости»
# разложил бы приватные ключи по произвольным каталогам новой машины.
Write-Host "8) накатываю копию, будто она снята на другой машине"
$foreignDir = Join-Path $Work 'чужие-ключи'
New-Item -ItemType Directory -Path $foreignDir -Force | Out-Null
$alienZip = Join-Path $Work 'с-другой-машины.zip'
Copy-Item -LiteralPath $zip.FullName -Destination $alienZip -Force

$manifestText = (Manifest $alienZip) | ConvertTo-Json -Depth 8
$manifestText = $manifestText -replace ('"machine":\s*"[^"]*"', '"machine": "ДРУГОЙ-ПК"')
$manifestText = $manifestText -replace ('"user":\s*"[^"]*"', '"user": "другой-пользователь"')
$manifestText = $manifestText -replace [regex]::Escape($keys.Replace('\', '\\')), $foreignDir.Replace('\', '\\')

# Опись заменяем в копии архива: снаружи архив остаётся валидным, менялись только поля пути.
$rewrite = [System.IO.Compression.ZipFile]::Open($alienZip, 'Update')
try {
    $entry = $rewrite.GetEntry('manifest.json')
    if ($entry) { $entry.Delete() }
    $fresh = $rewrite.CreateEntry('manifest.json')
    $writer = New-Object System.IO.StreamWriter($fresh.Open(), (New-Object System.Text.UTF8Encoding($false)))
    try { $writer.Write($manifestText) } finally { $writer.Dispose() }
}
finally { $rewrite.Dispose() }

Check 'опись чужой копии помнит другую машину' ((Manifest $alienZip).machine -eq 'ДРУГОЙ-ПК')
# Данные портим перед накатом: иначе «ключи не появились» было бы верно и для вовсе
# не работающего наката, и проверка ничего не доказывала бы.
Remove-Item -LiteralPath (Join-Path $dshHome 'settings.json') -Force -ErrorAction SilentlyContinue
$report = Run @('--restore', '--from', $alienZip, '--keys', '--no-safety') 'restore-alien-machine.txt'
Check 'чужая копия всё-таки накатилась (данные вернулись)' (Test-Path -LiteralPath (Join-Path $dshHome 'settings.json'))
Check 'чужой путь ключей файлов не получил' (-not (Test-Path -LiteralPath (Join-Path $foreignDir 'id_ed25519')))
Check 'незнакомый каталог ключей пропущен' (-not (Test-Path -LiteralPath (Join-Path $keys 'id_ed25519')))
Check 'свой каталог .ssh ключ получил' (Test-Path -LiteralPath (Join-Path $ssh 'id_ed25519'))

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host ("ПРОВАЛЕНО проверок: " + $failures.Count + " — " + ($failures -join '; '))
    Exit-With 1
}
Write-Host "накат копии: все проверки пройдены"
Exit-With 0
