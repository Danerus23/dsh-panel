# make-release-tree.ps1 — СБОРКА ПОЛНОГО ДЕРЕВА ВЫПУСКА ИЗ ТЕГА (ADR-001 §5.2; правка владельца 17.10.2026).
#
#   pwsh -NoProfile -File .\tools\make-release-tree.ps1 -Tag vX.Y.Z
#   pwsh -NoProfile -File .\tools\make-release-tree.ps1 -Tag vX.Y.Z -Out C:\Temp\release-out-2.1.0
#   pwsh -NoProfile -File .\tools\make-release-tree.ps1 -Rev <sha> -Out C:\Temp\release-out-2.1.1
#
# ⚠️ ЧТО ИЗМЕНИЛОСЬ 18.10.2026 (дефект найден владельцем, воспроизведён капитаном). Раньше инструмент
# ЛОЖНО ПАДАЛ, когда `HEAD` впереди тега: общая часть считала ожидание через `git rm --cached` БЕЗ `-f`,
# индекс был из тега, `HEAD` — другой коммит, git отказывал («staged content different from both the
# file and the HEAD»), а инструмент печатал «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО» — то есть падение ИНСТРУМЕНТА
# выдавал за расхождение ДЕРЕВА. Теперь: `-f` стоит, а падение инструмента называется своим именем.
#
# ВХОД — ТЕГ ИЛИ РЕВИЗИЯ (приёмка 2.1.1, 18.10.2026). `-Rev <sha>` собирает то же ПОЛНОЕ дерево из
# РЕВИЗИИ, не создавая объекта тега: ожидание считается тем же временным индексом, вывод говорит
# «ревизия», а сверка «номер тега против <Version>» заменяется сверкой `<Version>` из ревизии с тремя
# CHANGELOG. Нужен, чтобы проверить 2.1.1 ДО тега.
#
# ЧТО ИЗМЕНИЛОСЬ ПРОТИВ РЕДАКЦИИ ДО 17.10.2026. Раньше `-Out` получал только ДАННЫЕ выпуска (тогда
# 30 путей; с 18.10.2026 — 39, решение владельца о трёх новых видах кадров).
# Теперь `-Out` — **полное публичное дерево**: код по разрешённому списку (ADR §5.2 п. 1) плюс 39 путей
# данных в КОРНЕ (префикс `release/tree/` снят) — то есть ровно то, что уедет в публичный репозиторий
# одним коммитом. Всё берётся ИЗ ОБЪЕКТОВ ТЕГА/РЕВИЗИИ; рабочее дерево репозитория не читается вовсе.
#
# ОЖИДАНИЕ — тот же хеш, что считает `check-release-tree.ps1` (`Get-ExpectedFullTree` в общей части):
# временный индекс → `read-tree <тег>` → отброс всего вне списка → наложение 39 путей данных → `write-tree`.
# Сборка сверяется с ожиданием ОДИН раз — по ВРЕМЕННОМУ каталогу, ДО первой записи в `-Out` (правка F5,
# 17.10.2026: раньше финальная сверка шла по `-Out` уже после копирования, и при провале на диске
# оставались и дерево, и опись). После переноса сверяется КОПИЯ: пути, размеры, байты (SHA-256).
#
# ПОРЯДОК (все проверки — ДО первой записи в `-Out`; при провале на диске не остаётся ни дерева, ни описи):
#   1. тег/ревизия существует; для тега номер версии из имени тега = `<Version>` в теге, для ревизии —
#      `<Version>` из ревизии и три CHANGELOG ревизии с разделом `## <версия>` первым и непустым (иначе код 2/1);
#   2. рабочее дерево чистое (`git status --porcelain` пуст) — выпускают из тега/ревизии, а не из «почти того же»;
#   3. `ls-tree -r <тег|ревизия>` → классификация: что код, что данные, что отброшено и по какому правилу;
#      НЕЗНАКОМЫЙ отброшенный путь (новый каталог кода) — отказ кодом 1 с именами;
#   4. ожидаемое полное дерево из объекта git → `$expectedFull`;
#   5. `check-release-tree -Tag <тег>` (или `-Rev <sha>`) — состав/архив/данные обязаны быть зелёными (код 0);
#   6. сборка во ВРЕМЕННОМ каталоге: `git archive <тег|ревизия>` → убрать всё вне списка → наложить данные
#      (`git archive '<тег|ревизия>:release/tree'` в КОРЕНЬ); пути `-Out`/`-Manifest` проверяются ЗДЕСЬ же,
#      чтобы отказы стояли до записи;
#   7. опись `release-tree.txt` пишется РЯДОМ с временным деревом, и ФИНАЛЬНАЯ сверка
#      `check-release-tree -Tree <временное дерево>` идёт по нему — ДО копирования (код 1 при расхождении);
#   8. только теперь перенос в `-Out` и описи на её место (РЯДОМ с деревом, не внутри: внутри она стала бы
#      лишним файлом выпуска и сломала бы сверку тождества), затем сверка КОПИИ (пути, размеры, SHA-256);
#      при любом сбое переноса записанное УБИРАЕТСЯ — на диске снова ничего;
#   9. печатает следующие команды (коммит в публичный клон, тег, push) — и НЕ выполняет их.
#
# ⚠️ ПАДЕНИЕ ИНСТРУМЕНТА — СВОИМ ИМЕНЕМ (18.10.2026). Вся сборка обёрнута так, что техническая авария
# (git отказал, tar не распаковал, индекс не снялся) печатается как «СБОРКА НЕ СДЕЛАНА: инструмент…»,
# а не как расхождение дерева и не сырой записью исключения PowerShell: у двух разных бед разные лекарства.
#
# МЕРА В ОПИСИ — ОБЪЕКТ GIT (требование владельца 17.10.2026): `git cat-file -s` и SHA-256 по байтам
# блоба; байт на диске — отдельная подписанная колонка и в сверке не участвует. Причина: на этой
# машине `core.autocrlf=true`, блоб хранит LF, рабочая копия получает CRLF — опись по диску печатала
# бы НЕ ТО, что сравнивается.
#
# КОДЫ: 0 — дерево собрано и проверено; 1 — расхождение (с путём и причиной) ИЛИ падение инструмента
# (тоже своим именем); 2 — неприменимо (нет тега/ревизии, каталога `release\tree` нет, номер тега не
# сходится с `<Version>`, в ревизии нет `<Version>`, названы оба входа или ни одного).
# «Неприменимо» — не «зелено» и не «красно».
#
# ⚠️ ПРАВКА 20.10.2026 (долг приёмки 2.3.0). Отказ «-Out внутри рабочего дерева репозитория» стоял
# на сравнении строк БЕЗ разделителя каталога и ЛОЖНО срабатывал на соседнем каталоге с тем же
# префиксом имени (`<repo>-sibling`); V 2.3.0 обходила это переименованием. Теперь сравнение —
# `Test-PathInside` (общая функция ниже): нормализация `[IO.Path]::GetFullPath` + сравнение
# с `DirectorySeparatorChar`, поэтому соседний префикс разрешён, а настоящий путь внутри
# репозитория по-прежнему получает отказ. Тем же правилом проверяется опись против дерева выпуска.
#
# ЧЕГО ЭТОТ ИНСТРУМЕНТ НЕ ДЕЛАЕТ (названо, а не спрятано): не ставит тег и не пушит (только печатает
# команды); не собирает установщик и архив раздачи (это следующий шаг выпуска); не проверяет смысл
# README/CHANGELOG и свежесть кадров; не сверяет пары `(path, blob-sha)` с публичным клоном.

param(
    [string]$Tag = '',
    [string]$Rev = '',
    [string]$Out = '',
    [string]$Manifest = '',
    [string]$Repo = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-tree-common.ps1')

$problems = @()
$tempRoot = $null

# ── «Путь лежит ВНУТРИ каталога» — сравнение С РАЗДЕЛИТЕЛЕМ (правка 2.3.1, 20.10.2026) ───────────
#
# ⚠️ ОТ КАКОГО СЛУЧАЯ. Здесь стояло `$Out.StartsWith($repoFull, …)` — сравнение БЕЗ разделителя
# каталога. Соседний каталог с тем же префиксом имени (`<repo>-sibling`, `<repo>-x`) получал ЛОЖНЫЙ
# отказ «-Out внутри рабочего дерева репозитория», и приёмка 2.3.0 обходила его переименованием.
# `C:\work\repo` — НЕ родитель `C:\work\repo-sibling`, но сравнение строк без разделителя этого
# не знает. Та же болезнь была у проверки описи: `<out>-manifest.txt` считался «внутри» дерева.
#
# ⚠️ РАЗДЕЛИТЕЛИ. Оба пути нормализуются `[IO.Path]::GetFullPath` (на Windows `/` становится `\`),
# а хвостовой разделитель снимается: «C:\work\repo\» и «C:\work\repo» — один каталог. Поэтому
# «C:\…/repo-sibling» и «C:\…\repo-sibling» ведут себя одинаково: отказ не зависит от того, каким
# разделителем набран путь.
#
# ⚠️ ЧЕГО ФУНКЦИЯ НЕ ДЕЛАЕТ: не разворачивает симлинки/junction (ссылка внутрь репозитория не
# распознаётся как «внутри») и сравнивает регистр так же, как сравнивал прежний код на Windows
# (OrdinalIgnoreCase). Для отказа «-Out внутри рабочего дерева» этого достаточно: предмет — путь
# на диске, а не физический объект за ссылкой.
function Test-PathInside {
    param(
        [string]$Path,
        [string]$Root
    )

    if (-not $Path -or -not $Root) { return $false }

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')

    if ($rootFull.Length -eq 0) { return $false }

    # Корень диска («C:\») после TrimEnd стал бы «C:» — префикс с разделителем не собрать.
    # Такой корень содержит всё на своём диске.
    if ($rootFull.Length -eq 2 -and $rootFull[1] -eq ':') { return $true }

    if ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }

    return $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host 'неприменимо: git не найден в PATH — собирать не из чего'
    exit 2
}

$repoRoot = $(if ($Repo) { (Resolve-Path -LiteralPath $Repo).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path })
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host ('неприменимо: в «' + $repoRoot + '» нет репозитория git')
    exit 2
}

$namedInputs = @(@($Tag, $Rev) | Where-Object { $_ })
if ($namedInputs.Count -eq 0) {
    Write-Host 'неприменимо: вход не назван — назовите тег (-Tag vX.Y.Z) или ревизию (-Rev <sha>): вход выпуска называется объектом git, а не рабочей копией'
    exit 2
}
if ($namedInputs.Count -gt 1) {
    Write-Host ('неприменимо: названы ОБА входа (-Tag «' + $Tag + '» и -Rev «' + $Rev + '») — нужен ровно один')
    exit 2
}

$checkTool = Join-Path $PSScriptRoot 'check-release-tree.ps1'
if (-not (Test-Path -LiteralPath $checkTool)) {
    Write-Host ('неприменимо: рядом нет check-release-tree.ps1 — без него сборка не проверяется, а непроверенную не отдаём: ' + $checkTool)
    exit 2
}

# ── 1. Тег/ревизия и номер версии ───────────────────────────────────────────────────────────────
$spec = $null
$commit = $null
$projectVersion = $null
$changelogReport = $null

if ($Tag) {
    $spec = $Tag
    $tagCheck = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('rev-parse', '--verify', '--quiet', ('refs/tags/' + $Tag))
    if ($tagCheck.Code -ne 0) {
        Write-Host ('неприменимо: тега «' + $Tag + '» нет в репозитории — дерево выпуска собирается ТОЛЬКО из тега или ревизии')
        exit 2
    }

    $tagVersion = $null
    if ($Tag -match '(\d+\.\d+\.\d+)') { $tagVersion = $Matches[1] }
    if (-not $tagVersion) {
        Write-Host ('неприменимо: в имени тега «' + $Tag + '» нет номера версии вида X.Y.Z — сверять <Version> не с чем')
        exit 2
    }
    $projectVersion = Get-ReleaseProjectVersion -Repo $repoRoot -Spec $spec
    if (-not $projectVersion) {
        Write-Host ('неприменимо: в теге «' + $Tag + '» нет src/DshPanel/DshPanel.csproj или в нём нет <Version> — версию выпуска взять неоткуда')
        exit 2
    }
    if ($projectVersion -ne $tagVersion) {
        Write-Host ('неприменимо: номер тега «' + $Tag + '» (' + $tagVersion + ') не совпадает с <Version> в теге (' + $projectVersion + ')')
        Write-Host 'правило проекта: тег vX.Y.Z ставится на ревизию, где <Version> уже поднят до X.Y.Z. Такой тег выпуском не считается.'
        exit 2
    }
}
else {
    # Режим приёмки 2.1.1: тега ещё нет, поэтому версию даёт сама ревизия, а историю — три CHANGELOG.
    $commit = Resolve-ReleaseCommit -Repo $repoRoot -Rev $Rev
    if (-not $commit) {
        Write-Host ('неприменимо: в «' + $repoRoot + '» нет ревизии «' + $Rev + '» — дерево выпуска собирается только из объекта git')
        exit 2
    }
    $spec = $commit
    $projectVersion = Get-ReleaseProjectVersion -Repo $repoRoot -Spec $commit
    if (-not $projectVersion) {
        Write-Host ('неприменимо: в ревизии «' + $commit + '» нет src/DshPanel/DshPanel.csproj или в нём нет <Version> — версию выпуска взять неоткуда')
        exit 2
    }
    $changelogProblems = @(Get-ReleaseChangelogProblems -Repo $repoRoot -Spec $commit -Version $projectVersion)
    if ($changelogProblems.Count -gt 0) {
        Write-Host ''
        Write-Host ('СБОРКА НЕ ПОЙДЁТ: три CHANGELOG ревизии не знают её версии — ' + $changelogProblems.Count + ' замечаний (версия ' + $projectVersion + ')')
        Write-Host 'в режиме -Rev сверки «номер тега против <Version>» нет; её место занимает эта проверка, и она красная'
        foreach ($item in $changelogProblems) { Write-Host ('  ' + $item) }
        exit 1
    }
    $changelogReport = 'три CHANGELOG знают «## ' + $projectVersion + '» первым разделом и он непустой'
}

# Подписи: одна и та же формула, но вывод обязан честно говорить, ЧТО было входом (тег или ревизия).
$sourceWord = if ($Tag) { 'теге' } else { 'ревизии' }
$sourceLabel = if ($Tag) { 'тег ' + $Tag } else { 'ревизия ' + $commit }

# ── 2. Рабочее дерево чистое ───────────────────────────────────────────────────────────────────
# ⚠️ ЭТОТ ВЫЗОВ — БЕЗ ПРИНУДИТЕЛЬНЫХ ФЛАГОВ ПЕРЕВОДОВ СТРОК (дефект найден 17.10.2026 на `ad69c85`).
# `git status` сравнивает РАБОЧУЮ форму файла с блобом индекса, и настройка репозитория
# (`core.autocrlf=true`) — часть этого сравнения. Принудительный `-c core.autocrlf=false` заставлял
# сравнивать CRLF рабочей копии с LF блобом: у свежего клона stat-записи индекса «racy», git сравнивал
# СОДЕРЖИМОЕ — и «изменённым» оказывалось почти всё дерево. Выпуск отказывал на ЧИСТОМ клоне
# («рабочее дерево не чисто»), то есть был невозможен вовсе. Число «изменённых» при этом зависит от
# stat конкретного клона (замеры: 440 сразу после клонирования, 11 после `fetch`+`reset --hard`) —
# поэтому лечится не числом и не `update-index --refresh` (он обновляет stat, но любой следующий
# `touch` возвращает ложные «изменения»), а САМИМ ВЫЗОВОМ: рабочее дерево сверяется настройками
# репозитория, флаги остаются только на операциях с блобами (`archive`, `add`, `write-tree`, `ls-tree`,
# `cat-file`, `read-tree`, `rm --cached`, `update-index`).
$status = Invoke-WorktreeGit -Repo $repoRoot -GitArgs @('status', '--porcelain')
if ($status.Code -ne 0) { throw ('git status не сработал: ' + $status.Text) }
$dirty = @($status.Lines | Where-Object { $_ })
if ($dirty.Count -gt 0) {
    Write-Host ('СБОРКА НЕ ПОЙДЁТ: рабочее дерево не чисто — ' + $dirty.Count + ' незакоммиченных изменения(й)')
    Write-Host 'выпускают из ТЕГА/РЕВИЗИИ, а не из «почти того же»: закоммитьте (или уберите) изменения и повторите'
    $dirty | Select-Object -First 10 | ForEach-Object { Write-Host ('  ' + $_) }
    if ($dirty.Count -gt 10) { Write-Host ('  … всего строк: ' + $dirty.Count) }
    exit 1
}

# ── 3. Данные и классификация путей тега/ревизии ───────────────────────────────────────────────
$dataOverlay = Get-ReleaseDataOverlay -Repo $repoRoot -Spec $spec
if ($null -eq $dataOverlay) {
    Write-Host ('неприменимо: в ' + $sourceLabel + ' нет каталога release/tree — собирать нечего')
    exit 2
}
if ($dataOverlay.Count -eq 0) {
    Write-Host ('неприменимо: поддерево «' + $spec + ':release/tree» ПУСТО')
    exit 2
}

Write-Host ('сборка полного дерева выпуска: ' + $sourceLabel + ', версия ' + $projectVersion)
if ($changelogReport) { Write-Host ('версия: ' + $projectVersion + ' из ревизии; ' + $changelogReport) }
Write-Host ('источник: ' + $repoRoot + ' (рабочее дерево не читается — только объекты ' + $(if ($Tag) { 'тега' } else { 'ревизии; объект тега не создаётся' }) + ')')
Write-Host ''

$tagPathsResult = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('ls-tree', '-r', '--name-only', $spec)
if ($tagPathsResult.Code -ne 0) { throw ('git ls-tree не отдал пути источника: ' + $tagPathsResult.Text) }
$tagPaths = @($tagPathsResult.Lines | Where-Object { $_ })
$selection = Get-ReleaseSelection -TagPaths $tagPaths

$discardedByRule = ($selection.ByRule | ForEach-Object { $_.Rule + ' = ' + $_.Count }) -join ', '
$replacements = @($selection.Replacements)
Write-Host ('путей в ' + $sourceWord + ': ' + $tagPaths.Count + '; снято с индекса (rm --cached -f) ' + $selection.Discarded.Count + ' — по именованным правилам ' + ($selection.Discarded.Count - $selection.Unknown.Count) + ', НЕЗНАКОМЫХ ' + $selection.Unknown.Count)
if ($discardedByRule) { Write-Host ('  правила: ' + $discardedByRule) }
Write-Host ('наложено данных: ' + $dataOverlay.Count + ' = ' + ($dataOverlay.Count - $replacements.Count) + ' новых + ' + $replacements.Count + ' замена' + $(if ($replacements.Count -gt 0) { ' (' + ($replacements -join ', ') + ' — блоб заменяется, путь остаётся один)' } else { '' }))
Write-Host ('не уехало содержимым: ' + ($selection.Discarded.Count + $replacements.Count) + ' = ' + $selection.Discarded.Count + ' снятых + внутреннее содержимое ' + $replacements.Count + ' заменённого пути')
if ($selection.Unknown.Count -gt 0) {
    Write-Host ''
    Write-Host ('СБОРКА НЕ ПОЙДЁТ: в ' + $sourceWord + ' есть пути, которых разрешённый список не знает — ' + $selection.Unknown.Count)
    Write-Host 'новый каталог кода обязан остановить выпуск, а не потеряться молча: дополните разрешённый список'
    $selection.Unknown | Select-Object -First 12 | ForEach-Object { Write-Host ('  ' + $_) }
    exit 1
}

# ── 4. Ожидаемое ПОЛНОЕ дерево из объекта git ──────────────────────────────────────────────────
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('dsh-make-release-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $expected = Get-ExpectedFullTree -Repo $repoRoot -Spec $spec -Selection $selection -DataOverlay $dataOverlay -TempRoot $tempRoot
    # Проверка независима от классификатора — см. пояснение в `check-release-tree.ps1` и мутацию CI-11.
    foreach ($name in @($expected.Paths | Where-Object { $null -ne (Get-ReleaseInternalViolation -Path $_) })) {
        Write-Host ('СБОРКА НЕ ПОЙДЁТ: в ожидаемом дереве внутренний файл — ' + $name + ' (' + (Get-ReleaseInternalViolation -Path $name) + ')')
        exit 1
    }
    Write-Host ('ожидаемое ПОЛНОЕ дерево: ' + $expected.Tree + ' (кода ' + $expected.CodeCount + ' + данных ' + $expected.DataCount + ' = ' + $expected.Total + ' путей)')
    Write-Host ''

    # ── 5. Сторож выпуска: состав, архив, данные ───────────────────────────────────────────────
    Write-Host ('шаг 1/3: проверка ' + $(if ($Tag) { 'тега' } else { 'ревизии' }) + ' (check-release-tree)')
    # Тот же вход, что и у сборки: тег — ключом -Tag, ревизия — ключом -Rev (полный sha).
    $preArgs = @('-NoProfile', '-File', $checkTool)
    $preArgs += $(if ($Tag) { @('-Tag', $Tag) } else { @('-Rev', $commit) })
    $preArgs += @('-Repo', $repoRoot)
    $preCheck = & pwsh @preArgs 2>&1
    $preCode = $LASTEXITCODE
    $preCheck | Where-Object { $_ -match 'ожидаемое ПОЛНОЕ|кода |отброшено путей|снято с индекса|данные на диске|архив выпуска|тождество|НЕ СОВПАЛ|неприменимо|замечан|лишн|НЕТ файла|НЕЗНАКОМ|версия из ревизии' } | ForEach-Object { Write-Host ('  ' + $_) }
    if ($preCode -ne 0) {
        Write-Host ''
        Write-Host ('СБОРКА НЕ ПОЙДЁТ: проверка по ' + $(if ($Tag) { 'тегу' } else { 'ревизии' }) + ' вернула код ' + $preCode + ' (0 — совпало, 1 — расхождение, 2 — неприменимо)')
        $preCheck | Where-Object { $_ -match '^  ' } | ForEach-Object { Write-Host $_ }
        exit $preCode
    }

    # ── 6. Сборка во ВРЕМЕННОМ каталоге, опись и ФИНАЛЬНАЯ сверка — ДО первой записи в -Out ──────
    Write-Host ''
    Write-Host 'шаг 2/3: сборка в временном каталоге, опись и финальная сверка'
    $staging = Join-Path $tempRoot 'tree'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $fullTar = Join-Path $tempRoot 'full.tar'
    $archiveFull = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('archive', '--format=tar', $spec, '-o', $fullTar)
    if ($archiveFull.Code -ne 0) { throw ('git archive ' + $(if ($Tag) { 'тега' } else { 'ревизии' }) + ' не сработал: ' + $archiveFull.Text) }
    & tar -xf $fullTar -C $staging
    if ($LASTEXITCODE -ne 0) { throw ('tar не распаковал архив (код ' + $LASTEXITCODE + ')') }

    # убрать с диска всё, чего нет в разрешённом списке
    $removed = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($staging.Length).TrimStart('\').Replace('\', '/')
        if ($selection.Discarded.Path -ccontains $relative) { Remove-Item -LiteralPath $file.FullName -Force; $removed++ }
    }
    Write-Host ('  с диска убрано путей вне списка: ' + $removed + ' (ожидалось ' + $selection.Discarded.Count + ')')
    if ($removed -ne $selection.Discarded.Count) {
        $onDisk = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($staging.Length).TrimStart('\').Replace('\', '/') })
        foreach ($item in $selection.Discarded) { if ($onDisk -ccontains $item.Path) { Write-Host ('  остался неубранным: ' + $item.Path) } }
        throw 'убрано не столько путей, сколько отброшено — сборка не продолжается'
    }
    # пустые каталоги после чистки
    foreach ($dir in @(Get-ChildItem -LiteralPath $staging -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)) {
        if (-not (Get-ChildItem -LiteralPath $dir.FullName -Force)) { Remove-Item -LiteralPath $dir.FullName -Force }
    }

    # наложение данных в КОРЕНЬ
    $dataTar = Join-Path $tempRoot 'data.tar'
    $archiveData = Invoke-ReleaseGit -Repo $repoRoot -GitArgs @('archive', '--format=tar', ($spec + ':release/tree'), '-o', $dataTar)
    if ($archiveData.Code -ne 0) { throw ('git archive данных не сработал: ' + $archiveData.Text) }
    & tar -xf $dataTar -C $staging
    if ($LASTEXITCODE -ne 0) { throw ('tar не распаковал архив данных (код ' + $LASTEXITCODE + ')') }
    # ── Куда писать: пути -Out и -Manifest разбираются ДО сверки, чтобы отказы стояли до записи ──
    if (-not $Out) { $Out = Join-Path (Split-Path -Parent $repoRoot) ('release-out-' + $projectVersion) }
    $Out = [IO.Path]::GetFullPath($Out)
    $repoFull = [IO.Path]::GetFullPath($repoRoot)
    if (Test-PathInside $Out $repoFull) {
        Write-Host ('СБОРКА НЕ ПОЙДЁТ: -Out внутри рабочего дерева репозитория («' + $Out + '») — дерево выпуска стало бы лишним путём в нём')
        exit 1
    }
    $outExisted = Test-Path -LiteralPath $Out
    if ($outExisted) {
        $existing = @(Get-ChildItem -LiteralPath $Out -Force)
        if ($existing.Count -gt 0) {
            Write-Host ('СБОРКА НЕ ПОЙДЁТ: «' + $Out + '» уже существует и не пуст (' + $existing.Count + ' записей) — старое дерево не смешиваем с новым, назовите другой -Out или уберите каталог')
            exit 1
        }
    }

    if (-not $Manifest) { $Manifest = Join-Path (Split-Path -Parent $Out) 'release-tree.txt' }
    $Manifest = [IO.Path]::GetFullPath($Manifest)
    if (Test-PathInside $Manifest $Out) {
        Write-Host ('СБОРКА НЕ ПОЙДЁТ: опись внутри дерева выпуска («' + $Manifest + '») — внутри она стала бы лишним файлом и сломала бы сверку')
        exit 1
    }

    # опись считается по ВРЕМЕННОМУ дереву: мера — объект git, «байт на диске» — отдельная колонка
    $blobInfo = Get-ReleaseBlobInfo -Repo $repoRoot -Shas @($expected.Entries.Values | ForEach-Object { $_.Blob }) -TempRoot $tempRoot
    $rows = @()
    foreach ($name in ($expected.Paths | Sort-Object)) {
        $file = Join-Path $staging $name
        if (-not (Test-Path -LiteralPath $file)) { throw ('в собранном дереве нет файла ожидаемого дерева: ' + $name) }
        $blob = $expected.Entries[$name].Blob
        $info = $blobInfo[$blob]
        $rows += [pscustomobject]@{
            Path        = $name
            ObjectBytes = $info.Size
            ObjectSha   = $info.Sha256
            Blob        = $blob
            DiskBytes   = [int64](Get-Item -LiteralPath $file).Length
        }
    }
    $totalObjectBytes = ($rows | Measure-Object -Property ObjectBytes -Sum).Sum
    $totalDiskBytes = ($rows | Measure-Object -Property DiskBytes -Sum).Sum
    $deltaBytes = $totalDiskBytes - $totalObjectBytes

    $measureLines = @(
        '# МЕРА: размер и SHA-256 — ОБЪЕКТА GIT (блоба), а не файла на диске: сверка идёт по блобам.'
        ('# сумма размеров объектов git: {0} байт; в собранном дереве на диске: {1} байт; разница {2} байт' -f $totalObjectBytes, $totalDiskBytes, $deltaBytes)
    )
    if ($deltaBytes -eq 0) {
        $measureLines += '# в СОБРАННОМ дереве диск совпал с блобом: git archive с -c core.autocrlf=false отдаёт байты блоба'
    } else {
        # Замер 17.10.2026: +16 736 Б. Причина НЕ core.autocrlf (флаги стоят), а `.gitattributes`
        # самого дерева: `*.ps1`, `*.iss`, `*.cmd`, `*.bat`, `*.json`, `engine-lock.json` объявлены
        # `eol=crlf`, и `git archive` выводит их рабочей формой. В индексе они нормализуются обратно
        # (eol=crlf = «хранить LF»), поэтому хеш дерева сходится — а байты на диске длиннее.
        $measureLines += ('# ⚠️ диск собранного дерева длиннее блобов на {0} Б: это `.gitattributes` самого дерева (eol=crlf для .ps1/.iss/.cmd/.bat/.json) — git archive выводит их рабочей формой; в индексе они нормализуются обратно, поэтому хеш дерева сходится' -f $deltaBytes)
    }
    $measureLines += '# ⚠️ В РАБОЧЕЙ КОПИИ РЕПОЗИТОРИЯ те же блобы длиннее на пары CRLF (core.autocrlf=true) — опись по диску печатала бы НЕ ТО, что сравнивается.'

    $manifestLines = @(
        '# release-tree.txt — опись ПОЛНОГО дерева выпуска, собранного из тега или ревизии (ADR-001 §5.2 — 17.10.2026; режим -Rev — 18.10.2026)'
        $(if ($Tag) { ('# тег: {0}' -f $Tag) } else { ('# ревизия: {0} (тег НЕ создавался)' -f $commit) })
        ('# версия: {0} (<Version> в src\DshPanel\DshPanel.csproj {1})' -f $projectVersion, $(if ($Tag) { 'тега' } else { 'ревизии' }))
        ('# дерево выпуска (git tree): {0}' -f $expected.Tree)
        ('# путей: {0} = кода {1} + данных {2}' -f $rows.Count, $expected.CodeCount, $expected.DataCount)
    ) + $measureLines + @(
        '# формат: путь|байт объекта git|sha256 объекта git|blob-sha1|байт на диске'
    )
    foreach ($row in $rows) { $manifestLines += ('{0}|{1}|{2}|{3}|{4}' -f $row.Path, $row.ObjectBytes, $row.ObjectSha, $row.Blob, $row.DiskBytes) }
    $tempManifest = Join-Path $tempRoot 'release-tree.txt'
    [IO.File]::WriteAllLines($tempManifest, $manifestLines, (New-Object Text.UTF8Encoding($false)))

    # ── ФИНАЛЬНАЯ сверка: по ВРЕМЕННОМУ дереву, ДО первой записи в -Out ────────────────────────
    Write-Host ''
    Write-Host '  финальная сверка: собранное сверяется с ожиданием ДО записи в -Out'
    $finalArgs = @('-NoProfile', '-File', $checkTool)
    $finalArgs += $(if ($Tag) { @('-Tag', $Tag) } else { @('-Rev', $commit) })
    $finalArgs += @('-Repo', $repoRoot, '-Tree', $staging)
    $finalCheck = & pwsh @finalArgs 2>&1
    $finalCode = $LASTEXITCODE
    if ($finalCode -ne 0) {
        Write-Host ('СБОРКА ПРОВАЛЕНА: собранное дерево не сошлось с ожиданием (код ' + $finalCode + ')')
        Write-Host ('  в «' + $Out + '» не записано НИЧЕГО: сверка идёт по временному каталогу ДО копирования')
        $finalCheck | Where-Object { $_ -match '^  ' } | ForEach-Object { Write-Host $_ }
        exit $finalCode
    }
    Write-Host ('  сборка совпала с ожиданием: ' + $expected.Tree)

    # ── 7. Перенос в -Out и сверка КОПИИ ────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'шаг 3/3: перенос в -Out, опись рядом с деревом и сверка копии'
    $outCreated = $false
    $manifestWritten = $false
    try {
        if (-not $outExisted) { New-Item -ItemType Directory -Path $Out -Force | Out-Null; $outCreated = $true }
        Get-ChildItem -LiteralPath $staging -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Out -Recurse -Force
        }
        Copy-Item -LiteralPath $tempManifest -Destination $Manifest -Force
        $manifestWritten = $true

        # сверка КОПИИ: те же пути, те же размеры, те же байты — записанное равно сверенному
        $copyProblems = @()
        $stagingFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($staging.Length).TrimStart('\').Replace('\', '/') } | Sort-Object)
        $outFiles = @(Get-ChildItem -LiteralPath $Out -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($Out.Length).TrimStart('\').Replace('\', '/') } | Sort-Object)
        foreach ($name in $stagingFiles) { if ($outFiles -cnotcontains $name) { $copyProblems += ('нет в копии — ' + $name) } }
        foreach ($name in $outFiles) { if ($stagingFiles -cnotcontains $name) { $copyProblems += ('лишний в копии — ' + $name) } }
        foreach ($name in $stagingFiles) {
            if ($outFiles -cnotcontains $name) { continue }
            $fromStaging = Join-Path $staging $name
            $inOut = Join-Path $Out $name
            if ((Get-Item -LiteralPath $fromStaging).Length -ne (Get-Item -LiteralPath $inOut).Length) { $copyProblems += ('размер разошёлся — ' + $name); continue }
            if ((Get-FileHash -LiteralPath $fromStaging -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $inOut -Algorithm SHA256).Hash) { $copyProblems += ('байты разошлись — ' + $name) }
        }
        if ((Get-FileHash -LiteralPath $tempManifest -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $Manifest -Algorithm SHA256).Hash) {
            $copyProblems += 'опись на своём месте не совпала с проверенной'
        }
        if ($copyProblems.Count -gt 0) {
            throw ('копия разошлась со сверенным деревом: ' + $copyProblems.Count + ' расхождени(й); первое — ' + $copyProblems[0])
        }
        Write-Host ('  копия сверена со сверенным деревом: путей ' + $stagingFiles.Count + ', байты совпали (SHA-256)')
    } catch {
        # НИ ДЕРЕВА, НИ ОПИСИ: убираем то, что успели записать (правка F5, 17.10.2026)
        $reason = $_.Exception.Message
        if ($manifestWritten -and (Test-Path -LiteralPath $Manifest)) { Remove-Item -LiteralPath $Manifest -Force -ErrorAction SilentlyContinue }
        elseif (Test-Path -LiteralPath $Manifest) { Write-Host ('  внимание: опись «' + $Manifest + '» была на месте ДО прогона — этот прогон её не писал и не трогал') }
        if ($outCreated) {
            $outRoot = [IO.Path]::GetPathRoot($Out)
            if ($Out -ne $outRoot -and (Test-Path -LiteralPath $Out)) { Remove-Item -LiteralPath $Out -Recurse -Force -ErrorAction SilentlyContinue }
        } else {
            foreach ($item in @(Get-ChildItem -LiteralPath $Out -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Host ''
        Write-Host ('СБОРКА ПРОВАЛЕНА на переносе в -Out: ' + $reason)
        Write-Host ('  записанное убрано: в «' + $Out + '» снова ничего, описи «' + $Manifest + '» нет')
        exit 1
    }
} catch {
    # ⚠️ ПАДЕНИЕ ИНСТРУМЕНТА — СВОИМ ИМЕНЕМ (18.10.2026). Без этой ветки техническая авария
    # выглядела как сырая запись исключения PowerShell, а рядом стоит совсем другая беда — «дерево
    # не совпало». Разные беды — разные лекарства, поэтому называются по-разному.
    Write-Host ''
    Write-Host ('СБОРКА НЕ СДЕЛАНА: инструмент не смог собрать дерево — ' + $_.Exception.Message)
    Write-Host ('  в ' + $(if ($Out) { '«' + $Out + '»' } else { 'каталог -Out' }) + ' не записано НИЧЕГО: все сверки идут до первой записи')
    exit 1
} finally {
    if (-not $KeepTemp -and $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'ДЕРЕВО ВЫПУСКА СОБРАНО И ПРОВЕРЕНО'
Write-Host ('  ' + $(if ($Tag) { 'тег:      ' } else { 'ревизия:  ' }) + $spec)
if (-not $Tag) { Write-Host '  тег:      НЕ создавался (режим -Rev: приёмка временным индексом, без тега)' }
Write-Host ('  версия:   ' + $projectVersion)
Write-Host ('  путей:    ' + $rows.Count + ' = кода ' + $expected.CodeCount + ' + данных ' + $expected.DataCount)
Write-Host ('  дерево:   ' + $expected.Tree)
Write-Host ('  объектов git: ' + ('{0:N0}' -f $totalObjectBytes) + ' Б; на диске: ' + ('{0:N0}' -f $totalDiskBytes) + ' Б (разница ' + $deltaBytes + ' Б — переводы строк рабочей копии)')
Write-Host ('  папка:    ' + $Out)
Write-Host ('  опись:    ' + $Manifest)
Write-Host ''
Write-Host 'СЛЕДУЮЩИЕ КОМАНДЫ (инструмент их НЕ выполняет — тег и публикация по слову владельца):'
Write-Host ('  # положить содержимое «' + $Out + '» в корень публичного дерева одним коммитом')
Write-Host '  git -C <клон-публичного> add -A; git -C <клон-публичного> commit -m "2.0 вместо 1.x"'
Write-Host '  # проверить публикацию по ДЕРЕВУ КОММИТА (это и есть тождество, а не диск): check-release-tree -Tree <клон-публичного>'
Write-Host '  # показать владельцу, дождаться его слова'
$tagForNext = if ($Tag -match '^v\d+\.\d+\.\d+$') { $Tag } elseif ($projectVersion) { 'v' + $projectVersion } else { 'vX.Y.Z' }
Write-Host ('  git -C <клон-публичного> tag -a ' + $tagForNext + ' -m "..."')
Write-Host '  git -C <клон-публичного> push origin main --tags   # только по слову владельца'
Write-Host ''
Write-Host 'ЧЕСТНО О ГРАНИЦЕ: доказаны СОСТАВ и БЛОБЫ дерева выпуска (код по разрешённому списку + данные). Пары (path, blob-sha) с публичным клоном не сверяются — для них нужен публичный тег того же выпуска.'
exit 0
