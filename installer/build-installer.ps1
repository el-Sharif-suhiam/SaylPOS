# Builds the Sayl POS installer:
#   1. dotnet publish Sayl.UI (self-contained win-x64) -> installer/staging/app
#   2. Stages SQL scripts (normalized to UTF-8)        -> installer/staging/database
#   3. Compiles installer/setup.iss with Inno Setup    -> installer/output/SaylPOS-Setup-<version>.exe
#
# Usage: .\build-installer.ps1 [-Version 1.0.0] [-Configuration Release]

param(
    [string]$Version = "1.0.0",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipRedistDownload
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$stagingApp = Join-Path $installerDir "staging\app"
$stagingDb = Join-Path $installerDir "staging\database"
$stagingRedist = Join-Path $installerDir "staging\redist"
$redistDir = Join-Path $installerDir "redist"
$outputDir = Join-Path $installerDir "output"

# --- Locate ISCC.exe (Inno Setup compiler) ---
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php or run: choco install innosetup"
}
Write-Host "Using ISCC: $iscc" -ForegroundColor Cyan

# --- Clean staging ---
Remove-Item -LiteralPath (Join-Path $installerDir "staging") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stagingApp, $stagingDb, $stagingRedist, $outputDir -Force | Out-Null

# --- 1) Publish the app ---
Write-Host "`n[1/3] Publishing Sayl.UI ($Configuration, win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "Sayl.UI\Sayl.UI.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $stagingApp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (-not (Test-Path (Join-Path $stagingApp "Sayl.UI.exe"))) {
    throw "Publish output is missing Sayl.UI.exe"
}

# --- Verify the publish actually shipped the CURRENT license-aware build ---
# The packaged app MUST include PosLicensing.dll (startup license gate) and
# the freshly built exe - otherwise we would silently repackage stale code.
$requiredAppFiles = @(
    "Sayl.UI.exe",
    "PosLicensing.dll",
    "Sayl.Core.dll",
    "Common.dll",
    "DependencyInjection.dll",
    "appsettings.json"
)
foreach ($name in $requiredAppFiles) {
    $file = Join-Path $stagingApp $name
    if (-not (Test-Path $file)) { throw "Publish output is missing required file: $file" }
}
Write-Host "  verified: PosLicensing.dll included (startup license gate will be enforced)."
$stagedExe = Get-Item (Join-Path $stagingApp "Sayl.UI.exe")
$srcStamp  = (Get-Item (Join-Path $repoRoot "Sayl.UI\Program.cs")).LastWriteTime
Write-Host ("  Sayl.UI.exe built at : {0:yyyy-MM-dd HH:mm:ss}" -f $stagedExe.LastWriteTime)
Write-Host ("  Program.cs last edit : {0:yyyy-MM-dd HH:mm:ss}" -f $srcStamp)
if ($stagedExe.LastWriteTime -lt $srcStamp) {
    throw "Staged Sayl.UI.exe is OLDER than the source Program.cs - stale publish detected. Rerun with a clean output."
}

# --- 2) Stage database scripts ---
# Each script is copied as-is (for manual use) AND wrapped in an XML envelope
# (CDATA) so the installer can read exact UTF-8 text via MSXML - Inno's
# Pascal Script cannot read UTF-8 files natively (ADODB.Stream 'Type' is a
# reserved word there).
Write-Host "`n[2/3] Staging SQL scripts..." -ForegroundColor Cyan
$sqlSources = @(
    (Join-Path $repoRoot "SaylDB.sql"),
    (Join-Path $repoRoot "DataSeed.sql"),
    # (Join-Path $repoRoot "SQL_Upgrades\Upgrade_01_Production.sql")
)
$stagingXml = Join-Path $stagingDb "xml"
New-Item -ItemType Directory -Path $stagingXml -Force | Out-Null
$utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
foreach ($src in $sqlSources) {
    if (-not (Test-Path $src)) { throw "Missing SQL script: $src" }
    $bytes = [System.IO.File]::ReadAllBytes($src)
    try   { $text = $utf8Strict.GetString($bytes) }   # valid UTF-8?
    catch { $text = [System.Text.Encoding]::Default.GetString($bytes) }  # fall back to ANSI

    # drop any UTF-8 BOM the source script starts with - it must not be
    # baked into the CDATA (SQL Server would choke on the leading U+FEFF)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }

    # original script, normalized to UTF-8 (manual reference)
    [System.IO.File]::WriteAllText((Join-Path $stagingDb (Split-Path -Leaf $src)), $text, $utf8NoBom)

    # XML envelope (CDATA) for the installer
    $base = [System.IO.Path]::GetFileNameWithoutExtension($src)
    $cdata = $text.Replace(']]>', ']]]]><![CDATA[>')
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<r><![CDATA[$cdata]]></r>"
    [System.IO.File]::WriteAllText((Join-Path $stagingXml "$base.xml"), $xml, $utf8NoBom)
    Write-Host "  staged: $(Split-Path -Leaf $src)"
}

# --- 3) Stage SQL Server Express redistributable ---
# Bundled INSIDE the setup so shops with poor internet get a fully offline
# installer. Downloaded once into installer/redist/ and reused afterwards.
Write-Host "`n[3/5] Staging SQL Server Express redistributable..." -ForegroundColor Cyan
$sqlexprName = "SQLEXPR_x64_ENU.exe"
# Official Microsoft CDN - SQL Server 2022 Express, engine-only, x64, ENU
$sqlexprUrl = "https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SQLEXPR_x64_ENU.exe"
$redistFile = Join-Path $redistDir $sqlexprName
if (-not (Test-Path $redistFile)) {
    if ($SkipRedistDownload) { throw "Redistributable missing and -SkipRedistDownload set: $redistFile" }
    Write-Host "  downloading (~250 MB, one-time, cached in installer/redist/)..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $redistDir -Force | Out-Null
    Invoke-WebRequest -Uri $sqlexprUrl -OutFile $redistFile -UseBasicParsing
}
$sizeMB = [math]::Round((Get-Item $redistFile).Length / 1MB)
if ($sizeMB -lt 50) { throw "Cached $sqlexprName looks too small (${sizeMB} MB) - delete it and rebuild." }
Copy-Item $redistFile (Join-Path $stagingRedist $sqlexprName) -Force
Write-Host ("  staged: {0} ({1} MB)" -f $sqlexprName, $sizeMB)

# --- 4) Ensure setup.iss is UTF-8 with BOM ---
# Inno only treats .iss as Unicode when a BOM is present.
$issPath = Join-Path $installerDir "setup.iss"
$text = [System.IO.File]::ReadAllText($issPath)
[System.IO.File]::WriteAllText($issPath, $text, (New-Object System.Text.UTF8Encoding($true)))

# --- 5) Compile installer ---
Write-Host "`n[4/5] Compiling installer..." -ForegroundColor Cyan
& $iscc "/DAPP_VERSION=$Version" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed with exit code $LASTEXITCODE" }

$setupExe = Get-ChildItem -Path $outputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`n[5/5] Done." -ForegroundColor Green
Write-Host ("Installer : {0}" -f $setupExe.FullName)
Write-Host ("Size      : {0:N1} MB" -f ($setupExe.Length / 1MB))
