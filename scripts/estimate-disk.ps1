<#
.SYNOPSIS
  Disk budget / preflight: how much space the stage files take, the peak working space of a build, and the free space to require.

.DESCRIPTION
  installed size  = enabled stages x average container size (mean of work/acr/batch/report.json when it exists, else 700 MB) + registry
  working peak    = shared data (DR2 layer cache, legacy extraction, tools) + light IR of every stage + ONE stage in flight
                    (heavy IR + its DR2 location cache / build scratch). That is the flow of `build-stages.ps1 -PrepareOnDemand`
                    (heavy IR pruned after each build, location cache dropped when the location changes). `-Flow bulk` prices the old
                    "prepare everything, then build everything" flow instead.
  free space      = output still to be written + working peak + 2 GB margin (output counted twice with -InstallMode copy / other volumes)

  The numbers are the measured constants in scripts/disk-lib.ps1 (docs/resume-notes.md, "Disk budget"). The launcher should
  reimplement Get-DiskEstimate from that file: it is a pure function; the JSON this script prints (-Json, schema 1) is its contract.

.PARAMETER Stages       number of stages to price (default: enabled stages in config/stages.toml)
.PARAMETER ContainerMB  average container size (default: measured from the last batch report, else 700)
.PARAMETER Flow         streaming (default) | bulk | build-only (IR already prepared)
.PARAMETER InstallMode  hardlink (default; needs work\ and the game on one NTFS volume) | copy
.PARAMETER Game         ACR install dir (default $env:ACR_GAME_DIR or the Steam path)
.PARAMETER Json         print ONLY the JSON object (for the launcher), no table
.PARAMETER OutFile      also write the JSON here
.PARAMETER Check        exit code 3 when the free space on the work / game volume is below the requirement (preflight)

.EXAMPLE
  pwsh scripts\estimate-disk.ps1
  pwsh scripts\estimate-disk.ps1 -Json -Stages 12
#>
param(
    [int]$Stages = 0,
    [double]$ContainerMB = 0,
    [ValidateSet('streaming', 'bulk', 'build-only')][string]$Flow = 'streaming',
    [ValidateSet('hardlink', 'copy')][string]$InstallMode = 'hardlink',
    [string]$Game = $(if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' }),
    [string]$Config = (Join-Path (Split-Path $PSScriptRoot) 'config\stages.toml'),
    [switch]$Json,
    [string]$OutFile,
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'stages-lib.ps1')
. (Join-Path $PSScriptRoot 'disk-lib.ps1')

$data = Read-StagesToml $Config
$enabled = @($data.Stages | ? { $_.enabled })
if ($Stages -le 0) { $Stages = $enabled.Count }
if ($Stages -le 0) { throw "no enabled stages in $Config" }

# average container size: measured from the last batch report, else the default
$source = 'default'
$reportPath = Join-Path $root 'work\acr\batch\report.json'
if ($ContainerMB -le 0) {
    $ContainerMB = $script:DiskBudget.container_mb_default
    if (Test-Path $reportPath) {
        $mbs = @((Get-Content $reportPath -Raw | ConvertFrom-Json).stages | ? { $_.container_mb } | % { [double]$_.container_mb })
        if ($mbs.Count -ge 3) { $ContainerMB = [math]::Round(($mbs | Measure-Object -Average).Average); $source = "work/acr/batch/report.json ($($mbs.Count) stages)" }
    }
}
else { $source = 'parameter' }

# containers already built (resumed run): only enabled stages, current naming
$dist = Join-Path $root 'work\acr\additive\dist'
$existing = 0
foreach ($st in $enabled) {
    $loc = $data.Locations[$st.location]
    if (-not $loc) { continue }
    $id = (Get-AcrId $st.dr2 $loc.code).ToLowerInvariant()
    $f = Join-Path $dist "zz_acrxs_s_${id}_P.ucas"
    if (Test-Path $f) { $existing += (Get-Item $f).Length / 1MB }
}
$existing = [math]::Round($existing)

$workVol = Split-Path -Qualifier (Resolve-Path $root).Path
$gameDir = if (Test-Path $Game) { (Resolve-Path $Game).Path } else { $null }
$sameVolume = if ($gameDir) { (Split-Path -Qualifier $gameDir) -eq $workVol } else { $true }
$freeWork = Get-DriveFreeMB $root
$freeGame = if ($gameDir) { Get-DriveFreeMB $gameDir } else { -1 }

$est = Get-DiskEstimate -Stages $Stages -ContainerMB $ContainerMB -Flow $Flow -InstallMode $InstallMode -ExistingOutputMB $existing `
    -SameVolume $sameVolume -FreeWorkMB $freeWork -FreeGameMB $freeGame
$est.inputs['container_mb_source'] = $source
$est.inputs['work_volume'] = $workVol
$est.generated = (Get-Date).ToString('s')
$est.message = if ($est.ok) { 'enough free space' } else { "need $([math]::Round($est.shortfall_mb / 1024, 1)) GB more free space" }

$jsonText = $est | ConvertTo-Json -Depth 6
if ($OutFile) {
    New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($OutFile))) | Out-Null
    Set-Content $OutFile $jsonText -Encoding UTF8
}
if ($Json) { $jsonText }
else {
    function G([double]$mb) { '{0,6:N1} GB' -f ($mb / 1024) }
    $w = $est.working_peak_mb
    Write-Host "Disk budget: $Stages stage(s), $Flow flow, install = $($est.inputs.install_mode), avg container $ContainerMB MB ($source)"
    Write-Host ("  stage files (dist -> ~mods) {0}   ({1} x {2} MB + registry; already built: {3})" -f (G $est.output_mb.total), $Stages, $ContainerMB, (G $existing))
    Write-Host ("  working peak                {0}   shared {1} + light IR {2} + heavy IR {3} + in-flight stage {4}" -f (G $w.total), (G $w.shared), (G $w.light_ir), (G $w.heavy_ir), (G $w.transient))
    Write-Host ("  safety margin               {0}" -f (G $est.safety_margin_mb))
    Write-Host ("  FREE SPACE REQUIRED         {0}   (work volume {1}{2})" -f (G $est.required_free_mb.work_volume), $workVol, $(if (-not $sameVolume) { "; game volume needs $(G $est.required_free_mb.game_volume)" }))
    Write-Host ("  free now                    {0}   -> {1}" -f (G $freeWork), $(if ($est.ok) { 'OK' } else { "NOT ENOUGH, $($est.message)" }))
    if ($sameVolume -and $InstallMode -eq 'hardlink') { Write-Host '  (hard-link install: the stage files are on disk once)' }
}
if ($Check -and -not $est.ok) { exit 3 }
