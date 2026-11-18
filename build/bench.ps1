<#
.SYNOPSIS
  Best-case frame-rate benchmark: every transport x target fps x resolution, desktop layer.
  Runs samples/GpuRenderer (Release) against the Release daemon/host and writes one CSV row per run.

.EXAMPLE
  dotnet build WinWallpaperManager.slnx -c Release
  ./build/bench.ps1                                   # full matrix (~12 min)
  ./build/bench.ps1 -Apis d3d11,vulkan -Sizes 1920x1080 -Fps 60,180   # a slice

  producerFps  frames the producer published per second (its own pacing)
  hostFps      frames the host actually copied + presented per second (what you see)
  skipped      GPU only: ticks the producer skipped because the host still held the slot
  torn         CPU only: frames the host dropped because the producer overwrote them mid-copy
#>
param(
    # Comma-separated, so they also work through "powershell -File".
    [string]$Apis = "cpu,d3d11,d3d12,vulkan",
    [string]$Fps = "30,60,90,120,150,180",
    [string]$Sizes = "1280x720,1920x1080,2560x1440,3840x2160",
    [int]$Seconds = 6,
    [string]$Csv = "artifacts/bench/bench.csv"
)
$ErrorActionPreference = "Stop"
$apiList = $Apis -split "," | ForEach-Object { $_.Trim() }
$fpsList = $Fps -split "," | ForEach-Object { [int]$_ }
$sizeList = $Sizes -split "," | ForEach-Object { $_.Trim() }
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root "artifacts/bin/Release"
$renderer = Get-ChildItem (Join-Path $root "samples/GpuRenderer/bin/Release") -Recurse -Filter GpuRenderer.exe | Select-Object -First 1
if (-not $renderer -or -not (Test-Path "$bin/WallpaperDaemon.exe")) { throw "Build first: dotnet build WinWallpaperManager.slnx -c Release" }

function Request([hashtable]$msg, [int]$timeoutMs = 10000) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "WinWallpaper.Daemon.v2", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect($timeoutMs)
    try {
        $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true; $w.NewLine = "`n"
        $r = New-Object System.IO.StreamReader($pipe)
        $w.WriteLine(($msg | ConvertTo-Json -Compress)); $r.ReadLine() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}

Get-Process WallpaperTray, WallpaperDaemon, WallpaperHost -ErrorAction SilentlyContinue | Stop-Process -Force
$state = "$env:LOCALAPPDATA\WinWallpaper\state.json"
if (Test-Path $state) { Move-Item $state "$state.bench-backup" -Force }
Start-Process "$bin/WallpaperDaemon.exe"
$hostProc = $null
for ($i = 0; $i -lt 50 -and -not $hostProc; $i++) {
    try { Request @{ cmd = "host.status" } 500 | Out-Null; $hostProc = Get-Process WallpaperHost -ErrorAction SilentlyContinue } catch { }
    if (-not $hostProc) { Start-Sleep -Milliseconds 200 }
}
if (-not $hostProc) { throw "daemon/host didn't start" }

New-Item -ItemType Directory -Force (Split-Path (Join-Path $root $Csv)) | Out-Null
$csvPath = Join-Path $root $Csv
"api,width,height,target,producerFps,hostFps,skipped,torn,producerCpu,hostCpu,hostMsPerFrame" | Set-Content $csvPath

try {
    foreach ($size in $sizeList) {
        $w, $h = $size -split "x"
        foreach ($api in $apiList) {
            foreach ($fps in $fpsList) {
                $out = Join-Path $env:TEMP "bench-$api.txt"
                $p = Start-Process $renderer.FullName -ArgumentList "--api $api --width $w --height $h --fps $fps --seconds $Seconds" `
                    -RedirectStandardOutput $out -NoNewWindow -PassThru
                Start-Sleep -Milliseconds 1000                                   # warm-up
                $s0 = (Request @{ cmd = "host.status" }).desktop
                $hostProc.Refresh(); $hc0 = $hostProc.TotalProcessorTime; $t0 = [Diagnostics.Stopwatch]::StartNew()
                Start-Sleep -Milliseconds (($Seconds - 2) * 1000)                # measured window
                $s1 = (Request @{ cmd = "host.status" }).desktop
                $hostProc.Refresh(); $hc1 = $hostProc.TotalProcessorTime; $dt = $t0.Elapsed.TotalSeconds
                $p.WaitForExit(15000) | Out-Null
                $line = Get-Content $out | Where-Object { $_ -like "BENCH *" } | Select-Object -First 1
                if (-not $line) { Write-Host "FAILED $api $size @$fps`: $((Get-Content $out) -join ' ')" -ForegroundColor Red; continue }
                $b = $line.Substring(6) | ConvertFrom-Json
                $frames = $s1.presented - $s0.presented
                $row = [pscustomobject]@{
                    api = $api; width = [int]$w; height = [int]$h; target = $fps
                    producerFps = [math]::Round($b.rendered / $b.seconds, 1)
                    hostFps = [math]::Round($frames / $dt, 1)
                    skipped = $b.skipped
                    torn = $s1.torn - $s0.torn
                    producerCpu = [math]::Round($b.cpuMs / $b.seconds / 10, 1)
                    hostCpu = [math]::Round(($hc1 - $hc0).TotalMilliseconds / $dt / 10, 1)
                    hostMsPerFrame = if ($frames -gt 0) { [math]::Round(($s1.presentMs - $s0.presentMs) / $frames, 2) } else { 0 }
                }
                ($row.PSObject.Properties.Value -join ",") | Add-Content $csvPath
                Write-Host ("{0,-6} {1,9} @{2,3}: producer {3,6} fps  host {4,6} fps  skipped {5,4}  torn {6,3}  cpu p/h {7,5}%/{8,5}%  host {9} ms/frame" -f `
                    $api, $size, $fps, $row.producerFps, $row.hostFps, $row.skipped, $row.torn, $row.producerCpu, $row.hostCpu, $row.hostMsPerFrame)
            }
        }
    }
}
finally {
    try { Request @{ cmd = "quit" } | Out-Null } catch { }
    Start-Sleep 2
    Get-Process WallpaperDaemon, WallpaperHost -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path "$state.bench-backup") { Move-Item "$state.bench-backup" $state -Force }
}
Write-Host "==> $csvPath" -ForegroundColor Green
