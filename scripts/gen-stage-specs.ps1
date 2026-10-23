<#
.SYNOPSIS
  Writes work/acr/specs/*.json (generated, gitignored) from config/stages.toml and work/ir/*/stage.json.
  Only the first stage under a new location gets Location.Template; later ones reuse the row.
  Prefer listing stages so the Template owner sorts first by output filename (Registry also creates
  locations in a first pass, so batch order is not required for correctness).
#>
param(
    [string]$Config = (Join-Path (Split-Path $PSScriptRoot) 'config\stages.toml'),
    [string]$IrRoot = (Join-Path (Split-Path $PSScriptRoot) 'work\ir'),
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot) 'work\acr\specs'),
    [switch]$EnabledOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

. (Join-Path $PSScriptRoot 'stages-lib.ps1')

$data = Read-StagesToml $Config
New-Item -ItemType Directory -Force $OutDir | Out-Null
# Remove previously generated specs (keep hand-written prototypes elsewhere)
Get-ChildItem $OutDir -Filter 'acrxs*.json' | Remove-Item -Force
Get-ChildItem $OutDir -Filter 'acrdirt*.json' | Remove-Item -Force   # legacy names (before the AcrDirt -> AcrXs rename)

# DR2 loading art for Menu.Art (scripts/prepare-menu-art.ps1): the stage's own route when DR2 has art for it (routes 0, 2, 3 per
# rally), else route 0 of the same rally, else any art of the location. `menu_art = "<tpk name>"` overrides, `menu_art = "none"` disables.
$artDir = Join-Path $root 'work\dr2\menu-art\frontend\streamed_textures\loading'
if (@($data.Stages | ? { $_.enabled -and $_.menu_images }) -and -not (Get-ChildItem $artDir -Filter '*.tpk' -ErrorAction SilentlyContinue | Select-Object -First 1)) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-menu-art.ps1') | Out-Host
}
$artNames = @(Get-ChildItem $artDir -Filter '*.tpk' -ErrorAction SilentlyContinue | % BaseName)
function Get-MenuArt([string]$dr2, $override) {
    if ($override -eq 'none') { return $null }
    if ($override) { if ($override -notin $artNames) { throw "${dr2}: menu_art $override not in $artDir" }; return $override }
    if ($dr2 -notmatch '^(?<rally>(?<loc>.+)_rally_\d+)_route_\d+$') { return $null }
    $rally = $Matches.rally; $locName = $Matches.loc
    foreach ($c in @($dr2, "${rally}_route_0")) { if ($c -in $artNames) { return $c } }
    return ($artNames | ? { $_ -like "${locName}_rally_*" } | Sort-Object | Select-Object -First 1)
}

$locationCreated = @{}
$n = 0
foreach ($st in $data.Stages) {
    if (-not $st.enabled) { continue }   # disabled stages never get a spec
    $dr2 = $st.dr2
    $locKey = $st.location
    $loc = $data.Locations[$locKey]
    if (-not $loc) { throw "unknown location $locKey for $dr2" }
    $code = $loc.code
    $acrId = Get-AcrId $dr2 $code
    $irPath = Join-Path $IrRoot "$dr2\stage.json"
    if (-not (Test-Path $irPath)) { Write-Warning "missing IR $irPath - no spec for $acrId (needs prepare)"; continue }
    $ir = Get-Content $irPath -Raw | ConvertFrom-Json
    $km = [math]::Round([double]$ir.length_m / 1000, 3)

    $locBlock = [ordered]@{ Row = $(if ($loc.row) { $loc.row } else { ($locKey -replace '_', '') }) }
    # PascalCase location row for new locations: new_zealand -> NewZealand
    if (-not $loc.row) {
        $locBlock.Row = ($locKey -split '_' | ForEach-Object { $_.Substring(0, 1).ToUpper() + $_.Substring(1) }) -join ''
    }
    # Every stage of a new location carries the Template block: Registry.EnsureLocation creates the row once and later specs reuse it,
    # so any subset of stages (spot-check builds, -PassedOnly registries) still gets its location.
    if (-not $loc.row) {
        $locBlock.Template = $loc.template
        $locBlock.Name = $loc.name
        if ($loc.description) { $locBlock.Description = $loc.description }
        # main-menu tile / logo: reuse a matching vanilla texture row (DT_TracksLocationsBackgrounds / Logos); default = template's
        if ($loc.tile) { $locBlock.Tile = $loc.tile }
        else {
            # generated tile from DR2 loading art: tile_art, else the location's first rally, route 0
            $tileArt = if ($loc.tile_art) { $loc.tile_art } else { $artNames | ? { $_ -like "${locKey}_rally_*_route_0" } | Sort-Object | Select-Object -First 1 }
            if ($tileArt) { $locBlock.TileArt = $tileArt } else { Write-Warning "${locKey}: no DR2 loading art for the location tile; keeping the template's" }
        }
        if ($loc.logo) { $locBlock.Logo = $loc.logo }
        elseif ($loc.logo_colors) { $locBlock.GenLogo = $true; $locBlock.LogoColors = $loc.logo_colors }
        if ($loc.country) { $locBlock.Country = $loc.country }
    }
    if ($loc.surface) { $locBlock.Surface = $loc.surface }

    $sky = if ($st.sky) { $st.sky } elseif ($loc.sky) { $loc.sky } else { 'nz' }
    $spec = [ordered]@{
        Id          = $acrId
        Stage       = $dr2
        SkySection  = $sky
        Level       = "/Game/AcrXs/Stages/$acrId/$acrId"
        Location    = $locBlock
        # Stage display names (Track.Name, Variants[].Name / StageName) are NOT written here: acr-additive resolves them at registry /
        # verify time from the user's own DR2 localisation (work/ir/<dr2>/stage.json "name", written by dr2-extract stage-ir from
        # language_eng.lng). `track_name` in stages.toml is an explicit per-stage override of the track card name.
        Track       = [ordered]@{ Template = 'WalesS4HafrenSouth' }
        Variants    = @(
            [ordered]@{
                Row       = "${acrId}FullForward"
                Template  = 'WelesS4HafrenSouthFullForward'
                Length    = $km
            }
        )
        Clone = [ordered]@{
            Packages = [ordered]@{
                '/Game/Levels/WalesS4HafrenSouth' = "/Game/AcrXs/Stages/$acrId/$acrId"
                '/Game/Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData' = "/Game/AcrXs/Stages/$acrId/DA_${acrId}BakedData"
                '/Game/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward' = "/Game/AcrXs/Stages/$acrId/DT_Pacenote${acrId}Forward"
            }
            Objects = [ordered]@{
                'WalesS4HafrenSouth' = $acrId
                'DA_WalesS4HafrenSouthBakedData' = "DA_${acrId}BakedData"
                'DT_PacenoteHafrenSouthForward' = "DT_Pacenote${acrId}Forward"
            }
        }
    }
    # Optional per-stage placement tweak (metres added to the computed height); omitted when unset
    if ($null -ne $st.ground_offset) { $spec.GroundOffset = [double]$st.ground_offset }
    # Optional generated menu images (route map, track map, elevation profile; docs/menu-images.md); omitted when unset
    if ($st.menu_images) {
        $spec.Menu = [ordered]@{ Images = $true }
        $art = Get-MenuArt $dr2 $st.menu_art
        if ($art) { $spec.Menu.Art = $art } else { Write-Warning "$acrId`: no DR2 loading art; backdrop / banner / lobby images stay the template's" }
        if ($null -ne $st.menu_elevation_offset) { $spec.Menu.ElevationOffset = [double]$st.menu_elevation_offset }
    }
    if ($st.track_name) { $spec.Track.Name = $st.track_name }
    $out = Join-Path $OutDir "$($acrId.ToLowerInvariant()).json"
    $json = $spec | ConvertTo-Json -Depth 8
    # every nested table is [ordered]: a plain @{} enumerates in a per-process random order, which changed the spec bytes (and build-stages' input hash) on every run
    Set-Content -Path $out -Value $json -Encoding utf8
    Write-Host "wrote $out ($dr2, $km km)"
    $n++
}
Write-Host "generated $n stage specs in $OutDir"
