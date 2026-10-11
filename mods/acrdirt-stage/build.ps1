<#
.SYNOPSIS
  Ports one DR2 stage (from its stage IR) into ACR, editor-less, using Hafren South's forward variant
  ("Afon Bidno - Severn", variant 1/2) as the host. Produces one IoStore container
  work\acr\dist\zz_acrdirt_<short>_P.{utoc,ucas,pak}.

  Overridden host packages (all under /Game):
    Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData   physics (DR2 collision added, host flattened)
    Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP             forward gameplay cell (splines, triggers, start)
    Data/Database/Main/TrackSelection/DT_TracksVariants                          forward row Length
    Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward                           emptied (Hafren calls would be wrong)

  Nothing is copied into the game install; see docs/first-stage-test.md.

.PARAMETER Stage  IR stage id under work\ir (default new_zealand_rally_01_route_5)
.PARAMETER Short  container name part (default nz01r5)
.PARAMETER ExtractOnly  only extract the host packages to work\acr\legacy
.PARAMETER Verify re-read the built container with CUE4Parse
#>
param(
    [string]$Stage = 'new_zealand_rally_01_route_5',
    [string]$Short = 'nz01r5',
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
$paks = Join-Path $Game 'acr\Content\Paks'
if (-not (Test-Path (Join-Path $paks 'global.utoc'))) { throw "no ACR Paks at $paks" }

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
$legacy = Join-Path $work 'legacy'
foreach ($rel in $host_.Values) {
    if (-not (Test-Path (Join-Path $legacy "$rel.uexp"))) {
        Run $Retoc to-legacy --version UE5_6 --no-shaders --filter (Split-Path $rel -Leaf) $paks $legacy
    }
}
if ($ExtractOnly) { return }

$acrRef = Join-Path $root 'work\bin\acr-ref\acr-ref.exe'
Run dotnet build (Join-Path $root 'tools\acr-ref\AcrRef.csproj') -c Release -o (Split-Path $acrRef) --nologo -v q

$ir = Join-Path $root "work\ir\$Stage"
if (-not (Test-Path (Join-Path $ir 'stage.json'))) { throw "no stage IR at $ir (run dr2-extract stage-ir $Stage)" }
$mod = Join-Path $work "mod-$Short"
Remove-Item -Recurse -Force $mod -ErrorAction SilentlyContinue
$sbArgs = @('stage-build', '--ir', $ir, '--legacy', $legacy, '--out', $mod, '--report', (Join-Path $work "stage-$Short.report.json"))
if ($KeepPacenotes) { $sbArgs += @('--keep-pacenotes', '1') }
Run $acrRef @sbArgs

$dist = Join-Path $work 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Remove-Item (Join-Path $dist "zz_acrdirt_$($Short)_P.*") -ErrorAction SilentlyContinue
Run $Retoc to-zen --version UE5_6 $mod (Join-Path $dist "zz_acrdirt_$($Short)_P.utoc")

if ($Verify) {
    $check = Join-Path $work "check-$Short"
    $checkPaks = Join-Path $check 'acr\Content\Paks'
    Remove-Item -Recurse -Force $check -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $checkPaks | Out-Null
    Copy-Item (Join-Path $paks 'global.*') $checkPaks
    Copy-Item (Join-Path $dist "zz_acrdirt_$($Short)_P.*") $checkPaks
    Run $acrRef stage-verify --game $check --ir $ir --report (Join-Path $work "stage-$Short.report.json") --out (Join-Path $check 'verify.txt')
    Get-Content (Join-Path $check 'verify.txt')
}

Get-ChildItem $dist "zz_acrdirt_$($Short)_P.*" | Format-Table Name, Length
