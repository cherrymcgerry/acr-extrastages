<#
.SYNOPSIS
  Builds the additive stage containers: one registry container with the merged menu tables plus one container per
  stage, each under its own package paths, so ported stages appear next to the vanilla ones (docs/additive-plan.md).

.DESCRIPTION
  Inputs : mods\acrdirt-additive\stages\*.json   (one spec per stage: menu rows + the packages that make up its level)
           the vanilla game containers (hard-link view, never ~mods)
  Outputs: work\acr\additive\dist\zz_acrdirt_registry_P.*          DT_Levels / DT_Tracks / DT_TracksVariants / DT_TracksLocations
           work\acr\additive\dist\zz_acrdirt_s_<id>_P.*            the stage's level, cells and BakedData (new paths only)
           work\acr\additive\check\verify.txt                      with -Verify

  The registry is regenerated from the vanilla tables on every build: install it together with every stage container
  built in the same run, and rebuild after an ACR patch.

.PARAMETER Stages  stage spec folder or files (default mods\acrdirt-additive\stages)
.PARAMETER Only    build only these stage ids (the registry still lists every spec)
.PARAMETER Verify  re-read the built containers with CUE4Parse over the vanilla containers and run the offline checks
#>
param(
    [string[]]$Stages,
    [string[]]$Only,
    [string]$Game = $(if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' }),
    [string]$Retoc,
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = Join-Path $root 'work\acr\additive'
if (-not $Stages) { $Stages = @(Join-Path $PSScriptRoot 'stages') }
if (-not $Retoc) { $Retoc = Join-Path $root 'work\bin\bin\retoc.exe' }
if (-not (Test-Path $Retoc)) { throw "retoc not found at $Retoc" }
. (Join-Path $root 'mods\common\vanilla.ps1')

function Run([string]$exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $args failed ($LASTEXITCODE)" }
}

$specFiles = @($Stages | % { if (Test-Path $_ -PathType Container) { Get-ChildItem $_ -Filter *.json | % FullName } else { (Resolve-Path $_).Path } })
$specs = @($specFiles | % { Get-Content $_ -Raw | ConvertFrom-Json })
if (-not $specs) { throw "no stage specs in $Stages" }

$vanilla = New-VanillaView $Game (Join-Path $work 'vanilla')
$paks = Join-Path $vanilla 'acr\Content\Paks'

# legacy copies of the DB tables and of every package a stage clones (retoc --filter is a path substring)
$legacy = Join-Path $work 'legacy'
$current = @(Get-ChildItem $paks -File -Filter '*.utoc' | % { "$($_.BaseName) $($_.Length)" })
$stamped = if (Test-VanillaStamp $legacy) { @((Get-Content (Join-Path $legacy '.source.json') -Raw | ConvertFrom-Json).containers) } else { @() }
if (-not $stamped -or (Compare-Object $stamped $current)) {
    # no stamp, or the game was patched since the extraction: start from the current vanilla tables and levels
    Remove-Item -Recurse -Force $legacy -ErrorAction SilentlyContinue
}
$filters = @('Data/Database/Main/')
foreach ($s in $specs) { foreach ($p in $s.Clone.Packages.PSObject.Properties.Name) { $filters += $p.Substring('/Game/'.Length) } }
$need = $filters | ? { -not (Get-ChildItem (Join-Path $legacy ('acr\Content\' + ($_ -replace '/', '\'))) -ErrorAction SilentlyContinue -Include *.uasset, *.umap -Recurse) -and
                       -not (Test-Path (Join-Path $legacy ('acr\Content\' + ($_ -replace '/', '\') + '.umap'))) -and
                       -not (Test-Path (Join-Path $legacy ('acr\Content\' + ($_ -replace '/', '\') + '.uasset'))) }
if ($need) {
    Assert-VanillaPaks $paks
    Run $Retoc to-legacy --version UE5_6 --no-shaders @($need | % { '--filter'; $_ }) $paks $legacy
}
Write-VanillaStamp $legacy $paks

$tool = Join-Path $root 'work\bin\acr-additive\acr-additive.exe'
Run dotnet build (Join-Path $root 'tools\acr-additive\AcrAdditive.csproj') -c Release -o (Split-Path $tool) --nologo -v q

$dist = Join-Path $work 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# registry: vanilla tables + every spec's rows
$reg = Join-Path $work 'mod-registry'
Remove-Item -Recurse -Force $reg -ErrorAction SilentlyContinue
Run $tool registry --legacy $legacy --out $reg @specFiles
Remove-Item (Join-Path $dist 'zz_acrdirt_registry_P.*') -ErrorAction SilentlyContinue
Run $Retoc to-zen --version UE5_6 $reg (Join-Path $dist 'zz_acrdirt_registry_P.utoc')

# stages: relocated copies of their level packages
foreach ($s in $specs) {
    if ($Only -and $s.Id -notin $Only) { continue }
    $id = $s.Id.ToLowerInvariant()
    $mod = Join-Path $work "mod-stage-$id"
    Remove-Item -Recurse -Force $mod -ErrorAction SilentlyContinue
    $relArgs = @('relocate', '--legacy', $legacy, '--out', $mod)
    foreach ($p in $s.Clone.Packages.PSObject.Properties) { $relArgs += @('--map', "$($p.Name)=$($p.Value)") }
    foreach ($p in $s.Clone.Objects.PSObject.Properties) { $relArgs += @('--object', "$($p.Name)=$($p.Value)") }
    Run $tool @relArgs
    Remove-Item (Join-Path $dist "zz_acrdirt_s_${id}_P.*") -ErrorAction SilentlyContinue
    Run $Retoc to-zen --version UE5_6 $mod (Join-Path $dist "zz_acrdirt_s_${id}_P.utoc")
}

if ($Verify) {
    # CUE4Parse over the vanilla containers (hard links, read-only use) + the built ones
    $check = Join-Path $work 'check'
    $full = Join-Path $check 'acr\Content\Paks'
    Remove-Item -Recurse -Force $check -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $full | Out-Null
    Get-ChildItem $paks -File | % { New-Item -ItemType HardLink -Path (Join-Path $full $_.Name) -Target $_.FullName | Out-Null }
    Get-ChildItem $dist -File 'zz_acrdirt_*' | % { New-Item -ItemType HardLink -Path (Join-Path $full $_.Name) -Target $_.FullName | Out-Null }
    $specDir = Join-Path $check 'specs'
    New-Item -ItemType Directory -Force $specDir | Out-Null
    $specFiles | % { Copy-Item $_ $specDir }
    Run $tool verify --vanilla $vanilla --game $check --spec $specDir --out (Join-Path $check 'verify.txt')
    # byte-level: the registry's export data converted back to legacy must equal what registry wrote (headers differ:
    # with only global + the registry mounted, retoc cannot name imports from other containers, e.g. ST_Track)
    $rtPaks = Join-Path $check 'roundtrip-paks'
    New-Item -ItemType Directory -Force $rtPaks | Out-Null
    Copy-Item (Join-Path $paks 'global.*'), (Join-Path $dist 'zz_acrdirt_registry_P.*') $rtPaks
    $rt = Join-Path $check 'roundtrip'
    Run $Retoc to-legacy --version UE5_6 --no-shaders $rtPaks $rt
    $uexp = @(Get-ChildItem $reg -Recurse -File -Filter *.uexp)
    $diff = @($uexp | ? {
        $o = Join-Path $rt ($_.FullName.Substring($reg.Length + 1))
        -not (Test-Path $o) -or (Get-FileHash $o).Hash -ne (Get-FileHash $_.FullName).Hash })
    if ($diff) { throw "registry round trip differs: $($diff.Name -join ', ')" }
    Write-Host "registry round trip: $($uexp.Count) tables' export data byte-identical"
}

Get-ChildItem $dist 'zz_acrdirt_*' | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
