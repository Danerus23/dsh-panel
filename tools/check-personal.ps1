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
# которые уезжают к людям: README, история версий и текст установщика.
$publicDocs = @('README.md', 'README.ru.md', 'README.zh.md',
    'CHANGELOG.md', 'CHANGELOG.en.md', 'CHANGELOG.zh.md',
    'installer\dsh-panel.iss', 'winget\Danerus23.DSHPanel.locale.en-US.yaml')

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

if ($found.Count -gt 0) {
    Write-Host ('ЛИЧНЫЕ ДАННЫЕ: {0} находок' -f $found.Count)
    foreach ($item in $found) { Write-Host ('  ' + $item) }
    Write-Host ''
    Write-Host ('проверено текстовых файлов: {0} ({1}); не читались двоичные: {2}' -f $scanned, $how, $skippedBinary)
    Write-Host 'двоичные (PNG, ICO, ZIP, EXE) не читаются вовсе — про них этот вердикт не говорит НИЧЕГО'
    exit 1
}

Write-Host ('личных данных не нашёл: {0} текстовых файлов ({1}); не читались двоичные: {2}' -f $scanned, $how, $skippedBinary)
Write-Host 'напоминание о границе: PNG, ICO, ZIP и EXE не читаются — снимки и архивы смотрит человек'
exit 0
