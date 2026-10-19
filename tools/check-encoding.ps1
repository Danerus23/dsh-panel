# check-encoding.ps1 — сторожок кодировки. Перенесён из панели 1.x (tools\check-encoding.ps1)
# и приспособлен к раскладке 2.0.
#
#   pwsh -NoProfile -File .\tools\check-encoding.ps1
#   pwsh -NoProfile -File .\tools\check-encoding.ps1 -Fix
#
# ЗАЧЕМ. Windows PowerShell 5.1 читает файл без BOM в кодировке консоли (на русской Windows —
# 866), и .ps1 с кириллицей падает с невнятной ошибкой разбора внутри строки. Обратная беда
# не мягче: BOM в .json ломает разбор (System.Text.Json читает BOM как мусор перед значением).
# Поэтому правило ДВУХ родов, и оно следует за тем, КТО файл читает:
#
#   * .ps1, .iss  — UTF-8 **с** BOM и переводы строк CRLF (их читают PowerShell 5.1 и Inno);
#   * .cmd, .bat  — переводы строк CRLF (их читает cmd.exe);
#   * .json, .csproj, .props, .targets, .slnx, .config — UTF-8 **без** BOM и без ведущего U+FEFF
#     (их читают .NET и System.Text.Json: «The JSON value could not be converted» из-за
#     невидимых трёх байт — ровно та поломка, которую ищет эта проверка).
#   * ⚠️ **.json — ЕЩЁ И CRLF** (правило введено 12.10.2026 вместе с `*.json text eol=crlf`
#     в `.gitattributes`). От какого случая: три словаря жили с LF, а `.gitattributes` их
#     переводы строк не закреплял, поэтому правка ШЕСТИ строк в них показала коммит
#     в 2446 строк — `git diff --ignore-cr-at-eol` давал +424/−191 против +4084/−3851.
#     Переводы строк файла данных — такая же часть формата, как отсутствие BOM: пока их
#     не закрепляет никто, их меняет любой инструмент правки, и дифф перестаёт что-либо
#     доказывать. **Сторож этого не видел вовсе** — поэтому .json и попал в список CRLF.
#   * ЛЮБОЙ файл из списка — **без управляющих символов** в тексте: U+0000–U+0008, U+000B, U+000C,
#     U+000E–U+001F, U+007F. Разрешены ровно три — \t (U+0009), \n (U+000A), \r (U+000D): без них
#     текста не бывает. От какого случая (e, 19.10.2026): при правке `NEXT.md` в файл попали U+000B
#     и U+0000 (класс из коммита `375034e`), и в коммит они ушли **уже исправленными**.
#     ⚠️ **СПИСОК РАСШИРЕН 19.10.2026 (e2) — класса ради, а не для полноты:** первая редакция правила
#     действовала только на прежние типы, и `NEXT.md` — ровно тот файл, где случай и случился, —
#     ею НЕ проверялся. Теперь управляющие символы ищутся ещё и в текстовых файлах продукта:
#     **.md · .cs · .xaml · .axaml · .yml · .yaml · .txt · .html · .js · .mjs**. Служебные правила
#     (BOM, концы строк, лишний U+FEFF) к НОВЫМ типам НЕ применяются: у них свой хозяин и свои
#     правила, а сторож честно проверяет в них только невидимые знаки — и ничего не обещает про шапку.
#     Находка — отказ (код 1) с **именем файла и строкой**, как у остальных отказов. **-Fix такие
#     знаки НЕ вырезает:** это правка содержимого, а не шапки файла (та же граница, что у U+FEFF).
#
# ⚠️ ЧЕСТНО О ГРАНИЦАХ, и это не оговорка, а измеренный факт:
#   * запись через [IO.File]::WriteAllText(путь, текст) **БЕЗ** явной кодировки пишет UTF-8
#     БЕЗ BOM (замерено: 208,191,209 — это «пр», а не «EF BB BF»); Set-Content -Encoding UTF8
#     в PowerShell 7 — тоже без BOM. То есть инструмент теряет BOM молча, и восстановить его
#     может только явная запись `New-Object Text.UTF8Encoding($true)` — это и делает -Fix;
#   * проверка НЕ смотрит внутрь PNG, ICO и ZIP: расширения в списке нет, а решение по таким
#     файлам не выносится вовсе (иначе вердикт читался бы шире, чем он есть);
#   * «переводы строк не CRLF» и «нет BOM» — это ПРАВКИ, а «U+FEFF внутри текста» — уже
#     правка содержимого: -Fix его НЕ вырезает, а только показывает (иначе сторожок молча
#     менял бы текст, который кто-то написал намеренно);
#   * управляющие символы ищутся в ДЕКОДИРОВАННОМ тексте, и это равенство, а не упрощение: в UTF-8
#     байты 00–1F и 7F не бывают частью многобайтного знака (продолжение всегда ≥ 0x80), поэтому
#     знак текста = свой байт. **А вот НЕВЕРНЫЙ UTF-8 правило не видит:** байт ≥ 0x80, не сложившийся
#     в знак, декодируется в U+FFFD и в запрещённый набор не попадает. «Битая кодировка» этим
#     правилом не ловится и им не обещана;
#   * под -Fix печатаются только НЕИСПРАВИМЫЕ находки (управляющие символы и лишний U+FEFF) — и тогда
#     код 1. Исправимые (BOM/CRLF) в этот список не попадают: «поправил: N» и «НЕ ИСПРАВЛЕНО: M»
#     говорят о разном и в одном вердикте не смешиваются;
#   * **в НОВЫХ типах (.md/.cs/.xaml/.axaml/.yml/.yaml/.txt/.html/.js/.mjs) сторож не судит о шапке
#     вовсе:** ни «нет BOM», ни «BOM лишний», ни «концы строк не CRLF», ни «лишний U+FEFF» — только
#     управляющие символы. Это не упущение, а граница: у этих файлов BOM законен (`ServerStateFiles.cs`
#     в дереве лежит С BOM — замер 19.10.2026), и правило «снимите BOM» испортило бы файл. Заодно
#     поэтому `-Fix` их не переписывает НИКОГДА: переписывать там нечего, а шапку он бы испортил.
#     ⚠️ И обратная сторона: `-Fix` не «долечит» и не «перепишет» эти файлы — он их только читает.
#
# Коды возврата, как у прочих проверок проекта: 0 — в порядке, 1 — не в порядке, 2 — нечего
# проверять (неприменимо; «неприменимо» — не «зелено»).

param(
    [switch]$Fix,
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

# Корень — параметром, а не только «на папку выше»: без этого сторожок нельзя прогнать на
# песочнице, а проверка, которую нельзя уронить нарочно, ничего не стоит (её и роняют так).
if (-not $Root) {
    $Root = Split-Path -Parent $PSScriptRoot
}
# ⚠️ ПРОПАВШИЙ ВХОД = ОТКАЗ, А НЕ АВАРИЯ (t15, 17.10.2026). Было: `-Root <нет такого>` роняло
# Resolve-Path — исключение и код 1 вместо названного отказа. Замер до правки это подтвердил.
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host ('неприменимо: под корнем «' + $Root + '» нет каталога — проверять нечего')
    exit 2
}
$root = (Resolve-Path -LiteralPath $Root).Path

# Папки сборки и лаборатории: там лежат копии исходников, и вердикт по ним ничего не значит.
$excluded = '\\(bin|obj|dist|upd-build|_lab[^\\]*|\.git|\.vs|\.nuget)\\'
$withBom = @('.ps1', '.iss')
$withoutBom = @('.json', '.csproj', '.props', '.targets', '.slnx', '.config')
$crlfOnly = @('.ps1', '.iss', '.cmd', '.bat', '.json')
# ⚠️ НОВЫЕ ТИПЫ (e2, 19.10.2026): в них ищутся ТОЛЬКО управляющие символы — и ни одного служебного
# правила. Список расширен не «для полноты», а потому что класс из `375034e` случился в `.md`, и файл,
# в который попали U+000B и U+0000, прежним списком не читался вовсе.
$controlOnly = @('.md', '.cs', '.xaml', '.axaml', '.yml', '.yaml', '.txt', '.html', '.js', '.mjs')

$candidates = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excluded })

$files = @($candidates | Where-Object {
    $withBom -contains $_.Extension.ToLowerInvariant() -or
    $withoutBom -contains $_.Extension.ToLowerInvariant() -or
    $crlfOnly -contains $_.Extension.ToLowerInvariant() -or
    $controlOnly -contains $_.Extension.ToLowerInvariant()
})

if ($files.Count -eq 0) {
    Write-Host ('нечего проверять: под корнем «' + $root + '» нет ни одного файла из списка' +
        ' (.ps1/.iss/.json/.csproj/.props/.targets/.slnx/.config/.cmd/.bat — BOM и концы строк;' +
        ' .md/.cs/.xaml/.axaml/.yml/.yaml/.txt/.html/.js/.mjs — управляющие символы)')
    exit 2
}

# ⚠️ ПРОПАВШИЙ ВХОД: ОБХОД ПАПКИ ВИДИТ ТОЛЬКО ТО, ЧТО ЛЕЖИТ (t15, 17.10.2026). Файл, который есть
# в git, но исчез с диска, для обхода не существует — и сторож молчал: замер до правки — убран
# `tools\check-personal.ps1`, код 0, «кодировка в порядке: 27 файлов» вместо 28. Перечень входа
# знает git, поэтому сверяемся и с ним; без git (или в собранном дереве) остаётся обход.
$checkedExtensions = @($withBom + $withoutBom + $crlfOnly + $controlOnly | Select-Object -Unique)
$missingTracked = @()
if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $trackedFiles = @(& git -C $root ls-files --cached 2>$null)
    if ($LASTEXITCODE -eq 0) {
        foreach ($rel in $trackedFiles) {
            if ($checkedExtensions -notcontains [IO.Path]::GetExtension($rel).ToLowerInvariant()) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $root ($rel -replace '/', '\')))) { $missingTracked += $rel }
        }
    }
}

if ($missingTracked.Count -gt 0) {
    Write-Host ('ПРОПАВШИЙ ВХОД: в git файл есть, на диске нет — ' + $missingTracked.Count)
    foreach ($item in $missingTracked) { Write-Host ('  ' + $item) }
    Write-Host 'эти файлы сторож обязан был проверить, а обход папки их не видит вовсе'
    exit 1
}

$utf8Bom = New-Object Text.UTF8Encoding($true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)
# $bad — находки, которые -Fix ИСПРАВЛЯЕТ (BOM, концы строк). $contentBad — находки, которые он
# исправлять не имеет права: это содержимое файла (управляющие символы, лишний U+FEFF). Списки
# разные не для красоты: под -Fix первые уже исправлены, и печатать их повторно значило бы врать.
$bad = @()
$contentBad = @()
$fixed = 0
$counts = @{}

foreach ($file in $files) {
    $extension = $file.Extension.ToLowerInvariant()
    if ($counts.ContainsKey($extension)) { $counts[$extension]++ } else { $counts[$extension] = 1 }

    $bytes = [IO.File]::ReadAllBytes($file.FullName)

    # Считаем ВСЕ ведущие BOM, а не только первый: файл EF BB BF EF BB BF прежняя проверка
    # 1.x пропускала (смотрела три байта) — лишний U+FEFF делает первую строку кодом, а не
    # комментарием, и .ps1 перестаёт разбираться целиком.
    $bomCount = 0
    while ($bytes.Length -ge ($bomCount * 3) + 3 -and
           $bytes[$bomCount * 3] -eq 0xEF -and
           $bytes[$bomCount * 3 + 1] -eq 0xBB -and
           $bytes[$bomCount * 3 + 2] -eq 0xBF) {
        $bomCount++
    }

    $offset = $bomCount * 3
    $text = $utf8NoBom.GetString($bytes, $offset, $bytes.Length - $offset)
    $needsCrlf = ($text -match "(?<!`r)`n") -and ($crlfOnly -contains $extension)
    $strayBom = [regex]::Matches($text, '\uFEFF').Count
    $name = $file.FullName.Substring($root.Length).TrimStart('\')

    # ⚠️ СЛУЖЕБНЫЕ ПРАВИЛА — ТОЛЬКО ДЛЯ ПРЕЖНИХ ТИПОВ (e2, 19.10.2026). У новых (.md/.cs/.xaml/…)
    # судится ОДНО: управляющие символы. BOM там законен (замер: `ServerStateFiles.cs` лежит с BOM),
    # концы строк — дело их собственного хозяина, а «лишний U+FEFF» — не тот класс, ради которого
    # список расширяли. Так вердикт не становится шире, чем он есть.
    $serviceRules = ($withBom + $withoutBom + $crlfOnly) -contains $extension

    if ($serviceRules) {
        if ($withBom -contains $extension) {
            if ($bomCount -eq 0) { $bad += ($name + ' — нет BOM (его читает PowerShell 5.1 / Inno)') }
            if ($bomCount -gt 1) { $bad += ($name + ' — ДВОЙНОЙ BOM (' + $bomCount + ' подряд)') }
        }
        elseif ($withoutBom -contains $extension -or $crlfOnly -contains $extension) {
            # Файл обязан быть чистым: BOM в .json/.csproj/.slnx/.config/.cmd — это поломка разбора.
            if ($bomCount -gt 0) { $bad += ($name + ' — BOM там, где его быть не должно (' + $bomCount + ')') }
        }

        if ($strayBom -gt 0) { $contentBad += ($name + ' — лишний U+FEFF внутри текста: ' + $strayBom) }
        if ($needsCrlf) { $bad += ($name + ' — переводы строк не CRLF (файл читает cmd.exe или PowerShell 5.1)') }
    }

    # ⚠️ УПРАВЛЯЮЩИЕ СИМВОЛЫ — ОТКАЗ, А НЕ «СТРАННЫЙ ТЕКСТ» (e, 19.10.2026). От случая `375034e`:
    # при правке `NEXT.md` в файл попали U+000B и U+0000. В коммит они ушли уже исправленными, но
    # **сторож обязан был их показать** — прежде он смотрел только шапку и концы строк, и такой
    # файл проходил зелёным. Разрешены ровно три знака: \t (09), \n (0A), \r (0D) — без них текста
    # не бывает; всё остальное из 00–1F плюс 7F — находка, **код 1 и имя файла со строкой**.
    #
    # Проверка идёт по ДЕКОДИРОВАННОМУ тексту, и это не упрощение: в UTF-8 байты 00–1F и 7F не
    # бывают частью многобайтного знака (продолжение всегда ≥ 0x80), поэтому знак текста = свой байт.
    # `\uXXXX` в шаблоне — это escape-последовательности регулярного выражения, а не настоящие знаки
    # в этом файле: иначе сторож ловил бы сам себя.
    $controlHits = [regex]::Matches($text, '[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]')
    if ($controlHits.Count -gt 0) {
        # Строка считается по уже пройденному тексту: совпадения идут по возрастанию позиции,
        # поэтому переводы строк между соседними находками досчитываются, а не пересчитываются.
        $line = 1
        $from = 0
        $shown = 0
        foreach ($hit in $controlHits) {
            $line += ([regex]::Matches($text.Substring($from, $hit.Index - $from), "`n")).Count
            $from = $hit.Index
            if ($shown -ge 10) { continue }
            $code = [int]$hit.Value[0]
            # Позиция — в знаках текста ПОСЛЕ шапки BOM (её в $text уже нет), а не в байтах файла.
            $contentBad += ($name + ' — управляющий символ U+' + $code.ToString('X4') + ' (код ' +
                $code + ') на строке ' + $line + ', позиция ' + $hit.Index)
            $shown++
        }
        # Потолок на файл: файл, забитый мусором, не должен хоронить в выводе остальные замечания.
        # Потолок НЕ молчит о хвосте — он его называет числом.
        if ($controlHits.Count -gt $shown) {
            $contentBad += ($name + ' — ещё управляющих символов: ' + ($controlHits.Count - $shown) +
                ' (показаны первые ' + $shown + ')')
        }
    }

    # ASCII-требование к сценариям замены файлов: cmd.exe читает .cmd в кодировке консоли
    # (на русской Windows — cp866), и не-ASCII знак делает путь несуществующим. Это правило
    # проекта (UpdateScript), поэтому сторожок говорит о нём прямо, а не «на глаз».
    if ($extension -in '.cmd', '.bat') {
        for ($i = 0; $i -lt $text.Length; $i++) {
            if ([int]$text[$i] -ge 128) {
                $bad += ($name + ' — не-ASCII знак в сценарии: код ' + [int]$text[$i] + ' на позиции ' + $i)
                break
            }
        }
    }

    # ⚠️ -Fix ПЕРЕПИСЫВАЕТ ТОЛЬКО ТЕ ФАЙЛЫ, У КОТОРЫХ ЕСТЬ СЛУЖЕБНЫЕ ПРАВИЛА (e2). У новых типов
    # править нечего, а «поправить» им шапку значило бы снять законный BOM (например у .cs).
    if ($Fix -and $serviceRules) {
        $changed = $false
        $target = $text

        if ($needsCrlf) {
            $target = ($target -replace "`r`n", "`n") -replace "`n", "`r`n"
            $changed = $true
        }

        $wantBom = $withBom -contains $extension
        if ($bomCount -ne ($(if ($wantBom) { 1 } else { 0 }))) { $changed = $true }

        if ($changed) {
            # Лишние ведущие BOM снимаются сами: пишем текст после них, ровно с нужной шапкой.
            # U+FEFF внутри текста НЕ вырезаем: это правка содержимого, о ней сказано отдельно.
            [IO.File]::WriteAllText($file.FullName, $target, $(if ($wantBom) { $utf8Bom } else { $utf8NoBom }))
            $fixed++
            Write-Host ('  поправил: ' + $name)
        }
    }
}

$summary = ($counts.Keys | Sort-Object | ForEach-Object { $_ + '=' + $counts[$_] }) -join ', '

if ($Fix) {
    Write-Host ('проверено файлов: {0} ({1}); поправлено: {2}' -f $files.Count, $summary, $fixed)
    # ⚠️ «Поправил» — это про шапку и концы строк, и только про них. Находки содержимого -Fix не
    # трогает, поэтому они обязаны быть названы и здесь, и код возврата при них — 1 (e, 19.10.2026).
    # Было: -Fix печатал «поправил: N» и выходил 0 — то есть находка содержимого ИСЧЕЗАЛА из вердикта
    # вовсе, и прогон «с -Fix» выглядел зелёным на файле с U+000B внутри.
    if ($contentBad.Count -gt 0) {
        Write-Host ('НЕ ИСПРАВЛЕНО: это содержимое файла, а не шапка — -Fix его не вырезает ({0} замечаний):' -f $contentBad.Count)
        foreach ($item in $contentBad) { Write-Host ('  ' + $item) }
        exit 1
    }
    exit 0
}

if ($bad.Count -gt 0 -or $contentBad.Count -gt 0) {
    if ($bad.Count -gt 0) {
        Write-Host ('кодировка не та ({0} замечаний; перезапустите с -Fix, если речь о BOM и CRLF):' -f $bad.Count)
        foreach ($item in $bad) { Write-Host ('  ' + $item) }
    }
    if ($contentBad.Count -gt 0) {
        Write-Host ('в тексте невидимые знаки — это ПРАВКА СОДЕРЖИМОГО, -Fix её не делает ({0} замечаний):' -f $contentBad.Count)
        foreach ($item in $contentBad) { Write-Host ('  ' + $item) }
    }
    exit 1
}

Write-Host ('кодировка в порядке: {0} файлов ({1})' -f $files.Count, $summary)
exit 0
