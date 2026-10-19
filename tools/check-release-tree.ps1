# check-release-tree.ps1 — ДОКАЗАТЕЛЬСТВО ТОЖДЕСТВА ДЕРЕВА ВЫПУСКА (ADR-001 §5.2, правка владельца 17.10.2026).
#
#   pwsh -NoProfile -File .\tools\check-release-tree.ps1 -Tag <тег>
#   pwsh -NoProfile -File .\tools\check-release-tree.ps1 -Tag <тег> -Tree C:\Temp\release-out-2.1.0
#   pwsh -NoProfile -File .\tools\check-release-tree.ps1 -Rev <sha>            # приёмка без тега (2.1.1)
#   pwsh -NoProfile -File .\tools\check-release-tree.ps1 -Rev <sha> -Tree C:\Temp\release-out-2.1.1
#
# ЧТО ИЗМЕНИЛОСЬ ПРОТИВ ПЕРВОЙ РЕДАКЦИИ (и почему). Раньше ожидание бралось как ПОДДЕРЕВО
# `'<тег>:release/tree'` — то есть только файлы данных (тогда их было 30; с 18.10.2026 — 39,
# решение владельца о трёх новых видах кадров). Но ADR §5.2(1) называет `release\tree\`
# ПОЛНЫМ деревом выпуска (код + данные), а §5.2(3) требовал равенства хеша `release\tree` с этим
# поддеревом: хеши не совпадут НИКОГДА, значит доказывались только данные, а полное дерево — нет.
# Владелец 17.10.2026: «`release\tree` — источник данных; ожидаемое ПОЛНОЕ дерево считается из тега
# фильтром по разрешённому списку (временный индекс: read-tree <тег> → rm --cached лишнего →
# write-tree) и с ним сверяется сборка».
#
# ОЖИДАНИЕ СЧИТАЕТСЯ ТОЛЬКО ИЗ ОБЪЕКТА GIT (`Get-ExpectedFullTree` в `release-tree-common.ps1`):
#   временный индекс → `read-tree <тег|ревизия>` → отброс всего вне разрешённого списка (список путей
#   подаётся ФАЙЛОМ и через NUL) → наложение 39 путей данных из `'<тег|ревизия>:release/tree'` в КОРЕНЬ
#   (префикс снимается) → `write-tree`. Рабочее дерево репозитория не читается вовсе.
#
# ДВА ВХОДА, ОДНА ФОРМУЛА (приёмка 2.1.1, 18.10.2026). `-Tag <тег>` — выпуск по тегу; `-Rev <sha>` —
# тот же расчёт по РЕВИЗИИ, без создания объекта тега: так проверяется дерево 2.1.1 ДО тега. Различия
# ровно два и оба названы в выводе: подписи говорят «ревизия», а сверка «номер тега против <Version>»
# заменяется сверкой `<Version>` из `<sha>:src/DshPanel/DshPanel.csproj` с тремя CHANGELOG ревизии.
#
# МЕРА ДЛЯ `-Tree` — ДЕРЕВО КОММИТА, А НЕ ДИСК (Ф-9-2, 18.10.2026). Если названная папка —
# репозиторий (её корень), сверяется `<commit>^{tree}` и пары блобов из `ls-tree`: публикация и есть
# коммит. Раньше всегда хешировался ДИСК, и на отвергнутом коммите 2.1.0 (диск совпал, коммит — нет)
# проверка дала ЛОЖНОЕ «0». Для простой папки коммита нет — там содержимое считается объектами git
# (папка пишется `make-release-tree`), и вывод честно называет эту меру слабее.
#
# ЧТО ПРОВЕРЯЕТСЯ (каждая строка способна упасть — доказано мутациями серии CI и t1):
#   1. состав ОЖИДАНИЯ печатается числом: кода + данных = всего (на 2.1.0 — 414 + 30 = 444;
#      на ревизии 2.1.1 — 416 + 39 = 455);
#   2. отброшенное НАЗВАНО: «снято с индекса N, по именованным правилам M, незнакомых K»; K > 0 — код 1:
#      новый каталог кода в теге, которого разрешённый список не знает, обязан остановить выпуск,
#      а не потеряться молча (это проверка НА БУДУЩЕЕ, а не только на сегодня);
#   3. в ОЖИДАНИИ нет ни одного внутреннего файла (`AGENTS.md`, `docs/**`, `*.dshclaim`) — правило
#      «никогда» из ADR §5.2(1) проверяется, а не подразумевается;
#   4. тождество ДАННЫХ отдельной проверкой: 39 путей на диске в `<корень>\release\tree` против
#      `'<тег|ревизия>:release/tree'` (и ловля `*.dshclaim` на диске — локальный `.git\info\exclude`
#      тут не помощник: проверка обходит диск);
#   5. в архиве выпуска ни одного `*.dshclaim` (список путей `git archive` — то, что уедет);
#   6. при `-Tree`: собранное сверяется с ожиданием — ДЕРЕВО КОММИТА, если папка репозиторий, иначе
#      содержимое папки объектами git; двусторонние наборы путей и имена расхождений («нет в сборке»,
#      «лишний в сборке», «блоб разошёлся» — первые 12, дальше счётом); опись `release-tree.txt`
#      сверяется с деревом.
#
# ⚠️ ПАДЕНИЕ ИНСТРУМЕНТА — НЕ ПРИГОВОР ДЕРЕВУ (18.10.2026). Если свёртка ожидания не удалась (git
# отказал, индекс не снялся, объектов нет), печатается «СВЕРКА НЕ ПОСЧИТАНА: инструмент не смог…»
# и код 1 — НО НЕ «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО»: второе значит «сверка сделана, и она красная».
#
# Флаги `-c core.autocrlf=false -c core.safecrlf=false` — на КАЖДОМ вызове git, включая `archive`:
# без них `git archive` отдаёт рабочую форму файла (LF→CRLF) и проверка падает на исправном дереве.
#
# КОДЫ: 0 — совпало; 1 — расхождение (названо) или падение инструмента (названо отдельно);
# 2 — неприменимо (нет тега/ревизии, нет каталога `release\tree`, номер тега не сходится с `<Version>`,
# в ревизии нет `<Version>`, поддерево данных пусто, названы оба входа или ни одного).
#
# ЧЕГО НЕ ВИДИТ (говорит и в выводе): соответствия КОДА тегу эта проверка не доказывает — она
# доказывает, что фильтр по списку ничего не потерял и ничего лишнего не принёс; смысл текстов
# README/CHANGELOG не читается; сверка пар `(path, blob-sha)` с ПУБЛИЧНЫМ клоном не делается
# (для неё нужен публичный тег того же выпуска — решение владельца 4, отдельная работа).

param(
    [string]$Tag = '',
    [string]$Rev = '',
    [string]$Repo = '',
    [string]$Tree = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-tree-common.ps1')

$problems = @()
$tempRoot = $null
$selection = $null
$expected = $null
$archiveEntries = @()
$markersInArchive = @()
$tagPaths = @()
$treeReport = $null

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host 'неприменимо: git не найден в PATH — сверять нечем'
    exit 2
}
$repoRoot = $(if ($Repo) { (Resolve-Path -LiteralPath $Repo).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path })
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host ('неприменимо: в «' + $repoRoot + '» нет репозитория git')
    exit 2
}

# ── 0. Вход: РОВНО ОДИН из двух — тег или ревизия ──────────────────────────────────────────────
$namedInputs = @(@($Tag, $Rev) | Where-Object { $_ })
if ($namedInputs.Count -eq 0) {
    Write-Host 'неприменимо: вход не назван — назовите тег (-Tag vX.Y.Z) или ревизию (-Rev <sha>): ожидание берётся из объекта git, а не из рабочего дерева'
    exit 2
}
if ($namedInputs.Count -gt 1) {
    Write-Host ('неприменимо: названы ОБА входа (-Tag «' + $Tag + '» и -Rev «' + $Rev + '») — нужен ровно один: иначе непонятно, из чего считать ожидание')
    exit 2
}

$spec = $null          # имя объекта git для read-tree/ls-tree/show/archive: тег или полный sha
$commit = $null        # для -Rev: полный sha коммита
$projectVersion = $null
$changelogProblems = @()

if ($Tag) {
    $spec = $Tag
    Write-Host ('сверка дерева выпуска: ' + $repoRoot)
    Write-Host ('тег: ' + $Tag + '; ожидание — ПОЛНОЕ дерево, посчитанное из этого тега по разрешённому списку')
}
else {
    $commit = Resolve-ReleaseCommit -Repo $repoRoot -Rev $Rev
    if (-not $commit) {
        Write-Host ('неприменимо: в «' + $repoRoot + '» нет ревизии «' + $Rev + '» (git rev-parse --verify ' + $Rev + '^{commit} не нашёл её)')
        Write-Host 'это не «зелено» и не «красно»: нечего сверять'
        exit 2
    }
    $spec = $commit
    Write-Host ('сверка дерева выпуска: ' + $repoRoot)
    Write-Host ('ревизия: ' + $commit + ' (вход «' + $Rev + '»); тег НЕ создаётся; ожидание — ПОЛНОЕ дерево, посчитанное из этой ревизии по разрешённому списку')
}
Write-Host ''

# ── 1. Тег/ревизия и номер версии против <Version> ────────────────────────────────────────────
if ($Tag) {
    $tagCheck = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('rev-parse', '--verify', '--quiet', ('refs/tags/' + $Tag))
    if ($tagCheck.Code -ne 0) {
        Write-Host ('неприменимо: тега «' + $Tag + '» нет в репозитории (git rev-parse refs/tags/' + $Tag + ' — код ' + $tagCheck.Code + ')')
        Write-Host 'это не «зелено» и не «красно»: до появления тега проверять нечего'
        exit 2
    }

    $tagVersion = $null
    if ($Tag -match '(\d+\.\d+\.\d+)') { $tagVersion = $Matches[1] }
    $projectInTag = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('show', ($Tag + ':src/DshPanel/DshPanel.csproj'))
    if ($projectInTag.Code -ne 0) {
        Write-Host ('неприменимо: в теге «' + $Tag + '» нет src/DshPanel/DshPanel.csproj — сверять номер не с чем')
        exit 2
    }
    $found = [regex]::Matches($projectInTag.Text, '<Version>([^<]+)</Version>')
    if ($found.Count -eq 0) {
        Write-Host ('неприменимо: в DshPanel.csproj тега «' + $Tag + '» нет <Version>')
        exit 2
    }
    $projectVersion = $found[0].Groups[1].Value.Trim()
    if (-not $tagVersion) {
        Write-Host ('неприменимо: в имени тега «' + $Tag + '» нет номера версии вида X.Y.Z')
        exit 2
    }
    if ($tagVersion -ne $projectVersion) {
        Write-Host ('неприменимо: номер тега «' + $Tag + '» (' + $tagVersion + ') не совпадает с <Version> в теге (' + $projectVersion + ')')
        Write-Host 'правило проекта: тег vX.Y.Z ставится на ревизию, где <Version> уже поднят. Такой тег выпуском не считается.'
        exit 2
    }
}
else {
    # ⚠️ В РЕЖИМЕ `-Rev` ТЕГА НЕТ, и сверять «номер тега против <Version>» не с чем. Замена — не
    # ослабление, а другой вопрос: версия берётся из САМОЙ ревизии, а три CHANGELOG обязаны её знать.
    $projectVersion = Get-ReleaseProjectVersion -Repo $repoRoot -Spec $commit
    if (-not $projectVersion) {
        Write-Host ('неприменимо: в ревизии «' + $commit + '» нет src/DshPanel/DshPanel.csproj или в нём нет <Version> — версию взять неоткуда')
        exit 2
    }
    $changelogProblems = @(Get-ReleaseChangelogProblems -Repo $repoRoot -Spec $commit -Version $projectVersion)
    foreach ($item in $changelogProblems) {
        $problems += ('версия ревизии: ' + $projectVersion + ' — ' + $item)
    }
    $changelogReport = if ($changelogProblems.Count -eq 0) {
        'три CHANGELOG знают «## ' + $projectVersion + '» первым разделом и он непустой'
    } else {
        'три CHANGELOG разошлись с версией — ' + $changelogProblems.Count + ' замечаний (ниже)'
    }
    Write-Host ('версия из ревизии: ' + $projectVersion + ' (src/DshPanel/DshPanel.csproj); ' + $changelogReport)
}

# ── 2. Данные выпуска: поддерево тега/ревизии и тождество данных на диске ──────────────────────
$dataOverlay = Get-ReleaseDataOverlay -Repo $repoRoot -Spec $spec
if ($null -eq $dataOverlay) {
    Write-Host ('неприменимо: в «' + $spec + '» нет каталога release/tree — источник данных выпуска отсутствует')
    exit 2
}
if ($dataOverlay.Count -eq 0) {
    Write-Host ('неприменимо: поддерево «' + $spec + ':release/tree» ПУСТО — данных выпуска нет')
    exit 2
}
$dataNames = @($dataOverlay.Keys | Sort-Object)
$missingData = @($script:ReleaseDataPaths | Where-Object { $dataNames -cnotcontains $_ } | Sort-Object)
$extraData = @($dataNames | Where-Object { $script:ReleaseDataPaths -cnotcontains $_ } | Sort-Object)
foreach ($name in $missingData) { $problems += ('данные: в источнике НЕТ файла данных выпуска — ' + $name) }
foreach ($name in $extraData) {
    $why = Get-ReleaseDiscardRule -Path $name
    if ($why) { $problems += ('данные: в поддереве лишний путь — ' + $name + ' (' + $why + ')') }
    else { $problems += ('данные: в поддереве лишний путь вне разрешённого списка — ' + $name) }
}

# Тождество данных НА ДИСКЕ (отдельная проверка, не подменяется полным деревом).
$diskRoot = Join-Path $repoRoot 'release\tree'
$diskReport = 'каталога release\tree в этом дереве нет — сверка диска не делается'
if (Test-Path -LiteralPath $diskRoot) {
    $diskNames = @(Get-ChildItem -LiteralPath $diskRoot -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($diskRoot.Length).TrimStart('\').Replace('\', '/') } | Sort-Object)
    $extraOnDisk = @($diskNames | Where-Object { $dataNames -cnotcontains $_ })
    $missingOnDisk = @($dataNames | Where-Object { $diskNames -cnotcontains $_ })
    foreach ($name in $extraOnDisk) {
        # ⚠️ Для пути, который САМ ЕСТЬ в списке данных выпуска, правило отброса не называется
        # (нашлось 18.10.2026, когда данных стало 39): `docs/**` — внутреннее для КОДА, но кадры
        # `docs/screenshots/**` и есть данные выпуска, и подпись «(внутренние docs/**)» на кадре
        # вводила бы в заблуждение.
        $why = if (Test-ReleaseDataPath -Path $name) { $null } else { Get-ReleaseDiscardRule -Path $name }
        $problems += ('данные: на диске лишний файл, которого нет в источнике — release\tree\' + ($name -replace '/', '\') + $(if ($why) { ' (' + $why + ')' } else { '' }))
    }
    foreach ($name in $missingOnDisk) {
        $problems += ('данные: на диске нет файла, который есть в источнике — release\tree\' + ($name -replace '/', '\'))
    }
    $diskReport = ('диск ' + $diskNames.Count + ' / источник ' + $dataNames.Count + ' — ' + $(if ($extraOnDisk.Count -eq 0 -and $missingOnDisk.Count -eq 0) { 'состав совпал' } else { 'лишних ' + $extraOnDisk.Count + ', не хватает ' + $missingOnDisk.Count }))
}

# ── 3. Ожидание: ПОЛНОЕ дерево выпуска из объекта git ───────────────────────────────────────────
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('dsh-release-tree-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    $tagPathsResult = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('ls-tree', '-r', '--name-only', $spec)
    if ($tagPathsResult.Code -ne 0) { throw ('git ls-tree не отдал пути источника: ' + $tagPathsResult.Text) }
    $tagPaths = @($tagPathsResult.Lines | Where-Object { $_ })

    $selection = Get-ReleaseSelection -TagPaths $tagPaths
    foreach ($name in $selection.Unknown) {
        $problems += ('НЕЗНАКОМЫЙ отброшенный путь: ' + $name + ' — разрешённый список его не знает; новый каталог кода обязан остановить выпуск, а не потеряться молча')
    }

    $expected = Get-ExpectedFullTree -Repo $repoRoot -Spec $spec -Selection $selection -DataOverlay $dataOverlay -TempRoot $tempRoot

    # Ожидание не имеет права содержать внутренние файлы: правило «никогда» проверяется, а не подразумевается.
    # ⚠️ Проверка НЕЗАВИСИМА от классификатора (`Get-ReleaseInternalViolation`, а не `Get-ReleaseDiscardRule`):
    # иначе ошибка в правиле прячется за собой — мутация CI-11 (фильтр по голому имени AGENTS.md) так
    # и осталась бы зелёной. Данные выпуска — исключение по определению: они лежат в `docs/…` и `README.md`.
    foreach ($name in @($expected.Paths | Where-Object { $null -ne (Get-ReleaseInternalViolation -Path $_) } | Sort-Object)) {
        $problems += ('в ОЖИДАЕМОМ дереве внутренний файл — ' + $name + ' (' + (Get-ReleaseInternalViolation -Path $name) + ')')
    }
    # Каждый путь кода, существующий в источнике, обязан быть в ожидании; каждый путь данных — тоже.
    foreach ($name in $selection.Kept) {
        if ($expected.Paths -cnotcontains $name) { $problems += ('выпал путь кода: ' + $name + ' — он есть в источнике и в разрешённом списке, но его нет в ожидаемом дереве') }
    }
    foreach ($name in $script:ReleaseDataPaths) {
        if ($expected.Paths -cnotcontains $name) { $problems += ('выпал путь данных: ' + $name) }
    }

    # ── 4. Архив выпуска: ни одного служебного маркера ─────────────────────────────────────────
    $tarPath = Join-Path $tempRoot 'full.tar'
    $archive = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('archive', '--format=tar', $spec, '-o', $tarPath)
    if ($archive.Code -ne 0) { throw ('git archive не сработал (код ' + $archive.Code + '): ' + $archive.Text) }
    $archiveEntries = @(Get-ReleaseArchiveEntries -TarPath $tarPath)
    $markersInArchive = @($archiveEntries | Where-Object { $_ -match '\.dshclaim$' } | Sort-Object)
    foreach ($name in $markersInArchive) {
        $problems += ('в архиве выпуска служебный маркер заявок рабочей области — ' + $name)
    }

    # ── 5. Собранное против ожидания: МЕРА — ДЕРЕВО КОММИТА, если папка репозиторий (Ф-9-2) ────
    if ($Tree) {
        $treeFolder = (Resolve-Path -LiteralPath $Tree).Path
        $repoTree = Get-RepositoryTree -Folder $treeFolder
        $measureNote = $null
        if ($repoTree) {
            # Публикация — это КОММИТ. Диск может быть хоть CRLF, хоть LF (Ф-9-2, опыт 8):
            # сверяются объекты коммита, а не байты на диске.
            $built = $repoTree
            $measure = 'ДЕРЕВО КОММИТА ' + $repoTree.Commit + ' (^{tree} ' + $repoTree.Tree + ')'
            $measureNote = 'сверяются блобы КОММИТА (ls-tree по его дереву), диск не хешируется'
            if ($repoTree.DirtyLines -gt 0) {
                Write-Host ('ВНИМАНИЕ: рабочее дерево «' + $treeFolder + '» не чисто — ' + $repoTree.DirtyLines + ' незакоммиченных строк; сверяется КОММИТ, а не диск')
            }
            elseif ($repoTree.DirtyLines -lt 0) {
                Write-Host ('ВНИМАНИЕ: состояние рабочего дерева «' + $treeFolder + '» прочитать не удалось; сверяется КОММИТ')
            }
        }
        else {
            $built = Get-FolderTreeHash -Repo $repoRoot -Folder $treeFolder -TempRoot $tempRoot
            $measure = 'СОДЕРЖИМОЕ ПАПКИ'
            $measureNote = 'коммита нет (папка не корень репозитория): содержимое посчитано объектами git через временный индекс, ДЕРЕВО КОММИТА это не доказывает — стройте публикацию коммитом'
        }

        $treeMatches = ($built.Tree -eq $expected.Tree)
        if (-not $treeMatches) {
            $problems += ('ХЕШ ДЕРЕВА не равен ожидаемому: ожидание ' + $expected.Tree + ', сборка ' + $built.Tree)
        }
        $missingInBuild = @($expected.Paths | Where-Object { $built.Paths -cnotcontains $_ } | Sort-Object)
        $extraInBuild = @($built.Paths | Where-Object { $expected.Paths -cnotcontains $_ } | Sort-Object)
        $shown = 0
        foreach ($name in $missingInBuild) {
            $shown++
            if ($shown -le 12) { $problems += ('  нет в сборке — ' + $name) }
        }
        if ($missingInBuild.Count -gt 12) { $problems += ('  … всего нет в сборке: ' + $missingInBuild.Count + ' (показаны первые 12)') }
        $shown = 0
        foreach ($name in $extraInBuild) {
            $shown++
            $why = Get-ReleaseDiscardRule -Path $name
            if ($shown -le 12) { $problems += ('  лишний в сборке — ' + $name + $(if ($why) { ' (' + $why + ')' } else { '' })) }
        }
        if ($extraInBuild.Count -gt 12) { $problems += ('  … всего лишних в сборке: ' + $extraInBuild.Count + ' (показаны первые 12)') }

        $diffBlobs = @()
        foreach ($name in $expected.Paths) {
            if ($built.Paths -cnotcontains $name) { continue }
            if ($expected.Entries[$name].Blob -ne $built.Entries[$name].Blob) { $diffBlobs += $name }
        }
        $shown = 0
        foreach ($name in $diffBlobs) {
            $shown++
            if ($shown -le 12) {
                $problems += ('  блоб разошёлся — ' + $name + ': ожидание ' + $expected.Entries[$name].Blob.Substring(0, 12) + '…, сборка ' + $built.Entries[$name].Blob.Substring(0, 12) + '…')
            }
        }
        if ($diffBlobs.Count -gt 12) { $problems += ('  … всего разошлось блобов: ' + $diffBlobs.Count + ' (показаны первые 12)') }

        # ── 6. Опись рядом с деревом: мера — ОБЪЕКТ GIT ───────────────────────────────────────
        $manifestPath = Join-Path (Split-Path -Parent $treeFolder) 'release-tree.txt'
        $manifestReport = 'описи рядом нет — сверять её не с чем'
        if (Test-Path -LiteralPath $manifestPath) {
            $declared = @([IO.File]::ReadAllLines($manifestPath) | Where-Object { $_ -and -not $_.StartsWith('#') })
            $declaredMap = @{}
            $badForm = 0
            foreach ($line in $declared) {
                $parts = $line -split '\|'
                if ($parts.Count -lt 5) { $badForm++; continue }
                $declaredMap[$parts[0]] = $parts
            }
            if ($badForm -gt 0) { $problems += ('опись: строк не по форме «путь|байт объекта git|sha256 объекта git|blob-sha1|байт на диске» — ' + $badForm) }
            if ($declaredMap.Count -ne $expected.Paths.Count) {
                $problems += ('опись: в ней ' + $declaredMap.Count + ' строк, а в ожидаемом дереве ' + $expected.Paths.Count + ' путей')
            }
            $blobInfo = Get-ReleaseBlobInfo -Repo $repoRoot -Shas @($expected.Entries.Values | ForEach-Object { $_.Blob }) -TempRoot $tempRoot
            $shown = 0
            foreach ($name in ($expected.Paths | Sort-Object)) {
                if (-not $declaredMap.ContainsKey($name)) {
                    $shown++
                    if ($shown -le 12) { $problems += ('опись: файла нет в описи — ' + $name) }
                    continue
                }
                $row = $declaredMap[$name]
                $blob = $expected.Entries[$name].Blob
                $info = $blobInfo[$blob]
                if ([int64]$row[1] -ne $info.Size) { $problems += ('опись: размер ОБЪЕКТА GIT не совпал — ' + $name + ' (в описи ' + $row[1] + ', в источнике ' + $info.Size + ')') }
                if ($row[2] -ne $info.Sha256) { $problems += ('опись: SHA-256 ОБЪЕКТА GIT не совпал — ' + $name) }
                if ($row[3] -ne $blob) { $problems += ('опись: blob-sha1 не совпал — ' + $name) }
                if ($built.Paths -ccontains $name) {
                    # ⚠️ Колонка «байт на диске» — про ДИСК, а в режиме коммита мера — коммит: под
                    # `core.autocrlf=true` та же рабочая копия длиннее на пары CRLF, и сверка по диску
                    # дала бы КРАСНОЕ на исправном дереве (Ф-9-2, опыт 8). Поэтому она сверяется
                    # только там, где диск и есть мера — у простой папки.
                    if (-not $repoTree) {
                        $diskBytes = (Get-Item -LiteralPath (Join-Path $treeFolder $name)).Length
                        if ([int64]$row[4] -ne $diskBytes) { $problems += ('опись: байт на диске не совпал — ' + $name) }
                    }
                }
            }
            $shown = 0
            foreach ($name in ($declaredMap.Keys | Sort-Object)) {
                if ($expected.Paths -cnotcontains $name) {
                    $shown++
                    if ($shown -le 12) { $problems += ('опись: называет путь, которого нет в ожидаемом дереве — ' + $name) }
                }
            }
            $manifestReport = $manifestPath + ' — сверена с деревом по объектам git (' + $declaredMap.Count + ' строк' + $(if ($repoTree) { '; колонка «байт на диске» не сверяется — мера коммита' } else { '' }) + ')'
        }
        else {
            # ⚠️ ПРОПАВШИЙ ВХОД (t15, 17.10.2026): `make-release-tree` пишет опись ВСЕГДА (по умолчанию —
            # рядом с деревом), поэтому у собранного дерева описи не бывает «просто так». Замер до правки:
            # опись убрана — строка «описи рядом нет — сверять её не с чем» и код 0. После — код 1 с путём.
            $problems += ('опись: нет файла ' + $manifestPath + ' — у собранного дерева опись есть всегда: её пишет make-release-tree')
        }

        $treeReport = [pscustomobject]@{
            Folder    = $treeFolder
            Count     = $built.Paths.Count
            Tree      = $built.Tree
            Matches   = $treeMatches
            Missing   = $missingInBuild.Count
            Extra     = $extraInBuild.Count
            DiffBlobs = $diffBlobs.Count
            Manifest  = $manifestReport
            Measure   = $measure
            MeasureNote = $measureNote
        }
    }
} catch {
    Write-Host ''
    Write-Host ('СВЕРКА НЕ ПОСЧИТАНА: инструмент не смог свести ожидание — ' + $_.Exception.Message)
    Write-Host 'это ПАДЕНИЕ ИНСТРУМЕНТА, а не «дерево не совпало» и не «неприменимо»: причина названа выше; пустое дерево и пустой индекс успехом не считаются'
    exit 1
} finally {
    if (-not $KeepTemp -and $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── Вывод ──────────────────────────────────────────────────────────────────────────────────────
# ⚠️ КОНВЕНЦИЯ СЧЁТА НАЗВАНА (владелец 17.10.2026). «Снято с индекса» — это `rm --cached`, а путь,
# который есть и в источнике, и в данных, — ЗАМЕНА, а не «лишний»: на 2.1.0 он один (README.md).
# Второе честное число — «не уехало содержимым»: к снятым добавляется внутреннее содержимое заменённого.
$sourceWord = if ($Tag) { 'теге' } else { 'ревизии' }
$discardedByRule = ($selection.ByRule | ForEach-Object { $_.Rule + ' = ' + $_.Count }) -join ', '
$replacements = @($selection.Replacements)
$newFromOverlay = $expected.DataCount - $replacements.Count
Write-Host ('путей в ' + $sourceWord + ':       ' + $tagPaths.Count)
Write-Host ('ожидаемое ПОЛНОЕ дерево: ' + $expected.Tree)
Write-Host ('  кода ' + $expected.CodeCount + ' + данных ' + $expected.DataCount + ' = всего ' + $expected.Total + ' путей')
Write-Host ('снято с индекса (rm --cached -f): ' + $selection.Discarded.Count + ' — по именованным правилам ' + ($selection.Discarded.Count - $selection.Unknown.Count) + ', НЕЗНАКОМЫХ ' + $selection.Unknown.Count)
if ($discardedByRule) { Write-Host ('  правила: ' + $discardedByRule) }
Write-Host ('наложено данных:          ' + $expected.DataCount + ' = ' + $newFromOverlay + ' новых + ' + $replacements.Count + ' замена' + $(if ($replacements.Count -gt 0) { ' (' + ($replacements -join ', ') + ' — внутренний блоб заменён данными, путь остаётся один)' } else { '' }))
$contentNotReleased = $selection.Discarded.Count + $replacements.Count
Write-Host ('не уехало содержимым:     ' + $contentNotReleased + ' = ' + $selection.Discarded.Count + ' снятых + внутреннее содержимое ' + $replacements.Count + ' заменённого пути')
Write-Host ('счёт:                     ' + $tagPaths.Count + ' − ' + $selection.Discarded.Count + ' + ' + $newFromOverlay + ' = ' + ($tagPaths.Count - $selection.Discarded.Count + $newFromOverlay) + ' (ожидание ' + $expected.Total + ')')
Write-Host ('данные на диске:          ' + $diskReport)
Write-Host ('архив выпуска:            путей ' + $archiveEntries.Count + ', служебных маркеров *.dshclaim: ' + $markersInArchive.Count)
if ($treeReport) {
    Write-Host ('двусторонние наборы:      ожидание ⊆ сборка: ' + $(if ($treeReport.Missing -eq 0) { 'да' } else { 'НЕТ (' + $treeReport.Missing + ')' }) + '; сборка ⊆ ожидание: ' + $(if ($treeReport.Extra -eq 0) { 'да' } else { 'НЕТ (' + $treeReport.Extra + ')' }) + '; блобов разошлось: ' + $treeReport.DiffBlobs)
    Write-Host ('собранное дерево:         ' + $treeReport.Folder)
    Write-Host ('  путей ' + $treeReport.Count + ', хеш ' + $treeReport.Tree + ' — ' + $(if ($treeReport.Matches) { 'СОВПАЛ с ожидаемым' } else { 'НЕ СОВПАЛ' }))
    Write-Host ('  мера: ' + $treeReport.Measure + ' — ' + $treeReport.MeasureNote)
    Write-Host ('  опись: ' + $treeReport.Manifest)
} else {
    Write-Host 'двусторонние наборы:      не считаны — собранное дерево не названо (-Tree)'
}

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host ('ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО: ' + $problems.Count + ' замечаний')
    foreach ($item in $problems) { Write-Host ('  ' + $item) }
    Write-Host ''
    Write-Host 'ЧЕГО ЭТА ПРОВЕРКА НЕ ВИДИТ: соответствия КОДА тегу (она доказывает, что фильтр по списку ничего не потерял и ничего лишнего не принёс), смысла текстов README/CHANGELOG, пар (path, blob-sha) с публичным клоном.'
    exit 1
}

Write-Host ''
if ($treeReport) {
    Write-Host ('тождество доказано: ожидание (полное дерево из ' + $(if ($Tag) { 'тега' } else { 'ревизии' }) + ') = ' + $treeReport.Measure + ', ни один путь не выпал и лишний не попал')
} else {
    Write-Host 'ожидание посчитано и внутренних файлов в нём нет; СБОРКА НЕ НАЗВАНА (-Tree) — тождество НЕ сверялось'
}
Write-Host 'ЧЕСТНО О ГРАНИЦЕ: проверка доказывает СОСТАВ и БЛОБЫ дерева выпуска против разрешённого списка; смысл текстов и публикацию (пары с публичным клоном) она не проверяет.'
exit 0
