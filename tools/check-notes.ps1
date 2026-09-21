# check-notes.ps1 — заметки к выпуску: три языка в теле релиза.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-notes.ps1
#
# Что проверяется (скрипт НИЧЕГО не запускает и никуда не ходит: собирает тело релиза тем же
# tools\release-notes.ps1, каким его собирает конвейер и GitHub Actions, и разбирает результат):
#   1. в теле есть все три блока: открывающий и закрывающий маркер каждого языка, ровно по одному;
#   2. порядок маркеров правильный: en открыт, en закрыт, затем ru открыт/закрыт, затем zh —
#      открывающий от закрывающего отличается, перевёрнутая пара эту проверку не пройдёт;
#   3. английский блок не пуст;
#   4. каждый блок несёт текст СВОЕГО раздела: английский — CHANGELOG.en.md, китайский —
#      CHANGELOG.zh.md (ловит подстановку русского раздела в перевод, которую release-notes.ps1
#      делает, когда перевода для версии ещё нет); у версий ДО 1.21.0 раздела в переводах нет
#      и быть не должно — тогда проверка утверждает, что в блок честно подставлен русский;
#      у 1.21.0 и новее отсутствие перевода — ошибка выпуска;
#   5. раздел версии есть ровно один раз в русской истории (CHANGELOG.md) и не больше одного раза
#      в переводах: Read-Section берёт ПЕРВОЕ совпадение «## <версия>», и дубль раздела молча
#      развёл бы тело выпуска с файлом;
#   6. версия в теле совпадает с <Version> из DshTray.csproj;
#   7. в теле нет HTML-тегов (<details, <summary, </): панели СТАРЕЕ 1.21.0 показывают тело
#      выпуска как есть, и тег человек увидел бы текстом;
#   8. повторный запуск release-notes.ps1 даёт тот же файл (идемпотентность).
#
# Случаи «блок отсутствует → берём английский» и «маркеров нет → тело целиком» проверяются не
# здесь, а таблицей кейсов `UpdateService.PickNotes` в `DshTray.exe --selftest`: там они
# прогоняются на синтетических телах, а не на живом выпуске.
#
# Ноль — успех, ненулевой код — провал (как у остальных проверок проекта).
#
# Варианты: -Version <версия> (по умолчанию из DshTray.csproj), -Keep (не удалять временную папку).

param(
    [string]$Version = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'DshTray.csproj'
$work = Join-Path $env:TEMP ('dsh-notes-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = @()
$failed = 0

New-Item -ItemType Directory -Force -Path $work | Out-Null

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{
        Итог = $(if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }); Проверка = $name; Подробности = $detail
    }
    if (-not $ok) { $script:failed++ }
}

function Cleanup {
    if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

# Уборка обязана случиться и при исключении: иначе останется временная папка. Причину при
# этом надо напечатать: с `trap { Cleanup; exit 1 }` (так было раньше) провал проверки
# выглядел как код 1 без единого слова — и владелец не знал, что именно не сошлось.
# Образец — tools\check-restore.ps1.
trap {
    Write-Host ('ОШИБКА проверки: ' + $_.Exception.Message) -ForegroundColor Red
    Cleanup
    exit 1
}

# --- версия: источник истины — проект ------------------------------------------
if (-not $Version) {
    $match = Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>'
    if (-not $match) { throw ('В ' + $project + ' нет <Version>') }
    $Version = $match.Matches[0].Groups[1].Value
}

# --- сборка тела релиза тем же скриптом, что и конвейер -------------------------
$notes = Join-Path $work 'release-notes.md'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\release-notes.ps1') `
    -Version $Version -Out $notes
if ($LASTEXITCODE -ne 0) { throw ('release-notes.ps1 вернул код ' + $LASTEXITCODE) }
if (-not (Test-Path -LiteralPath $notes)) { throw ('release-notes.ps1 не создал файл: ' + $notes) }

# UTF8Encoding($false) — файл без BOM, читаем байты и декодируем сами: Get-Content -Encoding UTF8
# в PowerShell 5.1 читает без BOM верно, но здесь важно видеть байты один в один (сверка ниже).
$body = [IO.File]::ReadAllText($notes, (New-Object Text.UTF8Encoding($false)))

# --- 1. Маркеры: все три языка, ровно по одному открывающему и закрывающему ------
$languages = @(
    [pscustomobject]@{ Language = 'en'; Section = 'CHANGELOG.en.md' },
    [pscustomobject]@{ Language = 'ru'; Section = 'CHANGELOG.md' },
    [pscustomobject]@{ Language = 'zh'; Section = 'CHANGELOG.zh.md' }
)

foreach ($item in $languages) {
    $open = ([regex]::Matches($body, [regex]::Escape('<!-- dsh-notes:' + $item.Language + ' -->'))).Count
    $close = ([regex]::Matches($body, [regex]::Escape('<!-- /dsh-notes:' + $item.Language + ' -->'))).Count
    Check ('Маркеры ' + $item.Language + ': открывающий и закрывающий по одному') `
        (($open -eq 1) -and ($close -eq 1)) `
        ('открывающих: ' + $open + ', закрывающих: ' + $close)
}

# --- 2. Порядок маркеров: английский первым, каждый блок закрыт до следующего ---
# Ищем маркеры ПО СТРОКАМ: маркер, найденный функцией-разбором (PickNotes) только в целом виде,
# не должен считаться границей — ровно так же разбирает тело и панель. Роль маркера (открывающий
# или закрывающий) различается явно: перевёрнутая пара эту проверку пройти не должна.
$order = @()
foreach ($line in ($body -replace "`r`n", "`n").Split("`n")) {
    $trimmed = $line.Trim()
    $match = [regex]::Match($trimmed, '^<!--\s*(/?)\s*dsh-notes:([A-Za-z0-9_-]+)\s*-->$')
    if ($match.Success) {
        $role = 'open'
        if ($match.Groups[1].Value -ceq '/') { $role = 'close' }
        $order += ($match.Groups[2].Value + ':' + $role)
    }
}
$expected = @('en:open', 'en:close', 'ru:open', 'ru:close', 'zh:open', 'zh:close')
$sameOrder = ($order.Count -eq $expected.Count)
if ($sameOrder) {
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if ($order[$index] -cne $expected[$index]) { $sameOrder = $false; break }
    }
}
Check 'Порядок маркеров: en открыт и закрыт, затем ru, затем zh' $sameOrder `
    ('порядок: ' + ($order -join ' → '))

# --- разбор блоков: то же правило, что у панели (маркер один на всей строке) ----
# Плюс то же исключение: содержимое ограждённых блоков (``` и ~~~) разбор не видит — панель
# пропускает их (UpdateService.NotesBlocks), и проверка обязана разбирать тело так же, иначе
# она «увидит» блок там, где панель его не видит.
function Get-FenceLength([string]$line) {
    $trimmed = $line.TrimEnd()
    $spaces = 0
    $index = 0
    while ($index -lt $trimmed.Length) {
        if ($trimmed[$index] -eq ' ') { $spaces++ }
        elseif ($trimmed[$index] -eq "`t") { $spaces += 4 }
        else { break }
        $index++
    }
    if ($spaces -gt 3) { return 0 }
    $rest = $trimmed.Substring($index)
    if ($rest.Length -lt 3) { return 0 }
    $first = $rest[0]
    if (($first -ne '`') -and ($first -ne '~')) { return 0 }
    $length = 0
    while (($length -lt $rest.Length) -and ($rest[$length] -eq $first)) { $length++ }
    if ($length -lt 3) { return 0 }
    return $length
}

function Block([string]$language) {
    $open = '<!-- dsh-notes:' + $language + ' -->'
    $close = '<!-- /dsh-notes:' + $language + ' -->'
    $lines = ($body -replace "`r`n", "`n").Split("`n")
    $text = @()
    $inside = $false
    $fence = 0
    foreach ($line in $lines) {
        $run = Get-FenceLength $line
        if ($fence -gt 0) {
            if ($run -ge $fence) { $fence = 0 } else { if ($inside) { $text += $line } }
            continue
        }
        if ($run -gt 0) {
            $fence = $run
            if ($inside) { $inside = $false; $text = @() }
            continue
        }

        $trimmed = $line.Trim()
        if ($trimmed -ceq $open) { $inside = $true; $text = @(); continue }
        if ($trimmed -ceq $close) { $inside = $false; continue }
        if ($inside) { $text += $line }
    }
    return (($text -join [Environment]::NewLine).Trim())
}

$blocks = @{}
foreach ($item in $languages) { $blocks[$item.Language] = Block $item.Language }

# --- 3. Английский блок не пуст ------------------------------------------------
Check 'Английский блок не пуст' ($blocks['en'].Length -gt 0) `
    ('знаков: ' + $blocks['en'].Length)

# --- 4. Каждый блок — текст своего раздела истории ------------------------------
function Section([string]$file, [string]$version) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $lines = Get-Content -LiteralPath $path -Encoding UTF8
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match ('^##\s+' + [regex]::Escape($version) + '(\s|$)')) { $start = $index; break }
    }
    if ($start -lt 0) { return '' }
    $text = @()
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^##\s') { break }
        $text += $lines[$index]
    }
    return (($text -join [Environment]::NewLine).Trim())
}

# Переводы ведутся с 1.21.0: у более старых версий раздела в них нет и быть не должно, а вот
# у 1.21.0 и новее отсутствие перевода — уже ошибка выпуска (человек с английским интерфейсом
# прочитал бы русский текст). Сравнение версий — как у панели: сначала числа, потом хвост.
function VersionCompare([string]$left, [string]$right) {
    function Numbers([string]$version) {
        $head = ($version -split '\+')[0]
        $dash = $head.IndexOf('-')
        if ($dash -ge 0) { $head = $head.Substring(0, $dash) }
        $numbers = @()
        foreach ($part in $head.Split('.')) {
            $digits = ($part.ToCharArray() | Where-Object { [char]::IsDigit($_) }) -join ''
            if (-not $digits) { break }
            $numbers += [int]$digits
        }
        return $numbers
    }

    $a = Numbers $left
    $b = Numbers $right
    for ($index = 0; $index -lt [Math]::Max($a.Count, $b.Count); $index++) {
        $x = if ($index -lt $a.Count) { $a[$index] } else { 0 }
        $y = if ($index -lt $b.Count) { $b[$index] } else { 0 }
        if ($x -ne $y) { if ($x -gt $y) { return 1 } else { return -1 } }
    }
    return 0
}

$translationsHold = (VersionCompare $Version '1.21.0') -ge 0

foreach ($item in $languages) {
    $source = Section $item.Section $Version
    if ($source.Length -eq 0) {
        # Раздела для этой версии в файле нет — так и должно быть у версий до 1.21.0: переводы
        # ведутся с неё, а история до неё живёт только в CHANGELOG.md. Тогда release-notes.ps1
        # ЧЕСТНО подставляет русский раздел (и говорит об этом), и блок обязан быть русским:
        # молчаливый английский блок с русским текстом — это то, что проверка и ловит.
        $fellBack = ($blocks[$item.Language] -ceq $blocks['ru']) -and ($blocks['ru'].Length -gt 0)
        if ($translationsHold -and $item.Language -ne 'ru') {
            Check ('Раздел ' + $Version + ' есть в ' + $item.Section) $false `
                ('переводы ведутся с 1.21.0 — раздела для этой версии быть не может')
            continue
        }
        Check ('Блок ' + $item.Language + ' — раздела в ' + $item.Section + ' нет: подставлен русский') $fellBack `
            $(if ($fellBack) { 'так и задумано для версий до 1.21.0 (переводы ведутся с 1.21.0)' }
              else { 'в блок попал не русский раздел — откуда взялся текст?' })
        continue
    }
    Check ('Блок ' + $item.Language + ' — раздел ' + $item.Section) `
        ($blocks[$item.Language] -ceq $source) `
        ('знаков в теле: ' + $blocks[$item.Language].Length + ', в разделе: ' + $source.Length)
}

# --- 5. Раздел версии в истории: ровно один в русской, не больше одного в переводах ---
# Read-Section берёт ПЕРВОЕ совпадение «## <версия>» и второй такой раздел молча пропускает:
# так бывает при переносе перевода, и тогда тело выпуска расходится с файлом. Поэтому в русской
# истории раздел обязан быть ровно один, а в переводах — ноль или один (ноль законен: переводы
# ведутся с 1.21.0, и для старой версии release-notes.ps1 честно берёт русский раздел).
function Count-Sections([string]$file, [string]$version) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $pattern = '^##\s+' + [regex]::Escape($version) + '(\s|$)'
    $count = 0
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        if ($line -match $pattern) { $count++ }
    }
    return $count
}

foreach ($item in $languages) {
    $count = Count-Sections $item.Section $Version
    if ($item.Language -eq 'ru') {
        Check ('Раздел ' + $Version + ' в ' + $item.Section + ' — ровно один') ($count -eq 1) `
            ('разделов: ' + $count + ', ожидался ровно один')
    }
    else {
        Check ('Раздел ' + $Version + ' в ' + $item.Section + ' — не больше одного') ($count -le 1) `
            ('разделов: ' + $count + ', допустимо ноль или один (переводы ведутся с 1.21.0)')
    }
}

# --- 6. Версия в теле совпадает с <Version> проекта -----------------------------
$firstLine = (($body -replace "`r`n", "`n").Split("`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
Check 'Версия в теле — как в DshTray.csproj' `
    ($firstLine -ceq ('# DSH Panel ' + $Version)) `
    ('первая строка: «' + $firstLine + '», ожидалась «# DSH Panel ' + $Version + '»')

# --- 7. В теле нет HTML-тегов ---------------------------------------------------
# Панели СТАРЕЕ 1.21.0 не выбирают блок: они показывают тело выпуска как есть. Любой тег в
# теле — это то, что человек прочитает текстом, поэтому тело обязано быть обычным Markdown.
$tags = @('<details', '<summary', '</')
$foundTags = @()
foreach ($tag in $tags) {
    if ($body -clike ('*' + $tag + '*')) { $foundTags += $tag }
}
Check 'В теле нет HTML-тегов' ($foundTags.Count -eq 0) `
    $(if ($foundTags.Count -eq 0) { 'ни <details, ни <summary, ни </ в теле нет' }
      else { 'найдено: ' + ($foundTags -join ', ') })

# --- 8. Повторная сборка даёт тот же файл ---------------------------------------
$again = Join-Path $work 'release-notes-again.md'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\release-notes.ps1') `
    -Version $Version -Out $again
if ($LASTEXITCODE -ne 0) { throw ('повторный release-notes.ps1 вернул код ' + $LASTEXITCODE) }
$same = (Test-Path -LiteralPath $again)
if ($same) {
    $first = [IO.File]::ReadAllBytes($notes)
    $second = [IO.File]::ReadAllBytes($again)
    $same = ($first.Length -eq $second.Length)
    if ($same) {
        for ($index = 0; $index -lt $first.Length; $index++) {
            if ($first[$index] -ne $second[$index]) { $same = $false; break }
        }
    }
}
Check 'Повторный запуск даёт тот же файл' $same `
    $(if ($same) { 'файлы совпали по байтам' } else { 'файлы разошлись' })

# --- уборка --------------------------------------------------------------------
Cleanup

$results | Format-Table -AutoSize
Write-Host ''
if ($failed -eq 0) { Write-Host ('Заметки к выпуску {0}: все проверки пройдены ({1})' -f $Version, $results.Count) }
else { Write-Host ('Заметки к выпуску {0}: проблем — {1} из {2}' -f $Version, $failed, $results.Count) }
if ($Keep) { Write-Host ('Временная папка: ' + $work) }
exit $(if ($failed -eq 0) { 0 } else { 1 })
