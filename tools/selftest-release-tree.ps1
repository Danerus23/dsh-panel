# selftest-release-tree.ps1 — РЕГРЕССИЯ СТОРОЖА ДЕРЕВА ОДНОЙ КОМАНДОЙ (t1, 18.10.2026; П-1 — 18.10.2026).
#
#   pwsh -NoProfile -File .\tools\selftest-release-tree.ps1
#   pwsh -NoProfile -File .\tools\selftest-release-tree.ps1 -Repo <клон репозитория> -Rev <sha>
#   pwsh -NoProfile -File .\tools\selftest-release-tree.ps1 -ExpectTagTree <40 hex> -ExpectRevTree <40 hex>
#
# ⚠️ ПРЕДПОСЫЛКА (П-1 приёмки, 18.10.2026): КОМАНДА РАБОТАЕТ НА ЛЮБОЙ РЕВИЗИИ ЛИНИИ, и тег выпуска
# для неё НЕ НУЖЕН. Прежде регрессия строилась вокруг тега `v2.1.0` и его ТРИДЦАТИ путей данных, а на
# линии 2.1.1 данных тридцать девять — команда краснела не по делу («диск 39 / источник 30 — лишних 9»),
# хотя инструменты были целы. Теперь стенд сам, в СВОЁМ %TEMP%-клоне, ставит ОДНОРАЗОВЫЙ тег
# `vtest-<Version>` на проверяемой ревизии (это прямо разрешено ADR-001 §5.1: «временный локальный тег
# `vtest-…`, который не пушится и удаляется») и делает сверху коммит только по внутренним документам.
# **В проекте теги не создаются** (`git tag` считается ДО и ПОСЛЕ), ничего не пушится.
# Ключ `-Tag` остался ДОБРОВОЛЬНЫМ — он для случая «названный тег действительно выпускной для этой
# ревизии» (тот же состав данных); на линии, где состав разошёлся, он законно краснеет.
#
# ЗАЧЕМ. 18.10.2026 владелец нашёл, а капитан воспроизвёл дефект: обе точки входа дерева выпуска
# ЛОЖНО падали, когда `HEAD` впереди тега (`git rm --cached` без `-f`), и падение ИНСТРУМЕНТА
# печаталось как «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО». Рядом жила дыра `Ф-9-2`: `-Tree` хешировал ДИСК, а не
# дерево коммита, и на отвергнутом коммите 2.1.0 дал ЛОЖНОЕ «0». Эта регрессия проверяет ОБА класса
# и режим `-Rev` (приёмка 2.1.1 без тега) — ОДНОЙ командой, потому что «проверку, которую надо
# собирать руками из пяти», не прогоняют.
#
# ГДЕ ИДЁТ РАБОТА. Стенд — КОПИИ в %TEMP%: `git clone --shared` названного репозитория (рабочее дерево
# проекта не читается и не меняется), собранные деревья рядом, мутации — в КОПИИ `tools\`. Проект не
# коммитит, тегов не создаёт (`git tag` считается ДО и ПОСЛЕ), `dist\DshPanel.exe` не касается.
#
# ОПЫТЫ (номер → что доказывается):
#   1  check -Tag при HEAD впереди тега → 0 (было 1 «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО»); тег — СВОЙ
#      одноразовый в стенде, поэтому опыт проверяет инструмент, а не совпадение с прошлым выпуском
#   2  make  -Tag → 0, файлов в дереве столько, сколько путей в ожидании (444 на 2.1.0, 455 на 2.1.1)
#   3  check -Tag -Tree <собранное> → 0, «тождество доказано»
#   4  check -Rev <HEAD> → 0 и НЕ создаёт тег (`git tag` до = после)
#   5  make  -Rev <HEAD> → 0; check -Rev -Tree <собранное> → 0
#   6  мутация «убрать -f» → 1, текст называет ИНСТРУМЕНТ («не смог снять с индекса», ошибка git),
#      а НЕ «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО»; контроль: та же копия с `-f` → 0
#   7  мутация «один байт» и «один файл убран» в собранном дереве → 1 с ИМЕНЕМ пути; возврат → 0
#   8  Ф-9-2 — три опыта:
#      8а  ДВА коммита из ОДНОГО И ТОГО ЖЕ диска: с `core.autocrlf=false` дерево коммита равно
#          ожиданию (0), с `autocrlf=true` блоб `src/DshPanel/Shell/PanelStrings.cs` нормализуется,
#          дерево коммита другое (1 с именем). Диск один и тот же — значит проверка диска дала бы
#          «0» ОБОИМ, и это ровно тот ложный «0», которым `Ф-9-2` пропустил отвергнутый коммит 2.1.0;
#      8б  правка на диске, НЕ в коммите, приговора коммиту не меняет (0 + предупреждение);
#      8в  одно и то же дерево, собранное и проверенное из репозиториев с `autocrlf=true` и `false`,
#          даёт 0 ОБА раза и ОДИН хеш (обе команды: `make` и `check`);
#   9  регрессия «клон тега → коммит сверху → оба инструмента 0» и «-Rev от коммита БЕЗ тега» → 0;
#      та же регрессия на копии с ОТКАТОМ фикса (без `-f`) → 1 (регрессия умеет быть красной)
#  10  версия ревизии против трёх CHANGELOG: подложенный пустой раздел версии → 1 с именем файла
#
# КОДЫ: 0 — все опыты дали ожидаемое; 1 — хотя бы один опыт разошёлся (назван номер и числа);
# 2 — стенд не поставить (нет git, нет репозитория, у ревизии нет csproj/<Version>).
# «Неприменимо» — не «зелено» и не «красно».

param(
    [string]$Repo = '',
    [string]$Tag = '',
    [string]$Rev = '',
    [string]$ExpectTagTree = '',
    [string]$ExpectRevTree = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'

$results = @()
$fail = 0
$work = $null

function Write-Step {
    param([string]$Id, [string]$Name, [string]$Expected, [string]$Actual, [bool]$Ok, [string]$Evidence)
    $script:results += [pscustomobject]@{ Id = $Id; Name = $Name; Expected = $Expected; Actual = $Actual; Ok = $Ok; Evidence = $Evidence }
    $flag = if ($Ok) { 'ЗЕЛЕНО' } else { 'КРАСНО' }
    Write-Host ('  [' + $Id + '] ' + $Name + ' — ' + $flag)
    Write-Host ('      ожидалось: ' + $Expected)
    Write-Host ('      получено:  ' + $Actual)
    if ($Evidence) { Write-Host ('      ' + $Evidence) }
    if (-not $Ok) { $script:fail++ }
}

# Запуск инструмента отдельным процессом: код возврата и весь текст — наружу.
function Invoke-Tool {
    param([string]$Script, [string[]]$ToolArgs)
    $all = @('-NoProfile', '-File', $Script) + @($ToolArgs)
    $raw = & pwsh @all 2>&1
    $code = $LASTEXITCODE
    $lines = @($raw | ForEach-Object { [string]$_ })
    return [pscustomobject]@{ Code = $code; Lines = $lines; Text = ($lines -join "`n") }
}

function Invoke-Git {
    param([string]$Path, [string[]]$GitArgs)
    $raw = & git -C $Path @GitArgs 2>&1
    $code = $LASTEXITCODE
    $lines = @($raw | ForEach-Object { [string]$_ })
    return [pscustomobject]@{ Code = $code; Lines = $lines; Text = (($lines | Where-Object { $_ }) -join "`n").Trim() }
}

function Get-FirstMatch {
    param([string]$Text, [string]$Pattern)
    $m = [regex]::Match($Text, $Pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Get-FileCount {
    param([string]$Folder)
    return @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force).Count
}

function Get-FileSha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# ── Стенд ──────────────────────────────────────────────────────────────────────────────────────
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host 'неприменимо: git не найден в PATH — ставить стенд нечем'
    exit 2
}
$repoRoot = $(if ($Repo) { (Resolve-Path -LiteralPath $Repo).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path })
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Host ('неприменимо: в «' + $repoRoot + '» нет репозитория git')
    exit 2
}
$toolsDir = $PSScriptRoot
$checkTool = Join-Path $toolsDir 'check-release-tree.ps1'
$makeTool = Join-Path $toolsDir 'make-release-tree.ps1'
$commonTool = Join-Path $toolsDir 'release-tree-common.ps1'
foreach ($required in @($checkTool, $makeTool, $commonTool)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host ('неприменимо: нет инструмента «' + $required + '» — регрессию прогнать нечем')
        exit 2
    }
}

$tagsBefore = (Invoke-Git -Path $repoRoot -GitArgs @('tag', '-l')).Text
if ($Tag) {
    # `-Tag` — ДОБРОВОЛЬНЫЙ ключ: «названный тег выпускной для этой ревизии». По умолчанию тег не нужен.
    $hasTag = Invoke-Git -Path $repoRoot -GitArgs @('rev-parse', '--verify', '--quiet', ('refs/tags/' + $Tag))
    if ($hasTag.Code -ne 0) {
        Write-Host ('неприменимо: тега «' + $Tag + '» нет в «' + $repoRoot + '»')
        exit 2
    }
}
$rev = $Rev
if (-not $rev) {
    $head = Invoke-Git -Path $repoRoot -GitArgs @('rev-parse', 'HEAD')
    if ($head.Code -ne 0) { Write-Host 'неприменимо: у репозитория не читается HEAD'; exit 2 }
    $rev = $head.Text
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('dsh-selftest-tree-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

Write-Host 'РЕГРЕССИЯ СТОРОЖА ДЕРЕВА (t1, 18.10.2026; П-1 — 18.10.2026)'
Write-Host ('  репозиторий: ' + $repoRoot)
Write-Host ('  ревизия:     ' + $rev + $(if ($Tag) { ';  тег назван ключом: ' + $Tag } else { ';  тег — свой одноразовый в стенде' }))
Write-Host ('  стенд:       ' + $work + ' (копии; рабочее дерево проекта не меняется)')
Write-Host ('  тегов ДО:    ' + $(if ($tagsBefore) { $tagsBefore -replace "`n", ', ' } else { 'нет' }))
Write-Host ''

$stand = Join-Path $work 'stand'
$cloneResult = Invoke-Git -Path $work -GitArgs @('clone', '--shared', '--quiet', $repoRoot, $stand)
if ($cloneResult.Code -ne 0) {
    Write-Host ('неприменимо: стенд не поставить — git clone отказал: ' + $cloneResult.Text)
    exit 2
}

# ── СВОЙ ОДНОРАЗОВЫЙ ТЕГ В СТЕНДЕ (П-1) ───────────────────────────────────────────────────────────
#
# ⚠️ ЗАЧЕМ. Регрессия проверяет ИНСТРУМЕНТ, а не совпадение дерева с прошлым выпуском. Пока стенд
# строился вокруг тега `v2.1.0` (30 путей данных), на линии 2.1.1 (39 путей) он краснел не по делу:
# «диск 39 / источник 30 — лишних 9». Поэтому тег ставится СВОЙ и на ПРОВЕРЯЕМОЙ ревизии, чтобы состав
# данных совпадал по определению; ADR-001 §5.1 разрешает временный локальный `vtest-…` — он не пушится
# и умирает вместе со стендом. В проекте тегов НЕ создаётся (см. `git tag` ДО и ПОСЛЕ).
if (-not $Tag) {
    # Номер версии — из ревизии: в режиме `-Tag` инструмент сверяет НОМЕР ТЕГА с `<Version>` проекта,
    # значит имя одноразового тега обязано этот номер нести.
    $projectResult = Invoke-Git -Path $stand -GitArgs @('show', ($rev + ':src/DshPanel/DshPanel.csproj'))
    if ($projectResult.Code -ne 0) {
        Write-Host ('неприменимо: в ревизии «' + $rev + '» нет src/DshPanel/DshPanel.csproj — номер тега взять неоткуда')
        exit 2
    }
    $versionMatch = [regex]::Match($projectResult.Text, '<Version>([^<]+)</Version>')
    if (-not $versionMatch.Success) {
        Write-Host ('неприменимо: в csproj ревизии «' + $rev + '» нет <Version> — номер тега взять неоткуда')
        exit 2
    }
    $Tag = 'vtest-' + $versionMatch.Groups[1].Value.Trim()

    $tagCommit = (Invoke-Git -Path $stand -GitArgs @('rev-parse', '--verify', '--quiet', ($rev + '^{commit}'))).Text
    if (-not $tagCommit) {
        Write-Host ('неприменимо: стенд не нашёл ревизию «' + $rev + '» — тег поставить не на что')
        exit 2
    }
    $tagCreate = Invoke-Git -Path $stand -GitArgs @('tag', '-a', $Tag, '-m', ('регрессия сторожа дерева: одноразовый тег стенда на ' + $tagCommit), $tagCommit)
    if ($tagCreate.Code -ne 0) {
        Write-Host ('неприменимо: стенд не смог поставить свой тег «' + $Tag + '»: ' + $tagCreate.Text)
        exit 2
    }
    Write-Host ('  свой тег:    ' + $Tag + ' на ' + $tagCommit + ' (одноразовый, только в стенде; в проекте теги не создаются)')
}

# ⚠️ HEAD ОБЯЗАН БЫТЬ ВПЕРЕДИ ТЕГА — иначе опыт 6 не может сработать ПО ПРИРОДЕ дефекта: без `-f` git
# отказывает только тогда, когда индекс (из тега) отличается от HEAD. Стендовый коммит трогает ТОЛЬКО
# внутренние документы, поэтому ожидание дерева выпуска от него не меняется (это и проверяет опыт 9).
$standHead = (Invoke-Git -Path $stand -GitArgs @('rev-parse', 'HEAD')).Text
$standAheadCount = (Invoke-Git -Path $stand -GitArgs @('rev-list', '--count', ($Tag + '..HEAD'))).Text
if ($standAheadCount -eq '0' -or -not $standAheadCount) {
    $notePath = @('docs\NEXT.md', 'docs\HISTORY.md') | ForEach-Object { Join-Path $stand $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $notePath) {
        Write-Host 'неприменимо: в стенде нет внутренних документов (docs\NEXT.md, docs\HISTORY.md) — коммит сверх тега поставить не на что'
        exit 2
    }
    [IO.File]::AppendAllText($notePath, "`n(стенд регрессии сторожа дерева: коммит сверх тега — в дерево выпуска не входит)`n")
    Invoke-Git -Path $stand -GitArgs @('config', 'user.email', 'selftest@example.invalid') | Out-Null
    Invoke-Git -Path $stand -GitArgs @('config', 'user.name', 'selftest') | Out-Null
    Invoke-Git -Path $stand -GitArgs @('add', '-A') | Out-Null
    $madeCommit = Invoke-Git -Path $stand -GitArgs @('commit', '-q', '-m', 'стенд: коммит сверх тега (внутренние документы)')
    if ($madeCommit.Code -ne 0) {
        Write-Host ('неприменимо: стенд не смог поставить коммит сверх тега: ' + $madeCommit.Text)
        exit 2
    }
    $standHead = (Invoke-Git -Path $stand -GitArgs @('rev-parse', 'HEAD')).Text
    $standAheadCount = (Invoke-Git -Path $stand -GitArgs @('rev-list', '--count', ($Tag + '..HEAD'))).Text
    Write-Host ('  стендовый коммит сверх тега: ' + $standHead + ' (только внутренние документы)')
}
$standDirty = @((Invoke-Git -Path $stand -GitArgs @('status', '--porcelain')).Lines | Where-Object { $_ }).Count
$standTagsBefore = (Invoke-Git -Path $stand -GitArgs @('tag', '-l')).Text
Write-Host ('  стенд: HEAD ' + $standHead + ', коммитов после тега ' + $standAheadCount + ', грязь ' + $standDirty)
Write-Host ''

try {
    # ── Опыт 1: check -Tag при HEAD впереди тега ───────────────────────────────────────────────
    Write-Host 'опыт 1: check-release-tree -Tag при HEAD впереди тега'
    $r1 = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand)
    $tree1 = Get-FirstMatch -Text $r1.Text -Pattern 'ожидаемое ПОЛНОЕ дерево:\s+([0-9a-f]{40})'
    $count1 = Get-FirstMatch -Text $r1.Text -Pattern '= всего (\d+) путей'
    $ok1 = ($r1.Code -eq 0) -and ($tree1 -ne '') -and (-not ($r1.Text -like '*ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО*'))
    if ($ExpectTagTree) { $ok1 = $ok1 -and ($tree1 -eq $ExpectTagTree) }
    Write-Step -Id '1' -Name 'check -Tag (HEAD впереди тега)' -Expected ('код 0, дерево ' + $(if ($ExpectTagTree) { $ExpectTagTree } else { 'названо (любой хеш)' })) -Actual ('код ' + $r1.Code + ', дерево ' + $tree1 + ', путей ' + $count1) -Ok $ok1 -Evidence 'дефект 18.10.2026: без -f здесь был код 1 и «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО»'

    # ── Опыт 2: make -Tag ──────────────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 2: make-release-tree -Tag (полное дерево из тега)'
    $tagDir = Join-Path $work 'tag'
    New-Item -ItemType Directory -Path $tagDir -Force | Out-Null
    $outTag = Join-Path $tagDir 'out'
    $r2 = Invoke-Tool -Script $makeTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Out', $outTag)
    $paths2 = Get-FirstMatch -Text $r2.Text -Pattern 'путей:\s+(\d+) = кода'
    $files2 = if (Test-Path -LiteralPath $outTag) { Get-FileCount -Folder $outTag } else { -1 }
    $manifestTag = Join-Path $tagDir 'release-tree.txt'
    $ok2 = ($r2.Code -eq 0) -and ($files2 -gt 0) -and ([string]$files2 -eq $paths2) -and (Test-Path -LiteralPath $manifestTag)
    Write-Step -Id '2' -Name 'make -Tag → -Out' -Expected 'код 0, файлов в -Out = путей в ожидании, опись рядом' -Actual ('код ' + $r2.Code + ', путей ' + $paths2 + ', файлов ' + $files2 + ', опись ' + (Test-Path -LiteralPath $manifestTag)) -Ok $ok2 -Evidence ''

    # ── Опыт 3: check -Tag -Tree ───────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 3: check-release-tree -Tag -Tree (собранное против ожидания)'
    $r3 = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $outTag)
    $measure3 = (Get-FirstMatch -Text $r3.Text -Pattern 'мера: ([^—]+)—').Trim()
    $ok3 = ($r3.Code -eq 0) -and ($r3.Text -like '*тождество доказано*')
    Write-Step -Id '3' -Name 'check -Tag -Tree' -Expected 'код 0 и «тождество доказано»' -Actual ('код ' + $r3.Code + ', мера «' + $measure3 + '»') -Ok $ok3 -Evidence 'у собранной папки коммита нет — мера честно называется «СОДЕРЖИМОЕ ПАПКИ»'

    # ── Опыт 4: check -Rev ────────────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 4: check-release-tree -Rev (ревизия вместо тега)'
    $r4 = Invoke-Tool -Script $checkTool -ToolArgs @('-Rev', $rev, '-Repo', $stand)
    $tree4 = Get-FirstMatch -Text $r4.Text -Pattern 'ожидаемое ПОЛНОЕ дерево:\s+([0-9a-f]{40})'
    $count4 = Get-FirstMatch -Text $r4.Text -Pattern '= всего (\d+) путей'
    $tagsAfter4 = (Invoke-Git -Path $stand -GitArgs @('tag', '-l')).Text
    $ok4 = ($r4.Code -eq 0) -and ($tree4 -ne '') -and ($tagsAfter4 -eq $standTagsBefore) -and ($r4.Text -like '*тег НЕ создаётся*')
    if ($ExpectRevTree) { $ok4 = $ok4 -and ($tree4 -eq $ExpectRevTree) }
    Write-Step -Id '4' -Name 'check -Rev' -Expected 'код 0, дерево ревизии названо, тег НЕ создан' -Actual ('код ' + $r4.Code + ', дерево ' + $tree4 + ', путей ' + $count4 + ', тегов в стенде ' + @($tagsAfter4 -split "`n" | Where-Object { $_ }).Count) -Ok $ok4 -Evidence 'объект тега не создаётся: в выводе «тег НЕ создаётся», список тегов до и после одинаков'

    # ── Опыт 5: make -Rev и check -Rev -Tree ──────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 5: make-release-tree -Rev и check -Rev -Tree'
    $revDir = Join-Path $work 'rev'
    New-Item -ItemType Directory -Path $revDir -Force | Out-Null
    $outRev = Join-Path $revDir 'out'
    $r5 = Invoke-Tool -Script $makeTool -ToolArgs @('-Rev', $rev, '-Repo', $stand, '-Out', $outRev)
    $paths5 = Get-FirstMatch -Text $r5.Text -Pattern 'путей:\s+(\d+) = кода'
    $files5 = if (Test-Path -LiteralPath $outRev) { Get-FileCount -Folder $outRev } else { -1 }
    $r5b = Invoke-Tool -Script $checkTool -ToolArgs @('-Rev', $rev, '-Repo', $stand, '-Tree', $outRev)
    $ok5 = ($r5.Code -eq 0) -and ($files5 -gt 0) -and ([string]$files5 -eq $paths5) -and ($r5b.Code -eq 0)
    Write-Step -Id '5' -Name 'make -Rev → -Out; check -Rev -Tree' -Expected 'оба кода 0, файлов = путей' -Actual ('make ' + $r5.Code + ' (путей ' + $paths5 + ', файлов ' + $files5 + '), check ' + $r5b.Code) -Ok $ok5 -Evidence 'собранное из ревизии сверено с ожиданием той же ревизии'

    # ── Опыт 6: мутация «убрать -f» ───────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 6: мутация «убрать -f» (копия tools\)'
    $toolsMut = Join-Path $work 'tools-mut'
    New-Item -ItemType Directory -Path $toolsMut -Force | Out-Null
    foreach ($file in @(Get-ChildItem -LiteralPath $toolsDir -File -Filter '*.ps1')) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $toolsMut $file.Name) -Force
    }
    $commonMut = Join-Path $toolsMut 'release-tree-common.ps1'
    $mutCheck = Join-Path $toolsMut 'check-release-tree.ps1'
    $originalCommon = [IO.File]::ReadAllText($commonMut)
    $mutated = $originalCommon.Replace("'rm', '--cached', '-f', '-r', '-q', '--ignore-unmatch'", "'rm', '--cached', '-r', '-q', '--ignore-unmatch'")
    if ($mutated -eq $originalCommon) {
        Write-Step -Id '6' -Name 'мутация «убрать -f»' -Expected 'строка с -f найдена в копии' -Actual 'строка НЕ найдена — мутация не применилась' -Ok $false -Evidence 'регрессия ничего не доказала бы'
    }
    else {
        [IO.File]::WriteAllText($commonMut, $mutated, (New-Object Text.UTF8Encoding($false)))
        $r6 = Invoke-Tool -Script $mutCheck -ToolArgs @('-Tag', $Tag, '-Repo', $stand)
        $namesTool = ($r6.Text -like '*ИНСТРУМЕНТ*') -and ($r6.Text -like '*не смог снять с индекса*')
        $blamesTree = $r6.Text -like '*ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО*'
        $gitError = $r6.Text -like '*use -f*'
        [IO.File]::WriteAllText($commonMut, $originalCommon, (New-Object Text.UTF8Encoding($false)))
        $r6b = Invoke-Tool -Script $mutCheck -ToolArgs @('-Tag', $Tag, '-Repo', $stand)
        $ok6 = ($r6.Code -eq 1) -and $namesTool -and (-not $blamesTree) -and $gitError -and ($r6b.Code -eq 0)
        Write-Step -Id '6' -Name 'мутация «убрать -f»' -Expected 'код 1, назван ИНСТРУМЕНТ и ошибка git, «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО» НЕ звучит; контроль с -f → 0' -Actual ('мутация ' + $r6.Code + ' (ИНСТРУМЕНТ: ' + $namesTool + ', ошибка git: ' + $gitError + ', винит дерево: ' + $blamesTree + '), контроль ' + $r6b.Code) -Ok $ok6 -Evidence 'мутация в КОПИИ tools\; рабочее дерево проекта не тронуто'
    }

    # ── Опыт 7: мутации в собранном дереве ────────────────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 7: мутации «один байт» и «один файл убран» в собранном дереве'
    $mutFile = Join-Path $outTag 'CHANGELOG.md'
    $mutSha = Get-FileSha -Path $mutFile
    $origBytes = [IO.File]::ReadAllBytes($mutFile)
    $changed = [byte[]]$origBytes.Clone()
    $changed[$changed.Length - 2] = [byte](($changed[$changed.Length - 2] + 1) % 256)
    [IO.File]::WriteAllBytes($mutFile, $changed)
    $r7 = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $outTag)
    [IO.File]::WriteAllBytes($mutFile, $origBytes)          # возврат ПЕРВОНАЧАЛЬНЫХ байтов, не «обратный сдвиг»
    $restored = (Get-FileSha -Path $mutFile) -eq $mutSha
    $r7back = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $outTag)
    Write-Step -Id '7а' -Name 'мутация «один байт»' -Expected 'код 1 и имя пути CHANGELOG.md; после возврата SHA-256 тот же и снова 0' -Actual ('код ' + $r7.Code + ', имя пути в выводе: ' + ($r7.Text -like '*CHANGELOG.md*') + '; байт возвращён побайтово: ' + $restored + '; контроль ' + $r7back.Code) -Ok (($r7.Code -eq 1) -and ($r7.Text -like '*CHANGELOG.md*') -and $restored -and ($r7back.Code -eq 0)) -Evidence ''

    $goneFile = Join-Path $outTag 'LICENSE'
    $goneBackup = [IO.File]::ReadAllBytes($goneFile)
    $goneSha = Get-FileSha -Path $goneFile
    Remove-Item -LiteralPath $goneFile -Force
    $r7b = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $outTag)
    [IO.File]::WriteAllBytes($goneFile, $goneBackup)
    $goneRestored = (Get-FileSha -Path $goneFile) -eq $goneSha
    $r7c = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $outTag)
    Write-Step -Id '7б' -Name 'мутация «один файл убран»' -Expected 'код 1 и имя пути LICENSE; после возврата дерево снова 0' -Actual ('код ' + $r7b.Code + ', имя пути в выводе: ' + ($r7b.Text -like '*LICENSE*') + '; возврат побайтово: ' + $goneRestored + '; контроль ' + $r7c.Code) -Ok (($r7b.Code -eq 1) -and ($r7b.Text -like '*LICENSE*') -and $goneRestored -and ($r7c.Code -eq 0)) -Evidence ''

    # ── Опыт 8: Ф-9-2 — мера «дерево коммита», а не байты диска ──────────────────────────────
    Write-Host ''
    Write-Host 'опыт 8: Ф-9-2 — мера «дерево коммита», а не байты диска'
    function New-PublicationRepo {
        param([string]$Parent, [string]$Source, [string]$AutoCrlf, [string]$Manifest)
        New-Item -ItemType Directory -Path $Parent -Force | Out-Null
        $path = Join-Path $Parent 'clone'
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $path -Recurse -Force
        }
        Copy-Item -LiteralPath $Manifest -Destination (Join-Path $Parent 'release-tree.txt') -Force
        Invoke-Git -Path $path -GitArgs @('init', '-q') | Out-Null
        Invoke-Git -Path $path -GitArgs @('config', 'core.autocrlf', $AutoCrlf) | Out-Null
        Invoke-Git -Path $path -GitArgs @('config', 'core.safecrlf', 'false') | Out-Null
        Invoke-Git -Path $path -GitArgs @('config', 'user.email', 'selftest@example.invalid') | Out-Null
        Invoke-Git -Path $path -GitArgs @('config', 'user.name', 'selftest') | Out-Null
        Invoke-Git -Path $path -GitArgs @('add', '-A') | Out-Null
        $c = Invoke-Git -Path $path -GitArgs @('commit', '-q', '-m', 'публикация: полное дерево выпуска')
        if ($c.Code -ne 0) { return $null }
        return [pscustomobject]@{
            Path = $path
            Commit = (Invoke-Git -Path $path -GitArgs @('rev-parse', 'HEAD')).Text
            Tree = (Invoke-Git -Path $path -GitArgs @('rev-parse', 'HEAD^{tree}')).Text
            Dirty = @((Invoke-Git -Path $path -GitArgs @('status', '--porcelain')).Lines | Where-Object { $_ }).Count
        }
    }

    $pubFalse = New-PublicationRepo -Parent (Join-Path $work 'pubfalse') -Source $outTag -AutoCrlf 'false' -Manifest $manifestTag
    $pubTrue = New-PublicationRepo -Parent (Join-Path $work 'pubtrue') -Source $outTag -AutoCrlf 'true' -Manifest $manifestTag
    $r8a = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $pubFalse.Path)
    $r8b = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $pubTrue.Path)
    $measure8 = Get-FirstMatch -Text $r8a.Text -Pattern 'мера: ДЕРЕВО КОММИТА ([0-9a-f]{40})'
    # какие блобы разошлись в коммите, собранном при autocrlf=true
    $diffLines = Compare-Object -ReferenceObject @((Invoke-Git -Path $pubFalse.Path -GitArgs @('ls-tree', '-r', 'HEAD')).Lines | Where-Object { $_ }) `
        -DifferenceObject @((Invoke-Git -Path $pubTrue.Path -GitArgs @('ls-tree', '-r', 'HEAD')).Lines | Where-Object { $_ })
    $diffNames = @($diffLines | ForEach-Object { ($_.InputObject -split "`t")[-1] } | Sort-Object -Unique)
    $ok8a = ($pubFalse.Tree -eq $tree1) -and ($r8a.Code -eq 0) -and ($measure8 -eq $pubFalse.Commit) -and ($r8b.Code -eq 1) -and ($diffNames.Count -gt 0) -and ($r8b.Text -like ('*' + $diffNames[0] + '*'))
    Write-Step -Id '8а' -Name 'Ф-9-2: два коммита из ОДНОГО диска (autocrlf=false и true)' -Expected 'коммит с autocrlf=false = ожиданию → 0; коммит с autocrlf=true нормализовал блоб → 1 с ИМЕНЕМ этого файла' -Actual ('деревья ' + $pubFalse.Tree + ' / ' + $pubTrue.Tree + '; check ' + $r8a.Code + ' и ' + $r8b.Code + '; разошлись блобы: ' + ($diffNames -join ', ') + '; грязь в клонах ' + $pubFalse.Dirty + '/' + $pubTrue.Dirty) -Ok $ok8a -Evidence 'диск в обоих клонах ОДИН И ТОТ ЖЕ — проверка по диску дала бы «0» обоим, и это ровно тот ложный «0», которым Ф-9-2 пропустил отвергнутый коммит 2.1.0'

    # 8б: неубранная правка НА ДИСКЕ не меняет приговор КОММИТУ
    $readmePub = Join-Path $pubFalse.Path 'README.md'
    $readmeBytes = [IO.File]::ReadAllBytes($readmePub)
    [IO.File]::AppendAllText($readmePub, "`n<!-- правка на диске, не в коммите -->`n")
    $r8c = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $pubFalse.Path)
    [IO.File]::WriteAllBytes($readmePub, $readmeBytes)
    $r8d = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Tree', $pubFalse.Path)
    $ok8b = ($r8c.Code -eq 0) -and ($r8c.Text -like '*сверяется КОММИТ*') -and ($r8d.Code -eq 0)
    Write-Step -Id '8б' -Name 'Ф-9-2: правка на диске, НЕ в коммите' -Expected 'код 0 и предупреждение «сверяется КОММИТ» (диск не приговор коммиту)' -Actual ('с правкой на диске ' + $r8c.Code + ', после возврата ' + $r8d.Code) -Ok $ok8b -Evidence 'прежняя мера «по диску» покраснела бы здесь на исправном коммите'

    # 8в: одно и то же дерево из репозиториев с autocrlf=true и false — обе команды.
    # ⚠️ Клонируется СТЕНД, а не исходный репозиторий: одноразовый тег `vtest-…` (П-1) живёт только
    # в стенде, и `make -Tag` в клоне исходного репозитория законно ответил бы «тега нет».
    $standT = Join-Path $work 'stand-true'
    $standF = Join-Path $work 'stand-false'
    Invoke-Git -Path $work -GitArgs @('clone', '--shared', '--quiet', '-c', 'core.autocrlf=true', $stand, $standT) | Out-Null
    Invoke-Git -Path $work -GitArgs @('clone', '--shared', '--quiet', '-c', 'core.autocrlf=false', $stand, $standF) | Out-Null
    $trueDir = Join-Path $work 'mtrue'
    $falseDir = Join-Path $work 'mfalse'
    New-Item -ItemType Directory -Path $trueDir, $falseDir -Force | Out-Null
    $outTrue = Join-Path $trueDir 'out'
    $outFalse = Join-Path $falseDir 'out'
    $r8e = Invoke-Tool -Script $makeTool -ToolArgs @('-Tag', $Tag, '-Repo', $standT, '-Out', $outTrue)
    $r8f = Invoke-Tool -Script $makeTool -ToolArgs @('-Tag', $Tag, '-Repo', $standF, '-Out', $outFalse)
    $treeTrue = Get-FirstMatch -Text $r8e.Text -Pattern 'дерево:\s+([0-9a-f]{40})'
    $treeFalse = Get-FirstMatch -Text $r8f.Text -Pattern 'дерево:\s+([0-9a-f]{40})'
    $filesTrue = if (Test-Path -LiteralPath $outTrue) { Get-FileCount -Folder $outTrue } else { -1 }
    $filesFalse = if (Test-Path -LiteralPath $outFalse) { Get-FileCount -Folder $outFalse } else { -1 }
    $r8g = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $standT, '-Tree', $outTrue)
    $r8h = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $standF, '-Tree', $outFalse)
    $ok8c = ($r8e.Code -eq 0) -and ($r8f.Code -eq 0) -and ($r8g.Code -eq 0) -and ($r8h.Code -eq 0) -and
            ($treeTrue -ne '') -and ($treeTrue -eq $treeFalse) -and ($filesTrue -eq $filesFalse)
    Write-Step -Id '8в' -Name 'Ф-9-2: одно дерево из репозиториев с autocrlf=true и false (обе команды)' -Expected 'make 0/0 и check 0/0, хеши деревьев и число файлов совпали' -Actual ('make ' + $r8e.Code + '/' + $r8f.Code + ', check ' + $r8g.Code + '/' + $r8h.Code + '; деревья ' + $treeTrue + ' / ' + $treeFalse + '; файлов ' + $filesTrue + '/' + $filesFalse) -Ok $ok8c -Evidence 'флаги -c core.autocrlf=false на операциях с блобами снимают настройку машины: результат не зависит от того, как настроен репозиторий'

    # ── Опыт 9: регрессия «клон тега → коммит сверху» и «-Rev от коммита БЕЗ тега» ─────────────
    Write-Host ''
    Write-Host 'опыт 9: регрессия «коммит сверх тега» и «-Rev от коммита БЕЗ тега»'
    $nextPath = Join-Path $stand 'docs\NEXT.md'
    [IO.File]::AppendAllText($nextPath, "`n(selftest: правка внутренних документов — в дерево выпуска не входит)`n")
    Invoke-Git -Path $stand -GitArgs @('config', 'user.email', 'selftest@example.invalid') | Out-Null
    Invoke-Git -Path $stand -GitArgs @('config', 'user.name', 'selftest') | Out-Null
    Invoke-Git -Path $stand -GitArgs @('add', '-A') | Out-Null
    $commit9 = Invoke-Git -Path $stand -GitArgs @('commit', '-q', '-m', 'selftest: коммит сверх тега (внутренние документы)')
    $newCommit = (Invoke-Git -Path $stand -GitArgs @('rev-parse', 'HEAD')).Text
    $tagsOnNew = (Invoke-Git -Path $stand -GitArgs @('tag', '--points-at', $newCommit)).Text
    $ahead9 = (Invoke-Git -Path $stand -GitArgs @('rev-list', '--count', ($Tag + '..HEAD'))).Text
    $regDir = Join-Path $work 'reg'
    New-Item -ItemType Directory -Path $regDir -Force | Out-Null
    $r9a = Invoke-Tool -Script $checkTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand)
    $r9b = Invoke-Tool -Script $makeTool -ToolArgs @('-Tag', $Tag, '-Repo', $stand, '-Out', (Join-Path $regDir 'out'))
    $r9c = Invoke-Tool -Script $checkTool -ToolArgs @('-Rev', $newCommit, '-Repo', $stand)
    # откат фикса: та же регрессия на копии без -f обязана быть красной
    $toolsRollback = Join-Path $work 'tools-rollback'
    New-Item -ItemType Directory -Path $toolsRollback -Force | Out-Null
    foreach ($file in @(Get-ChildItem -LiteralPath $toolsDir -File -Filter '*.ps1')) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $toolsRollback $file.Name) -Force
    }
    $rollbackCommon = Join-Path $toolsRollback 'release-tree-common.ps1'
    $rollbackText = [IO.File]::ReadAllText($rollbackCommon)
    [IO.File]::WriteAllText($rollbackCommon, $rollbackText.Replace("'rm', '--cached', '-f', '-r', '-q', '--ignore-unmatch'", "'rm', '--cached', '-r', '-q', '--ignore-unmatch'"), (New-Object Text.UTF8Encoding($false)))
    $r9d = Invoke-Tool -Script (Join-Path $toolsRollback 'check-release-tree.ps1') -ToolArgs @('-Tag', $Tag, '-Repo', $stand)
    $tagsAfter9 = (Invoke-Git -Path $stand -GitArgs @('tag', '-l')).Text
    $ok9 = ($r9a.Code -eq 0) -and ($r9b.Code -eq 0) -and ($r9c.Code -eq 0) -and ($r9d.Code -eq 1) -and ($tagsOnNew -eq '') -and ($tagsAfter9 -eq $standTagsBefore)
    Write-Step -Id '9' -Name 'регрессия: коммит сверх тега + -Rev без тега + откат фикса' -Expected 'check -Tag 0, make -Tag 0, check -Rev 0; на откате фикса → 1; тегов не прибавилось' -Actual ('коммитов сверх тега ' + $ahead9 + ', тег на новом коммите: «' + $tagsOnNew + '»; check ' + $r9a.Code + ', make ' + $r9b.Code + ', -Rev ' + $r9c.Code + ', откат ' + $r9d.Code + '; тегов ' + @($tagsAfter9 -split "`n" | Where-Object { $_ }).Count) -Ok $ok9 -Evidence 'одна команда: selftest-release-tree.ps1'

    # ── Опыт 10: версия ревизии против трёх CHANGELOG ─────────────────────────────────────────
    Write-Host ''
    Write-Host 'опыт 10: подложенный пустой раздел версии в CHANGELOG ревизии'
    $brokenChangelog = Join-Path $stand 'release\tree\CHANGELOG.zh.md'
    $changelogBackup = [IO.File]::ReadAllBytes($brokenChangelog)
    $version10 = Get-FirstMatch -Text $r4.Text -Pattern 'версия из ревизии: ([0-9.]+)'
    if (-not $version10) { $version10 = '2.1.0' }
    [IO.File]::WriteAllText($brokenChangelog, ('## ' + $version10 + "`n`n## 1.0.0`n`nстарое`n"), (New-Object Text.UTF8Encoding($false)))
    Invoke-Git -Path $stand -GitArgs @('add', '-A') | Out-Null
    $commit10 = Invoke-Git -Path $stand -GitArgs @('commit', '-q', '-m', 'selftest: пустой раздел версии в CHANGELOG.zh.md')
    $brokenCommit = (Invoke-Git -Path $stand -GitArgs @('rev-parse', 'HEAD')).Text
    $r10 = Invoke-Tool -Script $checkTool -ToolArgs @('-Rev', $brokenCommit, '-Repo', $stand)
    $ok10 = ($r10.Code -eq 1) -and ($r10.Text -like '*CHANGELOG.zh.md*') -and ($r10.Text -like '*пуст*')
    Write-Step -Id '10' -Name 'версия ревизии против трёх CHANGELOG' -Expected 'код 1, назван CHANGELOG.zh.md и «пуст»' -Actual ('код ' + $r10.Code + ', имя файла в выводе: ' + ($r10.Text -like '*CHANGELOG.zh.md*')) -Ok $ok10 -Evidence 'в режиме -Rev сверки «номер тега против <Version>» нет — эту дыру закрывает проверка трёх CHANGELOG'
    [IO.File]::WriteAllBytes($brokenChangelog, $changelogBackup)
}
catch {
    Write-Host ''
    Write-Host ('СТЕНД УПАЛ: регрессия не дошла до конца — ' + $_.Exception.Message)
    $script:fail++
}
finally {
    Write-Host ''
    $tagsFinally = (Invoke-Git -Path $repoRoot -GitArgs @('tag', '-l')).Text
    $tagsSame = ($tagsFinally -eq $tagsBefore)
    $repoDirtyNow = @((Invoke-Git -Path $repoRoot -GitArgs @('status', '--porcelain')).Lines | Where-Object { $_ }).Count
    Write-Host ('тегов в репозитории ПОСЛЕ: ' + @($tagsFinally -split "`n" | Where-Object { $_ }).Count + ' — ' + $(if ($tagsSame) { 'список не изменился' } else { 'СПИСОК ИЗМЕНИЛСЯ' }))
    Write-Host ('рабочее дерево проекта: ' + $(if ($repoDirtyNow -eq 0) { 'чисто' } else { $repoDirtyNow.ToString() + ' строк — так и должно быть, если правка ещё не закоммичена' }))
    if (-not $tagsSame) { $script:fail++ }
    if (-not $KeepTemp -and $work -and (Test-Path -LiteralPath $work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($KeepTemp) { Write-Host ('стенд оставлен: ' + $work) }
}

Write-Host ''
Write-Host ('ИТОГ: опытов ' + $results.Count + ', зелёных ' + @($results | Where-Object { $_.Ok }).Count + ', красных ' + @($results | Where-Object { -not $_.Ok }).Count)
if ($fail -gt 0) {
    Write-Host 'РЕГРЕССИЯ КРАСНАЯ: перечисленное выше — не «шум», а расхождение с обещанием инструмента'
    exit 1
}
Write-Host 'РЕГРЕССИЯ ЗЕЛЁНАЯ: обе точки входа работают при HEAD впереди тега, -Rev собирает дерево без тега, -Tree сверяет дерево коммита, падение инструмента называется своим именем'
exit 0
