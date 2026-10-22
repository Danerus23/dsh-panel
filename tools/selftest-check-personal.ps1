# selftest-check-personal.ps1 — самотест сторожа личных данных на ПОДСТАВНОМ репозитории.
#
#   pwsh -NoProfile -File .\tools\selftest-check-personal.ps1
#
# ЗАЧЕМ. Сторож обязан уметь падать — и падать на КАЖДОМ входе, который он обещает смотреть.
# Здесь проверяется то, чего не видно на рабочем дереве: сторож читает ИНДЕКС git (правка,
# добавленная в индекс и убранная из файла, уезжает в коммит) и — по ключу `-History` — ВСЮ
# историю коммитов (то, что уже в репозитории и оттуда не исчезнет никогда). B-08 цикла 2.3.2.
#
# ⚠️ **Стенд — только свой временный каталог под `%TEMP%`.** Рабочее дерево, его `.git`, `dist\`
# и панель владельца не трогаются вовсе: репозитории стенда создаются с нуля (`git init`),
# а в конце каталог снимается. Ни один коммит рабочего дерева здесь не создаётся.
#
# ⚠️ **ПРОПАВШИЙ ВХОД — ТОЖЕ ПРОВЕРКА.** У подставного репозитория обязаны быть все 8 файлов
# публичных текстов: без них сторож честно говорит «ПРОПАВШИЙ ВХОД» и выходит кодом 1 — то есть
# находка «спряталась» бы за другим отказом, и проверка была бы зелёной по чужой причине.
#
# Коды возврата: 0 — все проверки прошли, 1 — есть провал.

$ErrorActionPreference = 'Stop'

$guard = Join-Path $PSScriptRoot 'check-personal.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('dsh-personal-selftest-' + $PID)

$checks = 0
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++

    if ($ok) {
        Write-Host ('  прошло: ' + $name)
        return
    }

    $script:failed++
    Write-Host ('  ПРОВАЛ: ' + $name)
    if ($detail) { Write-Host ('    ' + $detail) }
}

# ПОСТАВИТЬ ПОДСТАВНОЙ РЕПОЗИТОРИЙ: файлы публичных текстов (иначе «пропавший вход»), один
# чистый продуктовый файл и один коммит. Возвращает путь.
function New-Repo([string]$name) {
    $path = Join-Path $root $name
    New-Item -ItemType Directory -Path $path -Force | Out-Null

    & git -C $path init -q 2>$null
    & git -C $path config user.email 'selftest@example.invalid' 2>$null
    & git -C $path config user.name 'selftest' 2>$null

    New-Item -ItemType Directory -Path (Join-Path $path 'src') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $path 'installer') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $path 'winget') -Force | Out-Null

    $public = @('README.md', 'README.ru.md', 'README.zh.md', 'CHANGELOG.md', 'CHANGELOG.ru.md',
        'CHANGELOG.zh.md', 'installer\dsh-panel.iss', 'winget\Danerus23.DSHPanel.locale.en-US.yaml')

    foreach ($file in $public) { Set-Content -LiteralPath (Join-Path $path $file) -Value 'ok' }

    Set-Content -LiteralPath (Join-Path $path 'src\a.cs') -Value '// ok'

    & git -C $path add -A 2>$null
    & git -C $path commit -qm 'чисто' 2>$null

    return $path
}

# ПРОГНАТЬ СТОРОЖ В ОТДЕЛЬНОМ ПРОЦЕССЕ: сторож зовёт `exit`, и внутри самого самотеста он вышел
# бы вместе с ним. Вывод забирается ЦЕЛИКОМ (никаких `Select-Object -First`: он обрывает поток
# и портит код возврата — проверено).
function Run-Guard([string]$path, [string[]]$extra = @()) {
    $arguments = @('-NoProfile', '-File', $guard, '-Root', $path) + $extra
    $out = (& pwsh @arguments 2>&1 | Out-String)

    return [pscustomobject]@{ Out = $out; Code = $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $guard)) {
    Write-Host ('неприменимо: рядом нет сторожа «' + $guard + '» — проверять нечего')
    exit 2
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host 'неприменимо: git не установлен — подставной репозиторий не создать'
    exit 2
}

New-Item -ItemType Directory -Path $root -Force | Out-Null

$leak = '// путь: C:\Users\' + $env:USERNAME

try {
    # (1) ЧИСТЫЙ РЕПОЗИТОРИЙ — ЗЕЛЁНЫЙ. Без этой проверки красное ниже ничего не значит: сторож
    # мог бы краснеть на всём подряд.
    $repo = New-Repo 'clean'
    $run = Run-Guard $repo
    Check 'чистый репозиторий: код 0' ($run.Code -eq 0) ('код ' + $run.Code + '; ' + $run.Out.Trim())

    # (2) НОВЫЙ ФАЙЛ (ещё не в индексе) с путём пользователя — НАХОДКА.
    $repo = New-Repo 'untracked'
    Set-Content -LiteralPath (Join-Path $repo 'src\new.cs') -Value $leak
    $run = Run-Guard $repo
    Check 'новый файл: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'новый файл: назван путь и строка' ($run.Out -match 'src\\new\.cs:1 ') $run.Out.Trim()

    # (3) ПОДГОТОВЛЕНО К КОММИТУ, А В ФАЙЛЕ УЖЕ УБРАНО — НАХОДКА ИЗ ИНДЕКСА.
    # Прежний сторож читал файл с диска и этот случай пропускал: в дереве чисто, а в коммит уедет.
    $repo = New-Repo 'staged'
    Set-Content -LiteralPath (Join-Path $repo 'src\a.cs') -Value (($leak, '// ok') -join "`n")
    & git -C $repo add 'src/a.cs' 2>$null
    Set-Content -LiteralPath (Join-Path $repo 'src\a.cs') -Value '// ok'
    $run = Run-Guard $repo
    Check 'индекс (правка убрана из файла): код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'индекс: находка помечена «в индексе git»' (($run.Out -match 'src\\a\.cs:1 ') -and ($run.Out -match '\(в индексе git\)')) $run.Out.Trim()

    # (4) ФАЙЛ СНЯТ С ДИСКА, НО ОСТАЛСЯ В ИНДЕКСЕ — НАХОДКА ИЗ ИНДЕКСА.
    # `git ls-files --cached` даёт имя, а файла нет: прежний сторож пропускал его по Test-Path.
    $repo = New-Repo 'index-only'
    Set-Content -LiteralPath (Join-Path $repo 'src\gone.cs') -Value $leak
    & git -C $repo add 'src/gone.cs' 2>$null
    Remove-Item -LiteralPath (Join-Path $repo 'src\gone.cs') -Force
    $run = Run-Guard $repo
    Check 'индекс (файла нет на диске): код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'индекс: назван снятый файл' (($run.Out -match 'src\\gone\.cs:1 ') -and ($run.Out -match '\(в индексе git\)')) $run.Out.Trim()

    # (5) ЗАКОММИЧЕНО И УБРАНО — ТОЛЬКО ИСТОРИЯ. По умолчанию сторож этого не видит и ГОВОРИТ,
    # что история не смотрена; с `-History` — находит.
    $repo = New-Repo 'history'
    Set-Content -LiteralPath (Join-Path $repo 'src\old.cs') -Value $leak
    & git -C $repo add 'src/old.cs' 2>$null
    & git -C $repo commit -qm 'с личным путём' 2>$null
    & git -C $repo rm -q 'src/old.cs' 2>$null
    & git -C $repo commit -qm 'убрано' 2>$null

    $run = Run-Guard $repo
    Check 'история не смотрена без ключа: код 0' ($run.Code -eq 0) ('код ' + $run.Code)
    Check 'история не смотрена — сказано словами' ($run.Out -match 'история коммитов: не смотрена') $run.Out.Trim()

    $run = Run-Guard $repo @('-History')
    Check 'история: с ключом -History код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'история: назван путь, строка и коммит' (($run.Out -match 'src\\old\.cs:1 ') -and ($run.Out -match '\(в истории: коммитов \d+')) $run.Out.Trim()

    # (6) ЧИСТАЯ ИСТОРИЯ — ЗЕЛЁНАЯ (иначе «красное» выше ничего не доказывает).
    $repo = New-Repo 'clean-history'
    $run = Run-Guard $repo @('-History')
    Check 'чистая история: код 0' ($run.Code -eq 0) ('код ' + $run.Code + '; ' + $run.Out.Trim())
    Check 'чистая история: сказано, что смотрена' ($run.Out -match 'история коммитов: смотрена вся') $run.Out.Trim()

    # (7) ПУТЬ ЭТОЙ МАШИНЫ в продуктовом исходнике — НАХОДКА (правило «уезжает к людям»).
    # Машинный путь подставного репозитория = его собственный адрес под `%TEMP%`.
    $repo = New-Repo 'machine-path'
    Set-Content -LiteralPath (Join-Path $repo 'src\b.cs') -Value ('// отладка: ' + $repo)
    & git -C $repo add 'src/b.cs' 2>$null
    $run = Run-Guard $repo
    Check 'путь машины в src: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'путь машины: назван как «путь этой машины»' ($run.Out -match 'путь этой машины') $run.Out.Trim()

    # (8) ТОКЕН в новом файле — НАХОДКА.
    $repo = New-Repo 'token'
    Set-Content -LiteralPath (Join-Path $repo 'notes.txt') -Value ('ghp_' + ('A' * 30))
    $run = Run-Guard $repo
    Check 'токен: код 1 и слово «токен»' (($run.Code -eq 1) -and ($run.Out -match 'похоже на токен')) ('код ' + $run.Code)

    # ---- B-09: СЕКРЕТЫ САМОГО ПРОДУКТА И ВТОРАЯ ЗАПИСЬ ДОМАШНЕГО ПУТИ ----------------------
    # Токен входа панели — 43 знака base64url (32 байта) в ссылке входа. Строка стенда:
    # ровно 43 знака из разрешённого алфавита, ни одного лишнего.
    $productToken = ('aB3dE5fG7hJ9kL2mN4pQ6rS8tU0vW1xY2zA4cD6eF8g').Substring(0, 43)

    # (12) ТОКЕН ПРОДУКТА в ссылке входа (форма `token=`) — НАХОДКА С ИМЕНЕМ ФАЙЛА.
    $repo = New-Repo 'product-token'
    Set-Content -LiteralPath (Join-Path $repo 'link.txt') -Value ('http://127.0.0.1:3080/?token=' + $productToken)
    $run = Run-Guard $repo
    Check 'токен продукта (token=): код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'токен продукта: назван файл и слово «токен входа панели»' (($run.Out -match 'link\.txt:1 ') -and ($run.Out -match 'токен входа панели')) $run.Out.Trim()

    # (13) ТОКЕН ПРОДУКТА в JSON-форме (`"token":"…"`) — НАХОДКА.
    $repo = New-Repo 'product-token-json'
    Set-Content -LiteralPath (Join-Path $repo 'state.json') -Value ('{"token":"' + $productToken + '"}')
    $run = Run-Guard $repo
    Check 'токен продукта в JSON: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'токен продукта в JSON: названо словом' ($run.Out -match 'токен входа панели \("token"') $run.Out.Trim()

    # (14) ЗАКРЫТЫЙ КЛЮЧ — НАХОДКА (RSA, EC, OPENSSH, PKCS#8 — шаблон общий).
    # ⚠️ СТРОКА СОБИРАЕТСЯ ИЗ ЧАСТЕЙ, и это не украшательство: сторож читает и сам этот файл
    # (он лежит в дереве и имеет текстовое расширение), а цельная строка ключа в нём — законная
    # находка, и сторож краснел бы на своей же проверке. Замер: первый прогон дал ровно это —
    # «tools\selftest-check-personal.ps1:185 — закрытый ключ».
    $keyLine = '-----BEGIN ' + 'OPENSSH PRIVATE ' + 'KEY-----'
    $repo = New-Repo 'private-key'
    Set-Content -LiteralPath (Join-Path $repo 'id_key.txt') -Value $keyLine
    $run = Run-Guard $repo
    Check 'закрытый ключ: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'закрытый ключ: назван словом и файлом' (($run.Out -match 'id_key\.txt:1 ') -and ($run.Out -match 'закрытый ключ')) $run.Out.Trim()

    # (15) ДОМАШНИЙ ПУТЬ С ПРЯМЫМИ СЛЭШАМИ (`C:/Users/<имя>`) — НАХОДКА.
    $repo = New-Repo 'slash-path'
    Set-Content -LiteralPath (Join-Path $repo 'src\c.cs') -Value ('// путь: C:/Users/' + $env:USERNAME + '/Documents')
    & git -C $repo add 'src/c.cs' 2>$null
    $run = Run-Guard $repo
    Check 'C:/Users: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'C:/Users: назван путь с прямыми слэшами' ($run.Out -match [regex]::Escape('C:/Users/' + $env:USERNAME)) $run.Out.Trim()

    # (16) МАСКА `token=***` И ХЕШ SHA-256 — НЕ НАХОДКИ. Без этих проверок «токен продукта» мог бы
    # краснеть на каждой маске в документации и на каждом хеше в журнале, и сторож стал бы шумом.
    $repo = New-Repo 'no-false-positive'
    Set-Content -LiteralPath (Join-Path $repo 'docs.md') -Value 'в журнале ссылка вырезана: token=***'
    Add-Content -LiteralPath (Join-Path $repo 'docs.md') -Value ('sha256: ' + ('ab' * 32))
    $run = Run-Guard $repo
    Check 'маска token=*** и хеш: код 0' ($run.Code -eq 0) ('код ' + $run.Code + '; ' + $run.Out.Trim())

    # (17) ТОКЕН ПРОДУКТА В ИНДЕКСЕ — НАХОДКА: те же признаки читаются и в индексе, а не только
    # в дереве (иначе новая проверка была бы слепой ровно там, где B-08 только что открыл глаза).
    $repo = New-Repo 'product-token-index'
    Set-Content -LiteralPath (Join-Path $repo 'notes.txt') -Value ('token=' + $productToken)
    & git -C $repo add 'notes.txt' 2>$null
    Set-Content -LiteralPath (Join-Path $repo 'notes.txt') -Value 'ok'
    $run = Run-Guard $repo
    Check 'токен продукта в индексе: код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'токен продукта в индексе: помечен «в индексе git»' (($run.Out -match 'токен входа панели') -and ($run.Out -match '\(в индексе git\)')) $run.Out.Trim()

    # (18) ТОКЕН ПРОДУКТА В ИСТОРИИ — НАХОДКА ПО КЛЮЧУ `-History`.
    $repo = New-Repo 'product-token-history'
    Set-Content -LiteralPath (Join-Path $repo 'notes.txt') -Value ('token=' + $productToken)
    & git -C $repo add 'notes.txt' 2>$null
    & git -C $repo commit -qm 'с токеном' 2>$null
    & git -C $repo rm -q 'notes.txt' 2>$null
    & git -C $repo commit -qm 'убрано' 2>$null
    $run = Run-Guard $repo @('-History')
    Check 'токен продукта в истории: -History код 1' ($run.Code -eq 1) ('код ' + $run.Code)
    Check 'токен продукта в истории: помечен «в истории»' (($run.Out -match 'токен входа панели') -and ($run.Out -match '\(в истории: коммитов')) $run.Out.Trim()

    # (9) ДВОИЧНЫЙ НОВЫЙ ФАЙЛ — НЕ НАХОДКА: сторож в двоичное не смотрит и говорит об этом.
    $repo = New-Repo 'binary'
    [IO.File]::WriteAllBytes((Join-Path $repo 'кадр.png'), [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
    $run = Run-Guard $repo
    Check 'двоичный файл: код 0' ($run.Code -eq 0) ('код ' + $run.Code)
    Check 'двоичный файл: сказано, что не читался' ($run.Out -match 'не читались двоичные: 1') $run.Out.Trim()

    # (10) ПРОПАВШИЙ ВХОД: `-Root` на несуществующий каталог — код 2 словами (t15, регрессия).
    $run = Run-Guard (Join-Path $root 'нет-такого')
    Check 'пропавший корень: код 2 словами' (($run.Code -eq 2) -and ($run.Out -match 'неприменимо')) ('код ' + $run.Code + '; ' + $run.Out.Trim())

    # (11) СПРОСИЛИ ИСТОРИЮ, А GIT НЕТ — «неприменимо» кодом 2, а не зелёным вердиктом.
    $plain = Join-Path $root 'not-a-repo'
    New-Item -ItemType Directory -Path $plain -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $plain 'src.txt') -Value 'ok'
    $run = Run-Guard $plain @('-History')
    Check 'история без git: код 2 словами' (($run.Code -eq 2) -and ($run.Out -match 'неприменимо')) ('код ' + $run.Code + '; ' + $run.Out.Trim())
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ('ПРОВЕРЕНО: ' + $checks + ' проверок, провалов ' + $failed)

if ($failed -gt 0) {
    Write-Host 'САМОТЕСТ ПРОВАЛ'
    exit 1
}

Write-Host 'САМОТЕСТ УСПЕХ'
exit 0
