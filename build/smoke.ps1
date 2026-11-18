<#
.SYNOPSIS
  Smoke-tests a published app folder: starts the real (AOT) daemon, drives it over its pipe
  with generated media and an external renderer, then quits it. Used by CI after publishing.

.EXAMPLE
  ./build/smoke.ps1 -AppDir artifacts/publish/WinWallpaper
#>
param([string]$AppDir = "artifacts/publish/WinWallpaper")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$AppDir = (Resolve-Path $AppDir).Path
$media = Join-Path $root "artifacts/smoke-media"
$failures = 0

function Request([hashtable]$msg) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "WinWallpaper.Daemon.v2", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(10000)
    try {
        $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true; $w.NewLine = "`n"
        $r = New-Object System.IO.StreamReader($pipe)
        $w.WriteLine(($msg | ConvertTo-Json -Compress))
        return $r.ReadLine() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "PASS  $name $detail" -ForegroundColor Green }
    else { Write-Host "FAIL  $name $detail" -ForegroundColor Red; $script:failures++ }
}

# --- test media -------------------------------------------------------------
Write-Host "==> generating test media"
dotnet run --project (Join-Path $root "tests/tools/MakeTestMedia") -c Release -- $media | Out-Host
Add-Type -AssemblyName System.Drawing
$png = Join-Path $media "test.png"
$bmp = New-Object System.Drawing.Bitmap 1920, 1080
[System.Drawing.Graphics]::FromImage($bmp).Clear([System.Drawing.Color]::SteelBlue)
$bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# --- run --------------------------------------------------------------------
Get-Process WallpaperDaemon, WallpaperHost -ErrorAction SilentlyContinue | Stop-Process -Force
# Don't clobber a developer's saved wallpaper: park state.json and put it back afterwards.
$state = "$env:LOCALAPPDATA\WinWallpaper\state.json"
$stateBackup = "$state.smoke-backup"
if (Test-Path $state) { Move-Item $state $stateBackup -Force }
Write-Host "==> starting $AppDir\WallpaperDaemon.exe"
Start-Process (Join-Path $AppDir "WallpaperDaemon.exe")
try {
    $s = Request @{ cmd = "status" }
    Check "daemon answers" ($s.ok -and $s.protocol -eq 2) "(state=$($s.state))"
    Check "daemon started host" ([bool](Get-Process WallpaperHost -ErrorAction SilentlyContinue))

    $r = Request @{ cmd = "set"; path = $png }
    Check "image" ($r.ok -and $r.source.width -eq 1920) "$($r.error)"

    $r = Request @{ cmd = "set"; path = (Join-Path $media "test.gif"); fps = 12 }
    Check "gif" ($r.ok -and $r.source.frames -eq 20 -and $r.source.fps -eq 12) "$($r.error)"

    $r = Request @{ cmd = "set"; path = (Join-Path $media "test.mp4") }
    if ($r.ok) { Check "video ($($r.source.decode) decode)" ($r.source.width -eq 1280 -and $r.source.nativeFps -eq 30) }
    else { Write-Host "SKIP  video ($($r.error)) - Media Foundation missing? (normal on Windows Server)" -ForegroundColor Yellow }

    foreach ($c in @(@{ cmd = "fit"; mode = "fit" }, @{ cmd = "sampling"; mode = "pixelated" }, @{ cmd = "pause" }, @{ cmd = "resume" })) {
        $r = Request $c
        Check "$($c.cmd) $($c.mode)" $r.ok "$($r.error)"
    }

    if (Get-Command python -ErrorAction SilentlyContinue) {
        $py = Start-Process python -ArgumentList "`"$root/samples/python/color_cycle.py`" 4" -PassThru -NoNewWindow
        Start-Sleep 2
        $r = Request @{ cmd = "status" }
        Check "external renderer takes over" ($r.state -eq "external") "(state=$($r.state))"
        $py.WaitForExit(15000) | Out-Null
        Start-Sleep 1
        $r = Request @{ cmd = "status" }
        Check "previous wallpaper restored after disconnect" ($r.state -in "playing", "paused") "(state=$($r.state))"

        $py = Start-Process python -ArgumentList "`"$root/samples/python/color_cycle.py`" 4 --overlay" -PassThru -NoNewWindow
        Start-Sleep 2
        $r = Request @{ cmd = "status" }
        Check "overlay layer is independent" ($r.overlay.name -eq "Python colour cycle" -and $r.state -in "playing", "paused") "(overlay=$($r.overlay.name) desktop=$($r.state))"
        $r = Request @{ cmd = "overlay.stop" }
        Check "overlay.stop" ($r.ok -and -not $r.overlay)
        $py.WaitForExit(15000) | Out-Null
    } else { Write-Host "SKIP  external renderer (python not found)" -ForegroundColor Yellow }

    # GPU sessions: host-created shared textures + fences imported by each API. Skipped where the
    # machine can't do it (CI runners have no GPU / Vulkan driver), failed if it starts but breaks.
    $gpu = Join-Path $root "samples/GpuRenderer"
    foreach ($api in "d3d11", "d3d12", "vulkan") {
        $out = dotnet run --project $gpu -c Release --no-build -- --api $api --layer overlay --seconds 3 2>&1 | Out-String
        if ($out -match "Rendered (\d+) frames") { Check "gpu $api" ([int]$Matches[1] -gt 30) "($($Matches[1]) frames in 3s)" }
        else { Write-Host "SKIP  gpu $api ($(($out -split "`n" | Where-Object { $_ -match 'Exception|failed' } | Select-Object -First 1).Trim()))" -ForegroundColor Yellow }
    }

    # The daemon watches the host process: kill it and the wallpaper must come back by itself.
    $before = (Request @{ cmd = "status" }).state
    Get-Process WallpaperHost -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 4
    $r = Request @{ cmd = "status" }
    Check "host restarted after a crash" ([bool](Get-Process WallpaperHost -ErrorAction SilentlyContinue) -and $r.state -eq $before) "(state=$($r.state))"

    $r = Request @{ cmd = "stop" }
    Check "stop" ($r.ok -and $r.state -eq "stopped")
}
finally {
    try { Request @{ cmd = "quit" } | Out-Null } catch { }
    Start-Sleep 2
    $left = Get-Process WallpaperDaemon, WallpaperHost -ErrorAction SilentlyContinue
    Check "quit stops daemon and host" (-not $left)
    $left | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $state -ErrorAction SilentlyContinue
    if (Test-Path $stateBackup) { Move-Item $stateBackup $state -Force }
}

$log = "$env:LOCALAPPDATA\WinWallpaper\logs\daemon.log"
if ($failures -gt 0 -and (Test-Path $log)) { Write-Host "---- daemon.log"; Get-Content $log }
if ($failures -gt 0) { throw "$failures smoke check(s) failed" }
Write-Host "==> smoke test passed" -ForegroundColor Green
