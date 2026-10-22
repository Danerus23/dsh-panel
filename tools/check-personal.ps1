# check-personal.ps1 — сторожок личных данных. Перенесён из панели 1.x
# (tools\check-personal.ps1) и приспособлен к раскладке 2.0.
#
#   pwsh -NoProfile -File .\tools\check-personal.ps1
#   pwsh -NoProfile -File .\tools\check-personal.ps1 -Root <каталог>
#   pwsh -NoProfile -File .\tools\check-personal.ps1 -History     # ещё и вся история коммитов
#
# ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. В правилах 2.0 записано: «автопроверки личных данных нет — перед
# любым push в публичный репозиторий смотреть глазами». Это правило, а не вопрос владельцу,
# и оно остаётся. Но глазами не смотрят то, что можно посчитать: путь к папке пользователя и
# имя машины — это строки, и они ищутся машиной вернее, чем взглядом. Скрипт НЕ отменяет
# правило «смотреть глазами»: он его исполняет на той части, которую можно проверить.
#
# Что ищет в текстовых файлах, которые уедут в публичный репозиторий:
#   1. путь к папке пользователя ЭТОЙ машины — в ОБЕИХ записях, `C:\Users\<имя>` и `C:/Users/<имя>`
#      (B-09: прямые слэши пишут JSON, URL, docker/CI-конфиги и чужой код), — и имя компьютера;
#   2. СЕКРЕТЫ: чужие токены (ghp_/gho_/ghu_/ghs_/ghr_/sk- длиной от 20 знаков); **токен входа
#      самой панели** — `token=` и 43 знака base64url (32 байта; так он стоит в ссылке входа,
#      `docs\ENGINE.md` §1), в том числе JSON-формой `"token":"…"`; **закрытые ключи**
#      (`-----BEGIN … PRIVATE KEY-----` — RSA, EC, OPENSSH, PKCS#8);
#   3. в ПУБЛИЧНЫХ текстах — голос модели, обращённый к своему человеку («по просьбе владельца»,
#      «решение владельца»), и заверения «личных данных и путей нет»: на GitHub такие строки
#      читает сообщество, и им там не место. Нашёл это владелец по снимку README в 1.x.
#
# ⚠️ **ГДЕ ИЩЕТ — ТРИ МЕСТА, А НЕ ОДНО (B-08 цикла 2.3.2, 22.10.2026).** Было: только рабочее
# дерево (`git ls-files --cached --others` даёт ИМЕНА, а читался файл с диска), поэтому
# подготовленная к коммиту строка ловилась, лишь пока она лежит в файле. Пропадали два случая,
# и оба настоящие:
#   * **индекс git** — в индексе лежит другое содержимое, чем на диске (правку уже добавили,
#     а в файле её убрали), либо файла на диске нет вовсе (его сняли, не сняв с индекса);
#   * **история коммитов** — то, что уже уехало в репозиторий: этого в дереве и индексе нет
#     и не будет НИКОГДА.
# Теперь по умолчанию смотрятся **дерево + индекс + новые файлы**, а история — ключом `-History`
# (почему не по умолчанию — написано ниже, у самого ключа).
#
# ⚠️ ЧЕГО НЕ ПОКРЫВАЕТ (важно не переоценить вердикт — он печатается и в выводе):
#   * смотрит ТОЛЬКО текстовые расширения: PNG, JPEG, ICO, ZIP и EXE не читаются вовсе,
#     поэтому про снимки и архивы вердикт не говорит НИЧЕГО. Именно поэтому настоящее имя
#     машины в кадре однажды нашёл OCR, а не эта проверка;
#   * «файлы репозитория» — это индекс git ПЛЮС новые файлы, ещё не добавленные в индекс
#     (список берётся `git ls-files --cached --others --exclude-standard`), поэтому проверка
#     годится и ДО коммита. Когда git недоступен, идёт обход папки — и его вердикт шире
#     («мимо» может попасть чужое рабочее дерево), поэтому об этом печатается отдельно;
#   * **история** по умолчанию НЕ смотрится — это названо в выводе отдельной строкой; ключ
#     `-History` смотрит ВСЮ историю коммитов (все ветки, все коммиты), и тогда в выводе
#     сказано, сколько коммитов смотрено;
#   * в истории ключом `-History` ищутся ЛИЧНЫЕ МАРКЕРЫ, ПУТИ МАШИНЫ и ТОКЕНЫ — голос модели
#     там не ищется: это стиль публичного текста, а не данные, и в исторических README он ожидаем
#     (в дереве и индексе он проверяется всегда);
#   * папки сборки, выпуска и лаборатории пропускаются: там лежат копии исходников, и находка
#     в копии — не находка в репозитории.
#
# ЧТО ЛИЧНЫМ НЕ СЧИТАЕТСЯ (и почему это не дыра):
#   * имя автора `Danerus23` — это копирайт, владелец репозитория и издатель: в публичном
#     репозитории оно законно и обязано там быть;
#   * адрес `github.com/Danerus23/dsh-panel` и адрес донатов — продукт для людей, а не данные
#     о машине;
#   * термин «владелец» в смысле прав доступа («каталог закрывается на владельца») — не голос
#     модели; шаблоны ниже подобраны по ФРАЗАМ, а не по слову.
#
# Коды возврата: 0 — чисто, 1 — найдено, 2 — нечего проверять (неприменимо). Второй код — ещё
# и «проверить не смогли»: если спросили `-History`, а git нет, это НЕ зелёный вердикт.

param(
    [string]$Root = '',
    [switch]$History
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

# ⚠️ ПРОПАВШИЙ ВХОД = ОТКАЗ, А НЕ АВАРИЯ (t15, 17.10.2026). Было: `-Root <нет такого>` роняло
# Resolve-Path — PowerShell печатал «не удаётся найти путь» и код 1, то есть не наш отказ, а
# исключение. Замер до правки: код 1 и строка .NET вместо названного отказа.
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host ('неприменимо: под корнем «' + $Root + '» нет каталога — проверять нечего')
    exit 2
}
$Root = (Resolve-Path -LiteralPath $Root).Path

# Только текстовые файлы: PNG и ZIP как текст разбирать бессмысленно.
$textExtensions = @('.cs', '.md', '.json', '.ps1', '.psm1', '.iss', '.yml', '.yaml', '.csproj',
    '.props', '.targets', '.slnx', '.config', '.txt', '.mjs', '.js', '.cmd', '.bat', '.axaml')
$opensWith = @('.gitignore', '.gitattributes', '.editorconfig')

# Папки, в которых лежат КОПИИ исходников, а не исходники: находка там не уезжает в репозиторий.
$skipFirst = @('bin', 'obj', 'dist', 'upd-build', '.git', '.vs', '.nuget')

$markers = @()
if ($env:USERNAME) {
    $markers += ('C:\Users\' + $env:USERNAME)

    # ⚠️ ТОТ ЖЕ путь, НО С ПРЯМЫМИ СЛЭШАМИ — отдельная строка, и это не дубль (B-09). Домашний
    # каталог пишут через `/` там, где обратный слэш неудобен или запрещён: в JSON, в URL,
    # в конфигах docker/CI, в путях чужого кода. Замер: прежний сторож такой строки не видел
    # вовсе — искал только `C:\Users\<имя>`.
    $markers += ('C:/Users/' + $env:USERNAME)
}
if ($env:COMPUTERNAME) { $markers += $env:COMPUTERNAME }

# ВТОРОЙ НАБОР — ПУТИ ЭТОЙ МАШИНЫ, КОТОРЫХ В ВЫПУСКЕ БЫТЬ НЕ ДОЛЖНО (заведён 10.10.2026).
# Рабочая область и репозиторий — тоже машинные пути. Личными данными человека они не являются,
# но в том, что уезжает к людям, означают ровно то же: «написано на этой машине и работает только
# на ней». Тогда это и нашлось — в `src\DshPanel\Update\UpdateDownload.cs` временная отладка
# писала в `C:\Harness\…\dbgA.txt`; у всех остальных людей такого каталога нет, `AppendAllText`
# бросил бы исключение вне `try`, и обновление панели падало бы У КАЖДОГО, а сторожа молчали:
# ни имени пользователя, ни имени машины в той строке не было.
#
# ⚠️ Смотрятся эти пути ТОЛЬКО там, где они и опасны: продуктовые исходники (`src\`), установщик
# и публичные тексты. Во внутренних документах путь рабочей области — обычное дело
# (`docs\…` называют `upd-build\…`), и запрет там сделал бы сторожа крикливым, а крикливый
# сторож не сторож. Родитель диска (`C:\`) в набор не попадает: он нашёлся бы в каждом пути.
$machineMarkers = @()
$workspace = Split-Path -Parent $Root
if ($workspace -and (Split-Path -Parent $workspace)) { $machineMarkers += $workspace }
if ($Root -and (Split-Path -Parent $Root)) { $machineMarkers += $Root }

if ($markers.Count -eq 0) {
    Write-Host 'не знаю, что искать: не видно ни имени пользователя, ни имени машины'
    exit 2
}

# Список файлов дерева: всё, что уедет в публичный репозиторий, — отслеживаемое И новое, ещё не
# добавленное в индекс (но не игнорируемое). Если git недоступен — обход папки.
$list = @()
$fromGit = $false
$hasGit = $false

if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $Root '.git'))) {
    $hasGit = $true

    # core.quotepath=false — чтобы имена с кириллицей приходили КАК ЕСТЬ: иначе git отдаёт их
    # экранированными (\320\272…), Join-Path строит несуществующий путь, и файл тихо выпадает из проверки.
    $tracked = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 0) {
        $list = @($tracked | ForEach-Object { Join-Path $Root $_ })
        $fromGit = $true
    }
}

if ($list.Count -eq 0) {
    $list = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $skipFirst -notcontains ($_.FullName.Substring($Root.Length).TrimStart('\') -split '\\')[0] -and
            $_.FullName -notmatch '\\_lab[^\\]*\\'
        } |
        Select-Object -ExpandProperty FullName)
}

# РАБОЧИЕ ДОКУМЕНТЫ проекта (`AGENTS.md`, `docs\…`) в этот список НЕ входят, и это не
# поблажка: в них «решение владельца» — рабочий термин («кто принял решение и какое»), а не
# голос модели, обращённый к человеку. Находка там не значит ничего, а находок там 140 —
# проверка, кричащая на всех, не проверка. Публичными здесь считаются только те файлы,
# которые уезжают к людям: данные выпуска (README ×3, CHANGELOG ×3, описание пакета winget)
# и текст установщика. Внутренний README (96 КБ рабочих заметок) публичным не считается —
# ни в одной раскладке.
#
# ⚠️ ДВЕ РАСКЛАДКИ ОДНОГО СПИСКА (Ф-9-1, 18.10.2026: из-за этого упал публичный конвейер 2.1.0).
# В ПРИВАТНОМ дереве данные выпуска лежат в `release\tree\` (CH-12 цикла V-01, ADR-001 §5.2(6)),
# в ПУБЛИКАЦИИ те же файлы уезжают в КОРЕНЬ (§7 RELEASE-V2). Список, написанный только под
# приватную раскладку, в опубликованном дереве объявлял 7 из 8 файлов пропавшими — код 1 на
# исправном дереве, шаг «Сторож личных данных» в CI. Поэтому список называет ИМЕНА, а путь
# ищется в двух местах: сначала `release\tree\<имя>`, иначе `<имя>` в корне.
# Проверено: подложенными находками (путь машины и голос модели в README) сторож краснеет
# в ОБЕИХ раскладках, а пропажа файла ловится, только когда его нет ни там, ни там.
$publicDocNames = @('README.md', 'README.ru.md', 'README.zh.md',
    'CHANGELOG.md', 'CHANGELOG.ru.md', 'CHANGELOG.zh.md',
    'installer\dsh-panel.iss', 'winget\Danerus23.DSHPanel.locale.en-US.yaml')

# Найденные пути — фактические: по ним и сверяются «голос модели» и «пути этой машины».
$publicDocs = @()
$missingDocs = @()
foreach ($name in $publicDocNames) {
    $resolved = $null
    foreach ($candidate in @((Join-Path 'release\tree' $name), $name)) {
        if (Test-Path -LiteralPath (Join-Path $Root $candidate)) { $resolved = $candidate; break }
    }
    if ($resolved) { $publicDocs += $resolved } else { $missingDocs += $name }
}

# И шаблоны узкие — по ЗАВЕРЕНИЯМ и ПРОСЬБАМ, а не по слову «владелец»: «порт владельца» и
# «машина владельца» — обычная речь о чужой машине, а вот «снимки сделаны на отдельном
# профиле, поэтому личных данных нет» и «по просьбе владельца» — это голос модели к своему
# человеку, и на GitHub его читает сообщество (нашёл владелец по снимку README в 1.x).
$voicePatterns = @(
    'личных данных и путей', 'личных путей', 'личных данных нет',
    'снимки сделаны на отдельном профиле', 'на отдельном профиле',
    'по просьбе владельца', 'владелец просил', 'увидел владелец',
    'не мешают владельцу',
    'no personal paths', 'personal paths or data', 'on a separate profile', 'disturb the owner',
    '画面里没有个人', '个人路径和数据'
)

# ПРИЗНАКИ СЕКРЕТОВ — ОДНА ТАБЛИЦА НА ДВУХ ЧИТАТЕЛЕЙ (B-09). Строку ищет и наш разбор строки
# (`Get-LineFindings`), и `git grep` по индексу и истории. ⚠️ Поэтому шаблоны написаны в ОБЩЕМ
# подмножестве двух диалектов: POSIX ERE (у `git grep -E`) и .NET-регекса (у PowerShell `-match`).
# Никаких `\s`, `\b`, lookaround и `[[:space:]]`: они есть только в одном из двух, и такая
# строка молча проверялась бы не там, где надо.
#
# Что здесь и от какого случая:
#   * `ghp_…`/`sk-…` — чужие токены привычных облачных форматов, от 20 знаков (было и раньше);
#   * **ТОКЕН ВХОДА ПАНЕЛИ** — 32 случайных байта в base64url, то есть ровно **43 знака**
#     `[A-Za-z0-9_-]` (`docs\ENGINE.md` §1: «сам HMAC-секрет подписи (32 случайных байта
#     в base64url)»; в ссылке входа он стоит как `?token=…`). ⚠️ Голое «43 знака подряд» брать
#     НЕЛЬЗЯ: оно ловит хеши SHA-256 в документах (64 знака) и вообще любую длинную строку —
#     признак только ВМЕСТЕ с `token=`. Маска `token=***` в шаблон не попадает и находкой не
#     считается (проверено отдельным случаем самотеста);
#   * **ЗАКРЫТЫЙ КЛЮЧ** — `-----BEGIN … PRIVATE KEY-----`: RSA, EC, OPENSSH, PKCS#8. Приватные
#     ключи человека живут вне `~/.dsh`, но копия с ключами — сама по себе секрет, и случись
#     такая строка в дереве, это находка (красная линия 7).
$secretChecks = @(
    @{ Pattern = 'gh[opusr]_[A-Za-z0-9]{20,}'; Words = 'похоже на токен' },
    @{ Pattern = 'sk-[A-Za-z0-9]{20,}'; Words = 'похоже на токен' },
    @{ Pattern = 'token=[A-Za-z0-9_-]{43,}'; Words = 'похоже на токен входа панели (token= и 43 знака base64url)' },
    @{ Pattern = '"token":"[A-Za-z0-9_-]{43,}"'; Words = 'похоже на токен входа панели ("token":"…")' },
    @{ Pattern = '-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----'; Words = 'закрытый ключ (-----BEGIN … PRIVATE KEY-----)' }
)

# Аргументы `git grep` для тех же признаков: одна таблица — одни и те же строки ищутся в индексе
# и в истории, а не «в дереве одно, в истории другое».
$secretGrepArguments = @()
foreach ($check in $secretChecks) { $secretGrepArguments += @('-e', $check.Pattern) }

$found = [System.Collections.Generic.List[string]]::new()
$seen = @{}
$scanned = 0
$scannedIndex = 0
$skippedBinary = 0

# ОДНО МЕСТО ДЛЯ ПРАВИЛА «ЭТО УЕЗЖАЕТ К ЛЮДЯМ». Прежде условие стояло строкой в цикле и
# действовало только на дерево; теперь по нему же проверяются индекс и история — иначе три
# места разошлись бы в том, какой путь считать продуктовым. ⚠️ Путь приходит в форме Windows
# (`src\…`): пути git (`src/…`) переводятся ДО вызова.
function Test-MachinePath([string]$relative) {
    if ($relative -like 'src\*') { return $true }
    if ($relative -like 'installer\*.iss') { return $true }

    return ($publicDocs -contains $relative)
}

# ЧТО НАЙДЕНО В ОДНОЙ СТРОКЕ — без записи в список: правило одно, а зовут его три места
# (дерево, индекс, история), и у истории своя арифметика (сколько коммитов, какой новее).
function Get-LineFindings([string]$relative, [string]$line) {
    $hits = @()

    foreach ($marker in $markers) {
        if ($line -match [regex]::Escape($marker)) {
            $hits += ('встречается «' + $marker + '»')
        }
    }

    if (Test-MachinePath $relative) {
        foreach ($marker in $machineMarkers) {
            if ($line -match [regex]::Escape($marker)) {
                $hits += ('путь этой машины «' + $marker + '» в том, что уезжает к людям')
            }
        }
    }

    # СЕКРЕТЫ — по таблице: чужие токены, токен входа панели, закрытые ключи (B-09).
    foreach ($check in $secretChecks) {
        if ($line -match $check.Pattern) {
            $hits += $check.Words
        }
    }

    if ($publicDocs -contains $relative) {
        foreach ($pattern in $voicePatterns) {
            if ($line -match [regex]::Escape($pattern)) {
                $hits += ('голос модели к своему человеку: «' + $pattern + '»')
            }
        }
    }

    return $hits
}

# ОДНА НАХОДКА — И ОДИН РАЗ. Ключ без пометки места: строка, найденная и в дереве, и в индексе,
# — это ОДНА находка для человека (а вот «в истории» отдельно: если строка ещё и в дереве,
# история ничего не добавляет, и наоборот).
function Add-Finding([string]$relative, [int]$number, [string]$what, [string]$where) {
    $key = $relative + '|' + $number + '|' + $what

    if ($seen.ContainsKey($key)) { return }

    $seen[$key] = $true
    $script:found.Add($relative + ':' + $number + ' — ' + $what + $where)
}

# ---- (1) РАБОЧЕЕ ДЕРЕВО ------------------------------------------------------------------
foreach ($file in $list) {
    if (-not (Test-Path -LiteralPath $file)) { continue }

    $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
    if (($textExtensions -notcontains $extension) -and ($opensWith -notcontains $extension)) {
        $skippedBinary++
        continue
    }

    $scanned++
    $relative = $file.Substring($Root.Length).TrimStart('\')
    $number = 0

    foreach ($line in @(Get-Content -LiteralPath $file -Encoding UTF8 -ErrorAction SilentlyContinue)) {
        $number++

        foreach ($what in @(Get-LineFindings $relative $line)) {
            Add-Finding $relative $number $what ''
        }
    }
}

# ---- (2) ИНДЕКС GIT -----------------------------------------------------------------------
# ⚠️ **ЧИТАЕТСЯ ИМЕННО СОДЕРЖИМОЕ ИНДЕКСА, а не файл с диска** (`git grep --cached`): правка,
# добавленная в индекс и убранная из файла, уезжает в коммит, хотя в дереве её уже нет; файл,
# снятый с диска и не снятый с индекса, тоже уезжает. Пока сторож смотрел только дерево,
# оба случая проходили зелёными.
$indexHow = 'не смотрелся: git недоступен или это не репозиторий'

if ($hasGit) {
    # Шаблоны — файлом (UTF-8 без BOM): так в один прогон входят и латиница, и кириллица
    # (`-e` с кириллицей в аргументах зависит от того, как их перекодирует консоль).
    $literalPatterns = @(@($markers) + @($machineMarkers) + @($voicePatterns) | Where-Object { $_ })
    $patternFile = Join-Path ([IO.Path]::GetTempPath()) ('dsh-personal-' + [guid]::NewGuid().ToString('N') + '.txt')

    try {
        [IO.File]::WriteAllText($patternFile, (($literalPatterns -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

        $indexLiterals = @(& git -C $Root -c core.quotepath=false grep --cached -n -I -i --no-color --full-name -F -f $patternFile 2>$null)
        $literalExit = $LASTEXITCODE
    }
    finally {
        if (Test-Path -LiteralPath $patternFile) { Remove-Item -LiteralPath $patternFile -Force }
    }

    if ($literalExit -gt 1) {
        Write-Host ('неприменимо: git не смог прочитать индекс (код ' + $literalExit + ') — проверять нечего')
        exit 2
    }

    $indexTokens = @(& git -C $Root -c core.quotepath=false grep --cached -n -I -i --no-color --full-name -E @secretGrepArguments 2>$null)

    if ($LASTEXITCODE -gt 1) {
        Write-Host 'неприменимо: git не смог прочитать индекс — проверять нечего'
        exit 2
    }

    $indexFiles = @(& git -C $Root ls-files 2>$null)
    foreach ($path in $indexFiles) {
        $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
        if (($textExtensions -contains $extension) -or ($opensWith -contains $extension)) { $scannedIndex++ }
    }

    $indexHow = 'смотрелся: ' + $scannedIndex + ' текстовых файлов в индексе'

    foreach ($line in @($indexLiterals) + @($indexTokens)) {
        if ($line -notmatch '^(.+?):(\d+):(.*)$') { continue }

        $relative = ($Matches[1] -replace '/', '\')
        $number = [int]$Matches[2]
        $text = $Matches[3]

        foreach ($what in @(Get-LineFindings $relative $text)) {
            Add-Finding $relative $number $what ' (в индексе git)'
        }
    }
}

# ---- (3) ИСТОРИЯ КОММИТОВ — ТОЛЬКО ПО КЛЮЧУ `-History` --------------------------------------
# ⚠️ **ПОЧЕМУ НЕ ПО УМОЛЧАНИЮ.** Находка в истории — это строка в УЖЕ ОТПРАВЛЕННОМ коммите:
# переписать её нельзя (правило выпуска: выпуски задним числом не переписываем), а значит
# ворота с историей были бы вечно красными и перестали бы быть воротами. Поэтому по умолчанию
# проверяется то, что ещё можно исправить (дерево и индекс), а история — отдельный режим
# аудита: его итог читает человек и решает, что с этим делать.
$historyHow = 'не смотрена — ключ -History'

if ($History) {
    if (-not $hasGit) {
        Write-Host 'неприменимо: спрошена история коммитов, а git нет или это не репозиторий — проверить нечем'
        exit 2
    }

    $revs = @(& git -C $Root rev-list --all 2>$null)

    if ($LASTEXITCODE -ne 0) {
        Write-Host 'неприменимо: git не смог перечислить коммиты — проверить нечем'
        exit 2
    }

    $historyHow = 'смотрена вся: ' + $revs.Count + ' коммитов (все ветки)'

    # Коммиты идут пачками: у длинной истории все ревизии в одной строке аргументов не влезут.
    $chunks = @()
    for ($start = 0; $start -lt $revs.Count; $start += 300) {
        $chunks += , @($revs[$start..([Math]::Min($start + 299, $revs.Count - 1))])
    }

    # путь+строка+что → сколько коммитов и какой из них новее (rev-list отдаёт новые первыми).
    $historyHits = @{}

    $literalPatterns = @(@($markers) + @($machineMarkers) | Where-Object { $_ })
    $patternFile = Join-Path ([IO.Path]::GetTempPath()) ('dsh-personal-hist-' + [guid]::NewGuid().ToString('N') + '.txt')

    try {
        [IO.File]::WriteAllText($patternFile, (($literalPatterns -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

        $historyLines = @()

        foreach ($chunk in $chunks) {
            $historyLines += @(& git -C $Root -c core.quotepath=false grep -n -I -i --no-color --full-name -F -f $patternFile @chunk 2>$null)

            if ($LASTEXITCODE -gt 1) {
                Write-Host 'неприменимо: git не смог прочитать историю — проверить нечем'
                exit 2
            }
        }

        foreach ($chunk in $chunks) {
            $historyLines += @(& git -C $Root -c core.quotepath=false grep -n -I -i --no-color --full-name -E @secretGrepArguments @chunk 2>$null)

            if ($LASTEXITCODE -gt 1) {
                Write-Host 'неприменимо: git не смог прочитать историю — проверить нечем'
                exit 2
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $patternFile) { Remove-Item -LiteralPath $patternFile -Force }
    }

    foreach ($line in $historyLines) {
        if ($line -notmatch '^([0-9a-f]{40}):(.+?):(\d+):(.*)$') { continue }

        $commit = $Matches[1]
        $relative = ($Matches[2] -replace '/', '\')
        $number = [int]$Matches[3]
        $text = $Matches[4]

        foreach ($what in @(Get-LineFindings $relative $text)) {
            $key = $relative + '|' + $number + '|' + $what

            if (-not $historyHits.ContainsKey($key)) {
                $historyHits[$key] = [pscustomobject]@{
                    Relative = $relative
                    Number   = $number
                    What     = $what
                    Commits  = 0
                    Newest   = $commit
                }
            }

            $historyHits[$key].Commits++
        }
    }

    foreach ($item in @($historyHits.Values | Sort-Object Relative, Number)) {
        Add-Finding $item.Relative $item.Number $item.What (
            ' (в истории: коммитов ' + $item.Commits + ', новый ' + $item.Newest.Substring(0, 8) + ')')
    }
}

# СКОЛЬКО РАЗ НАЙДЕН ПУТЬ ЭТОЙ МАШИНЫ — СЧИТАЕТСЯ, А НЕ ПЕЧАТАЕТСЯ ЛИТЕРАЛОМ (CH-06 цикла C1).
# Было: `-f (… | -join ', '), 0` — второй довод формата стоял числом 0, $found не участвовал,
# и строка «путей этой машины …: 0» была ОБЕЩАНИЕМ, а не измерением. Хуже того, печаталась она
# ПОСЛЕ ветки находок, то есть при любой находке не печаталась вовсе — и «0» в ней не мог быть
# не-нулём НИКОГДА. Теперь счётчик считается по найденному (дерево + индекс + история) и
# печатается ВЫШЕ ветки находок.
$machineHits = @($found | Where-Object { $_ -like '*путь этой машины*' }).Count

$how = $(if ($fromGit) { 'по списку git (отслеживаемое и новое, кроме игнорируемого)' }
         else { 'ОБХОДОМ ПАПКИ (git недоступен — вердикт шире и может задеть копии)' })

if ($scanned -eq 0) {
    Write-Host ('нечего проверять: под корнем «' + $root + '» нет ни одного текстового файла')
    exit 2
}

# ОХВАТ И ЧИСЛО — ВЫШЕ ВЕТКИ НАХОДОК (CH-06): при находке сторож выходит кодом 1, и всё, что
# напечатано ниже этой строки, человек не увидит. Число путей этой машины — ПОСЧИТАННОЕ.
Write-Host ('проверено текстовых файлов: {0} в дереве ({1}); не читались двоичные: {2}' -f $scanned, $how, $skippedBinary)
Write-Host ('индекс git: {0}' -f $indexHow)
Write-Host ('история коммитов: {0}' -f $historyHow)
Write-Host ('путей этой машины в том, что уезжает к людям ({0}): найдено {1}' -f (($machineMarkers | Where-Object { $_ }) -join ', '), $machineHits)

# ⚠️ ПРОПАВШИЙ ВХОД — ЭТО НАХОДКА, А НЕ МОЛЧАНИЕ (t15, 17.10.2026). Голос модели и пути машины
# ищутся ПО СПИСКУ `$publicDocs`; файл, которого нет, обход просто не встречает — и сторож молчал.
# Замер до правки: убран `release\tree\README.md` — код 0, «личных данных не нашёл».
# ⚠️ С 18.10.2026 (Ф-9-1) «пропал» значит «нет НИ в `release\tree\`, НИ в корне»: список
# разрешён в две раскладки ОДИН раз выше (там же), поэтому здесь только отказ и имена.
if ($missingDocs.Count -gt 0) {
    Write-Host ('ПРОПАВШИЙ ВХОД: нет файла из списка публичных текстов — ' + $missingDocs.Count + ' из ' + $publicDocNames.Count)
    foreach ($item in $missingDocs) { Write-Host ('  ' + $item + ' (искал: release\tree\' + $item + ' и ' + $item + ')') }
    Write-Host 'список публичных текстов — перечень того, что уезжает к людям: файл из него не исчезает молча'
    exit 1
}

if ($found.Count -gt 0) {
    Write-Host ('ЛИЧНЫЕ ДАННЫЕ: {0} находок' -f $found.Count)
    foreach ($item in $found) { Write-Host ('  ' + $item) }
    Write-Host ''

    if ($History) {
        Write-Host 'находка «в истории» — это строка в уже отправленном коммите: переписать её нельзя (правило выпуска),'
        Write-Host 'и это ответ для решения, а не для правки файлов; выход из такого — решать владельцу/капитану'
    }

    Write-Host 'двоичные (PNG, ICO, ZIP, EXE) не читаются вовсе — про них этот вердикт не говорит НИЧЕГО'
    exit 1
}

Write-Host ('личных данных не нашёл: {0} текстовых файлов в дереве + {1} в индексе git ({2}); не читались двоичные: {3}' -f $scanned, $scannedIndex, $how, $skippedBinary)
Write-Host 'напоминание о границе: PNG, ICO, ZIP и EXE не читаются — снимки и архивы смотрит человек'

if (-not $History) {
    Write-Host 'напоминание о границе: история коммитов не смотрена — её смотрит ключ -History'
}

exit 0
