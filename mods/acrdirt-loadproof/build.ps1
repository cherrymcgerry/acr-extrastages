<#
.SYNOPSIS
  Rebuilds the two ACR load-proof IoStore containers from the user's own game install.

  zz_acrdirt_test_P  DT_TracksVariants with Hafren South "Afon Bidno" FullForward Length 4.8 -> 42.0 km
  zz_acrdirt_ramp_P  DA_WalesS4HafrenSouthBakedData with one extra always-active mesh collider
                     (an invisible 6 m x 8 m, 1 m high gravel ramp ~47 m after the FullForward start line)

  Everything is written under work/ (gitignored). Nothing is copied into the game install;
  see docs/load-proof.md for the manual install/test/rollback steps.

.PARAMETER Game   ACR install dir (default: $env:ACR_GAME_DIR or the Steam default)
.PARAMETER Retoc  retoc.exe (default: work/bin/bin/retoc.exe; install with
                  cargo install --locked --git https://github.com/trumank/retoc retoc_cli --root work\bin)
.PARAMETER Verify Re-read the built containers with CUE4Parse (the ramp check exports ~1 GB of JSON).
#>
param(
    [string]$Game = $(if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' }),
    [string]$Retoc,
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = Join-Path $root 'work\acr'
if (-not $Retoc) { $Retoc = Join-Path $root 'work\bin\bin\retoc.exe' }
if (-not (Test-Path $Retoc)) { throw "retoc not found at $Retoc" }
. (Join-Path $root 'mods\common\vanilla.ps1')
# game inputs come from a vanilla-only view (no ~mods: an installed load-proof would otherwise feed back into the build)
$vanilla = New-VanillaView $Game (Join-Path $work 'vanilla')
$paks = Join-Path $vanilla 'acr\Content\Paks'

function Run([string]$exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $args failed ($LASTEXITCODE)" }
}

$acrRef = Join-Path $root 'work\bin\acr-ref\acr-ref.exe'
Run dotnet build (Join-Path $root 'tools\acr-ref\AcrRef.csproj') -c Release -o (Split-Path $acrRef) --nologo -v q

$legacy = Join-Path $work 'legacy-vanilla'
if (-not (Test-VanillaStamp $legacy)) { Remove-Item -Recurse -Force $legacy -ErrorAction SilentlyContinue }
$dtRel = 'acr\Content\Data\Database\Main\TrackSelection\DT_TracksVariants'
$bdRel = 'acr\Content\Environments\WalesS4HafrenSouth\DataAssets\DA_WalesS4HafrenSouthBakedData'
foreach ($p in @(@($dtRel, 'DT_TracksVariants'), @($bdRel, 'DA_WalesS4HafrenSouthBakedData'))) {
    if (-not (Test-Path (Join-Path $legacy "$($p[0]).uexp"))) {
        Assert-VanillaPaks $paks
        Run $Retoc to-legacy --version UE5_6 --no-shaders --filter $p[1] $paks $legacy
    }
}
Write-VanillaStamp $legacy $paks

# (a) DataTable override: patch the FullForward Length float. The reverse row has identical Length and
# Elevation, so the match is anchored on ElevationAtStart (int32 1, f32 478.28683) as well.
$modDt = Join-Path $work 'mod-dt'
Remove-Item -Recurse -Force $modDt -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Split-Path (Join-Path $modDt $dtRel)) | Out-Null
Copy-Item (Join-Path $legacy "$dtRel.uasset") (Join-Path $modDt "$dtRel.uasset")
Run $acrRef patch-f32 (Join-Path $legacy "$dtRel.uexp") --expect 4.8 --value 42 --context '345,410' `
    --context-hex '01000000B724EF43' --out (Join-Path $modDt "$dtRel.uexp")

# (b) BakedData override: append a ramp collider (physics space: metres, x = UE X/100, z = UE Y/100).
# --at is CenterSpline point 274 of the FullForward variant; --dir points along the driving direction.
# baked-add-ramp writes both .uasset and .uexp (header sizes are patched) - do not copy the original .uasset.
$modRamp = Join-Path $work 'mod-ramp'
Remove-Item -Recurse -Force $modRamp -ErrorAction SilentlyContinue
Run $acrRef baked-add-ramp --uexp (Join-Path $legacy "$bdRel.uexp") --ref-collider 10 `
    --at '-1014.34,939.68' --dir '-19.77,2.98' --width 6 --length 8 --rise 1 --out (Split-Path (Join-Path $modRamp $bdRel))

$dist = Join-Path $work 'dist-loadproof'
New-Item -ItemType Directory -Force $dist | Out-Null
Remove-Item (Join-Path $dist 'zz_acrdirt_*') -ErrorAction SilentlyContinue
Run $Retoc to-zen --version UE5_6 $modDt (Join-Path $dist 'zz_acrdirt_test_P.utoc')
Run $Retoc to-zen --version UE5_6 $modRamp (Join-Path $dist 'zz_acrdirt_ramp_P.utoc')

if ($Verify) {
    $check = Join-Path $work 'check'
    $checkPaks = Join-Path $check 'acr\Content\Paks'
    Remove-Item -Recurse -Force $check -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $checkPaks | Out-Null
    Copy-Item (Join-Path $paks 'global.*') $checkPaks
    Copy-Item (Join-Path $dist 'zz_acrdirt_*') $checkPaks
    Run $acrRef export ($dtRel.Replace('\', '/') + '.uasset') --game $check --out (Join-Path $check 'dt.json')
    $row = (Get-Content -Raw (Join-Path $check 'dt.json') | ConvertFrom-Json)[0].Rows.WelesS4HafrenSouthFullForward
    "DT check: FullForward Length = $($row.Length) (expected 42)"
    Run $acrRef export ($bdRel.Replace('\', '/') + '.uasset') --game $check --out (Join-Path $check 'baked.json')
    Run $acrRef baked-summary (Join-Path $check 'baked.json') --out (Join-Path $check 'summary')
    "BakedData check: see $check\summary (expect 735 mesh colliders, last one in the 'None' stride set)"
}

Get-ChildItem $dist 'zz_acrdirt_*' | Format-Table Name, Length
