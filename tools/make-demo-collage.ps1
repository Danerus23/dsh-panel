# make-demo-collage.ps1 — собрать docs\demo.png из снимков окон панели.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-demo-collage.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-demo-collage.ps1 `
#       -SourceDir C:\Users\Public\DshPanelDemo\shots -Out docs\demo-fresh.png
#
# Зачем: живой GIF (docs\demo.gif) требует ffmpeg или ImageMagick, которых на машине нет,
# а рисовать анимацию вручную — дороже, чем она стоит. Поэтому витриной служит коллаж из
# настоящих снимков окон: их рисует сама панель (`DshTray.exe --shot <папка>\panel.png`,
# рядом появляются settings.png, backups.png, menu.png и остальные).
#
# Снимки берутся из -SourceDir (по умолчанию docs\screenshots\<язык>). Файл docs\demo.png
# в репозитории собран из того же набора, что лежит в docs\screenshots\en: снимки окон
# пересняты на 1.21.0 прогоном `--shot` в обезличенном профиле
# C:\Users\Public\DshPanelDemo, и коллаж пересобран сразу после пересъёмки. Оба набора —
# настоящие снимки окон одной и той же версии:
#
#   -SourceDir C:\Users\Public\DshPanelDemo\shots      (свежий прогон, тот же вид)
#   -SourceDir .\docs\screenshots\en                   (то, что лежит в репозитории и в коллаже)
#
# Профиль обезличен: ни имени владельца, ни баланса, ни чужого порта в кадре нет.
#
# Рисуем через System.Drawing: он есть и в PowerShell 5.1, и в PowerShell 7 на Windows.

param(
    [string]$SourceDir = '',
    [string]$Out = '',
    [string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root ('docs\screenshots\' + $Language) }
if (-not $Out) { $Out = Join-Path $root 'docs\demo.png' }
$Out = [IO.Path]::GetFullPath($Out)

# Сценарий показа: панель → настройки → копии → меню трея.
$tiles = @(
    @{ File = 'panel.png'; Caption = 'Panel — server state, peak hours, balance' },
    @{ File = 'settings.png'; Caption = 'Settings — language, port, environment' },
    @{ File = 'backups.png'; Caption = 'Backups — verify, restore, keep the last N' },
    @{ File = 'menu.png'; Caption = 'Tray menu — every command one right-click away' }
)

foreach ($tile in $tiles) {
    $path = Join-Path $SourceDir $tile.File
    if (-not (Test-Path -LiteralPath $path)) { throw ('нет снимка: ' + $path) }
}

$title = 'DSH Panel — Windows tray panel for the local DeepSeek Harness server'
$titleFont = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Bold)
$captionFont = New-Object Drawing.Font('Segoe UI', 10.5)
$captionBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(55, 65, 81))
$titleBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(17, 24, 39))
$cardBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
$pageBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(243, 244, 246))
$borderPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(209, 213, 219)), 1

$shotMax = 400   # предел ширины снимка внутри карточки
$shotMaxH = 470  # предел высоты
$pad = 12        # поле внутри карточки
$captionH = 20   # строка подписи
$gutter = 20     # промежуток между карточками
$margin = 20
$cols = 2

# Считаем размеры: каждое изображение вписываем в предел, сохраняя пропорции.
$scaled = @()
foreach ($tile in $tiles) {
    $image = [Drawing.Image]::FromFile((Join-Path $SourceDir $tile.File))
    $ratio = [Math]::Min($shotMax / $image.Width, $shotMaxH / $image.Height)
    if ($ratio -gt 1) { $ratio = 1 }
    $scaled += [pscustomobject]@{
        Image = $image
        Width = [int][Math]::Round($image.Width * $ratio)
        Height = [int][Math]::Round($image.Height * $ratio)
        Caption = $tile.Caption
    }
}

$shotW = [int]($scaled | Measure-Object -Property Width -Maximum).Maximum
$shotH = [int]($scaled | Measure-Object -Property Height -Maximum).Maximum
$cardW = [int]$shotW + 2 * $pad
$cardH = [int]$shotH + $pad + $captionH + $pad
$rows = [int][Math]::Ceiling($scaled.Count / $cols)

$probe = New-Object Drawing.Bitmap(1, 1)
$probeGraphics = [Drawing.Graphics]::FromImage($probe)
$titleH = [int][Math]::Ceiling($probeGraphics.MeasureString($title, $titleFont).Height) + 8
$probeGraphics.Dispose()
$probe.Dispose()

$canvasW = [int](2 * $margin + $cols * $cardW + ($cols - 1) * $gutter)
$canvasH = [int](2 * $margin + $titleH + $rows * $cardH + ($rows - 1) * $gutter)

$bitmap = New-Object Drawing.Bitmap($canvasW, $canvasH)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$graphics.FillRectangle($pageBrush, 0, 0, $canvasW, $canvasH)

$graphics.DrawString($title, $titleFont, $titleBrush, $margin, $margin)

for ($index = 0; $index -lt $scaled.Count; $index++) {
    $column = $index % $cols
    $row = [int][Math]::Floor($index / $cols)
    $cardX = $margin + $column * ($cardW + $gutter)
    $cardY = $margin + $titleH + $row * ($cardH + $gutter)

    $graphics.FillRectangle($cardBrush, $cardX, $cardY, $cardW, $cardH)
    $graphics.DrawRectangle($borderPen, $cardX, $cardY, $cardW - 1, $cardH - 1)

    $item = $scaled[$index]
    $imageX = $cardX + [int](($cardW - $item.Width) / 2)
    $imageY = $cardY + $pad + [int](($shotH - $item.Height) / 2)
    $graphics.DrawImage($item.Image, $imageX, $imageY, $item.Width, $item.Height)

    $captionSize = $graphics.MeasureString($item.Caption, $captionFont)
    $captionX = $cardX + [int](($cardW - $captionSize.Width) / 2)
    $captionY = $cardY + $pad + $shotH + [int](($captionH - $captionSize.Height) / 2)
    $graphics.DrawString($item.Caption, $captionFont, $captionBrush, $captionX, $captionY)

    $item.Image.Dispose()
}

$graphics.Dispose()
$directory = Split-Path -Parent $Out
if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$info = Get-Item -LiteralPath $Out
Write-Host ('Коллаж: {0}' -f $Out)
Write-Host ('Размер: {0}x{1}, {2:N0} КБ, кадров {3}' -f $canvasW, $canvasH, ($info.Length / 1KB), $scaled.Count)
