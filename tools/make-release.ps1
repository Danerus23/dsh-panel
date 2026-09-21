# make-release.ps1 — собрать всё, что нужно для выпуска, и проверить это.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-release.ps1
#
# Что делает (ничего не публикует — публикация за владельцем):
#   1) проверяет словари интерфейса;
#   2) собирает панель в dist\panel (не в app\, чтобы запущенная панель не мешала);
#   3) прогоняет приёмку «как чужой» по собранной панели;
#   4) собирает панель «без .NET» (один exe со средой внутри) в dist\panel-selfcontained;
#   5) складывает оба варианта в dist\DshPanel.zip и dist\DshPanel-selfcontained.zip;
#   6) собирает установщик dist\dsh-panel-setup.exe;
#   7) проверяет, что версии в комплекте сходятся (tools\check-versions.ps1);
#   8) собирает тело выпуска из всех трёх историй (CHANGELOG.md, CHANGELOG.en.md,
#      CHANGELOG.zh.md) в dist\release-notes.md;
#   9) проверяет, что в проект не затесались личные ключи и данные владельца;
#  10) печатает, что осталось сделать руками (репозиторий, тег, выпуск).
#
# Варианты: -SkipAcceptance, -SkipInstaller, -SkipSelfContained (тяжёлая сборка ~130 МБ),
#           -SkipUpdate (не проверять путь обновления через локальную заглушку GitHub).

param(
    [switch]$SkipAcceptance,
    [switch]$SkipInstaller,
    [switch]$SkipSelfContained,
    [switch]$SkipUpdate
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$panel = Join-Path $dist 'panel'
$project = Join-Path $root 'DshTray.csproj'
$failed = $false

function Step([string]$title, [scriptblock]$action) {
    Write-Host ''
    Write-Host ('=== ' + $title) -ForegroundColor Cyan

    # Чисто-PowerShell шаги $LASTEXITCODE не трогают, и в нём остался бы код прошлой
    # внешней программы. Обнуляем сами: тогда ненулевой код здесь — провал этого шага.
    $global:LASTEXITCODE = 0
    & $action
    if ($global:LASTEXITCODE -ne 0) {
        $script:failed = $true
        throw ('шаг провален с кодом ' + $global:LASTEXITCODE + ': ' + $title)
    }
}

# Личные ключи и данные владельца не уходят в раздачу — это красная линия проекта,
# поэтому проверка шагом, а не подсказкой: пропустить её нельзя.
function Assert-NoSecrets {
    $patterns = @(
        'keystore', '\.jks$', '\.keystore$', 'id_rsa', 'id_ed25519', 'id_ecdsa',
        '\.pem$', '\.pfx$', 'credentials', 'web-url', 'settings\.json$'
    )
    $found = @()

    # 1. Если проект уже под git — смотрим, что лежит в индексе: именно это уедет
    #    в публичный репозиторий.
    if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
        $tracked = & git -C $root ls-files 2>$null
        if ($global:LASTEXITCODE -eq 0 -and $tracked) {
            foreach ($file in $tracked) {
                foreach ($pattern in $patterns) {
                    if ($file -match $pattern) { $found += ('в индексе git: ' + $file); break }
                }
            }
        }
    }

    # 2. До git init единственная проверка — что лежит в папке проекта.
    $skip = @('app', 'app-staging', 'dist', 'bin', 'obj', '.git', '.nuget', '.appdata', '.dotnet-home', 'logs')
    $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $relative = $_.FullName.Substring($root.Length).TrimStart('\')
            $skip -notcontains ($relative -split '\\')[0]
        }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\')
        foreach ($pattern in $patterns) {
            if ($relative -match $pattern) { $found += ('в папке проекта: ' + $relative); break }
        }
    }

    if ($found.Count -gt 0) {
        throw ('похоже на личные данные, в раздачу нельзя: ' + ($found -join '; '))
    }

    Write-Host ('  личного не нашлось: проверено {0} файлов проекта' -f $files.Count)
    if (-not (Test-Path -LiteralPath (Join-Path $root '.gitignore'))) {
        Write-Host '  ВНИМАНИЕ: нет .gitignore — собранное и служебное легко уедет в репозиторий.' -ForegroundColor Yellow
    }
}

$version = (Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw 'Не нашёл <Version> в DshTray.csproj' }
Write-Host ('Версия панели: ' + $version)

try {
    # Кодировка — первым шагом: без BOM скрипты падают у того, кто откроет их в PowerShell 5.1.
    Step 'Кодировка скриптов' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-encoding.ps1')
    }

    Step 'Личные данные не уезжают' {
    Assert-NoSecrets
    & (Join-Path $PSScriptRoot 'check-personal.ps1') -Root $root
    if ($LASTEXITCODE -ne 0) { throw 'личные данные в репозитории — публиковать нельзя' }
}

    Step 'Словари интерфейса' { node (Join-Path $root 'tools\check-lang.mjs') }

    Step 'Сборка панели' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -OutDir $panel
    }

    if (-not $SkipSelfContained) {
        Step 'Сборка «без .NET» (один exe, со средой внутри)' {
            $single = Join-Path $dist 'panel-selfcontained'
            Remove-Item -LiteralPath $single -Recurse -Force -ErrorAction SilentlyContinue
            # Источник пакетов указан явно: NuGet.config очищает список, а сборке нужен runtime pack.
            & dotnet publish (Join-Path $root 'DshTray.csproj') -c Release -r win-x64 --self-contained true `
                -p:PublishSingleFile=true -o $single --source https://api.nuget.org/v3/index.json
        }
    }

    # Упаковка и проверка «служебное не уехало» — один общий скрипт: ровно его же зовёт CI,
    # иначе артефакт из CI отличается от проверенного здесь.
    $pack = Join-Path $root 'tools\pack-panel.ps1'
    Step 'Архив панели' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $pack `
            -Source $panel -Destination (Join-Path $dist 'DshPanel.zip')
    }

    if (-not $SkipSelfContained) {
        Step 'Архив «без .NET»' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $pack `
                -Source (Join-Path $dist 'panel-selfcontained') -Destination (Join-Path $dist 'DshPanel-selfcontained.zip')
        }
    }

    # Адреса, откуда панель ставит Node: однажды имя архива собиралось по образцу MSI, и установка
    # Node падала с 404 у всех — это увидел владелец на приёмке. Проверка ловит такое до выпуска.
    Step 'Адреса для установки Node' {
        $target = Join-Path $dist 'node-check.txt'
        # Панель — GUI-приложение: PowerShell её не дожидается, нужен Start-Process -Wait.
        $process = Start-Process -FilePath (Join-Path $panel 'DshTray.exe') `
            -ArgumentList @('--node-check', '--out', ('"' + $target + '"')) -Wait -PassThru
        Get-Content -LiteralPath $target -Encoding UTF8 | ForEach-Object { Write-Host ('  ' + $_) }
        if ($process.ExitCode -ne 0) { throw 'адреса для установки Node не отвечают — установка Node у людей не сработает' }
    }

    if (-not $SkipAcceptance) {
        Step 'Приёмка «как чужой»' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\acceptance.ps1') -Exe (Join-Path $panel 'DshTray.exe')
        }

        # Накат копии проверяется отдельно и в своей песочнице: у него свои правила — предохранительная
        # копия, права на ключах, записи архива с путями наружу. Приложение берём собранное, из dist.
        Step 'Накат резервной копии' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-restore.ps1') -Dll (Join-Path $panel 'DshTray.dll') -Work (Join-Path $dist 'restore-check')
        }

        # Автозапуск: запись в HKCU\...\Run хранит абсолютный путь к exe, и её надо сверять
        # с текущей копией. Проверка уводит реестр в свою ветку (DSH_PANEL_RUN_KEY) и в конце
        # убеждается, что настоящий автозапуск человека не тронут.
        Step 'Автозапуск (сверка и починка записи)' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-autostart.ps1') -Exe (Join-Path $panel 'DshTray.exe')
        }

        # Перенос настроек прежней панели — тоже в своей песочнице: прежняя папка подменяется
        # переменной DSH_PANEL_LEGACY_DATA, поэтому настоящий %APPDATA%\DeepSeekHarness не
        # читается. Дефект был живой: файл прежней панели всегда новее панельного, и настройки
        # панели затирались при каждом запуске — проверка обязана это ловить.
        Step 'Перенос настроек прежней панели' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-migrate.ps1') -Exe (Join-Path $panel 'DshTray.exe')
        }

        # Путь обновления проверяется отдельно: поднимается локальная заглушка GitHub API,
        # и панель проходит его целиком — скачивание, сверка контрольных сумм, распаковка.
        # Публиковать выпуск для этого не нужно.
        if (-not $SkipUpdate) {
            Step 'Путь обновления (суммы и отказ при подмене)' {
                & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-update.ps1') -Exe (Join-Path $panel 'DshTray.exe')
            }

            # Сама замена файлов: панель ставится в путь с пробелом, и именно на нём обновление
            # однажды откатилось молча. Проверка повторяет весь путь целиком и падает, если
            # robocopy не справился.
            Step 'Замена файлов при обновлении' {
                & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-update-apply.ps1') -Exe (Join-Path $panel 'DshTray.exe')
            }
        }

    }

    if (-not $SkipInstaller) {
        Step 'Установщик' {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'installer\build-installer.ps1') -SkipBuild -AppDir $panel
        }
    }

    # Номер версии живёт в четырёх местах: проект, штамп сборки, номер установщика и сам
    # установщик. Раньше за этим следил отдельный KitStatus; теперь проверка вынесена в скрипт —
    # иначе в комплект для чистой машины легко попадёт установщик от прошлой сборки.
    Step 'Сверка версий комплекта' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-versions.ps1') -Dist $dist
    }

    # Без этого файла в выпуске панель отказывается обновлять себя: сертификата у проекта
    # нет, и единственная проверка скачанного архива — сверка с опубликованной суммой.
    Step 'Контрольные суммы выпуска' {
        # Перечисляем только то, что действительно собрано: с -SkipInstaller или
        # -SkipSelfContained соответствующих файлов в dist нет. Скрипт зовём прямо,
        # а не через powershell -File: при -File список аргументов доезжает одним
        # элементом, и в суммы попадала только первая сборка.
        $assets = @('DshPanel.zip')
        if (-not $SkipInstaller) { $assets = @('dsh-panel-setup.exe') + $assets }
        if (-not $SkipSelfContained) { $assets += 'DshPanel-selfcontained.zip' }

        & (Join-Path $root 'tools\write-checksums.ps1') -Dist $dist -Files $assets
    }

    Step 'Заметки к выпуску' {
        # Тот же скрипт зовёт GitHub Actions при выпуске по тегу: заметки показывает
        # окно обновлений в панели, и собираться они должны одинаково в обоих местах.
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\release-notes.ps1') `
            -Version $version -Out (Join-Path $dist 'release-notes.md')
    }

    # Без этой проверки заметки уедут в выпуск «как получилось»: панель покажет пустое окно
    # «Что нового» (маркеров нет) или русский текст человеку с английским интерфейсом.
    Step 'Проверка заметок к выпуску' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-notes.ps1') `
            -Version $version
    }

    Write-Host ''
    Write-Host '=== Что готово' -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $dist -File -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ('  {0} — {1:N1} МБ' -f $_.Name, ($_.Length / 1MB))
    }
    if (Test-Path -LiteralPath $panel) { Write-Host ('  panel\ — папка панели ({0} файлов)' -f (Get-ChildItem -LiteralPath $panel -Recurse -File).Count) }
}
catch {
    $failed = $true
    Write-Host ''
    Write-Host ('=== ПРОВАЛ: ' + $_.Exception.Message) -ForegroundColor Red
}

if (-not $failed) {
    Write-Host ''
    Write-Host '=== Что осталось сделать руками' -ForegroundColor Yellow
    Write-Host ('  1. Поднять версию: <Version> в DshTray.csproj и раздел в КАЖДОЙ из трёх историй —')
    Write-Host ('     CHANGELOG.md, CHANGELOG.en.md, CHANGELOG.zh.md. Сейчас ' + $version + '.')
    Write-Host '  2. Закоммитить и отправить:'
    Write-Host '       git add -A; git commit -m "..."; git push origin main'
    Write-Host '  3. Выпуск — тегом (тег обязан совпасть с версией проекта):'
    Write-Host ('       git tag -a v' + $version + ' -m "DSH Panel ' + $version + '"; git push origin v' + $version)
    Write-Host '     Тег запускает сборку в GitHub Actions: она сама соберёт установщик, сверит тег'
    Write-Host '     с версией проекта и приложит к выпуску dsh-panel-setup.exe, DshPanel.zip,'
    Write-Host '     DshPanel-selfcontained.zip и SHA256SUMS.txt. Заметки собираются из всех трёх'
    Write-Host '     историй — их показывает окно обновлений в панели.'
    Write-Host ('     Проверить уже опубликованный выпуск: tools\verify-release.ps1 -Tag v' + $version)
    Write-Host '  4. ПОСЛЕ публикации — пересобрать манифест winget под выпущенную версию и отправить'
    Write-Host '     его в microsoft/winget-pkgs отдельным pull request:'
    Write-Host ('       tools\winget-manifest.ps1 -ReleaseTag v' + $version + ' -Validate')
}

exit $(if ($failed) { 1 } else { 0 })
