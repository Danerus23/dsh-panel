# release-tree-common.ps1 — ОБЩАЯ ЧАСТЬ ДВУХ ИНСТРУМЕНТОВ ВЫПУСКА (ADR-001 §5.2 п. 1–3; правка владельца 17.10.2026).
#
#   . (Join-Path $PSScriptRoot 'release-tree-common.ps1')
#
# ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. Разрешённый список, правила отброса и формула ожидаемого ПОЛНОГО дерева
# нужны ОБОИМ инструментам: `check-release-tree` доказывает, `make-release-tree` собирает — и оба
# обязаны видеть один и тот же фильтр. Два списка однажды разойдутся, и «проверено» перестанет
# значить «совпало»; поэтому список и формула живут ОДНИМ файлом, а не копией в каждом инструменте.
#
# ЭТО НЕ ИНСТРУМЕНТ: сам по себе ничего не делает, кодов возврата не имеет, файлов не пишет
# (кроме временного индекса в %TEMP%, который за собой убирает).
#
# ФОРМУЛА ОЖИДАНИЯ (слово владельца, 17.10.2026): ожидаемое ПОЛНОЕ дерево выпуска считается
# ТОЛЬКО из объекта git — временный индекс: `read-tree <тег>` → `rm --cached` всего, чего нет
# в разрешённом списке → наложение 39 путей данных из `'<тег>:release/tree'` (в КОРЕНЬ, префикс
# снимается) → `write-tree`. Рабочее дерево репозитория при этом не читается вовсе.
#
# Флаги `-c core.autocrlf=false -c core.safecrlf=false` стоят на КАЖДОМ вызове git (в том числе на
# `archive`): замер 17.10.2026 — `git archive` сам применяет eol-настройку машины, и без флага
# блобы в черновом репозитории перестают совпадать с блобами тега на ИСПРАВНОМ дереве.
#
# ⚠️ `-f` У `git rm --cached` — НЕ УКРАШЕНИЕ (дефект найден владельцем 18.10.2026, воспроизведён
# капитаном). Индекс наполняется из ТЕГА/РЕВИЗИИ, а `HEAD` рабочего репозитория — обычно ДРУГОЙ коммит
# (после выпуска в дерево идут правки). Без `-f` git честно отказывает («staged content different from
# both the file and the HEAD»), индекс остаётся целым, ожидание НЕ считается — а инструмент печатал
# «ДЕРЕВО ВЫПУСКА НЕ СОВПАЛО», то есть падение ИНСТРУМЕНТА выдавал за расхождение ДЕРЕВА. Теперь `-f`
# стоит ВСЕГДА, а отказ `rm --cached` называется именем инструмента, а не приговором дереву.
#
# ВХОД — ТЕГ ИЛИ РЕВИЗИЯ (приёмка 2.1.1, 18.10.2026). Обе точки входа принимают `-Rev <sha>` и считают
# ожидание ТЕМ ЖЕ временным индексом (`read-tree <sha>` → `rm --cached -f` лишнего → наложение данных из
# `<sha>:release/tree` → `write-tree`): объект тега НЕ создаётся, вывод говорит «ревизия», а проверка
# «тег против <Version>» заменяется проверкой версии из `<sha>:src/DshPanel/DshPanel.csproj` и трёх
# CHANGELOG. Для git «тег» и «ревизия» — одно и то же имя объекта, поэтому весь код ниже общий, а
# различие живёт в подписях и в проверке версии.
#
# МЕРА ДЛЯ `-Tree` (Ф-9-2, 18.10.2026). Если названная папка — РЕПОЗИТОРИЙ, сверяется ДЕРЕВО КОММИТА
# (`<commit>^{tree}` и пары блобов из `ls-tree`), а не то, что лежит на диске: в отвергнутом коммите
# 2.1.0 диск совпал, а коммит — нет, и проверка диска дала ЛОЖНОЕ «0». Публикация — это коммит, значит
# и мера — коммит. Для простой папки (её пишет `make-release-tree`) коммита нет: там содержимое
# считается объектами git, и это честно называется в выводе.

# ── РАЗРЕШЁННЫЙ СПИСОК (ADR-001 §5.2 п. 1, дословно) ──────────────────────────────────────────
$script:ReleaseExactPaths = @(
    '.editorconfig'
    '.gitattributes'
    '.gitignore'
    'LICENSE'
    'Directory.Build.props'
    'Directory.Packages.props'
    'DshPanel.slnx'
    'global.json'
    'NuGet.config'
    'engine-lock.json'
    # ⚠️ ВТОРОЙ ЗАМОК НАБОРА (цикл 2.2.0, блок B) — И ЭТО НЕ «ЕЩЁ ОДИН ФАЙЛ РЯДОМ».
    # С линии 0.2.x панель ставит движок ДВУМЯ наборами: `engine-lock.json` (наша 0.1.5-rc.2, 588
    # пакетов) и `engine-lock-0.2.1-rc.2.json` (683 пакета). Оба ВСТРОЕНЫ В СБОРКУ как ресурсы
    # (`DshPanel.csproj` → `EngineTreeLock`), и суммы обоих закреплены в коде
    # (`EngineTreeLock.PinnedSum` / `PinnedSumOnKnown`). Значит без этого пути в дереве выпуска
    # панель у людей не смогла бы поставить линию 0.2.x вовсе — файл обязан быть в дереве.
    # Найдено проверкой дерева на ревизии `e51beb2` (задача `t16`): путь лежал в корне, а список
    # его не знал, и выпуск 2.2.0 был НЕВОЗМОЖЕН (код 1, «НЕЗНАКОМЫЙ отброшенный путь»).
    'engine-lock-0.2.1-rc.2.json'
    'engine-hints.json'
    '.github/FUNDING.yml'
)
$script:ReleaseDirs = @('assets/', 'installer/', 'src/', 'tests/', 'tools/')

# ── 39 ПУТЕЙ ДАННЫХ ВЫПУСКА (ADR-001 §5.2 п. 1; 30 → 39 — решение владельца 18.10.2026, цикл 2.1.1) ──
$script:ReleaseDataPaths = @(
    'README.md'
    'README.ru.md'
    'README.zh.md'
    'CHANGELOG.md'
    'CHANGELOG.ru.md'
    'CHANGELOG.zh.md'
    '.github/workflows/build.yml'
    'winget/Danerus23.DSHPanel.yaml'
    'winget/Danerus23.DSHPanel.installer.yaml'
    'winget/Danerus23.DSHPanel.locale.en-US.yaml'
    'docs/logo.png'
    'docs/demo.png'
)
foreach ($lang in @('ru', 'en', 'zh')) {
    # ⚠️ ДЕВЯТЬ ВИДОВ КАДРОВ, А НЕ ШЕСТЬ (решение владельца 18.10.2026, вариант «А»): к шести кадрам
    # 2.0.x добавлены три вида, которых на них не было, — раздел «Телефон», блок «Сервер DSH» и окно
    # первого запуска. Число путей данных выросло 30 → 39 и это СЛОВО ВЛАДЕЛЬЦА, а не удобство:
    # ADR-001 §5.2 п. 1 говорит «Число 30 — слово владельца; расхождение называется, а не подгоняется»,
    # поэтому сторож ниже переставлен вместе со списком, а не отключён.
    foreach ($shot in @('panel', 'settings', 'peaks', 'pricing-history', 'about', 'update', 'phone', 'server', 'first-run')) {
        $script:ReleaseDataPaths += ('docs/screenshots/' + $lang + '/' + $shot + '.png')
    }
}
if ($script:ReleaseDataPaths.Count -ne 39) {
    throw ('путей данных выпуска не 39, а ' + $script:ReleaseDataPaths.Count + ' — общая часть испорчена')
}

function Get-ReleaseGitFlags { return @('-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false') }

# ── ФЛАГИ ПЕРЕВОДОВ СТРОК: КОГДА СТАВИТЬ, А КОГДА НЕЛЬЗЯ (дефект найден 17.10.2026 на `ad69c85`) ──
#
# Флаги `-c core.autocrlf=false -c core.safecrlf=false` снимают настройку МАШИНЫ и нужны там, где
# сравниваются БЛОБЫ: `archive`, `add`, `write-tree`, `ls-tree`, `cat-file`, `hash-object`, `read-tree`,
# `rm --cached`, `update-index`, `ls-files`. Без них `git archive` отдаёт рабочую форму файла (LF→CRLF),
# и блобы чернового перестают совпадать с блобами тега на ИСПРАВНОМ дереве.
#
# ⚠️ НА ОПЕРАЦИЯХ ПО РАБОЧЕМУ ДЕРЕВУ (`status`, `diff`) ФЛАГИ СТАВИТЬ НЕЛЬЗЯ — ЭТО И БЫЛ ДЕФЕКТ.
# `status` сравнивает РАБОЧУЮ форму файла с блобом индекса. У свежего клона stat-записи индекса
# «racy» (mtime совпадает с секундой клонирования), и git сравнивает СОДЕРЖИМОЕ: при
# `core.autocrlf=false` CRLF рабочей копии сравнивается с LF блоба — и «изменёнными» оказываются все
# текстовые файлы. Замер на чистом клоне `ad69c85` (17.10.2026):
#   * обычный `git status --porcelain`                          → 0 строк;
#   * он же с принудительными флагами                            → 440 строк (`M .editorconfig`, …);
#   * после `git update-index -q --refresh` с флагами             → 0 — но достаточно тронуть mtime
#     одного файла, и снова 1: «освежение» лечит симптом, а не причину.
# ⚠️ Число ложных «изменённых» зависит от stat КОНКРЕТНОГО клона (440 сразу после клонирования,
# 11 после `fetch`+`reset --hard`), и `update-index --refresh` — тоже не лечение: у проверяющего
# после refresh те же 440. Поэтому НЕ предлагаем refresh как лечение — лечится сам вызов.
# Поэтому здесь ДВА помощника: с флагами (блобы) и без флагов (рабочее дерево).

# Путь принадлежит данным выпуска (они публикуются в КОРЕНЬ и потому живут в `docs/…` и `README.md`,
# куда внутренним файлам хода нет). Без этой оговорки самопроверка «в ожидании нет внутренних файлов»
# ругалась бы на собственные данные выпуска — замерено 17.10.2026: 21 ложное замечание.
function Test-ReleaseDataPath {
    param([string]$Path)
    return ($script:ReleaseDataPaths -ccontains $Path)
}

# Операции с БЛОБАМИ и ИНДЕКСОМ — с принудительными флагами переводов строк.
function Invoke-ReleaseGit {
    param([string]$Repo, [string[]]$GitArgs)
    $args = @()
    $args += (Get-ReleaseGitFlags)
    $args += @('-C', $Repo)
    $args += $GitArgs
    $out = & git @args 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Lines = @($out); Text = (($out | Out-String).TrimEnd()) }
}

# Операции, которые СМОТРЯТ НА РАБОЧЕЕ ДЕРЕВО (`status`, `diff`) — с настройками самого репозитория,
# без принудительных флагов. См. замер выше: флаг на `status` даёт 440 ложных «изменений» в свежем клоне.
function Invoke-WorktreeGit {
    param([string]$Repo, [string[]]$GitArgs)
    $args = @('-C', $Repo) + $GitArgs
    $out = & git @args 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Lines = @($out); Text = (($out | Out-String).TrimEnd()) }
}

# Почему путь отброшен. Порядок правил ВАЖЕН: «никогда» проверяется раньше «каталог целиком»,
# иначе `src\DshPanel\AGENTS.md` попал бы в выпуск как файл каталога `src\`.
function Get-ReleaseDiscardRule {
    param([string]$Path)
    if ($Path -match '(^|/)AGENTS\.md$') { return 'AGENTS.md (внутренняя инструкция агента)' }
    if ($Path -match '\.dshclaim$') { return 'dshclaim (служебный маркер заявок рабочей области)' }
    if ($Path -like 'docs/*') { return 'внутренние docs/**' }
    if ($Path -like 'release/*' || $Path -eq 'release') { return 'release/** (сам источник выпуска)' }
    if ($Path -like 'upd-build/*' || $Path -eq 'upd-build') { return 'upd-build/** (площадка сборки, вне git)' }
    if ($Path -like 'dist/*' || $Path -eq 'dist') { return 'dist/** (вывод сборки)' }
    if ($Path -match '(^|/)(bin|obj)/') { return 'bin/obj (вывод сборки)' }
    if ($Path -like '.git/*' || $Path -eq '.git') { return '.git (метаданные репозитория)' }
    if ($Path -eq 'README.md') { return 'внутренний README.md (его заменяет README данных выпуска)' }
    return $null
}

# ⚠️ НЕЗАВИСИМАЯ ПРОВЕРКА «ВНУТРЕННЕГО» — ПИШЕТСЯ ОТДЕЛЬНО ОТ КЛАССИФИКАТОРА (найдено мутацией
# CI-11, 17.10.2026). Смысл: если самопроверка пользуется тем же правилом, что и фильтр, то ошибка
# в правиле прячется за собой — «фильтр по голому имени» давал ожидание с 4 вложенными `AGENTS.md`
# и всё равно зелёный код 0. Здесь правило записано ВТОРОЙ раз и намеренно иначе (по имени файла,
# а не по регулярному выражению пути), чтобы совпадение двух записей что-то значило.
function Get-ReleaseInternalViolation {
    param([string]$Path)
    $leaf = ($Path -split '/')[-1]
    if ($leaf -eq 'AGENTS.md') { return 'AGENTS.md (внутренняя инструкция агента), на любой глубине' }
    if ($Path -match '\.dshclaim$') { return 'служебный маркер заявок рабочей области' }
    if (Test-ReleaseDataPath -Path $Path) { return $null }   # данные выпуска публикуются в корень — это не «внутреннее»
    if ($Path -like 'release/*') { return 'release/** (сам источник выпуска)' }
    if ($Path -like 'upd-build/*') { return 'upd-build/**' }
    if ($Path -like 'dist/*') { return 'dist/**' }
    if ($Path -like 'docs/*') { return 'внутренние docs/**' }
    if ($Path -eq 'README.md') { return 'внутренний README.md' }
    if ($Path -match '(^|/)(bin|obj)/') { return 'bin/obj' }
    if ($Path -like '.git/*') { return '.git' }
    return $null
}

# Классификация путей тега: что оставить кодом, что снять с индекса, а что ЗАМЕНИТЬ данными.
# Возвращает Kept (пути кода), Discarded (путь + правило, $null = НЕЗНАКОМЫЙ), Unknown (имена),
# Replacements (пути, которые есть и в теге, и в данных выпуска — блоб заменится, путь останется).
#
# ⚠️ ЗАМЕНА — ЭТО НЕ «ЛИШНИЙ ПУТЬ» (конвенция счёта названа владельцем 17.10.2026). На `75db96b`
# такой путь ОДИН: корневой внутренний `README.md` (блоб 7a4b7c44…, 95 630 Б) — на его место
# публикуется `README.md` данных выпуска (36d7f89d…, 11 013 Б). Числа ниже — замер ТОЙ ревизии
# (тогда путей данных было 30; с 18.10.2026 их 39 — см. список выше), поэтому:
#   * `git rm --cached` снимает 77 путей (42 внутренних docs + 5 AGENTS.md + 30 release/tree);
#   * наложение данных даёт 30 = 29 НОВЫХ + 1 ЗАМЕНА; счёт: 489 − 77 + 29 = 441;
#   * «не уехало содержимым» — 78: к 77 снятым добавляется внутреннее содержимое README.md.
function Get-ReleaseSelection {
    param([string[]]$TagPaths)
    $kept = New-Object System.Collections.Generic.List[string]
    $discarded = New-Object System.Collections.Generic.List[object]
    $unknown = New-Object System.Collections.Generic.List[string]
    $replacements = New-Object System.Collections.Generic.List[string]
    $tagSet = @{}
    foreach ($path in $TagPaths) { $tagSet[$path] = $true }
    $replaceMap = @{}
    foreach ($path in $script:ReleaseDataPaths) {
        if ($tagSet.ContainsKey($path)) { $replacements.Add($path) | Out-Null; $replaceMap[$path] = $true }
    }
    foreach ($path in $TagPaths) {
        if ($replaceMap.ContainsKey($path)) { continue }   # остаётся в индексе; блоб заменится наложением
        $rule = Get-ReleaseDiscardRule -Path $path
        if ($rule) { $discarded.Add([pscustomobject]@{ Path = $path; Rule = $rule }) | Out-Null; continue }
        if ($script:ReleaseExactPaths -ccontains $path) { $kept.Add($path) | Out-Null; continue }
        $inDir = $false
        foreach ($dir in $script:ReleaseDirs) { if ($path.StartsWith($dir, [StringComparison]::Ordinal)) { $inDir = $true; break } }
        if ($inDir) { $kept.Add($path) | Out-Null; continue }
        $unknown.Add($path) | Out-Null
        $discarded.Add([pscustomobject]@{ Path = $path; Rule = $null }) | Out-Null
    }
    $byRule = @($discarded | Where-Object { $_.Rule } | Group-Object Rule | ForEach-Object {
        [pscustomobject]@{ Rule = $_.Name; Count = $_.Count }
    })
    # ⚠️ `.ToArray()`, а не `@($list)`: замерено 17.10.2026 — `@()` вокруг `List[object]` с
    # PSCustomObject внутри, да ещё в литерале `[pscustomobject]@{…}`, падает с «Argument types do
    # not match» (PowerShell превращает список в Object[] и путается). Список отдаёт массив сам.
    return [pscustomobject]@{
        Kept         = $kept.ToArray()
        Discarded    = $discarded.ToArray()
        Unknown      = $unknown.ToArray()
        Replacements = $replacements.ToArray()
        ByRule    = $byRule
    }
}

# Карта путей дерева/поддерева: путь → режим и блоб (из объекта git, не из файловой системы).
# `-Prefix` нужен для собранного дерева: его git-каталог лежит в %TEMP%, и без `--git-dir`
# обычный `git -C <папка>` репозитория не находит (замерено 17.10.2026: карта возвращалась пустой,
# и проверка падала «Невозможно индексировать в массив значений null»).
function Get-ReleaseTreeEntries {
    param([string]$Repo, [string]$Spec, [string[]]$Prefix = @())
    $gitArgs = @()
    $gitArgs += $Prefix
    $gitArgs += @('ls-tree', '-r', '--full-tree', $Spec)
    $result = Invoke-ReleaseGit -Repo $Repo -GitArgs $gitArgs
    if ($result.Code -ne 0) { return $null }
    $map = @{}
    foreach ($line in $result.Lines) {
        if (-not $line) { continue }
        $tab = $line.IndexOf("`t")
        if ($tab -lt 0) { continue }
        $meta = $line.Substring(0, $tab) -split '\s+'
        $map[$line.Substring($tab + 1)] = [pscustomobject]@{ Mode = $meta[0]; Type = $meta[1]; Blob = $meta[2] }
    }
    return $map
}

# Наложение данных: 39 путей из `'<тег|ревизия>:release/tree'`, префикс снимается — публикуются в КОРЕНЬ.
function Get-ReleaseDataOverlay {
    param([string]$Repo, [string]$Spec)
    $entries = Get-ReleaseTreeEntries -Repo $Repo -Spec ($Spec + ':release/tree')
    return $entries
}

# ОЖИДАНИЕ: полное дерево выпуска из объекта git (формула владельца, §3 записки).
# `-Spec` — тег ИЛИ ревизия: и то, и другое git понимает как имя объекта, и формула одна и та же.
function Get-ExpectedFullTree {
    param([string]$Repo, [string]$Spec, $Selection, $DataOverlay, [string]$TempRoot)
    if (-not (Test-Path -LiteralPath $TempRoot)) { New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null }
    # ⚠️ Имя `$tag` здесь НЕЛЬЗЯ: PowerShell не различает регистр, и локальная `$tag` затирает
    # одноимённый параметр — замерено 17.10.2026 (git read-tree уходил с обрывком guid вместо имени тега).
    $stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $indexFile = Join-Path $TempRoot ('index-expected-' + $stamp)
    $pathspecFile = Join-Path $TempRoot ('drop-' + $stamp + '.nul')
    if (Test-Path -LiteralPath $indexFile) { Remove-Item -LiteralPath $indexFile -Force }

    $dropped = @($Selection.Discarded | ForEach-Object { $_.Path })
    if ($dropped.Count -gt 0) {
        # Список путей подаётся ФАЙЛОМ и через NUL: в командную строку 489 путей не влезут,
        # а переводы строк в путях сделали бы список двусмысленным.
        [IO.File]::WriteAllText($pathspecFile, (($dropped -join "`0") + "`0"), (New-Object Text.UTF8Encoding($false)))
    }

    $oldIndex = $env:GIT_INDEX_FILE
    $env:GIT_INDEX_FILE = $indexFile
    try {
        $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('read-tree', $Spec)
        if ($r.Code -ne 0) { throw ('git read-tree ' + $Spec + ' не сработал: ' + $r.Text) }

        if ($dropped.Count -gt 0) {
            # ⚠️ `-f` ОБЯЗАТЕЛЕН — см. шапку файла. Индекс здесь из тега/ревизии, а `HEAD` рабочего
            # репозитория обычно ДРУГОЙ коммит; без `-f` git отказывает, и падение инструмента
            # выдавалось за «дерево не совпало» (дефект 18.10.2026). Проверка на это — мутация
            # «убрать -f» в `selftest-release-tree.ps1`, опыт 6.
            $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('rm', '--cached', '-f', '-r', '-q', '--ignore-unmatch',
                ('--pathspec-from-file=' + $pathspecFile), '--pathspec-file-nul')
            if ($r.Code -ne 0) { throw ('ИНСТРУМЕНТ: git rm --cached не смог снять с индекса отброшенные пути (код ' + $r.Code + '): ' + $r.Text) }
        }

        foreach ($path in $DataOverlay.Keys) {
            $entry = $DataOverlay[$path]
            $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('update-index', '--add', '--cacheinfo', ($entry.Mode + ',' + $entry.Blob + ',' + $path))
            if ($r.Code -ne 0) { throw ('git update-index --cacheinfo не сработал для ' + $path + ': ' + $r.Text) }
        }

        $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('write-tree')
        if ($r.Code -ne 0) { throw ('git write-tree не сработал: ' + $r.Text) }
        $tree = $r.Text.Trim()

        # ⚠️ ПУСТОЕ ДЕРЕВО — ЭТО ПАДЕНИЕ, А НЕ УСПЕХ (найдено 17.10.2026). Если список путей подан
        # с CRLF-концами, git на каждой строке говорит «Ignoring path …», индекс остаётся пустым, и
        # `write-tree` возвращает 4b825dc642cb6eb9a060e54bf8d69288fbee4904 — хеш ПУСТОГО дерева.
        # Сравнение с ожиданием тогда «сошлось бы» только с пустотой, то есть проверка не падала бы.
        if ($tree -eq '4b825dc642cb6eb9a060e54bf8d69288fbee4904') {
            throw 'индекс пуст, список путей не подан: git write-tree вернул хеш пустого дерева 4b825dc6… — ожидание считать не из чего (список путей обязан быть с LF или NUL-концами)'
        }

        $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('ls-files')
        if ($r.Code -ne 0) { throw ('git ls-files не сработал: ' + $r.Text) }
        $paths = @($r.Lines | Where-Object { $_ })
    } finally {
        $env:GIT_INDEX_FILE = $oldIndex
        if (Test-Path -LiteralPath $indexFile) { Remove-Item -LiteralPath $indexFile -Force }
        if (Test-Path -LiteralPath $pathspecFile) { Remove-Item -LiteralPath $pathspecFile -Force }
    }

    $entries = Get-ReleaseTreeEntries -Repo $Repo -Spec $tree
    if ($null -eq $entries) { throw ('не удалось прочитать ожидаемое дерево ' + $tree) }

    return [pscustomobject]@{
        Tree      = $tree
        Paths     = $paths
        Entries   = $entries
        CodeCount = $Selection.Kept.Count
        DataCount = $DataOverlay.Count
        Total     = $paths.Count
    }
}

# Хеш дерева, собранного НА ДИСКЕ в папке: чистый git-каталог в %TEMP%, `add -A`, `write-tree`.
# Никакого `.git` в самой папке не появляется — папка остаётся такой, какой её увидит человек.
function Get-FolderTreeHash {
    param([string]$Repo, [string]$Folder, [string]$TempRoot)
    if (-not (Test-Path -LiteralPath $TempRoot)) { New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null }
    $gitDir = Join-Path $TempRoot ('git-folder-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    # ⚠️ Собирается ДВУМЯ присваиваниями, а не литералом `@('a' + $x, 'b' + $y)`: запятая связывает
    # сильнее `+`, и такой литерал даёт один склеенный аргумент — замерено 17.10.2026 («fatal: Invalid
    # path '…/git-folder-fe6e03b8 --work-tree=C:'»: git получил оба ключа одной строкой).
    $prefix = @()
    $prefix += ('--git-dir=' + $gitDir)
    $prefix += ('--work-tree=' + $Folder)
    try {
        $r = Invoke-ReleaseGit -Repo $Folder -GitArgs (@() + $prefix + @('init', '-q'))
        if ($r.Code -ne 0) { throw ('git init во временном каталоге не сработал: ' + $r.Text) }
        $r = Invoke-ReleaseGit -Repo $Folder -GitArgs (@() + $prefix + @('add', '-A'))
        if ($r.Code -ne 0) { throw ('git add -A в собранном дереве не сработал: ' + $r.Text) }
        $r = Invoke-ReleaseGit -Repo $Folder -GitArgs (@() + $prefix + @('write-tree'))
        if ($r.Code -ne 0) { throw ('git write-tree в собранном дереве не сработал: ' + $r.Text) }
        $tree = $r.Text.Trim()
        $r = Invoke-ReleaseGit -Repo $Folder -GitArgs (@() + $prefix + @('ls-files'))
        $paths = @($r.Lines | Where-Object { $_ })
        $entries = Get-ReleaseTreeEntries -Repo $Folder -Spec $tree -Prefix $prefix
        if ($null -eq $entries) { throw ('не удалось прочитать дерево собранного каталога ' + $tree) }
    } finally {
        if (Test-Path -LiteralPath $gitDir) { Remove-Item -LiteralPath $gitDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    return [pscustomobject]@{ Tree = $tree; Paths = $paths; Entries = $entries }
}

# Размер и SHA-256 ОБЪЕКТА git — одним вызовом `cat-file --batch` на все блобы.
# `git cat-file blob` по одному на 445 файлов — это 445 процессов; здесь один.
function Get-ReleaseBlobInfo {
    param([string]$Repo, [string[]]$Shas, [string]$TempRoot)
    $map = @{}
    $unique = @($Shas | Where-Object { $_ } | Sort-Object -Unique)
    if ($unique.Count -eq 0) { return $map }
    if (-not (Test-Path -LiteralPath $TempRoot)) { New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null }
    $stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $inFile = Join-Path $TempRoot ('batch-in-' + $stamp + '.txt')
    $outFile = Join-Path $TempRoot ('batch-out-' + $stamp + '.bin')
    [IO.File]::WriteAllText($inFile, (($unique -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    $flags = (Get-ReleaseGitFlags) -join ' '
    & cmd /c ('git ' + $flags + ' -C "' + $Repo + '" cat-file --batch < "' + $inFile + '" > "' + $outFile + '"')
    if ($LASTEXITCODE -ne 0) { throw ('git cat-file --batch не сработал (код ' + $LASTEXITCODE + ')') }

    $bytes = [IO.File]::ReadAllBytes($outFile)
    $pos = 0
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        while ($pos -lt $bytes.Length) {
            $nl = [Array]::IndexOf($bytes, [byte]10, $pos)
            if ($nl -lt 0) { break }
            $header = [Text.Encoding]::ASCII.GetString($bytes, $pos, $nl - $pos)
            $pos = $nl + 1
            $parts = $header -split ' '
            if ($parts.Count -lt 3) { continue }   # «<sha> missing» — такого объекта нет
            $sha = $parts[0]
            $size = [int64]$parts[2]
            if ($pos + $size -gt $bytes.Length) { throw ('cat-file --batch отдал неполный объект ' + $sha) }
            $digest = $sha256.ComputeHash($bytes, [int]$pos, [int]$size)
            $map[$sha] = [pscustomobject]@{
                Size   = $size
                Sha256 = ([BitConverter]::ToString($digest)).Replace('-', '')
            }
            $pos = $pos + $size + 1
        }
    } finally {
        $sha256.Dispose()
        if (Test-Path -LiteralPath $inFile) { Remove-Item -LiteralPath $inFile -Force }
        if (Test-Path -LiteralPath $outFile) { Remove-Item -LiteralPath $outFile -Force }
    }
    return $map
}

# Пути архива (список того, что уедет) — независимо от состава по `ls-tree`.
function Get-ReleaseArchiveEntries {
    param([string]$TarPath)
    if (Get-Command tar -ErrorAction SilentlyContinue) {
        return @(& tar -tf $TarPath 2>$null | ForEach-Object { $_.Replace('\', '/') })
    }
    $entries = New-Object System.Collections.Generic.List[string]
    $reader = [System.Formats.Tar.TarReader]::new($TarPath)
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) { $entries.Add($entry.Name.Replace('\', '/')) | Out-Null }
    } finally { $reader.Dispose() }
    return @($entries)
}

# ── ВХОД «РЕВИЗИЯ» И ПРОВЕРКА ВЕРСИИ В НЕЙ (приёмка 2.1.1, 18.10.2026) ───────────────────────
#
# Полный sha КОММИТА по любому имени ревизии (сокращение, `HEAD`, `refs/tags/...`) — или $null.
# `^{commit}` намеренно: тег в git аннотированный, и «ревизия тега» — это коммит, а не объект тега.
# Объект тега здесь НЕ создаётся и не нужен: приёмка 2.1.1 идёт ВРЕМЕННЫМ ИНДЕКСОМ, без тега.
function Resolve-ReleaseCommit {
    param([string]$Repo, [string]$Rev)
    if (-not $Rev) { return $null }
    $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('rev-parse', '--verify', '--quiet', ($Rev + '^{commit}'))
    if ($r.Code -ne 0) { return $null }
    $sha = @($r.Lines | Where-Object { $_ }) | Select-Object -First 1
    if (-not $sha) { return $null }
    return ([string]$sha).Trim()
}

# `<Version>` из `src\DshPanel\DshPanel.csproj` РЕВИЗИИ (не с диска). Возвращает $null, если файла нет.
function Get-ReleaseProjectVersion {
    param([string]$Repo, [string]$Spec)
    $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('show', ($Spec + ':src/DshPanel/DshPanel.csproj'))
    if ($r.Code -ne 0) { return $null }
    $found = [regex]::Matches($r.Text, '<Version>([^<]+)</Version>')
    if ($found.Count -eq 0) { return $null }
    return $found[0].Groups[1].Value.Trim()
}

# Три CHANGELOG ревизии против её `<Version>` — ТЕ ЖЕ правила, что у `check-changelogs.ps1`: раздел
# новой версии есть в каждом файле, стоит ПЕРВЫМ и НЕПУСТОЙ. Зачем здесь: в режиме `-Rev` сверки
# «номер тега против <Version>» нет (тега нет), и единственная связка версии с историей — эта.
#
# ⚠️ Диапазон PowerShell ОБРАТНЫЙ, когда конец меньше начала: у ПУСТОГО раздела выражение
# ($first+1)..($end-1) даёт «5..4», и телом «пустого» раздела становятся заголовок и строка перед ним.
# Этот дефект ловили мутацией M2 (17.10.2026); здесь он закрыт тем же способом — диапазон собирается
# только прямым. Мутация на эту проверку — опыт 10 в `selftest-release-tree.ps1`.
function Get-ReleaseChangelogProblems {
    param([string]$Repo, [string]$Spec, [string]$Version)
    $problems = New-Object System.Collections.Generic.List[string]
    $files = @(
        [pscustomobject]@{ Name = 'CHANGELOG.ru.md'; Title = 'русский (оригинал)' }
        [pscustomobject]@{ Name = 'CHANGELOG.md';    Title = 'английский' }
        [pscustomobject]@{ Name = 'CHANGELOG.zh.md'; Title = 'китайский' }
    )
    foreach ($file in $files) {
        $path = 'release/tree/' + $file.Name
        $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('show', ($Spec + ':' + $path))
        if ($r.Code -ne 0) {
            $problems.Add($file.Name + ' (' + $file.Title + ') — файла нет в ревизии: ' + $path) | Out-Null
            continue
        }
        $lines = @($r.Text.Replace("`r`n", "`n") -split "`n")
        $headings = @()
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^##\s+') { $headings += $i } }
        if ($headings.Count -eq 0) {
            $problems.Add($file.Name + ' (' + $file.Title + ') — ни одного раздела «## версия»') | Out-Null
            continue
        }
        $first = $headings[0]
        $firstTitle = $lines[$first].Trim()
        if ($firstTitle -notmatch ('^##\s+' + [regex]::Escape($Version) + '(\s|$)')) {
            $problems.Add($file.Name + ' (' + $file.Title + ') — первым разделом стоит «' + $firstTitle + '», а не «## ' + $Version + '»') | Out-Null
            continue
        }
        $end = if ($headings.Count -gt 1) { $headings[1] } else { $lines.Count }
        $body = @()
        if ($end -gt $first + 1) { $body = @($lines[($first + 1)..($end - 1)] | Where-Object { $_.Trim().Length -gt 0 }) }
        if ($body.Count -eq 0) {
            $problems.Add($file.Name + ' — раздел «' + $Version + '» пуст: описание выпуска соберётся пустым') | Out-Null
        }
    }
    return $problems.ToArray()
}

# ── МЕРА ДЛЯ `-Tree`: ДЕРЕВО КОММИТА, а не диск (Ф-9-2, 18.10.2026) ───────────────────────────
#
# Возвращает сведения о КОММИТЕ папки или $null, если папка — не корень репозитория (или у неё нет
# `HEAD`). Тогда и только тогда вызывающий считает содержимое папки объектами git.
# ⚠️ `--show-toplevel` сверяется с самой папкой: иначе подкаталог чужого репозитория молча сверялся
# бы по ЧУЖОМУ коммиту — то есть проверка отвечала бы не на тот вопрос.
function Get-RepositoryTree {
    param([string]$Folder)
    $top = Invoke-WorktreeGit -Repo $Folder -GitArgs @('rev-parse', '--show-toplevel')
    if ($top.Code -ne 0) { return $null }
    $topPath = @($top.Lines | Where-Object { $_ }) | Select-Object -First 1
    if (-not $topPath) { return $null }
    $topFull = [IO.Path]::GetFullPath(([string]$topPath).Trim())
    $folderFull = [IO.Path]::GetFullPath($Folder).TrimEnd('\', '/')
    if ($topFull.TrimEnd('\', '/') -ne $folderFull) { return $null }

    $head = Invoke-WorktreeGit -Repo $Folder -GitArgs @('rev-parse', '--verify', '--quiet', 'HEAD')
    if ($head.Code -ne 0) { return $null }
    $commit = @($head.Lines | Where-Object { $_ }) | Select-Object -First 1
    if (-not $commit) { return $null }
    $commit = ([string]$commit).Trim()

    $treeResult = Invoke-WorktreeGit -Repo $Folder -GitArgs @('rev-parse', '--verify', '--quiet', ($commit + '^{tree}'))
    if ($treeResult.Code -ne 0) { return $null }
    $tree = @($treeResult.Lines | Where-Object { $_ }) | Select-Object -First 1
    if (-not $tree) { return $null }
    $tree = ([string]$tree).Trim()

    $entries = Get-ReleaseTreeEntries -Repo $Folder -Spec $tree
    if ($null -eq $entries) { return $null }

    $status = Invoke-WorktreeGit -Repo $Folder -GitArgs @('status', '--porcelain')
    $dirty = if ($status.Code -eq 0) { @($status.Lines | Where-Object { $_ }).Count } else { -1 }

    return [pscustomobject]@{
        Commit     = $commit
        Tree       = $tree
        Paths      = @($entries.Keys | Sort-Object)
        Entries    = $entries
        DirtyLines = $dirty
    }
}
