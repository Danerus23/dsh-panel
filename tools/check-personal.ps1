# check-personal.ps1 — проверка, что в файлы репозитория не попали личные данные сборщика.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-personal.ps1
#
# Что ищет: путь к папке пользователя этой машины (C:\Users\<имя>) и имя компьютера в текстовых
# файлах, которые уедут в публичный репозиторий, а также строки, похожие на токены.
# Зачем отдельным скриптом: тот же сторожок нужен и в конвейере выпуска, и в CI, и человеку,
# который просто правит текст. Одна реализация на всех — иначе они расходятся.
#
# Важно: имя автора (Danerus23) в репозитории законно — это копирайт, владелец репозитория и
# издатель в установщике. Личным считается именно ПУТЬ к папке пользователя и имя машины.

param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

# Читаем только текстовые файлы: PNG и ZIP как текст разбирать бессмысленно.
$textExtensions = @('.cs', '.md', '.json', '.ps1', '.psm1', '.iss', '.yml', '.yaml', '.csproj',
    '.config', '.txt', '.mjs', '.js', '.cmd', '.bat', '.gitignore', '.gitattributes')

$markers = @()
if ($env:USERNAME) { $markers += ('C:\Users\' + $env:USERNAME) }
if ($env:COMPUTERNAME) { $markers += $env:COMPUTERNAME }

if ($markers.Count -eq 0) {
    Write-Host 'Не знаю, что искать: не видно ни имени пользователя, ни имени машины.'
    exit 0
}

# Список файлов: из индекса git, если репозиторий есть (это ровно то, что опубликуется),
# иначе — обход папки с исключением мест сборки.
$skip = @('app', 'app-staging', 'dist', 'bin', 'obj', '.git', '.nuget', '.appdata', '.dotnet-home', 'logs')
$list = @()

if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $Root '.git'))) {
    $tracked = @(& git -C $Root ls-files 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 0) {
        $list = $tracked | ForEach-Object { Join-Path $Root $_ }
    }
}

if ($list.Count -eq 0) {
    $list = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $skip -notcontains ($_.FullName.Substring($Root.Length).TrimStart('\') -split '\\')[0] } |
        Select-Object -ExpandProperty FullName)
}

$found = @()
foreach ($file in $list) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    if ($textExtensions -notcontains ([IO.Path]::GetExtension($file).ToLowerInvariant())) { continue }

    $relative = $file.Substring($Root.Length).TrimStart('\')
    $number = 0
    foreach ($line in @(Get-Content -LiteralPath $file -Encoding UTF8 -ErrorAction SilentlyContinue)) {
        $number++

        foreach ($marker in $markers) {
            if ($line -match [regex]::Escape($marker)) {
                $found += ($relative + ':' + $number + ' — встречается «' + $marker + '»')
            }
        }

        if ($line -match 'gh[opusr]_[A-Za-z0-9]{20,}' -or $line -match 'sk-[A-Za-z0-9]{20,}') {
            $found += ($relative + ':' + $number + ' — похоже на токен')
        }
    }
}

if ($found.Count -gt 0) {
    Write-Host ''
    Write-Host 'БЕДА  в файлах репозитория есть личные данные:' -ForegroundColor Red
    $found | Select-Object -Unique | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor Red }
    exit 1
}

Write-Host ('Личных данных нет (искали: ' + ($markers -join ', ') + ').')
exit 0
