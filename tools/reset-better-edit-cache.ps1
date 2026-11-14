# Сброс кэша плагина dsh-better-edit (runtime/hash-store.sqlite).
# Владелец 14.11.2026. Запускать, КОГДА ПАНЕЛЬ/СЕРВЕР ОСТАНОВЛЕНЫ:
#   pwsh -NoProfile -File .\tools\reset-better-edit-cache.ps1
# Кэш — одноразовый: плагин пересоберёт его при следующем чтении.
# Пропадёт только история отмены (7 дней); файлы проекта не трогаются.
$ErrorActionPreference = 'Stop'
$rt = Join-Path $env:USERPROFILE '.dsh\plugins\dsh-better-edit\runtime'
if (-not (Test-Path $rt)) { Write-Host "Кэш не найден: $rt"; exit 0 }
$engines = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -match 'dsh[\\/]lib[\\/]bin\.js web' })
if ($engines.Count -gt 0) {
    Write-Host 'Движок DSH ещё работает. Сначала остановите панель/сервер (чат оборвётся), затем повторите.'
    exit 2
}
$size = [math]::Round(((Get-ChildItem $rt -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum) / 1MB, 1)
Get-ChildItem $rt -Force | Remove-Item -Recurse -Force
Write-Host "Кэш плагина очищен: было $size МБ. Запустите панель — плагин пересоберёт кэш."