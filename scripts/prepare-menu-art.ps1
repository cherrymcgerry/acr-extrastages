<#
.SYNOPSIS
  Extracts DR2's loading-screen art (frontend/streamed_textures/loading/<location>_rally_NN_route_N.tpk, painted aerials without
  logos or text) from the local DR2 install into work/dr2/menu-art/ for the generated menu images (docs/menu-images.md).
  Idempotent; mods/additive/build.ps1 runs it when a spec's Menu.Art file is missing.
#>
param(
    [string]$Dr2Dir = $(if ($env:DR2_DIR) { $env:DR2_DIR } else { 'C:\Games\DiRT Rally 2.0' }),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'work\dr2\menu-art'
$dir = Join-Path $out 'frontend\streamed_textures\loading'
if (-not $Force -and (Get-ChildItem $dir -Filter '*.tpk' -ErrorAction SilentlyContinue | Select-Object -First 1)) { return }
$exe = Join-Path $root 'tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe'
if (-not (Test-Path $exe)) { throw "missing $exe (build tools\dr2-extract)" }
& $exe extract (Join-Path $Dr2Dir 'game\game.dat') $out --filter 'streamed_textures/loading/.*_rally_\d+_route_\d+\.tpk$'
if ($LASTEXITCODE -ne 0) { throw 'dr2-extract failed' }
