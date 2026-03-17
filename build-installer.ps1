param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.2"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "BlindTouchOled\BlindTouchOled.csproj"
$publishDir = Join-Path $projectRoot "publish"
$issFile = Join-Path $projectRoot "installer\BlindTouchOled.iss"

Write-Host "== 1/3 Build publish output ==" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

dotnet publish $projectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:Version=$Version `
    -o $publishDir

Write-Host "== 2/3 Find Inno Setup ==" -ForegroundColor Cyan
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install from https://jrsoftware.org/isdl.php and run again."
}

Write-Host "== 3/3 Build setup.exe ==" -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" $issFile

$distDir = Join-Path $projectRoot "dist"
Write-Host ""
Write-Host "Done. Installer output: $distDir" -ForegroundColor Green


