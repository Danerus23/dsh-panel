# check-personal.ps1 — сторожок личных данных. Перенесён из панели 1.x
# (tools\check-personal.ps1) и приспособлен к раскладке 2.0.
#
#   pwsh -NoProfile -File .\tools\check-personal.ps1
#   pwsh -NoProfile -File .\tools\check-personal.ps1 -Root <каталог>
#
# ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. В правилах 2.0 записано: «автопроверки личных данных нет — перед
# любым push в публичный репозиторий смотреть глазами». Это правило, а не вопрос владельцу,
# и оно остаётся. Но глазами не смотрят то, что можно посчитать: путь к папке пользователя и
# имя машины — это строки, и они ищутся машиной вернее, чем взглядом. Скрипт НЕ отменяет
# правило «смотреть глазами»: он его исполняет на той части, которую можно проверить.
#
# Что ищет в текстовых файлах, которые уедут в публичный репозиторий:
#   1. путь к папке пользователя ЭТОЙ машины (C:\Users\<имя>) и имя компьютера;
#   2. строки, похожие на токены (ghp_/gho_/ghu_/ghs_/ghr_/sk- длиной от 20 знаков);
#   3. в ПУБЛИЧНЫХ текстах — голос модели, обращённый к своему человеку («по просьбе владельца»,
#      «решение владельца»), и заверения «личных данных и путей нет»: на GitHub такие строки
#      читает сообщество, и им там не место. Нашёл это владелец по снимку README в 1.x.
#
# ⚠️ ЧЕГО НЕ ПОКРЫВАЕТ (важно не переоценить вердикт — он печатается и в выводе):
#   * смотрит ТОЛЬКО текстовые расширения: PNG, JPEG, ICO, ZIP и EXE не читаются вовсе,
#     поэтому про снимки и архивы вердикт не говорит НИЧЕГО. Именно поэтому настоящее имя
#     машины в кадре однажды нашёл OCR, а не эта проверка;
#   * «файлы репозитория» — это индекс git ПЛЮС новые файлы, ещё не добавленные в индекс
#     (список берётся `git ls-files --cached --others --exclude-standard`), поэтому проверка
#     годится и ДО коммита. Когда git недоступен, идёт обход папки — и его вердикт шире
#     («мимо» может попасть чужое рабочее дерево), поэтому об этом печатается отдельно;
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
# Коды возврата: 0 — чисто, 1 — найдено, 2 — нечего проверять (неприменимо).

param(
    [string]$Root = ''
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
if ($env:USERNAME) { $markers += ('C:\Users\' + $env:USERNAME) }
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

# Список файлов: всё, что уедет в публичный репозиторий, — отслеживаемое И новое, ещё не
# добавленное в индекс (но не игнорируемое). Если git недоступен — обход папки.
$list = @()
$fromGit = $false

if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $Root '.git'))) {
    $tracked = @(& git -C $Root ls-files --cached --others --exclude-standard 2>$null)
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
# которые уезжают к людям: данные выпуска из `release\tree\` (README ×3, CHANGELOG ×3,
# описание пакета winget) и текст установщика.
#
# ⚠️ ПУТИ ПЕРЕЕХАЛИ В `release\tree\` — CH-12 цикла V-01 (ADR-001 §5.2(6), 17.10.2026).
# До переноса список называл `README.md` и `CHANGELOG*.md` В КОРНЕ, то есть внутренние файлы,
# и вдобавок несуществующий `CHANGELOG.en.md` (настоящий — `CHANGELOG.ru.md`). После переноса
# источника выпуска в `release\tree\` публичные тексты лежат ТАМ, и список называет их:
# запись, которой нет на диске, упасть не может, а «список того, что уезжает к людям», где
# стоит внутренний README (96 КБ рабочих заметок), — обещание, а не измерение. Проверено
# подложенными находками (путь машины и голос модели в `release\tree\README.md`) — краснеет.
$publicDocs = @('release\tree\README.md', 'release\tree\README.ru.md', 'release\tree\README.zh.md',
    'release\tree\CHANGELOG.md', 'release\tree\CHANGELOG.ru.md', 'release\tree\CHANGELOG.zh.md',
    'installer\dsh-panel.iss', 'release\tree\winget\Danerus23.DSHPanel.locale.en-US.yaml')

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

$found = @()
$scanned = 0
$skippedBinary = 0

# СКОЛЬКО РАЗ НАЙДЕН ПУТЬ ЭТОЙ МАШИНЫ — СЧИТАЕТСЯ, А НЕ ПЕЧАТАЕТСЯ ЛИТЕРАЛОМ (CH-06 цикла C1).
# Было: `-f (… | -join ', '), 0` — второй довод формата стоял числом 0, $found не участвовал,
# и строка «путей этой машины …: 0» была ОБЕЩАНИЕМ, а не измерением. Хуже того, печаталась она
# ПОСЛЕ ветки находок, то есть при любой находке не печаталась вовсе — и «0» в ней не мог быть
# не-нулём НИКОГДА. Теперь счётчик считается по ходу разбора и печатается ВЫШЕ ветки находок.
$machineHits = 0

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

        foreach ($marker in $markers) {
            if ($line -match [regex]::Escape($marker)) {
                $found += ($relative + ':' + $number + ' — встречается «' + $marker + '»')
            }
        }

        # Пути этой машины — только в том, что уезжает к людям (см. объяснение в начале файла).
        # ⚠️ У установщика берётся ТОЛЬКО `.iss`: рядом лежит `installer\AGENTS.md` — внутренняя
        # инструкция агента, она никуда не уезжает, и путь рабочей области в ней законен
        # (проверено прогоном 10.10.2026: широкое правило назвало её находкой).
        if ($relative -like 'src\*' -or $relative -like 'installer\*.iss' -or ($publicDocs -contains $relative)) {
            foreach ($marker in $machineMarkers) {
                if ($line -match [regex]::Escape($marker)) {
                    $machineHits++
                    $found += ($relative + ':' + $number + ' — путь этой машины «' + $marker + '» в том, что уезжает к людям')
                }
            }
        }

        if ($line -match 'gh[opusr]_[A-Za-z0-9]{20,}' -or $line -match 'sk-[A-Za-z0-9]{20,}') {
            $found += ($relative + ':' + $number + ' — похоже на токен')
        }

        if ($publicDocs -contains $relative) {
            foreach ($pattern in $voicePatterns) {
                if ($line -match [regex]::Escape($pattern)) {
                    $found += ($relative + ':' + $number + ' — голос модели к своему человеку: «' + $pattern + '»')
                }
            }
        }
    }
}

$how = $(if ($fromGit) { 'по списку git (отслеживаемое и новое, кроме игнорируемого)' }
         else { 'ОБХОДОМ ПАПКИ (git недоступен — вердикт шире и может задеть копии)' })

if ($scanned -eq 0) {
    Write-Host ('нечего проверять: под корнем «' + $root + '» нет ни одного текстового файла')
    exit 2
}

# ОХВАТ И ЧИСЛО — ВЫШЕ ВЕТКИ НАХОДОК (CH-06): при находке сторож выходит кодом 1, и всё, что
# напечатано ниже этой строки, человек не увидит. Число путей этой машины — ПОСЧИТАННОЕ.
Write-Host ('проверено текстовых файлов: {0} ({1}); не читались двоичные: {2}' -f $scanned, $how, $skippedBinary)
Write-Host ('путей этой машины в том, что уезжает к людям ({0}): найдено {1}' -f (($machineMarkers | Where-Object { $_ }) -join ', '), $machineHits)

# ⚠️ ПРОПАВШИЙ ВХОД — ЭТО НАХОДКА, А НЕ МОЛЧАНИЕ (t15, 17.10.2026). Голос модели и пути машины
# ищутся ПО СПИСКУ `$publicDocs`; файл, которого нет, обход просто не встречает — и сторож молчал.
# Замер до правки: убран `release\tree\README.md` — код 0, «личных данных не нашёл».
$missingDocs = @($publicDocs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Root $_)) })
if ($missingDocs.Count -gt 0) {
    Write-Host ('ПРОПАВШИЙ ВХОД: нет файла из списка публичных текстов — ' + $missingDocs.Count + ' из ' + $publicDocs.Count)
    foreach ($item in $missingDocs) { Write-Host ('  ' + $item) }
    Write-Host 'список публичных текстов — перечень того, что уезжает к людям: файл из него не исчезает молча'
    exit 1
}

if ($found.Count -gt 0) {
    Write-Host ('ЛИЧНЫЕ ДАННЫЕ: {0} находок' -f $found.Count)
    foreach ($item in $found) { Write-Host ('  ' + $item) }
    Write-Host ''
    Write-Host 'двоичные (PNG, ICO, ZIP, EXE) не читаются вовсе — про них этот вердикт не говорит НИЧЕГО'
    exit 1
}

Write-Host ('личных данных не нашёл: {0} текстовых файлов ({1}); не читались двоичные: {2}' -f $scanned, $how, $skippedBinary)
Write-Host 'напоминание о границе: PNG, ICO, ZIP и EXE не читаются — снимки и архивы смотрит человек'
exit 0
