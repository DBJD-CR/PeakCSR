$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot

# 游戏运行时 DLL 会被进程锁定, 复制会抛 IOException; 这里做带提示的重试
function Copy-WithRetry {
    param([string]$Source, [string]$Target, [int]$Retries = 15)
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            Copy-Item -Path $Source -Destination $Target -Force -ErrorAction Stop
            return
        }
        catch {
            if ($i -eq $Retries) { throw }
            if ($i -eq 1) {
                Write-Host "[PeakCSR] 目标文件被占用 (游戏可能在运行), 等待释放..." -ForegroundColor DarkYellow
            }
            Start-Sleep -Milliseconds 700
        }
    }
}
$BinDir      = Join-Path $ProjectRoot "bin"
# Profile 路径可被环境变量 PEAK_PROFILE_DIR 或 local.props 覆盖 (csproj 同步该逻辑)
$ProfileRoot = $env:PEAK_PROFILE_DIR
if ([string]::IsNullOrWhiteSpace($ProfileRoot)) {
    $propsFile = Join-Path $ProjectRoot "local.props"
    if (Test-Path $propsFile) {
        Get-Content $propsFile | ForEach-Object {
            if ($_ -match "<ProfileDir>(.+)</ProfileDir>") { $ProfileRoot = $Matches[1].Trim() }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ProfileRoot)) {
    $ProfileRoot = "C:\Users\ASUS\AppData\Roaming\Thunderstore Mod Manager\DataFolder\PEAK\profiles\RANKED"
}

# 作者名改为个人 (Thunderstore 显示为 "PeakCSR vX by DBJD-CR")
$AuthorName  = "DBJD-CR"
$PackageName = "$AuthorName-PeakCSR"

# 版本号以 manifest.json 为唯一真相源, 避免多处硬编码不一致
$manifestPath = Join-Path $ProjectRoot "manifest.json"
$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$Version = [string]$manifest.version_number
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "manifest.json 缺少 version_number 字段"
}
$parts = $Version.Split('.')
$VerMajor = [int]$parts[0]
$VerMinor = [int]$parts[1]
$VerPatch = if ($parts.Length -gt 2) { [int]$parts[2] } else { 0 }
Write-Host "[PeakCSR] 目标版本 (来自 manifest.json): $Version" -ForegroundColor Cyan

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
Copy-WithRetry -Source $outDll -Target (Join-Path $DeployDir "PeakCSR.dll")
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
Copy-WithRetry -Source $outDll -Target (Join-Path $tempPack "PeakCSR.dll")
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

    # 旧组织名条目: 先整体改名为个人条目, 后续统一同步字段
    if ($modsContent -match "name:\s*PancakesLabs-PeakCSR") {
        $modsContent = $modsContent -replace "(?m)^(\s*)name:\s*PancakesLabs-PeakCSR", "`$1name: $PackageName"
        $modsContent = $modsContent -replace "(?m)^(\s*)authorName:\s*PancakesLabs", "`$1authorName: $AuthorName"
        Write-Host "[PeakCSR] mods.yml 旧条目已更名为 $PackageName" -ForegroundColor Yellow
    }

    if ($modsContent -match "name:\s*$([regex]::Escape($PackageName))") {
        # 已存在条目: 就地同步 authorName / websiteUrl / versionNumber 三行
        $lines = $modsContent -split "`r?`n"
        $entryStart = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "name:\s*$([regex]::Escape($PackageName))\s*$") { $entryStart = $i; break }
        }
        if ($entryStart -ge 0) {
            # 找到本条目内的 versionNumber 段并重写数字
            $verIdx = -1
            for ($i = $entryStart; $i -lt [Math]::Min($entryStart + 40, $lines.Count); $i++) {
                if ($lines[$i] -match "^\s*versionNumber:\s*$") { $verIdx = $i; break }
            }
            if ($verIdx -ge 0) {
                for ($k = $verIdx + 1; $k -lt [Math]::Min($verIdx + 4, $lines.Count); $k++) {
                    if ($lines[$k] -match "^(\s*)major:\s*\d+")     { $lines[$k] = "$($Matches[1])major: $VerMajor" }
                    elseif ($lines[$k] -match "^(\s*)minor:\s*\d+") { $lines[$k] = "$($Matches[1])minor: $VerMinor" }
                    elseif ($lines[$k] -match "^(\s*)patch:\s*\d+") { $lines[$k] = "$($Matches[1])patch: $VerPatch" }
                }
            }
            # 顺带修正作者名与仓库地址
            for ($i = $entryStart; $i -lt [Math]::Min($entryStart + 40, $lines.Count); $i++) {
                if ($lines[$i] -match "^(\s*)authorName:")  { $lines[$i] = "$($Matches[1])authorName: $AuthorName" }
                if ($lines[$i] -match "^(\s*)websiteUrl:")  { $lines[$i] = "$($Matches[1])websiteUrl: https://github.com/$AuthorName/PeakCSR" }
            }
            Set-Content -Path $modsYmlPath -Value ($lines -join "`r`n") -Encoding UTF8
            Write-Host "[PeakCSR] mods.yml 已同步: author=$AuthorName, version=$Version" -ForegroundColor Yellow
        }
    }
    else {
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
