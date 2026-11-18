param(
  [Parameter(Mandatory = $true, Position = 0)][string[]]$InputPath,
  [string]$Id,
  [string]$OutRoot = "data",
  [ValidateSet("web", "hq", "preview")][string]$Profile = "web",
  [double]$Spacing = 0,
  [string]$Attributes = "RGB",
  [string]$Aabb = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root "tools\PotreeConverter\PotreeConverter_1.7_windows_x64\PotreeConverter.exe"

if (-not (Test-Path -LiteralPath $exe)) { throw "Не найден PotreeConverter: $exe" }

# --- проверка входных файлов ---
$files = @()
foreach ($p in $InputPath) {
  if (-not (Test-Path -LiteralPath $p)) { throw "Не найден файл: $p" }
  $item = Get-Item -LiteralPath $p
  if ($item.PSIsContainer) { throw "Это папка, нужен файл: $p" }
  $ext = $item.Extension.ToLower()
  if ($ext -eq ".rcp" -or $ext -eq ".rcs") {
    Write-Host ""
    Write-Host "RCP/RCS — закрытый формат Autodesk, конвертер его не читает." -ForegroundColor Yellow
    Write-Host "Выгрузите облако из ReCap Pro в LAS (или PTX/PLY) и запустите скрипт снова:"
    Write-Host "  ReCap Pro -> открыть проект -> Export -> LAS -> сохранить .las"
    Write-Host ""
    exit 1
  }
  if ($ext -notin @(".las", ".laz", ".ptx", ".ply")) {
    Write-Host "Формат $ext не поддерживается (нужен LAS, LAZ, PTX или PLY)." -ForegroundColor Yellow
    exit 1
  }
  $files += $item
}

# --- id ---
if (-not $Id -or $Id.Trim() -eq "") {
  $map = @{ 'а'='a';'б'='b';'в'='v';'г'='g';'д'='d';'е'='e';'ё'='yo';'ж'='zh';'з'='z';'и'='i';'й'='y';'к'='k';'л'='l';'м'='m';'н'='n';'о'='o';'п'='p';'р'='r';'с'='s';'т'='t';'у'='u';'ф'='f';'х'='h';'ц'='c';'ч'='ch';'ш'='sh';'щ'='sch';'ъ'='';'ы'='y';'ь'='';'э'='e';'ю'='yu';'я'='ya' }
  $raw = [IO.Path]::GetFileNameWithoutExtension($files[0].Name).ToLower()
  $raw = -join ($raw.ToCharArray() | ForEach-Object { if ($map.ContainsKey("$_")) { $map[$_] } else { "$_" } })
  $Id = ($raw -replace '[^a-zA-Z0-9]+', '-').Trim('-').ToLower()
  if ($Id -eq "") { $Id = "cloud-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
}

$outDir = Join-Path $root (Join-Path $OutRoot $Id)
if (Test-Path -LiteralPath $outDir) { Write-Host "Каталог существует, перезаписываю: $outDir" -ForegroundColor Yellow }

# --- параметры профиля ---
$format = "LAZ"
$extra  = @()
switch ($Profile) {
  "web"     { $format = "LAZ";     Write-Host "Профиль web: сжатый LAZ-октодерево, RGB, ~2x меньше по весу" -ForegroundColor Cyan }
  "hq"      { $format = "BINARY";  Write-Host "Профиль hq: несжатый BINARY, максимальное качество/скорость загрузки" -ForegroundColor Cyan }
  "preview" { $format = "LAZ";     if ($Spacing -le 0) { $Spacing = 0.05 }; Write-Host "Профиль preview: прореживание до $Spacing м между точками" -ForegroundColor Cyan }
}

$totalIn = ($files | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum
Write-Host ("Вход: {0} файл(ов), {1} МБ" -f $files.Count, [math]::Round($totalIn/1MB,1)) -ForegroundColor Cyan
Write-Host "Выход: $outDir" -ForegroundColor Cyan

# --- аргументы ---
$convArgs = @("-o", $outDir, "--overwrite", "--output-format", $format, "-a", $Attributes)
if ($Spacing -gt 0) { $convArgs += @("-s", $Spacing.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($Aabb -ne "")   { $convArgs += @("--aabb", $Aabb) }

$tmpList = $null
try {
  if ($files.Count -gt 1) {
    $tmpList = Join-Path ([IO.Path]::GetTempPath()) ("potree-list-" + [Guid]::NewGuid().ToString("N") + ".txt")
    ($files | ForEach-Object { $_.FullName }) | Set-Content -LiteralPath $tmpList -Encoding UTF8
    $convArgs += @("--list-of-files", $tmpList)
  } else {
    $convArgs = @($files[0].FullName) + $convArgs
  }

  Write-Host "Запуск PotreeConverter (может занять минуты)..." -ForegroundColor Cyan
  & $exe @convArgs
  $code = $LASTEXITCODE
} finally {
  if ($tmpList -and (Test-Path -LiteralPath $tmpList)) { Remove-Item -LiteralPath $tmpList -Force }
}
if ($code -ne 0) { throw "PotreeConverter завершился с кодом $code" }

# --- результат ---
$outFiles = Get-ChildItem -LiteralPath $outDir -Recurse -File
$sizeMB = [math]::Round((($outFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
$cloudJs = Join-Path $outDir "cloud.js"
$points = 0
if (Test-Path -LiteralPath $cloudJs) {
  try { $points = [int]((Get-Content -LiteralPath $cloudJs -Raw | ConvertFrom-Json).points) } catch {}
}

Write-Host ""
Write-Host ("Готово: {0} файлов, {1} МБ, точек: {2}" -f $outFiles.Count, $sizeMB, $points) -ForegroundColor Green
if ($points -gt 0 -and $sizeMB -gt 0) {
  Write-Host ("Средний вес на точку: {0} байт" -f [math]::Round($sizeMB*1MB/$points, 2))
}

Write-Host ""
if ($sizeMB -gt 400) {
  Write-Host "Внимание: больше 400 МБ. Для GitHub Pages это тяжело (репозиторий ~1 ГБ)." -ForegroundColor Yellow
  Write-Host "Варианты: профиль preview с -Spacing 0.05 (меньше точек) или внешнее хранилище (Cloudflare R2 / Яндекс Object Storage)." -ForegroundColor Yellow
} elseif ($sizeMB -gt 150) {
  Write-Host "Заметный вес. GitHub Pages выдержит, но лучше сократить: -Profile preview -Spacing 0.03" -ForegroundColor Yellow
}

Write-Host "Дальше:" -ForegroundColor Cyan
Write-Host "  1) git add `"$OutRoot/$Id`" ; git commit -m `"add $Id`" ; git push"
Write-Host "  2) Откройте admin.html, вставьте ссылку и нажмите «Проверить ссылку»:"
Write-Host "     https://<owner>.github.io/<repo>/$OutRoot/$Id/cloud.js"
