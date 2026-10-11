<#
.SYNOPSIS
  Ports one DR2 stage (from its stage IR) into ACR, editor-less, using Hafren South's forward variant
  ("Afon Bidno - Severn", variant 1/2) as the host. Produces one IoStore container
  zz_acrdirt_<short>_P.{utoc,ucas,pak}: v1 in work\acr\dist, v2 in work\acr\dist-v2 (same container name: both
  override the same host packages, so install only one of them at a time).

  Overridden host packages (all under /Game):
    Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData   physics (DR2 ground added, host flattened;
                                                                                 v2: + DR2 object colliders)
    Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP             forward gameplay cell (splines, triggers, start;
                                                                                 v2: respawn walls re-laid along the DR2 contour)
    Data/Database/Main/TrackSelection/DT_TracksVariants                          forward row Length
    Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward                           v1: emptied; v2: DR2 calls (config/pacenote-map.toml)

  Nothing is copied into the game install; see docs/first-stage-test.md.

.PARAMETER Stage    IR stage id under work\ir (default new_zealand_rally_01_route_5)
.PARAMETER Short    container name part (default nz01r5)
.PARAMETER Version  v1 = ground only, empty pacenotes, RespawnPlanes root moved (as first shipped);
                    v1.1 = v1 + render-only visuals (DR2 ground, edge posts, start/finish gates; config/visuals.toml)
                           and the DR2 ground lowered 47 m to the host terrain's mean (-> work\acr\dist-v1.1)
                    v1.2 = v1.1, but the start trigger's MeshComponent points at the end trigger's ring, so the native
                           trigger code no longer hides/rebuilds the visuals mesh (-> work\acr\dist-v1.2)
                    v1.3 = v1.2 + the v2 features (object collision, pacenotes, respawn walls re-laid) and the solid
                           objects drawn in the visuals mesh (-> work\acr\dist-v1.3)
                    v2 = + object collision, pacenotes, respawn walls re-laid (default)
.PARAMETER GroundOffset  metres added to the DR2 placement height (default -47 for v1.1+, 0 otherwise)
.PARAMETER NoVisuals  v1.1+ without the visuals
.PARAMETER VisualsHost / VisualsOwner / NoRetarget  v1.2 placement of the visuals mesh (docs/decisions.md D6):
                    host trigger start|end, re-parent to an actor (e.g. SplinesActor), keep the trigger's own reference
.PARAMETER VisualsConfig  alternative to config\visuals.toml (e.g. material experiments, docs/visuals-plan.md)
.PARAMETER Out      output folder for the container (default work\acr\dist for v1, work\acr\dist-<Version> otherwise)
.PARAMETER Tag      name for the intermediate mod folder, report and check folder (default <Short>-<Version>)
.PARAMETER Respawn  override the respawn mode: root (v1), sink (all walls 1 km down), relay (v2)
.PARAMETER NoObjects / NoPacenotes  v2 without object collision / with the emptied pacenote table
.PARAMETER ExtractOnly  only extract the host packages to work\acr\legacy
.PARAMETER Verify   re-read the built container with CUE4Parse and re-run the offline checks on a round trip
#>
param(
    [string]$Stage = 'new_zealand_rally_01_route_5',
    [string]$Short = 'nz01r5',
    [ValidateSet('v1', 'v1.1', 'v1.2', 'v1.3', 'v2')][string]$Version = 'v2',
    [double]$GroundOffset = [double]::NaN,
    [switch]$NoVisuals,
    [ValidateSet('start', 'end')][string]$VisualsHost = 'start',
    [string]$VisualsOwner,
    [string]$VisualsConfig,
    [switch]$NoRetarget,
    [string]$Out,
    [string]$Tag,
    [ValidateSet('', 'root', 'sink', 'relay')][string]$Respawn = '',
    [switch]$NoObjects,
    [switch]$NoPacenotes,
    [string]$Game = $(if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' }),
    [string]$Retoc,
    [switch]$KeepPacenotes,
    [switch]$ExtractOnly,
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = Join-Path $root 'work\acr'
if (-not $Retoc) { $Retoc = Join-Path $root 'work\bin\bin\retoc.exe' }
if (-not (Test-Path $Retoc)) { throw "retoc not found at $Retoc" }
. (Join-Path $root 'mods\common\vanilla.ps1')
# every game input (extraction, surface registry, verification) comes from this view: vanilla containers only, no ~mods
$vanilla = New-VanillaView $Game (Join-Path $work 'vanilla')
$paks = Join-Path $vanilla 'acr\Content\Paks'

function Run([string]$exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $args failed ($LASTEXITCODE)" }
}

$host_ = @{
    baked    = 'acr\Content\Environments\WalesS4HafrenSouth\DataAssets\DA_WalesS4HafrenSouthBakedData'
    cell     = 'acr\Content\Levels\WalesS4HafrenSouth\_Generated_\22VG0QIP9ATUQI633K86KWPFP'
    variants = 'acr\Content\Data\Database\Main\TrackSelection\DT_TracksVariants'
    pacenote = 'acr\Content\Data\Pacenote\Tables\DT_PacenoteHafrenSouthForward'
}
$legacy = Join-Path $work 'legacy-vanilla'
if (-not (Test-VanillaStamp $legacy)) { Remove-Item -Recurse -Force $legacy -ErrorAction SilentlyContinue }
foreach ($rel in $host_.Values) {
    if (-not (Test-Path (Join-Path $legacy "$rel.uexp"))) {
        Assert-VanillaPaks $paks
        Run $Retoc to-legacy --version UE5_6 --no-shaders --filter (Split-Path $rel -Leaf) $paks $legacy
    }
}
Write-VanillaStamp $legacy $paks

$acrRef = Join-Path $root 'work\bin\acr-ref\acr-ref.exe'
Run dotnet build (Join-Path $root 'tools\acr-ref\AcrRef.csproj') -c Release -o (Split-Path $acrRef) --nologo -v q
# surfaces known to the Kunos track physics; colliders with any other physical material crash the stage load
$registry = Join-Path $legacy 'DA_PhysMaterialsMappingCommon.json'
if (-not (Test-Path $registry)) {
    Run $acrRef export acr/Content/Data/Gameplay/PhysicalMaterials/DA_PhysMaterialsMappingCommon.uasset --game $vanilla --out $registry
}
Run $acrRef surface-map-check --map (Join-Path $root 'config\surface-map.toml') --registry $registry
if ($ExtractOnly) { return }

$ir = Join-Path $root "work\ir\$Stage"
if (-not (Test-Path (Join-Path $ir 'stage.json'))) { throw "no stage IR at $ir (run dr2-extract stage-ir $Stage)" }
$tag = if ($Tag) { $Tag } elseif ($Version -eq 'v1') { $Short } else { "$Short-$Version" }
$mod = Join-Path $work "mod-$tag"
$report = Join-Path $work "stage-$tag.report.json"
$dist = if ($Out) { $Out } elseif ($Version -eq 'v1') { Join-Path $work 'dist' } else { Join-Path $work "dist-$Version" }
if ($Version -ne 'v1' -and (Resolve-Path -LiteralPath $dist -ErrorAction SilentlyContinue).Path -eq (Join-Path $work 'dist')) {
    throw "refusing to write $Version into work\acr\dist (that holds v1)"
}
Remove-Item -Recurse -Force $mod -ErrorAction SilentlyContinue
$sbArgs = @('stage-build', '--ir', $ir, '--legacy', $legacy, '--out', $mod, '--report', $report, '--surface-registry', $registry)
if ($KeepPacenotes) { $sbArgs += @('--keep-pacenotes', '1') }
if ($Version -in 'v2', 'v1.3') {
    if (-not $NoObjects) { $sbArgs += @('--objects', (Join-Path $root 'config\object-collision.toml')) }
    if (-not $NoPacenotes -and -not $KeepPacenotes) { $sbArgs += @('--pacenote-map', (Join-Path $root 'config\pacenote-map.toml')) }
    if (-not $Respawn) { $Respawn = 'relay' }
}
if ($Version -in 'v1.1', 'v1.2', 'v1.3' -and -not $NoVisuals) {
    $sbArgs += @('--visuals', $(if ($VisualsConfig) { (Resolve-Path $VisualsConfig).Path } else { Join-Path $root 'config\visuals.toml' }), '--visuals-host', $VisualsHost,
                 '--visuals-retarget', $(if ($Version -eq 'v1.1' -or $NoRetarget) { '0' } else { '1' }))
    if ($VisualsOwner) { $sbArgs += @('--visuals-owner', $VisualsOwner) }
}
if ([double]::IsNaN($GroundOffset)) { $GroundOffset = if ($Version -in 'v1.1', 'v1.2', 'v1.3') { -47 } else { 0 } }
if ($GroundOffset -ne 0) { $sbArgs += @('--ground-offset', $GroundOffset.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($Respawn) { $sbArgs += @('--respawn', $Respawn) }
Run $acrRef @sbArgs

New-Item -ItemType Directory -Force $dist | Out-Null
Remove-Item (Join-Path $dist "zz_acrdirt_$($Short)_P.*") -ErrorAction SilentlyContinue
Run $Retoc to-zen --version UE5_6 $mod (Join-Path $dist "zz_acrdirt_$($Short)_P.utoc")

if ($Verify) {
    $check = Join-Path $work "check-$tag"
    $checkPaks = Join-Path $check 'acr\Content\Paks'
    Remove-Item -Recurse -Force $check -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $checkPaks | Out-Null
    Copy-Item (Join-Path $paks 'global.*') $checkPaks
    Copy-Item (Join-Path $dist "zz_acrdirt_$($Short)_P.*") $checkPaks
    # 1) what the game's reader sees: CUE4Parse over the vanilla containers (hard links, read-only use) + the built one,
    #    so imports into other containers (physical materials) resolve
    $full = Join-Path $check 'full\acr\Content\Paks'
    New-Item -ItemType Directory -Force $full | Out-Null
    Get-ChildItem $paks -File | % {
        New-Item -ItemType HardLink -Path (Join-Path $full $_.Name) -Target $_.FullName | Out-Null
    }
    Copy-Item (Join-Path $dist "zz_acrdirt_$($Short)_P.*") $full
    Run $acrRef stage-verify --game (Join-Path $check 'full') --ir $ir --out (Join-Path $check 'verify-cue4parse.txt')
    Get-Content (Join-Path $check 'verify-cue4parse.txt')
    # 2) byte-level: convert the shipped container back to legacy and re-run the offline checks (collision, raycast, splines)
    $rt = Join-Path $check 'roundtrip'
    Run $Retoc to-legacy --version UE5_6 --no-shaders $checkPaks $rt
    Run $acrRef stage-check --legacy $rt --ir $ir --report $report --out (Join-Path $check 'verify-roundtrip.txt')
    Get-Content (Join-Path $check 'verify-roundtrip.txt')
}

Get-ChildItem $dist "zz_acrdirt_$($Short)_P.*" | Format-Table Name, Length
