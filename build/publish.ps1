<#
.SYNOPSIS
  Produces the shippable app folder (and optionally the MSI). CI and local builds use this same script.

.EXAMPLE
  ./build/publish.ps1                       # artifacts/publish/WinWallpaper
  ./build/publish.ps1 -Version 0.2.0 -Msi   # + artifacts/installer/WinWallpaper-0.2.0-x64.msi

  WallpaperHost and WallpaperDaemon are Native AOT (small, fast start, no runtime needed).
  WallpaperTray is WPF, which can't be AOT-compiled, so it is published self-contained.
#>
param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$Msi
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts/publish/WinWallpaper"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be MAJOR.MINOR.PATCH (MSI requirement), got '$Version'" }

# Native AOT links with MSVC; the toolchain finds it through vswhere, which isn't on PATH by default.
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$vsInstaller\vswhere.exe") -and -not (Get-Command vswhere -ErrorAction SilentlyContinue)) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$common = @("-c", $Configuration, "-r", $Runtime, "-o", $out, "-p:Version=$Version", "-nologo")

function Publish([string]$project, [string[]]$extra) {
    Write-Host "==> publish $project" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src/$project") @common @extra
    if ($LASTEXITCODE -ne 0) { throw "publish $project failed" }
}

Publish "WallpaperHost"   @("-p:PublishAot=true")
Publish "WallpaperDaemon" @("-p:PublishAot=true")
Publish "WallpaperTray"   @("--self-contained", "true")

# The tray's "How to connect a custom renderer" link looks for docs/ next to the exe.
New-Item -ItemType Directory -Force "$out/docs" | Out-Null
Copy-Item "$root/docs/custom-render-outputs.md" "$out/docs/"
Copy-Item "$root/README.md" $out
Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item   # symbols stay out of the installer

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("==> app folder: {0} ({1:N0} MB)" -f $out, $size) -ForegroundColor Green

if ($Msi) {
    Write-Host "==> build MSI" -ForegroundColor Cyan
    # Rebuild: WiX's incremental check ignores a changed Version and would skip producing the new MSI.
    dotnet build (Join-Path $root "installer/WinWallpaper.wixproj") -t:Rebuild -c $Configuration -nologo "-p:Version=$Version" "-p:PublishDir=$out"
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }
    Get-ChildItem (Join-Path $root "artifacts/installer") -Filter *.msi | ForEach-Object {
        Write-Host ("==> {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
    }
}
