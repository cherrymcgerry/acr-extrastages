<#
.SYNOPSIS
  Batch build of the additive DiRT stages listed in config/stages.toml (docs/additive-plan.md, step 4).

.DESCRIPTION
  1. regenerates work/acr/specs/*.json (scripts/gen-stage-specs.ps1);
  2. runs `acr-ref stage-fit` once (rotation / physics world per stage; stages over -MaxLengthKm are deferred);
  3. for every selected stage: checks its prepared DR2 data, skips when inputs are unchanged, otherwise builds the container with
     mods/additive/build.ps1 (stage-build --bounds, overlay, relocate, retoc) - one child process per stage, so a failure never
     stops the batch;
  4. builds ONE registry (menu tables) listing only the stages whose container exists (-PassedOnly), optionally verifies it;
  5. writes work/acr/batch/report.md + report.json and work/acr/batch/registry-ids.json (what scripts/install-stages.ps1 installs).

  It never extracts DR2 data. A stage whose IR render data is missing is skipped with "needs prepare" (run scripts/prepare-stage-data.ps1).
  A missing ground bake manifest is only a warning: the stage builds with the DR2 grass-slice ground (the -GroundBake off equivalent).

  The registry always reflects THIS run's selection: `-Only` builds a registry for just those stages (spot-check sets);
  a later full run rebuilds it for everything that passed.

.PARAMETER Only         acr ids (AcrXsDe01r4) or DR2 ids (germany_rally_01_route_4); default: every enabled stage in stages.toml
.PARAMETER Force        rebuild even when inputs are unchanged
.PARAMETER NoVerify     skip the CUE4Parse verify + registry round trip after the registry build
.PARAMETER MaxLengthKm  stages longer than this are deferred (default 9)
.PARAMETER Install      install the registry + built containers into ~mods when done (scripts/install-stages.ps1: hard links on the same volume, else copies)
.PARAMETER ListOnly     print the plan (needs prepare / deferred / to build) and exit
.PARAMETER KeepIr       keep each stage's heavy IR (work\ir\<dr2 id>\render + textures, ~1.7 GB). Default: after a stage is built and its container
                        verified present, those two folders are deleted and only the light IR (stage.json + collision glbs, ~30 MB: the registry,
                        stage-fit and menu images read it) stays, plus ir-pruned.json with the input signatures, so an unchanged stage is still
                        "unchanged". A pruned stage whose inputs changed needs re-preparing (-PrepareOnDemand, or prepare-stage-data.ps1 -Only <id>).
                        IR of stages that were NOT built in this run ("unchanged") is never touched: use scripts\clean-work.ps1 for that.
.PARAMETER PrepareOnDemand  streaming flow for a clean tree: for each stage that lacks (or had pruned) IR run prepare-stage-data.ps1 -Only <id>
                        right before building it, drop the DR2 location cache (work\dr2\cache\<country>__<location>, 2-4 GB) once the next stage
                        is in another location, and prune the stage's heavy IR after the build. Peak working space stays ~15 GB for 30 stages
                        instead of ~115 GB (scripts\estimate-disk.ps1).
.PARAMETER KeepDr2Cache keep the DR2 location caches in -PrepareOnDemand runs
.PARAMETER SkipDiskCheck  do not stop when the free space is below the estimate (scripts\estimate-disk.ps1)
#>
param(
    [string[]]$Only,
    [switch]$Force,
    [switch]$KeepIr,
    [switch]$PrepareOnDemand,
    [switch]$KeepDr2Cache,
    [switch]$SkipDiskCheck,
    [switch]$NoVerify,
    [double]$MaxLengthKm = 9,
    [switch]$Install,
    [switch]$All,         # every enabled stage; without this, only stages marked default = true (the recommended 30)
    [switch]$ListOnly,
    [switch]$FailOnAnyError,   # exit 1 if any stage failed (default: exit 0 when at least one stage passed; failures are in the report)
    [string]$Config = (Join-Path (Split-Path $PSScriptRoot) 'config\stages.toml')
)
$ErrorActionPreference = 'Stop'
if ($Only) { $Only = @($Only | % { $_ -split '[,;\s]+' } | ? { $_ }) }   # `pwsh -File ... -Only a,b` arrives as one string
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'stages-lib.ps1')
. (Join-Path $PSScriptRoot 'disk-lib.ps1')
$inv = [Globalization.CultureInfo]::InvariantCulture
$work = Join-Path $root 'work\acr\additive'
$batch = Join-Path $root 'work\acr\batch'
$logs = Join-Path $batch 'logs'
$state = Join-Path $batch 'state'
$dist = Join-Path $work 'dist'
$specDir = Join-Path $root 'work\acr\specs'
$buildPs1 = Join-Path $root 'mods\additive\build.ps1'
$acrRef = Join-Path $root 'work\bin\acr-ref\acr-ref.exe'
$additive = Join-Path $root 'work\bin\acr-additive\acr-additive.exe'
New-Item -ItemType Directory -Force $batch, $logs, $state, $dist | Out-Null

function Hash-Text([string]$s) {
    [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s))).Replace('-', '').Substring(0, 24)
}
function File-Sig([string]$p) {
    if (-not (Test-Path $p)) { return 'none' }
    $i = Get-Item $p; "$($i.Length):$($i.LastWriteTimeUtc.Ticks)"
}
function Content-Hash([string]$p) { if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash.Substring(0, 16) } else { 'none' } }

# ---- plan -----------------------------------------------------------------------------------------------------------------
$data = Read-StagesToml $Config
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'gen-stage-specs.ps1') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'gen-stage-specs.ps1 failed' }

$plan = @()
foreach ($st in $data.Stages) {
    if (-not $st.enabled) { continue }
    if (-not $All -and -not $Only -and -not $st.default) { continue }
    $loc = $data.Locations[$st.location]
    $id = Get-AcrId $st.dr2 $loc.code
    if ($Only -and $id -notin $Only -and $st.dr2 -notin $Only) { continue }
    $plan += [pscustomobject]@{ Id = $id; Dr2 = $st.dr2; Location = $loc.name; Spec = Join-Path $specDir "$($id.ToLowerInvariant()).json" }
}
if (-not $plan) { throw "no stage selected (Only = $($Only -join ', '))" }

# tools (built once; build.ps1 gets -SkipToolBuild)
if (-not $ListOnly) {
    & dotnet build (Join-Path $root 'tools\acr-additive\AcrAdditive.csproj') -c Release -o (Split-Path $additive) --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { if (Test-Path $additive) { Write-Warning 'acr-additive build failed; using the existing work\bin build' } else { throw 'acr-additive build failed' } }
    & dotnet build (Join-Path $root 'tools\acr-ref\AcrRef.csproj') -c Release -o (Split-Path $acrRef) --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { if (Test-Path $acrRef) { Write-Warning 'acr-ref build failed; using the existing work\bin build' } else { throw 'acr-ref build failed' } }
}

# fit report (read-only, ~20 s for all 156 stages)
$fitPath = Join-Path $root 'work\acr\fit-report.json'
& $acrRef stage-fit --out $fitPath 2>&1 | Out-Null
$fit = @{}
if (Test-Path $fitPath) { foreach ($f in (Get-Content $fitPath -Raw | ConvertFrom-Json).stages) { $fit[$f.stage] = $f } }

$configSig = (@('visuals', 'sky', 'pacenote-map', 'object-collision', 'scenery', 'surface-map') | % { "$_=" + (Content-Hash (Join-Path $root "config\$_.toml")) }) -join ';'
$toolSig = (@('work\bin\acr-ref\acr-ref.dll', 'work\bin\acr-additive\acr-additive.dll') | % { Content-Hash (Join-Path $root $_) }) -join ';'
$scriptSig = Content-Hash $buildPs1
# .source.json is rewritten (with a fresh 'created' timestamp) by every build.ps1 run, so hash only its container list
$vanillaSig = Hash-Text ((& { $sj = Join-Path $work 'legacy\.source.json'; if (Test-Path $sj) { (Get-Content $sj -Raw | ConvertFrom-Json).containers -join ';' } else { 'none' } }))

# Input hash of one stage (spec, IR files, config, tools, build script, vanilla). Once the heavy IR was pruned (ir-pruned.json) the
# terrain / bake signatures come from the marker, so an unchanged stage keeps its hash without its 1.7 GB of render data.
function Get-StageInputHash([string]$spec, [string]$ir) {
    $terrain = Join-Path $ir 'render\terrain.glb'
    $bake = Join-Path $ir 'render\ground_bake\manifest.json'
    $marker = if (Test-IrPruned $ir) { Get-IrPruneMarker $ir } else { $null }
    $terrainSig = if (Test-Path $terrain) { File-Sig $terrain } elseif ($marker) { [string]$marker.terrain_sig } else { 'none' }
    $bakeSig = if (Test-Path $bake) { File-Sig $bake } elseif ($marker) { [string]$marker.bake_sig } else { 'none' }
    $sigText = @(
        "spec=" + (Content-Hash $spec), "ir=" + (File-Sig (Join-Path $ir 'stage.json')), "terrain=$terrainSig",
        "bake=$bakeSig", "cfg=$configSig", "tools=$toolSig", "script=$scriptSig", "vanilla=$vanillaSig"
    ) -join '|'
    Hash-Text $sigText
}

$rows = @()
foreach ($p in $plan) {
    $row = [ordered]@{ id = $p.Id; dr2 = $p.Dr2; location = $p.Location; status = 'pending'; reason = $null; warnings = @(); seconds = 0 }
    $ir = Join-Path $root "work\ir\$($p.Dr2)"
    $f = $fit[$p.Dr2]
    if ($f) {
        $row.name = $f.name; $row.length_km = [math]::Round($f.length_m / 1000, 2); $row.placement_mode = $f.placement_mode
        $row.theta_deg = $f.theta_deg; $row.world_km2 = $f.world_km2; $row.fit_status = $f.status
    }
    # heavy IR (render + textures) deleted after the last successful build (ir-pruned.json): only the light IR is expected then
    $heavyGone = (Test-IrPruned $ir) -and -not (Test-Path (Join-Path $ir 'render\terrain.glb'))
    $needs = @()
    if (-not (Test-Path (Join-Path $ir 'stage.json'))) { $needs += 'stage.json' }
    if (-not $heavyGone) {
        if (-not (Test-Path (Join-Path $ir 'render\terrain.glb'))) { $needs += 'render meshes' }
        if (-not (Get-ChildItem (Join-Path $ir 'textures') -ErrorAction SilentlyContinue | Select-Object -First 1)) { $needs += 'textures' }
    }
    $bakePath = Join-Path $ir 'render\ground_bake\manifest.json'
    if ($f -and $f.status -like 'deferred*') {
        $row.status = 'deferred'; $row.reason = ($f.notes -join '; ')
    }
    elseif ($f -and $f.status -eq 'error') {
        $row.status = 'failed'; $row.reason = "stage-fit: $($f.error)"
    }
    elseif ($needs -and -not $PrepareOnDemand) {
        $row.status = 'needs-prepare'; $row.reason = "needs prepare: missing $($needs -join ', ') in work\ir\$($p.Dr2) (run scripts\prepare-stage-data.ps1, or build-stages.ps1 -PrepareOnDemand; the batch never extracts DR2 data otherwise)"
    }
    else {
        if ($needs) { $row.prepare = $true }                      # -PrepareOnDemand: prepared right before its build
        elseif ($heavyGone) { $row.ir_pruned = $true }            # light IR only: fine while the inputs are unchanged
        elseif (-not (Test-Path $bakePath)) { $row.warnings += 'no ground_bake manifest: building with the DR2 grass-slice ground (-GroundBake off equivalent)' }
        if (-not $needs) { $row.input_hash = Get-StageInputHash $p.Spec $ir }
        $row.status = 'todo'
    }
    $rows += [pscustomobject]$row
}
$specOf = @{}; foreach ($p in $plan) { $specOf[$p.Id] = $p.Spec }

function Test-StageUnchanged($r) {
    if ($Force -or -not $r.input_hash) { return $false }
    $idl = $r.id.ToLowerInvariant()
    $stf = Join-Path $state "$idl.json"
    if (-not (Test-Path $stf) -or -not (Test-Path (Join-Path $dist "zz_acrxs_s_${idl}_P.utoc"))) { return $false }
    $pv = Get-Content $stf -Raw | ConvertFrom-Json
    return [bool]($pv.ok -and $pv.input_hash -eq $r.input_hash)
}

# ---- disk preflight (scripts/estimate-disk.ps1 has the same numbers) ----------------------------------------------------------
$todoCount = @($rows | ? { $_.status -eq 'todo' -and -not (Test-StageUnchanged $_) }).Count
$diskFlow = if ($PrepareOnDemand) { 'streaming' } else { 'build-only' }
$gameDir = if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' }
$sameVol = if (Test-Path $gameDir) { (Split-Path -Qualifier (Resolve-Path $gameDir).Path) -eq (Split-Path -Qualifier $root) } else { $true }
$diskEst = $null
if ($todoCount -gt 0) {
    $diskEst = Get-DiskEstimate -Stages $todoCount -Flow $diskFlow -SameVolume $sameVol -FreeWorkMB (Get-DriveFreeMB $root) `
        -FreeGameMB $(if (Test-Path $gameDir) { Get-DriveFreeMB $gameDir } else { -1 })
    Write-Host ("disk preflight: {0} stage(s) to build ({1}); need {2:N1} GB free = stage files {3:N1} + working peak {4:N1} + margin {5:N1}; free now {6:N1} GB" -f `
        $todoCount, $diskFlow, ($diskEst.required_free_mb.work_volume / 1024), ($diskEst.output_mb.total / 1024), ($diskEst.working_peak_mb.total / 1024), `
        ($diskEst.safety_margin_mb / 1024), ($diskEst.free_mb.work_volume / 1024))
    if (-not $diskEst.ok) {
        $msg = "not enough free disk space: short by $([math]::Round($diskEst.shortfall_mb / 1024, 1)) GB (scripts\estimate-disk.ps1; scripts\clean-work.ps1 frees regenerable data)"
        if ($ListOnly -or $SkipDiskCheck) { Write-Warning $msg } else { throw $msg }
    }
}
if ($ListOnly) {
    $rows | % { "{0,-15} {1,-32} {2,-14} {3}" -f $_.id, $_.dr2, $_.status, $(if ($_.prepare) { '(prepare DR2 data first)' } elseif ($_.ir_pruned) { '(heavy IR pruned; re-prepared only if inputs changed)' } else { $_.reason }) } | Write-Host
    return
}

# ---- build ----------------------------------------------------------------------------------------------------------------
$script:prepareBuilt = $false
$freeStart = Get-DriveFreeMB $root; $freeMin = $freeStart; $irFreedTotal = 0
function Sample-Free { $f = Get-DriveFreeMB $root; if ($f -lt $script:freeMin) { $script:freeMin = $f } }
function Get-Loc([string]$dr2) { $dr2 -replace '_route_\d+$', '' }

# prepare-stage-data.ps1 for ONE stage (no global report files); true when stage.json, render meshes and textures exist afterwards
function Invoke-StagePrepare($r) {
    $irDir = Join-Path $root "work\ir\$($r.dr2)"
    $log = Join-Path $logs "$($r.id).prepare.log"
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $prevSkip = $env:PREPARE_SKIP_BUILD
    if ($script:prepareBuilt) { $env:PREPARE_SKIP_BUILD = '1' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-stage-data.ps1') -Only $r.dr2 -NoReport -Config $Config *> $log
    $code = $LASTEXITCODE
    $env:PREPARE_SKIP_BUILD = $prevSkip
    $ErrorActionPreference = $prevEap
    if (Test-Path (Join-Path $root 'tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe')) { $script:prepareBuilt = $true }
    $ok = ($code -eq 0) -and (Test-Path (Join-Path $irDir 'stage.json')) -and (Test-Path (Join-Path $irDir 'render\terrain.glb')) -and
        [bool](Get-ChildItem (Join-Path $irDir 'textures') -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $ok) {
        $why = @(Get-Content $log -ErrorAction SilentlyContinue | ? { $_ -match 'FAILED|error|failed' } | Select-Object -Last 2) -join ' | '
        $r.reason = "needs prepare: prepare-stage-data.ps1 -Only $($r.dr2) failed (exit $code; $($why.Trim())); log work\acr\batch\logs\$($r.id).prepare.log"
    }
    $ok
}

function Prune-StageIr($r, [string]$why) {
    if ($KeepIr) { return }
    $irDir = Join-Path $root "work\ir\$($r.dr2)"
    if (-not (Test-Path (Join-Path $irDir 'stage.json'))) { return }
    $freed = Remove-StageIrHeavy $irDir
    $script:irFreedTotal += $freed
    $r | Add-Member -NotePropertyName ir_freed_mb -NotePropertyValue ([math]::Round($freed)) -Force
    Write-Host "    heavy IR pruned ($why): freed $([math]::Round($freed)) MB (light IR kept: work\ir\$($r.dr2))"
}

$total = $rows.Count; $n = 0
foreach ($r in $rows) {
    $n++
    $idl = $r.id.ToLowerInvariant()
    $container = Join-Path $dist "zz_acrxs_s_${idl}_P.utoc"
    $stFile = Join-Path $state "$idl.json"
    $irDir = Join-Path $root "work\ir\$($r.dr2)"
    if ($r.status -ne 'todo') {
        # not buildable now: never leave an old container around for the registry to pick up
        Remove-Item (Join-Path $dist "zz_acrxs_s_${idl}_P.*") -ErrorAction SilentlyContinue
        Write-Host "[$n/$total] $($r.id): $($r.status) - $($r.reason)"
        continue
    }
    $prev = if (Test-Path $stFile) { Get-Content $stFile -Raw | ConvertFrom-Json } else { $null }
    if (-not $Force -and $r.input_hash -and $prev -and $prev.input_hash -eq $r.input_hash -and $prev.ok -and (Test-Path $container)) {
        $r.status = 'unchanged'; $r.seconds = 0
        Write-Host "[$n/$total] $($r.id): unchanged (inputs identical, container present)"
        continue
    }
    $preparedNow = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($r.prepare -or $r.ir_pruned) {
        if (-not $PrepareOnDemand) {
            # light IR only (heavy part pruned after the last build) and the inputs changed: the render data has to be regenerated first
            $r.status = 'needs-prepare'
            $r.reason = "needs prepare: heavy IR was pruned after the last build and the inputs changed - run scripts\prepare-stage-data.ps1 -Only $($r.dr2) (or build-stages.ps1 -PrepareOnDemand)"
            Remove-Item (Join-Path $dist "zz_acrxs_s_${idl}_P.*") -ErrorAction SilentlyContinue
            Write-Host "[$n/$total] $($r.id): $($r.status) - $($r.reason)"
            continue
        }
        Write-Host "[$n/$total] $($r.id) ($($r.dr2)) preparing DR2 data..."
        $prepOk = Invoke-StagePrepare $r
        $preparedNow = $true
        Remove-Item (Join-Path $irDir 'ir-pruned.json') -ErrorAction SilentlyContinue
        if (-not $KeepDr2Cache) {
            # the next stage that needs preparing is in another location (or there is none): this location's extracted archive is dead weight
            $next = @($(if ($n -lt $rows.Count) { $rows[$n..($rows.Count - 1)] }) | ? { $_ -and $_.status -eq 'todo' -and ($_.prepare -or $_.ir_pruned) } | Select-Object -First 1)
            if (-not $next -or (Get-Loc $next[0].dr2) -ne (Get-Loc $r.dr2)) {
                $freedC = Remove-Dr2LocationCache $root $r.dr2
                if ($freedC) { Write-Host "    DR2 location cache dropped: freed $([math]::Round($freedC)) MB" }
            }
        }
        Sample-Free
        if (-not $prepOk) {
            $r.status = 'needs-prepare'; $r.seconds = [int]$sw.Elapsed.TotalSeconds
            Prune-StageIr $r 'prepare failed'
            Write-Host "    -> $($r.status): $($r.reason)"
            continue
        }
        # new stage.json may change the fit (the plan only had fit data for stages that already had IR)
        if (-not $fit[$r.dr2]) {
            & $acrRef stage-fit --out $fitPath 2>&1 | Out-Null
            if (Test-Path $fitPath) { foreach ($ff in (Get-Content $fitPath -Raw | ConvertFrom-Json).stages) { $fit[$ff.stage] = $ff } }
            $ff = $fit[$r.dr2]
            if ($ff) {
                foreach ($kv in @{ name = $ff.name; length_km = [math]::Round($ff.length_m / 1000, 2); placement_mode = $ff.placement_mode; theta_deg = $ff.theta_deg; world_km2 = $ff.world_km2; fit_status = $ff.status }.GetEnumerator()) {
                    $r | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
                }
                if ($ff.status -like 'deferred*' -or $ff.status -eq 'error') {
                    $r.status = if ($ff.status -eq 'error') { 'failed' } else { 'deferred' }
                    $r.reason = if ($ff.status -eq 'error') { "stage-fit: $($ff.error)" } else { ($ff.notes -join '; ') }
                    Remove-Item (Join-Path $dist "zz_acrxs_s_${idl}_P.*") -ErrorAction SilentlyContinue
                    Prune-StageIr $r $r.status
                    Write-Host "    -> $($r.status): $($r.reason)"
                    continue
                }
            }
        }
        $r.input_hash = Get-StageInputHash $specOf[$r.id] $irDir
        if (-not (Test-Path (Join-Path $irDir 'render\ground_bake\manifest.json'))) { $r.warnings += 'no ground_bake manifest: building with the DR2 grass-slice ground (-GroundBake off equivalent)' }
    }
    Write-Host "[$n/$total] $($r.id) ($($r.dr2)) building..."
    $log = Join-Path $logs "$($r.id).log"
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & pwsh -NoProfile -File $buildPs1 -Stages (Join-Path $specDir "$idl.json") -Only $r.id -SkipRegistry -SkipToolBuild *> $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    $r.seconds = [int]$sw.Elapsed.TotalSeconds
    if ($code -eq 0 -and (Test-Path $container)) {
        $r.status = 'built'
        # advisory offline checks (config/visuals.toml [checks] advisory) print WARN: listed in the report, they do not fail the stage
        $warn = @(Get-Content $log -ErrorAction SilentlyContinue | ? { $_ -match '^WARN ' } | % { ($_ -replace '^WARN (visuals ground: |visuals road corridor: |visuals: )?', '') -replace '\s*\(.*$', '' })
        if ($warn.Count) { $r.warnings += "advisory WARN x$($warn.Count): " + (($warn | % { $_.Substring(0, [Math]::Min(60, $_.Length)) }) -join ' / ') }
    }
    else {
        $r.status = 'failed'
        Remove-Item (Join-Path $dist "zz_acrxs_s_${idl}_P.*") -ErrorAction SilentlyContinue
        foreach ($d in "mod-build-$idl", "overlay-$idl", "mod-stage-$idl") { Remove-Item -Recurse -Force (Join-Path $work $d) -ErrorAction SilentlyContinue }   # build.ps1 cleans these itself; belt and braces for a killed child
        $lines = Get-Content $log -ErrorAction SilentlyContinue
        $why = @($lines | ? { $_ -match '^FAIL ' } | Select-Object -First 3) + @($lines | ? { $_ -match '(?i)\berror\b|exception|not enough space|failed \(' } | Select-Object -Last 2)
        $miss = $lines | ? { $_ -match "Could not find file '.*\\work\\ir\\" } | Select-Object -First 1
        $msg = "exit $code; " + ((($why | ? { $_ }) | % { $_.Trim() }) -join ' | ')
        $r.reason = $msg.Substring(0, [Math]::Min(500, $msg.Length))
        if ($miss) {
            # prepared data is incomplete (e.g. a texture the render materials reference was never extracted): a prepare problem, not a build bug
            $r.status = 'needs-prepare'
            $r.reason = "needs prepare: incomplete IR data - " + ($miss -replace '^.*Could not find file ''', '' -replace '''\.?\s*$', '')
        }
    }
    @{ input_hash = $r.input_hash; ok = ($r.status -eq 'built'); at = (Get-Date).ToString('s') } | ConvertTo-Json | Set-Content $stFile -Encoding UTF8
    Sample-Free
    # The container in dist is the product; the 1.7 GB of render meshes + textures can be regenerated from the DR2 install (~30 s).
    # A built stage always loses them (-KeepIr keeps them); a stage prepared by THIS run loses them even when it failed (a retry re-prepares).
    if ($r.status -eq 'built') { Prune-StageIr $r 'built' }
    elseif ($preparedNow) { Prune-StageIr $r $r.status }
    Sample-Free
    Write-Host "    -> $($r.status) in $($r.seconds) s$(if ($r.reason) { ': ' + $r.reason })"
}
# ---- per-stage facts from the stage-build reports -----------------------------------------------------------------------------
foreach ($r in $rows) {
    $idl = $r.id.ToLowerInvariant()
    $rep = Join-Path $work "stage-$idl.report.json"
    $c = Join-Path $dist "zz_acrxs_s_${idl}_P.ucas"
    if (Test-Path $c) { $r | Add-Member -NotePropertyName container_mb -NotePropertyValue ([math]::Round((Get-Item $c).Length / 1MB, 0)) -Force }
    if ($r.status -in 'built', 'unchanged' -and (Test-Path $rep)) {
        $j = Get-Content $rep -Raw | ConvertFrom-Json
        $pn = $j.tables.pacenotes
        if ($pn) {
            $r | Add-Member -NotePropertyName pacenote_rows -NotePropertyValue $pn.rows -Force
            $r | Add-Member -NotePropertyName pacenote_tokens -NotePropertyValue "$($pn.dr2_tokens_mapped)/$($pn.dr2_tokens)" -Force
            $un = if ($pn.unmapped) { ($pn.unmapped.PSObject.Properties | % { "$($_.Name) x$($_.Value)" }) -join '; ' } else { '' }
            $r | Add-Member -NotePropertyName pacenote_unmapped -NotePropertyValue $un -Force
        }
        if ($j.placement.world) {
            $w = $j.placement.world
            $r | Add-Member -NotePropertyName world_m -NotePropertyValue ("X {0:0}..{1:0}, Z {2:0}..{3:0}, Y {4:0}..{5:0}" -f $w.min_x, $w.max_x, $w.min_z, $w.max_z, $w.min_y, $w.max_y) -Force
        }
    }
}

# ---- registry (only stages that have a container) ---------------------------------------------------------------------------
$passed = @($rows | ? { $_.status -in 'built', 'unchanged' })
$registry = [ordered]@{ ids = @($passed.id); built = $false; verify = 'skipped'; error = $null }
$regLog = Join-Path $logs 'registry.log'
if ($passed.Count -gt 0) {
    $specFiles = @($plan | % { $_.Spec })
    $bArgs = @('-Stages', ($specFiles -join ','), '-RegistryOnly', '-PassedOnly', '-SkipToolBuild')
    # -PassedOnly lists stages whose container exists in dist, so stale containers of unselected stages must not leak in: the spec list is
    # already limited to the selection.
    if (-not $NoVerify) { $bArgs += '-Verify' }
    Write-Host "registry for $($passed.Count) stage(s)$(if (-not $NoVerify) { ' + verify' })..."
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & pwsh -NoProfile -File $buildPs1 @bArgs *> $regLog
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    $registry.built = ($code -eq 0)
    if ($code -ne 0) { $registry.error = (Get-Content $regLog -Tail 6 | ? { $_ }) -join ' | ' }
    $vt = Join-Path $work 'check\verify.txt'
    if (-not $NoVerify -and $registry.built -and (Test-Path $vt)) { $registry.verify = (Get-Content $vt -Tail 1) }
}
else {
    Remove-Item (Join-Path $dist 'zz_acrxs_registry_P.*') -ErrorAction SilentlyContinue
    $registry.error = 'no stage passed; no registry written'
}
$registry | ConvertTo-Json | Set-Content (Join-Path $batch 'registry-ids.json') -Encoding UTF8

# ---- report -----------------------------------------------------------------------------------------------------------------
$sum = [ordered]@{}
foreach ($g in ($rows | Group-Object status)) { $sum[$g.Name] = $g.Count }
# disk: free space is sampled at stage boundaries only, so sampled_peak_use_mb under-reports the mid-build scratch (~2 GB) a little
$disk = [ordered]@{
    flow = $diskFlow; keep_ir = [bool]$KeepIr; ir_freed_mb = [math]::Round($irFreedTotal)
    free_start_mb = $freeStart; free_min_mb = $freeMin; sampled_peak_use_mb = [math]::Max(0, $freeStart - $freeMin)
    estimate_required_free_mb = $(if ($diskEst) { $diskEst.required_free_mb.work_volume } else { $null })
}
$report = [ordered]@{ generated = (Get-Date).ToString('s'); selected = $rows.Count; summary = $sum; registry = $registry; disk = $disk; stages = $rows }
$report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $batch 'report.json') -Encoding UTF8

$md = [Text.StringBuilder]::new()
[void]$md.AppendLine("# Additive stage batch report`n`nGenerated $($report.generated). Selected $($rows.Count): " + (($sum.GetEnumerator() | % { "$($_.Value) $($_.Key)" }) -join ', ') + ".`n")
[void]$md.AppendLine("Registry: $(if ($registry.built) { "built for $($passed.Count) stage(s)" } else { 'NOT built' }); verify: $($registry.verify)$(if ($registry.error) { '; ' + $registry.error })`n")
[void]$md.AppendLine("Disk: $diskFlow flow; heavy IR $(if ($KeepIr) { 'kept (-KeepIr)' } else { "pruned after build, $([math]::Round($irFreedTotal / 1024, 1)) GB freed" }); free space at start $([math]::Round($freeStart / 1024, 1)) GB, lowest sampled $([math]::Round($freeMin / 1024, 1)) GB.`n")
[void]$md.AppendLine("| Stage | DR2 id | Location | Name | km | Placement | World km2 | Status | MB | s | Pacenote tokens | Unmapped / notes |")
[void]$md.AppendLine("|---|---|---|---|---:|---|---:|---|---:|---:|---|---|")
foreach ($r in $rows) {
    $notes = @($r.reason) + @($r.pacenote_unmapped) + @($r.warnings) | ? { $_ }
    [void]$md.AppendLine("| $($r.id) | $($r.dr2) | $($r.location) | $($r.name) | $($r.length_km) | $($r.placement_mode) | $($r.world_km2) | **$($r.status)** | $($r.container_mb) | $($r.seconds) | $($r.pacenote_tokens) | $((($notes -join '; ') -replace '\|', '/')) |")
}
[void]$md.AppendLine("`nLogs: work\acr\batch\logs\<id>.log. Fit: work\acr\fit-report.txt. Install: scripts\install-stages.ps1 (uninstall: -Uninstall).")
Set-Content (Join-Path $batch 'report.md') $md.ToString() -Encoding UTF8
Write-Host "report -> $batch\report.md  ($(($sum.GetEnumerator() | % { "$($_.Value) $($_.Key)" }) -join ', '))"

if ($Install) { & (Join-Path $PSScriptRoot 'install-stages.ps1') }
# Exit code: stage failures are listed in the report; they only fail the run with -FailOnAnyError (or when nothing passed at all).
# A registry that did not build always fails the run.
$failed = @($rows | ? { $_.status -eq 'failed' }).Count
if ($failed) { Write-Warning "$failed stage(s) failed - see report.md / logs" }
if (-not $registry.built -and $passed.Count -gt 0) { exit 1 }
if ($passed.Count -eq 0 -and $failed -gt 0) { exit 1 }
if ($FailOnAnyError -and $failed) { exit 1 }
exit 0
