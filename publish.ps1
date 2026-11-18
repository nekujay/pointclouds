param(
  [Parameter(Mandatory = $true, Position = 0)][string]$InputPath,
  [string]$Id,
  [string]$Title,
  [string]$Description = "",
  [ValidateSet("web", "hq", "preview")][string]$Profile = "web",
  [double]$Spacing = 0,
  [string]$Poster = "",
  [string]$Rcp = "",
  [string]$Remote = "",
  [switch]$SkipGit,
  [switch]$Open
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- входной файл ----------
if (-not (Test-Path -LiteralPath $InputPath)) { throw "Не найден файл: $InputPath" }
$inFile = Get-Item -LiteralPath $InputPath
$ext = $inFile.Extension.ToLower()

if ($ext -eq ".rcp" -or $ext -eq ".rcs") {
  Write-Host ""
  Write-Host "  Файл .rcp/.rcs конвертировать напрямую нельзя — формат закрытый (Autodesk)." -ForegroundColor Yellow
  Write-Host "  Дальше всё автоматически, но нужен один экспорт из ReCap Pro:" -ForegroundColor Yellow
  Write-Host ""
  Write-Host "    1) откройте проект .rcp в ReCap Pro"
  Write-Host "    2) Файл -> Экспорт (Export) -> LAS"
  Write-Host "    3) сохраните .las рядом с проектом"
  Write-Host ""
  Write-Host "  Затем снова:  .\publish.cmd `"C:\путь\scan.las`" -Title `"Офис, 1 этаж`""
  Write-Host ""
  Write-Host "  Подсказка: сам .rcp можно положить в репозиторий и указать параметром" -ForegroundColor DarkGray
  Write-Host "  -Rcp `"data/rcp/scan.rcp`" — тогда в карточке появится ссылка на исходник." -ForegroundColor DarkGray
  Write-Host ""
  exit 1
}

if ($ext -notin @(".las", ".laz", ".pts", ".ply", ".ptx", ".xyz")) {
  Write-Host "Формат $ext не поддерживается. Нужен LAS, LAZ, PTS, PTX, PLY или XYZ." -ForegroundColor Yellow
  exit 1
}

# ---------- идентификатор ----------
function Get-Slug([string]$text) {
  if ([string]::IsNullOrWhiteSpace($text)) { return "" }
  $map = @{ 'а'='a';'б'='b';'в'='v';'г'='g';'д'='d';'е'='e';'ё'='yo';'ж'='zh';'з'='z';'и'='i';'й'='y';'к'='k';'л'='l';'м'='m';'н'='n';'о'='o';'п'='p';'р'='r';'с'='s';'т'='t';'у'='u';'ф'='f';'х'='h';'ц'='c';'ч'='ch';'ш'='sh';'щ'='sch';'ъ'='';'ы'='y';'ь'='';'э'='e';'ю'='yu';'я'='ya' }
  $s = $text.ToLower()
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $s.ToCharArray()) {
    $k = "$ch"
    if ($map.ContainsKey($k)) { [void]$sb.Append($map[$k]) } else { [void]$sb.Append($k) }
  }
  $slug = ($sb.ToString() -replace '[^a-z0-9]+', '-').Trim('-')
  if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).Trim('-') }
  return $slug
}

if ([string]::IsNullOrWhiteSpace($Id)) {
  if (-not [string]::IsNullOrWhiteSpace($Title)) { $Id = Get-Slug $Title }
  if ([string]::IsNullOrWhiteSpace($Id)) { $Id = Get-Slug ([IO.Path]::GetFileNameWithoutExtension($inFile.Name)) }
  if ([string]::IsNullOrWhiteSpace($Id)) { $Id = "cloud-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
}
if ([string]::IsNullOrWhiteSpace($Title)) { $Title = [IO.Path]::GetFileNameWithoutExtension($inFile.Name) }

Write-Host ""
Write-Host "  Публикация облака точек" -ForegroundColor Cyan
Write-Host "  файл:    $($inFile.FullName)"
Write-Host "  id:      $Id"
Write-Host "  название: $Title"
Write-Host ""

# ---------- конвертация ----------
$convParams = @{ InputPath = $inFile.FullName; Id = $Id; Profile = $Profile }
if ($Spacing -gt 0) { $convParams["Spacing"] = $Spacing }

& (Join-Path $root "convert.ps1") @convParams
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { throw "Конвертация завершилась с кодом $LASTEXITCODE" }

$cloudJs = Join-Path $root ("data\$Id\cloud.js")
if (-not (Test-Path -LiteralPath $cloudJs)) { throw "Не найден результат конвертации: $cloudJs" }

$points = 0
try { $points = [int]((Get-Content -LiteralPath $cloudJs -Raw -Encoding UTF8 | ConvertFrom-Json).points) } catch { }

# ---------- данные для галереи ----------
$src = "data/$Id/cloud.js"
$date = (Get-Date).ToString("yyyy-MM-dd")

$jsonPath = Join-Path $root "clouds.json"
$items = @()
if (Test-Path -LiteralPath $jsonPath) {
  $raw = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8
  if (-not [string]::IsNullOrWhiteSpace($raw)) {
    $parsed = $null
    try { $parsed = $raw | ConvertFrom-Json } catch { $parsed = $null }
    # ConvertFrom-Json отдаёт массив одним объектом — разворачиваем вручную
    if ($parsed -ne $null) { $items = @($parsed | ForEach-Object { $_ }) }
  }
}
$items = @($items | Where-Object { $_ -ne $null -and $_.PSObject.Properties["id"] -ne $null -and $_.id -ne $Id })

$entry = [ordered]@{
  id          = $Id
  title       = $Title
  description = $Description
  src         = $src
}
if ($points -gt 0) { $entry["points"] = $points }
$entry["date"] = $date
if (-not [string]::IsNullOrWhiteSpace($Poster)) { $entry["poster"] = $Poster }
if (-not [string]::IsNullOrWhiteSpace($Rcp)) { $entry["rcp"] = $Rcp }

$all = @($entry) + @($items | ForEach-Object {
  $h = [ordered]@{}
  foreach ($p in $_.PSObject.Properties) { $h[$p.Name] = $p.Value }
  $h
})

function ConvertTo-JsonString([string]$text) {
  if ($null -eq $text) { return '""' }
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $text.ToCharArray()) {
    $code = [int]$ch
    if ($ch -eq '"') { [void]$sb.Append('\"') }
    elseif ($ch -eq '\') { [void]$sb.Append('\\') }
    elseif ($code -eq 10) { [void]$sb.Append('\n') }
    elseif ($code -eq 13) { [void]$sb.Append('\r') }
    elseif ($code -eq 9) { [void]$sb.Append('\t') }
    elseif ($code -lt 32) { [void]$sb.Append(('\u{0:x4}' -f $code)) }
    else { [void]$sb.Append($ch) }
  }
  return '"' + $sb.ToString() + '"'
}

$out = New-Object System.Text.StringBuilder
[void]$out.AppendLine("[")
for ($i = 0; $i -lt $all.Count; $i++) {
  $e = $all[$i]
  $fields = @()
  foreach ($key in $e.Keys) {
    $val = $e[$key]
    if ($null -eq $val -or "$val" -eq "") {
      if ($key -eq "description") { $fields += ('    "description": ""') }
      continue
    }
    if ($key -eq "points") { $fields += ('    "points": ' + [int]$val) }
    else { $fields += ('    "' + $key + '": ' + (ConvertTo-JsonString ([string]$val))) }
  }
  [void]$out.AppendLine("  {")
  [void]$out.AppendLine(($fields -join "," + "`r`n"))
  [void]$out.Append("  }")
  if ($i -lt $all.Count - 1) { [void]$out.Append(",") }
  [void]$out.AppendLine()
}
[void]$out.AppendLine("]")

[IO.File]::WriteAllText($jsonPath, $out.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ""
Write-Host "  clouds.json обновлён (запись «$Title»)" -ForegroundColor Green

# ---------- git ----------
$pushed = $false
if (-not $SkipGit) {
  Push-Location $root
  try {
    $isRepo = $false
    try { & git rev-parse --is-inside-work-tree *> $null; $isRepo = ($LASTEXITCODE -eq 0) } catch { $isRepo = $false }

    if (-not $isRepo) {
      Write-Host "  В папке нет git-репозитория — создаю." -ForegroundColor Cyan
      & git init *> $null
      & git branch -M main *> $null
      if (-not [string]::IsNullOrWhiteSpace($Remote)) { & git remote add origin $Remote *> $null }
    }

    $ignorePath = Join-Path $root ".gitignore"
    if (-not (Test-Path -LiteralPath $ignorePath)) {
      [IO.File]::WriteAllText($ignorePath,
        "tools/`r`n*.log`r`nThumbs.db`r`n.DS_Store`r`n",
        (New-Object System.Text.UTF8Encoding($false)))
      Write-Host "  Создан .gitignore (папка tools/ не попадает в репозиторий)" -ForegroundColor Cyan
    }

    & git add -- .gitignore clouds.json "data/$Id"
    $hasChanges = $true
    try { & git diff --cached --quiet *> $null; $hasChanges = ($LASTEXITCODE -ne 0) } catch { }

    if ($hasChanges) {
      & git commit -m ("add pointcloud: " + $Title) *> $null
      Write-Host "  Коммит создан." -ForegroundColor Green
    } else {
      Write-Host "  Изменений в git нет (файлы уже закоммичены)." -ForegroundColor DarkGray
    }

    $hasRemote = $false
    try { $r = (& git remote) 2>$null; $hasRemote = ($r -match "origin") } catch { }

    if ($hasRemote) {
      & git push
      if ($LASTEXITCODE -eq 0) { $pushed = $true; Write-Host "  Отправлено на GitHub." -ForegroundColor Green }
      else {
        & git push -u origin HEAD
        if ($LASTEXITCODE -eq 0) { $pushed = $true; Write-Host "  Отправлено на GitHub (ветка связана)." -ForegroundColor Green }
      }
      if (-not $pushed) {
        Write-Host "  Не удалось отправить автоматически. Выполните вручную:" -ForegroundColor Yellow
        Write-Host "    git push -u origin main" -ForegroundColor Yellow
      }
    } else {
      Write-Host "  Удалённый репозиторий не настроен. Добавьте его так:" -ForegroundColor Yellow
      Write-Host "    git remote add origin https://github.com/<owner>/<repo>.git" -ForegroundColor Yellow
      Write-Host "    git push -u origin main" -ForegroundColor Yellow
    }
  } finally { Pop-Location }
}

# ---------- ссылка ----------
$owner = $null; $repo = $null
try {
  $remoteUrl = (& git -C $root remote get-url origin) 2>$null
  if ($remoteUrl -match "github\.com[:/](?<o>[^/]+)/(?<r>[^/.]+)") {
    $owner = $Matches['o']; $repo = $Matches['r']
  }
} catch { }

Write-Host ""
if ($owner -and $repo) {
  $viewerLink = "https://$owner.github.io/$repo/website/index.html#cloud=" +
    [Uri]::EscapeDataString("https://$owner.github.io/$repo/$src") +
    "&name=" + [Uri]::EscapeDataString($Title)
  Write-Host "  Ссылка на облако:" -ForegroundColor Cyan
  Write-Host "  $viewerLink" -ForegroundColor Green
  Write-Host ""
  Write-Host "  Галерея: https://$owner.github.io/$repo/"
  if ($Open) { Start-Process $viewerLink }
} else {
  $rel = "website/index.html#cloud=" + [Uri]::EscapeDataString($src) + "&name=" + [Uri]::EscapeDataString($Title)
  Write-Host "  Ссылку можно собрать после настройки remote. Сейчас относительная:" -ForegroundColor Yellow
  Write-Host "  $rel"
}

if (-not $pushed) {
  Write-Host ""
  Write-Host "  Файлы готовы локально: data\$Id  ($points точек)" -ForegroundColor Gray
}

Write-Host ""
