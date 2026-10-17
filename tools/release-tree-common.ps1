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
# в разрешённом списке → наложение 30 путей данных из `'<тег>:release/tree'` (в КОРЕНЬ, префикс
# снимается) → `write-tree`. Рабочее дерево репозитория при этом не читается вовсе.
#
# Флаги `-c core.autocrlf=false -c core.safecrlf=false` стоят на КАЖДОМ вызове git (в том числе на
# `archive`): замер 17.10.2026 — `git archive` сам применяет eol-настройку машины, и без флага
# блобы в черновом репозитории перестают совпадать с блобами тега на ИСПРАВНОМ дереве.

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
    'engine-hints.json'
    '.github/FUNDING.yml'
)
$script:ReleaseDirs = @('assets/', 'installer/', 'src/', 'tests/', 'tools/')

# ── 30 ПУТЕЙ ДАННЫХ ВЫПУСКА (ADR-001 §5.2 п. 1) ───────────────────────────────────────────────
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
    foreach ($shot in @('panel', 'settings', 'peaks', 'pricing-history', 'about', 'update')) {
        $script:ReleaseDataPaths += ('docs/screenshots/' + $lang + '/' + $shot + '.png')
    }
}
if ($script:ReleaseDataPaths.Count -ne 30) {
    throw ('путей данных выпуска не 30, а ' + $script:ReleaseDataPaths.Count + ' — общая часть испорчена')
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
# публикуется `README.md` данных выпуска (36d7f89d…, 11 013 Б). Поэтому:
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

# Наложение данных: 30 путей из `'<тег>:release/tree'`, префикс снимается — публикуются в КОРЕНЬ.
function Get-ReleaseDataOverlay {
    param([string]$Repo, [string]$Tag)
    $entries = Get-ReleaseTreeEntries -Repo $Repo -Spec ($Tag + ':release/tree')
    return $entries
}

# ОЖИДАНИЕ: полное дерево выпуска из объекта git (формула владельца, §3 записки).
function Get-ExpectedFullTree {
    param([string]$Repo, [string]$Tag, $Selection, $DataOverlay, [string]$TempRoot)
    if (-not (Test-Path -LiteralPath $TempRoot)) { New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null }
    # ⚠️ Имя `$tag` здесь НЕЛЬЗЯ: PowerShell не различает регистр, и локальная `$tag` затирает
    # параметр `$Tag` — замерено 17.10.2026 (git read-tree уходил с обрывком guid вместо имени тега).
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
        $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('read-tree', $Tag)
        if ($r.Code -ne 0) { throw ('git read-tree ' + $Tag + ' не сработал: ' + $r.Text) }

        if ($dropped.Count -gt 0) {
            $r = Invoke-ReleaseGit -Repo $Repo -GitArgs @('rm', '--cached', '-r', '-q', '--ignore-unmatch',
                ('--pathspec-from-file=' + $pathspecFile), '--pathspec-file-nul')
            if ($r.Code -ne 0) { throw ('git rm --cached не сработал: ' + $r.Text) }
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
