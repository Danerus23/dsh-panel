<#
    Значок панели 2.0 — «пульт»: три полосы состояния и зелёная точка на тёмном скруглённом квадрате.

    ЗАЧЕМ ГЕНЕРАТОР, А НЕ ГОТОВЫЙ ФАЙЛ. Значок нужен в ДЕВЯТИ размерах (16 точек в трее, 20/24 —
    панель задач и мелкие списки, 32/48 — проводник, 256 — крупные плитки). Все размеры обязаны быть
    нарисованы из ОДНОГО источника: набор, нарисованный по клеточкам руками, расползается — правку
    в одном размере забывают в другом, и человек видит в трее один значок, а в проводнике другой.
    Здесь источник один: функция ниже, а `make-icon.ps1` раскладывает её результат по размерам.

    РЕШЕНИЕ ВЛАДЕЛЬЦА 26.09.2026: из трёх показанных вариантов выбран «C — пульт». Довод назван
    прямо: в трее значок живёт в 16 точках, и надпись «dsh» там превращается в мутное пятно, а полосы
    состояния и точка читаются. Показанная картинка «крупно + в размере трея» — та проверка, которой
    этот выбор и делался.

    ЧТО ПИШЕТ:
      assets\dsh-panel.png  256×256 — значок ОКОН (Avalonia рисует окно из PNG: ICO она не читает,
                            у Skia нет кодека ICO — проверено, это и есть причина двух файлов);
      assets\dsh-panel.ico  все размеры — значок самого exe (ApplicationIcon: его показывает
                            проводник) и ИСТОЧНИК ЗНАЧКА В ТРЕЕ: трей берёт байты этого файла
                            из сборки и собирает HICON сам — подменить значок своего процесса
                            «на лету» нельзя, а файла рядом с панелью не лежит (раздача — один exe).

    ФОРМАТ ICO. Кадры до 128 точек пишутся как BMP (BITMAPINFOHEADER + BGRA + маска), 256 — как PNG:
    так их понимает и Windows, и старые инструменты. Собирается файл здесь же, потому что
    System.Drawing умеет записать ICO только ОДНИМ кадром, а одного кадра для значка мало.

    Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

# --- рисование ---------------------------------------------------------------------------------

function New-RoundedPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90)
    $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90)
    $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $p.CloseFigure()
    return $p
}

<#
    Рисует значок одного размера. Всё, что нарисовано, задано в координатах 256×256 и умножается
    на долю размера: так 16 точек — это уменьшенный тот же рисунок, а не другой значок.
#>
function New-PanelIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $s = $size / 256.0

        $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 24, 40))
        $path = New-RoundedPath 0 0 ($size - 1) ($size - 1) ([int](56 * $s))
        $g.FillPath($bg, $path)
        $path.Dispose()

        $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 233, 240))
        $g.FillRectangle($bar, [single](52 * $s), [single](80 * $s), [single](152 * $s), [single](24 * $s))
        $g.FillRectangle($bar, [single](52 * $s), [single](118 * $s), [single](110 * $s), [single](24 * $s))
        $g.FillRectangle($bar, [single](52 * $s), [single](156 * $s), [single](74 * $s), [single](24 * $s))

        $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 204, 113))
        $g.FillEllipse($green, [single](188 * $s), [single](34 * $s), [single](36 * $s), [single](36 * $s))

        $bg.Dispose(); $bar.Dispose(); $green.Dispose()
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

# --- сборка ICO --------------------------------------------------------------------------------

function Get-BmpFrameBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $bytes = New-Object System.Collections.Generic.List[byte]

    # BITMAPINFOHEADER: высота ВДВОЕ больше — в ICO после пикселей идёт маска прозрачности.
    $bytes.AddRange([BitConverter]::GetBytes([uint32]40))
    $bytes.AddRange([BitConverter]::GetBytes([int32]$w))
    $bytes.AddRange([BitConverter]::GetBytes([int32]($h * 2)))
    $bytes.AddRange([BitConverter]::GetBytes([uint16]1))
    $bytes.AddRange([BitConverter]::GetBytes([uint16]32))
    $bytes.AddRange([BitConverter]::GetBytes([uint32]0))   # BI_RGB
    $bytes.AddRange([BitConverter]::GetBytes([uint32]0))   # размер образа не задаём
    $bytes.AddRange([BitConverter]::GetBytes([int32]0))
    $bytes.AddRange([BitConverter]::GetBytes([int32]0))
    $bytes.AddRange([BitConverter]::GetBytes([uint32]0))
    $bytes.AddRange([BitConverter]::GetBytes([uint32]0))

    # Пиксели: BGRA и СНИЗУ ВВЕРХ (так устроен BMP внутри ICO).
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bytes.Add($c.B); $bytes.Add($c.G); $bytes.Add($c.R); $bytes.Add($c.A)
        }
    }

    # Маска: прозрачность несёт альфа-канал, поэтому маска нулевая. Строка выравнивается по 4 байта.
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    for ($y = 0; $y -lt $h; $y++) {
        for ($i = 0; $i -lt $maskRow; $i++) { $bytes.Add(0) }
    }

    return $bytes.ToArray()
}

function Get-PngFrameBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    try {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    }
    finally {
        $ms.Dispose()
    }
}

$bmpSizes = @(16, 20, 24, 32, 40, 48, 64, 128)
$frames = New-Object System.Collections.Generic.List[object]

foreach ($size in $bmpSizes) {
    $bmp = New-PanelIcon $size
    try {
        $frames.Add(@{ Size = $size; Bytes = (Get-BmpFrameBytes $bmp) })
    }
    finally {
        $bmp.Dispose()
    }
}

$big = New-PanelIcon 256
try {
    $frames.Add(@{ Size = 256; Bytes = (Get-PngFrameBytes $big) })
    $pngPath = Join-Path $assetsDir 'dsh-panel.png'
    $big.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $big.Dispose()
}

$ico = New-Object System.Collections.Generic.List[byte]
$ico.AddRange([BitConverter]::GetBytes([uint16]0))                 # reserved
$ico.AddRange([BitConverter]::GetBytes([uint16]1))                 # тип: значок
$ico.AddRange([BitConverter]::GetBytes([uint16]$frames.Count))

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $size = [int]$frame.Size
    $data = [byte[]]$frame.Bytes
    $dim = 0
    if ($size -lt 256) { $dim = $size }
    $ico.Add([byte]$dim)                                           # ширина: 0 означает 256
    $ico.Add([byte]$dim)
    $ico.Add([byte]0)                                              # цветов в палитре нет
    $ico.Add([byte]0)
    $ico.AddRange([BitConverter]::GetBytes([uint16]1))             # плоскостей
    $ico.AddRange([BitConverter]::GetBytes([uint16]32))            # бит на точку
    $ico.AddRange([BitConverter]::GetBytes([uint32]$data.Length))
    $ico.AddRange([BitConverter]::GetBytes([uint32]$offset))
    $offset += $data.Length
}
foreach ($frame in $frames) { $ico.AddRange([byte[]]$frame.Bytes) }

$icoPath = Join-Path $assetsDir 'dsh-panel.ico'
[IO.File]::WriteAllBytes($icoPath, $ico.ToArray())

Write-Host ("значок собран: " + $icoPath + " (" + (Get-Item $icoPath).Length + " байт, кадров " + $frames.Count + ")")
Write-Host ("и " + $pngPath + " (" + (Get-Item $pngPath).Length + " байт)")
foreach ($frame in $frames) { Write-Host ("  кадр " + $frame.Size + ": " + ([byte[]]$frame.Bytes).Length + " байт") }
