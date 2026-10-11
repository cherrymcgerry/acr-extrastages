# Builds tools/dr2-extract. Run scripts/bootstrap.ps1 first.
# NuGetAudit is disabled because EgoEngineLibrary pins SixLabors.ImageSharp 2.1.13 (known advisories)
# with TreatWarningsAsErrors; we only use it offline on local game files.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
& $dotnet build (Join-Path $root 'tools\dr2-extract\Dr2Extract.csproj') -c Release -p:NuGetAudit=false @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
