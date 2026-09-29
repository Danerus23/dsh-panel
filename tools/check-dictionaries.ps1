# check-dictionaries.ps1 — сверка трёх словарей панели ПО СМЫСЛУ, а не только по ключам.
#
#   pwsh -NoProfile -File .\tools\check-dictionaries.ps1
#
# ЗАЧЕМ ОТДЕЛЬНО ОТ --lang-selftest. `--lang-selftest` сверяет КЛЮЧИ (у каждого члена
# `PanelStrings` есть запись во всех трёх словарях, лишних записей нет) и ПОДСТАНОВКИ.
# Владелец назвал ДРУГУЮ беду, и она ключами не ловится: *«сверка трёх словарей не только
# по ключам, но и по смыслу — нет ли строк, переведённых машинно и непонятных, нет ли
# потерянных подстановок `{0}` и оборванных фраз»* (docs\ROADMAP.md, пункт 5).
#
# ЧТО ЭТА ПРОВЕРКА МОЖЕТ, И ЭТО НАДО ЗНАТЬ ПРЕЖДЕ, ЧЕМ ЕЙ ВЕРИТЬ:
#   * она ловит ПОТЕРЮ ПОДСТАНОВОК и расхождение их ЧИСЛА (`{0}` против пустого места) —
#     это объективно и находится точно;
#   * она ловит BOM и следы машинной правки в самих словарях (BOM у .json ломает разбор);
#   * она ловит КИРИЛЛИЦУ в английском и китайском — признак непереведённой или склеенной строки;
#   * она ловит строки, ОДИНАКОВЫЕ во всех трёх языках, — кроме законного списка (имена,
#     символы, шаблоны без слов);
#   * она НЕ ПОНИМАЕТ СМЫСЛА. «Строка переведена, но непонятна» или «фраза оборвана»
#     машинной проверкой не находится вовсе. Это читает человек, и проверка об этом говорит
#     прямо в выводе, а не делает вид, что покрыла.
#
# Поэтому вердикт читается так: «механические беды исключены, смысл читает человек».

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$langDir = Join-Path $root 'src\DshPanel\Localization'

# Языки панели. Ключ — имя файла словаря, значение — как называть его в выводе.
$languages = @{ 'ru' = 'русский'; 'en' = 'английский'; 'zh' = 'китайский' }

# Строки, которые ОБЯЗАНЫ совпадать во всех трёх словарях: имена, символы и шаблоны без слов.
# Список явный и с причиной на каждой строке, а не «похоже на имя»: молчаливо разрешённое
# совпадение — это ровно то место, где живёт непереведённая фраза.
$mayMatch = @{
    'AboutCopyright'             = 'копирайт: имя автора одинаково на всех языках'
    'AppMonogram'                = 'монограмма логотипа'
    'EnvNode'                    = 'имя программы Node'
    'EnvNpm'                     = 'имя программы npm'
    'EnvPnpm'                    = 'имя программы pnpm'
    'SettingsLanguageChinese'    = 'название языка пишется на нём самом'
    'SettingsLanguageEnglish'    = 'название языка пишется на нём самом'
    'SettingsLanguageRussian'    = 'название языка пишется на нём самом'
    'SettingsCloseButton'        = 'значок ✕, слов нет'
    'PriceValueMissing'          = 'прочерк вместо цены, слов нет'
    'IssueReportEmptyValue'      = 'прочерк вместо пустого значения в отчёте о проблеме, слов нет'
    'PricingHistoryArrowUp'      = 'стрелка вверх, слов нет'
    'PricingHistoryArrowDown'    = 'стрелка вниз, слов нет'
    'PricingHistoryArrowNone'    = 'прочерк «сравнивать не с чем», слов нет'
    'BackupRotationFailedItemFormat' = 'шаблон из одних подстановок: {0} — {1}'
    'BackupRotationItemFormat'   = 'шаблон из одних подстановок: {0} ({1})'
}

$problems = @()
$scanned = 0
$dictionaries = @{}

foreach ($code in $languages.Keys | Sort-Object) {
    $path = Join-Path $langDir ($code + '.json')

    if (-not (Test-Path -LiteralPath $path)) {
        $problems += ('нет файла словаря: ' + $path)
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($path)

    # BOM у .json ломает разбор — то же правило, что у сторожа кодировки.
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $problems += (($code + '.json — BOM там, где его быть не должно'))
    }

    $dictionaries[$code] = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
}

if ($dictionaries.Count -lt 2) {
    Write-Host 'сверять нечего: словарей меньше двух'
    exit 2
}

$reference = 'ru'
$keys = @($dictionaries[$reference].Keys)

foreach ($code in $dictionaries.Keys | Sort-Object) {
    if ($code -eq $reference) { continue }

    # Разница ключей — беда, но её ловит и --lang-selftest; здесь она названа, чтобы
    # проверка не молчала, если её запустили одну.
    $extra = @($dictionaries[$code].Keys | Where-Object { $keys -notcontains $_ })
    $missing = @($keys | Where-Object { -not $dictionaries[$code].ContainsKey($_) })

    if ($extra.Count -gt 0) { $problems += ($code + ': лишних ключей ' + $extra.Count + ' (например ' + $extra[0] + ')') }
    if ($missing.Count -gt 0) { $problems += ($code + ': нет ключей ' + $missing.Count + ' (например ' + $missing[0] + ')') }
}

foreach ($key in $keys) {
    $scanned++

    $values = @{}
    $shots = @{}

    foreach ($code in $dictionaries.Keys) {
        if (-not $dictionaries[$code].ContainsKey($key)) { continue }

        $text = [string]$dictionaries[$code][$key]
        $values[$code] = $text

        # Число подстановок — то, что переносится из языка в язык БЕЗ изменений. Их порядок
        # в предложении законно свой (в китайском `{1}` стоит перед `{0}`), но ПОТЕРЯ или
        # лишняя подстановка — дефект: часть фразы пропала.
        $shots[$code] = @([regex]::Matches($text, '\{\d+\}') | ForEach-Object { $_.Value })
    }

    $referenceShots = @($shots[$reference])

    foreach ($code in $shots.Keys | Sort-Object) {
        if ($code -eq $reference) { continue }

        $got = @($shots[$code])
        $lost = @($referenceShots | Where-Object { $got -notcontains $_ })
        $added = @($got | Where-Object { $referenceShots -notcontains $_ })

        if ($lost.Count -gt 0) {
            $problems += ($code + ' / ' + $key + ': ПОТЕРЯНА подстановка ' + ($lost -join ', ') + ' — «' + $values[$code] + '»')
        }

        if ($added.Count -gt 0) {
            $problems += ($code + ' / ' + $key + ': ЛИШНЯЯ подстановка ' + ($added -join ', ') + ' — «' + $values[$code] + '»')
        }
    }

    # Кириллица в переводе — признак непереведённой строки (кроме имени языка).
    foreach ($code in @('en', 'zh')) {
        if (-not $values.ContainsKey($code)) { continue }
        if (($key -eq 'SettingsLanguageRussian')) { continue }

        if ($values[$code] -match '[\u0400-\u04FF]') {
            $problems += ($code + ' / ' + $key + ': кириллица в переводе — «' + $values[$code] + '»')
        }
    }

    # Совпадение всех трёх — почти всегда непереведённая строка.
    if ($values.Count -eq 3) {
        $all = @($values.Values)
        if (($all[0] -eq $all[1]) -and ($all[1] -eq $all[2])) {
            if (-not $mayMatch.ContainsKey($key)) {
                $problems += ('ВСЕ ТРИ ЯЗЫКА / ' + $key + ': строка не переведена — «' + $all[0] + '»')
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host ('СЛОВАРИ: {0} замечаний (ключей сверено: {1} × {2})' -f $problems.Count, $scanned, $dictionaries.Count)
    foreach ($item in $problems) { Write-Host ('  ' + $item) }
    Write-Host ''
    Write-Host 'ЧЕГО ЭТА ПРОВЕРКА НЕ ВИДИТ: смысла. «Переведено машинно и непонятно», «фраза оборвана» — читает человек.'
    exit 1
}

Write-Host ('словари в порядке: {0} ключей × {1} языка ({2})' -f $scanned, $dictionaries.Count, (($languages.Keys | Sort-Object) -join ', '))
Write-Host ('совпадать во всех трёх разрешено {0} строкам — имена, символы и шаблоны без слов' -f $mayMatch.Count)
Write-Host 'ЧЕСТНО О ГРАНИЦЕ: проверка механическая. Смысл переводов читает человек — это сказано в docs\ROADMAP.md.'
exit 0
