$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$BinDir      = Join-Path $ProjectRoot "bin"
$ProfileRoot = "C:\Users\ASUS\AppData\Roaming\Thunderstore Mod Manager\DataFolder\PEAK\profiles\RANKED"

# 作者名改为个人 (Thunderstore 显示为 "PeakCSR vX by DBJD-CR")
$AuthorName  = "DBJD-CR"
$PackageName = "$AuthorName-PeakCSR"
$Version     = "0.2.1"

$LegacyDirs  = @(
    (Join-Path $ProfileRoot "BepInEx\plugins\PeakCSR"),
    (Join-Path $ProfileRoot "BepInEx\plugins\PancakesLabs-PeakCSR")
)
$DeployDir   = Join-Path $ProfileRoot "BepInEx\plugins\$PackageName"

New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

# 清理旧的组织名部署目录, 避免管理器里出现重复条目
foreach ($dir in $LegacyDirs) {
    if (Test-Path $dir) {
        Remove-Item -Path $dir -Recurse -Force
        Write-Host "[PeakCSR] Removed legacy deploy dir: $dir" -ForegroundColor DarkYellow
    }
}

$csproj = Join-Path $ProjectRoot "PeakCSR.csproj"
$outDll = Join-Path $BinDir "PeakCSR.dll"

Write-Host "[PeakCSR] Building with modern .NET SDK..." -ForegroundColor Cyan
& dotnet build $csproj -c Release -o $BinDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code: $LASTEXITCODE"
}

# 1. 生成专属图标 (若不存在, 运行 logo.ps1 程序化绘制)
$iconPath = Join-Path $ProjectRoot "icon.png"
if (-not (Test-Path $iconPath)) {
    $logoScript = Join-Path $ProjectRoot "logo.ps1"
    if (Test-Path $logoScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $logoScript
    }
}

# 2. 部署到 Thunderstore Profile
Copy-Item -Path $outDll -Destination (Join-Path $DeployDir "PeakCSR.dll") -Force
Copy-Item -Path (Join-Path $ProjectRoot "manifest.json") -Destination (Join-Path $DeployDir "manifest.json") -Force
Copy-Item -Path (Join-Path $ProjectRoot "README.md") -Destination (Join-Path $DeployDir "README.md") -Force
if (Test-Path $iconPath) {
    Copy-Item -Path $iconPath -Destination (Join-Path $DeployDir "icon.png") -Force
}

# 3. 打包标准 Thunderstore .zip
$zipPath = Join-Path $BinDir "$PackageName-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$tempPack = Join-Path $BinDir "_pack_temp"
if (Test-Path $tempPack) { Remove-Item $tempPack -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tempPack | Out-Null
Copy-Item -Path $outDll -Destination (Join-Path $tempPack "PeakCSR.dll") -Force
Copy-Item -Path (Join-Path $ProjectRoot "manifest.json") -Destination (Join-Path $tempPack "manifest.json") -Force
Copy-Item -Path (Join-Path $ProjectRoot "README.md") -Destination (Join-Path $tempPack "README.md") -Force
if (Test-Path $iconPath) {
    Copy-Item -Path $iconPath -Destination (Join-Path $tempPack "icon.png") -Force
}
Compress-Archive -Path "$tempPack\*" -DestinationPath $zipPath -Force
Remove-Item $tempPack -Recurse -Force

# 4. 同步 Thunderstore Profile 的 mods.yml (替换旧的 PancakesLabs 条目)
$modsYmlPath = Join-Path $ProfileRoot "mods.yml"
if (Test-Path $modsYmlPath) {
    $modsContent = Get-Content -Path $modsYmlPath -Raw
    if ($modsContent -match "name:\s*PancakesLabs-PeakCSR") {
        $modsContent = $modsContent -replace "(?m)^(\s*)name:\s*PancakesLabs-PeakCSR", "`$1name: $PackageName"
        $modsContent = $modsContent -replace "(?m)^(\s*)authorName:\s*PancakesLabs", "`$1authorName: $AuthorName"
        Set-Content -Path $modsYmlPath -Value $modsContent -Encoding UTF8
        Write-Host "[PeakCSR] mods.yml migrated: author -> $AuthorName" -ForegroundColor Yellow
    }
    elseif ($modsContent -notmatch "name:\s*$([regex]::Escape($PackageName))") {
        $entry = @"
- manifestVersion: 1
  name: $PackageName
  authorName: $AuthorName
  websiteUrl: https://github.com/DBJD-CR/PeakCSR
  displayName: PeakCSR
  description: >-
    PCSR 1v1 Live Ranked extension for PEAK RANKED. Real-time same-seed 1v1
    matchmaking, Sudden Death rulesets, WebSocket ELO handshake, and native UI.
  gameVersion: '0'
  networkMode: both
  packageType: other
  installMode: managed
  installedAtTime: 1792750000000
  loaders: []
  dependencies:
    - BepInEx-BepInExPack_PEAK-5.4.75301
    - PeakCode-PEAK_RANKED-1.6.0
  incompatibilities: []
  optionalDependencies: []
  versionNumber:
    major: 0
    minor: 2
    patch: 1
  enabled: true
  onlineSource: false
  trustedPackage: false
"@
        Add-Content -Path $modsYmlPath -Value $entry -Encoding UTF8
        Write-Host "[PeakCSR] Registered $PackageName into Thunderstore mods.yml!" -ForegroundColor Yellow
    }
}

Write-Host "[PeakCSR] Built & deployed to $DeployDir" -ForegroundColor Green
Write-Host "[PeakCSR] Thunderstore Zip ready at: $zipPath" -ForegroundColor Green
