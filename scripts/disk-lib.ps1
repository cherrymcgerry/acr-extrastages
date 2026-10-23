<#
  Disk budget helpers (dot-source: . "$PSScriptRoot\disk-lib.ps1").

  Shared by scripts/estimate-disk.ps1, scripts/build-stages.ps1 and scripts/clean-work.ps1. The numbers in
  $script:DiskBudget are MEASURED (docs/resume-notes.md, "Disk budget"); the launcher is meant to reimplement
  Get-DiskEstimate with the same constants and the same JSON shape (scripts/estimate-disk.ps1 -Json, schema 1).
#>

# All sizes in MB (1 MB = 1048576 bytes).
$script:DiskBudget = [ordered]@{
    # output
    container_mb_default   = 700     # mean of the 30 built containers is 686 MB (range 534-976); used when no batch report exists
    registry_mb            = 20      # zz_acrxs_registry_P (17 MB measured)
    # working data that stays for the whole run
    dr2_layer_cache_mb     = 5100    # work\dr2\cache\game_2_1 + game_2 + game_1 + game (shared DR2 layers; measured 5.05 GB after all 30 stages)
    legacy_mb              = 600     # work\acr\additive\legacy (587 MB: only the packages the specs clone, not the 22 GB vanilla dump)
    misc_mb                = 1300    # work\bin 122 + menu-art 163 + menu-preview 784 + logs/specs/reports ~200
    light_ir_mb_per_stage  = 30      # stage.json + collision.glb + objects_collision.glb (avg 28.7, max 45.6 MB); kept after the build
    # transient, one stage at a time
    heavy_ir_mb_max        = 2500    # work\ir\<id>\render + textures: avg 1676, max 2432 MB over the 30 enabled stages
    heavy_ir_mb_avg        = 1700
    dr2_location_cache_mb  = 4300    # work\dr2\cache\<country>__<location>: ~0.92 x the DR2 location archive; largest of the 24 is France 02 (4211 MB archive)
    build_temp_mb          = 2500    # mod-build + overlay + mod-stage of one stage; measured peak 1927 MB on AcrXsWa02r4 (722 MB container), scaled up for the big ones
    safety_margin_mb       = 2048    # headroom for the OS, retoc scratch in %TEMP% and rounding
    # what the OLD flow (prepare everything, then build everything, no pruning) additionally keeps
    all_dr2_location_caches_mb = 58000   # the 24 locations of the 30 stages: 63061 MB of archives x 0.92 extracted
}

function Get-DiskBudgetConstants { $script:DiskBudget }

function Get-DirSizeMB([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    $s = (Get-ChildItem $Path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($null -eq $s) { 0 } else { [math]::Round($s / 1MB, 1) }
}

# "<length>:<last write ticks>" or 'none': the file signature build-stages.ps1 hashes into a stage's input hash
function Get-FileSig([string]$Path) {
    if (-not (Test-Path $Path)) { return 'none' }
    $i = Get-Item $Path; "$($i.Length):$($i.LastWriteTimeUtc.Ticks)"
}

function Get-DriveFreeMB([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $d = [IO.DriveInfo]::new($root)
    [math]::Round($d.AvailableFreeSpace / 1MB)
}

# Real space freed by deleting these paths: a file only frees its bytes when ALL of its hard links are inside the paths
# (work\acr\additive\dist is hard-linked into the game's ~mods; work\acr\verify-fast holds link farms of old containers).
if (-not ('AcrDisk.LinkSize' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.IO; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
namespace AcrDisk {
  public static class LinkSize {
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct BHFI { public uint Attr; public long Created, Accessed, Written; public uint Vol, SizeHigh, SizeLow, Links, IdxHigh, IdxLow; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out BHFI i);
    public static long Freed(string[] roots) {
      var seen = new Dictionary<string, long[]>();   // id -> [size, links, names inside the roots]
      foreach (var root in roots) {
        if (!Directory.Exists(root)) continue;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); } catch { continue; }
        foreach (var p in files) {
          try {
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1)) {
              BHFI i; if (!GetFileInformationByHandle(fs.SafeFileHandle, out i)) continue;
              var key = i.Vol + ":" + i.IdxHigh + ":" + i.IdxLow;
              long[] e;
              if (!seen.TryGetValue(key, out e)) { e = new long[] { ((long)i.SizeHigh << 32) | i.SizeLow, i.Links, 0 }; seen[key] = e; }
              e[2]++;
            }
          } catch { }
        }
      }
      long sum = 0;
      foreach (var e in seen.Values) if (e[2] >= e[1]) sum += e[0];
      return sum;
    }
  }
}
'@
}
function Get-FreedMB([string[]]$Paths) { [math]::Round([AcrDisk.LinkSize]::Freed([string[]]@($Paths | ? { Test-Path $_ } | % { (Resolve-Path $_).Path })) / 1MB, 1) }

# ---------------------------------------------------------------------------------------------------------------------
# Per-stage IR pruning. work\ir\<dr2 id> = light part (stage.json, collision.glb, objects_collision.glb: read by the registry,
# stage-fit and menu-assets, ~30 MB) + heavy part (render\ and textures\: ~1.7 GB, only read by stage-build).
# Pruning removes the heavy part and writes ir-pruned.json with the signatures the stage's input hash used, so an unchanged
# stage stays "unchanged" (build-stages.ps1) and a changed one says exactly what to re-prepare.
# ---------------------------------------------------------------------------------------------------------------------
function Test-IrPruned([string]$IrDir) { Test-Path (Join-Path $IrDir 'ir-pruned.json') }

function Get-IrPruneMarker([string]$IrDir) {
    $m = Join-Path $IrDir 'ir-pruned.json'
    if (Test-Path $m) { Get-Content $m -Raw | ConvertFrom-Json } else { $null }
}

function Remove-StageIrHeavy([string]$IrDir, [switch]$WhatIf) {
    $render = Join-Path $IrDir 'render'; $tex = Join-Path $IrDir 'textures'
    $freed = (Get-DirSizeMB $render) + (Get-DirSizeMB $tex)
    if ($WhatIf) { return $freed }
    if (-not (Test-Path (Join-Path $IrDir 'stage.json'))) { throw "refusing to prune ${IrDir}: no stage.json (light IR) to keep" }
    # signatures first: the marker must outlive the files it describes
    $marker = Get-IrPruneMarker $IrDir
    $terrain = if (Test-Path (Join-Path $render 'terrain.glb')) { Get-FileSig (Join-Path $render 'terrain.glb') } elseif ($marker) { $marker.terrain_sig } else { 'none' }
    $bake = if (Test-Path (Join-Path $render 'ground_bake\manifest.json')) { Get-FileSig (Join-Path $render 'ground_bake\manifest.json') } elseif ($marker) { $marker.bake_sig } else { 'none' }
    @{ pruned = (Get-Date).ToString('s'); terrain_sig = $terrain; bake_sig = $bake; freed_mb = [math]::Round($freed + $(if ($marker) { [double]$marker.freed_mb } else { 0 }))
       note = 'render\ and textures\ were deleted after a successful build; re-run scripts\prepare-stage-data.ps1 -Only <dr2 id> (or build-stages.ps1 -PrepareOnDemand) to regenerate' } |
        ConvertTo-Json | Set-Content (Join-Path $IrDir 'ir-pruned.json') -Encoding UTF8
    Remove-Item -Recurse -Force $render, $tex -ErrorAction SilentlyContinue
    $freed
}

# Extracted copy of one DR2 location archive (work\dr2\cache\<country>__<location>): regenerable, 2-4 GB each.
function Remove-Dr2LocationCache([string]$Root, [string]$Dr2Id, [switch]$WhatIf) {
    $loc = $Dr2Id -replace '_route_\d+$', ''
    $freed = 0
    foreach ($d in @(Get-ChildItem (Join-Path $Root 'work\dr2\cache') -Directory -Filter "*__$loc" -ErrorAction SilentlyContinue)) {
        $freed += Get-DirSizeMB $d.FullName
        if (-not $WhatIf) { Remove-Item -Recurse -Force $d.FullName -ErrorAction SilentlyContinue }
    }
    $freed
}

# ---------------------------------------------------------------------------------------------------------------------
# The estimate. Pure function of its inputs (no disk access) so the launcher can reimplement it 1:1.
#   Flow 'streaming' (build-stages.ps1 -PrepareOnDemand, the default estimate): prepare one stage, build it, prune its heavy IR,
#          drop the location cache when the location changes. Peak = shared + light IR + ONE stage in flight.
#   Flow 'build-only' (build-stages.ps1 on already prepared data): output still to be written + legacy + one stage's build scratch.
#   Flow 'bulk' (prepare-stage-data.ps1 for everything, then build-stages.ps1 -KeepIr): peak = everything prepared at once.
#   InstallMode 'hardlink': the installer links dist into ~mods (same NTFS volume): output counted once.
#               'copy'    : dist and ~mods both hold the containers: output counted twice.
# ---------------------------------------------------------------------------------------------------------------------
function Get-DiskEstimate {
    param(
        [Parameter(Mandatory)][int]$Stages,
        [double]$ContainerMB = $script:DiskBudget.container_mb_default,
        [ValidateSet('streaming', 'bulk', 'build-only')][string]$Flow = 'streaming',
        [ValidateSet('hardlink', 'copy')][string]$InstallMode = 'hardlink',
        [double]$HeavyIrMB = $script:DiskBudget.heavy_ir_mb_max,
        [double]$ExistingOutputMB = 0,        # containers already in dist (a resumed run only needs the rest)
        [bool]$SameVolume = $true,            # work folder and the game's ~mods on the same volume
        [double]$FreeWorkMB = -1,             # free space on the work volume (-1 = unknown)
        [double]$FreeGameMB = -1
    )
    $b = $script:DiskBudget
    $containers = [math]::Round($Stages * $ContainerMB)
    $output = $containers + $b.registry_mb
    $shared = $b.dr2_layer_cache_mb + $b.legacy_mb + $b.misc_mb
    if ($Flow -eq 'build-only') {
        # IR already prepared (build-stages.ps1 without -PrepareOnDemand): only legacy extraction + one stage's build scratch are new
        $shared = $b.legacy_mb + 300
        $light = 0; $heavy = 0
        $transient = $b.build_temp_mb
    }
    elseif ($Flow -eq 'streaming') {
        $light = [math]::Round($Stages * $b.light_ir_mb_per_stage)
        # prepare phase of one stage: heavy IR being written + its location cache; build phase: heavy IR + build temps. The cache is
        # dropped before the next stage's build, so the two never overlap.
        $transient = $HeavyIrMB + [math]::Max($b.dr2_location_cache_mb, $b.build_temp_mb)
        $heavy = 0
    }
    else {
        $light = [math]::Round($Stages * $b.light_ir_mb_per_stage)
        $heavy = [math]::Round($Stages * $b.heavy_ir_mb_avg)
        $transient = $b.all_dr2_location_caches_mb * [math]::Min(1, $Stages / 30) + $b.build_temp_mb
    }
    $working = [math]::Round($shared + $light + $heavy + $transient)
    # The installer hard-links dist into ~mods only on the same volume; otherwise (or with -Copy) it copies, i.e. the output exists twice.
    $effective = if ($SameVolume -and $InstallMode -eq 'hardlink') { 'hardlink' } else { 'copy' }
    $distNeed = [math]::Max(0, $output - $ExistingOutputMB)          # containers still to be written into work\acr\additive\dist
    $installCopy = if ($effective -eq 'copy') { $output } else { 0 } # second copy inside ~mods
    $onDisk = $output + $installCopy
    if ($SameVolume) {
        $reqWork = [math]::Round($distNeed + $installCopy + $working + $b.safety_margin_mb)
        $reqGame = $reqWork
    }
    else {
        $reqWork = [math]::Round($distNeed + $working + $b.safety_margin_mb)
        $reqGame = [math]::Round($output + $b.safety_margin_mb)
    }
    $ok = $true; $shortfall = 0
    if ($FreeWorkMB -ge 0 -and $FreeWorkMB -lt $reqWork) { $ok = $false; $shortfall = $reqWork - $FreeWorkMB }
    if (-not $SameVolume -and $FreeGameMB -ge 0 -and $FreeGameMB -lt $reqGame) { $ok = $false; $shortfall = [math]::Max($shortfall, $reqGame - $FreeGameMB) }
    [ordered]@{
        schema   = 1
        inputs   = [ordered]@{ stages = $Stages; container_mb = $ContainerMB; flow = $Flow; install_mode = $effective; same_volume = $SameVolume
                               existing_output_mb = $ExistingOutputMB; heavy_ir_mb = $HeavyIrMB }
        output_mb = [ordered]@{ containers = $containers; registry = $b.registry_mb; total = $output; on_disk_incl_install = $onDisk }
        working_peak_mb = [ordered]@{ shared = $shared; light_ir = $light; heavy_ir = $heavy; transient = [math]::Round($transient); total = $working }
        safety_margin_mb = $b.safety_margin_mb
        required_free_mb = [ordered]@{ total = if ($SameVolume) { $reqWork } else { $reqWork + $reqGame }; work_volume = $reqWork; game_volume = $reqGame }
        free_mb  = [ordered]@{ work_volume = $FreeWorkMB; game_volume = $FreeGameMB }
        ok       = $ok
        shortfall_mb = [math]::Round($shortfall)
    }
}