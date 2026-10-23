<#
.SYNOPSIS
  Per-stage DR2 data for every enabled stage in config/stages.toml: render meshes, ground bake and textures,
  cached in work/ir/<dr2 id>/. Skips complete stages; bounds retries (max 2) then records failed.json.
  Optional/shared textures (sponsor cloth, lazerstart specular, etc.) are recorded in missing_files but do not
  block "prepared" — stage-build DropIn already skips missing object diffuse. Writes prepare.done when finished.
#>
param(
    [string[]]$Only,
    [switch]$Force,
    [switch]$NoReport,   # do not write prepare-data.json / prepare.done (build-stages.ps1 -PrepareOnDemand prepares one stage at a time)
    [int]$MaxAttempts = 2,
    [string]$Config = (Join-Path (Split-Path $PSScriptRoot) 'config\stages.toml')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'stages-lib.ps1')
$builtExe = Join-Path $root 'tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe'
$exeDir = Join-Path $root 'work\bin\prepare-extract'
$exe = Join-Path $exeDir 'stageprep.exe'
$out = Join-Path $root 'work\acr\batch'
$attemptsPath = Join-Path $out 'prepare-attempts.json'

# Sponsor packs are not in location archives; lazerstart_s uses unsupported u8x4_srgb. Non-fatal for prepare.
$OptionalTexturePatterns = @(
    '^cloth(_[a-z])?$',
    '^teardrop$',
    '^drape$',
    '^board$',          # sponsor_pack_*.pssg|sponsor_fallback.pssg#board.tga (France, Wales)
    '^core_game_lazerstart'
)

function Test-OptionalTexture([string]$baseName) {
    foreach ($pat in $OptionalTexturePatterns) {
        if ($baseName -match $pat) { return $true }
    }
    return $false
}

function Get-MaterialTextureNames([string]$dir) {
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $render = Join-Path $dir 'render'
    if (-not (Test-Path $render)) { return @() }
    foreach ($f in Get-ChildItem $render -Filter '*.materials.json' -ErrorAction SilentlyContinue) {
        foreach ($mat in (Get-Content $f.FullName -Raw | ConvertFrom-Json)) {
            if (-not $mat.textures) { continue }
            foreach ($t in $mat.textures) {
                $ref = [string]$t.texture
                if ([string]::IsNullOrWhiteSpace($ref)) { continue }
                $hash = $ref.LastIndexOf('#')
                $name = if ($hash -ge 0) { $ref.Substring($hash + 1) } else { $ref }
                if ($name) { [void]$names.Add($name) }
            }
        }
    }
    return @($names)
}

function Test-TextureOnDisk([string]$texDir, [string]$textureName) {
    $base = [IO.Path]::GetFileNameWithoutExtension($textureName)
    if (Test-Path (Join-Path $texDir ($base + '.dds'))) { return $true }
    if (Test-Path (Join-Path $texDir ($base + '_slice0.dds'))) { return $true }
    return $false
}

function Get-IrMissingFiles([string]$dir, [switch]$IncludeOptional) {
    $missing = [System.Collections.Generic.List[string]]::new()
    $optional = [System.Collections.Generic.List[string]]::new()
    $stageJson = Join-Path $dir 'stage.json'
    $terrain = Join-Path $dir 'render\terrain.glb'
    $manifest = Join-Path $dir 'render\ground_bake\manifest.json'
    $skipped = Join-Path $dir 'render\ground_bake\skipped.json'
    $texDir = Join-Path $dir 'textures'

    if (-not (Test-Path $stageJson)) { $missing.Add('stage.json') }
    if (-not (Test-Path $terrain)) { $missing.Add('render/terrain.glb') }
    if (-not ((Test-Path $manifest) -or (Test-Path $skipped))) {
        $missing.Add('render/ground_bake/manifest.json|skipped.json')
    }

    if (Test-Path $manifest) {
        try {
            $man = Get-Content $manifest -Raw | ConvertFrom-Json
            $bakeDir = Join-Path $dir 'render\ground_bake'
            foreach ($tile in @($man.tiles)) {
                if ($tile.dds) {
                    $p = Join-Path $bakeDir ([string]$tile.dds)
                    if (-not (Test-Path $p)) { $missing.Add("render/ground_bake/$($tile.dds)") }
                }
            }
        }
        catch { $missing.Add('render/ground_bake/manifest.json(unreadable)') }
    }

    foreach ($name in (Get-MaterialTextureNames $dir)) {
        $base = [IO.Path]::GetFileNameWithoutExtension($name)
        if (Test-TextureOnDisk $texDir $name) { continue }
        $entry = "textures/$base.dds"
        if (Test-OptionalTexture $base) { $optional.Add($entry) }
        else { $missing.Add($entry) }
    }

    if ($IncludeOptional) {
        return @{ Required = @($missing); Optional = @($optional); All = @($missing) + @($optional) }
    }
    return @($missing)
}

function Test-StagePrepared([string]$dir) {
    $r = Get-IrMissingFiles $dir -IncludeOptional
    return $r.Required.Count -eq 0
}

function Write-BakeSkipped([string]$dir, [string]$reason) {
    $bakeDir = Join-Path $dir 'render\ground_bake'
    New-Item -ItemType Directory -Force $bakeDir | Out-Null
    @{ skipped = $true; reason = $reason; at = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content (Join-Path $bakeDir 'skipped.json') -Encoding utf8
}

function Write-StageFailed([string]$dir, [string]$reason) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    @{ failed = $true; reason = $reason; at = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content (Join-Path $dir 'prepare-failed.json') -Encoding utf8
}

function Invoke-Dr2Extract([string[]]$ArgList) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = & $exe @ArgList 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    $text = @($raw | ForEach-Object { "$_" }) -join "`n"
    $text -split "`n" | ForEach-Object { Write-Host $_ }
    return @{ Code = $code; Text = $text }
}

function Read-Attempts {
    if (Test-Path $attemptsPath) {
        try { return Get-Content $attemptsPath -Raw | ConvertFrom-Json -AsHashtable } catch { }
    }
    return @{}
}

function Save-Attempts($map) {
    New-Item -ItemType Directory -Force $out | Out-Null
    ($map.GetEnumerator() | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ stage = $_.Key; attempts = $_.Value }
    }) | ConvertTo-Json | Set-Content $attemptsPath -Encoding utf8
    # also keep hashtable form for next read — write as object
    $obj = [ordered]@{}
    foreach ($k in ($map.Keys | Sort-Object)) { $obj[$k] = [int]$map[$k] }
    $obj | ConvertTo-Json | Set-Content $attemptsPath -Encoding utf8
}

Push-Location $root
try {
    $skipBuild = $env:PREPARE_SKIP_BUILD -eq '1'
    if (-not $skipBuild) {
        & dotnet build (Join-Path $root 'tools\dr2-extract\Dr2Extract.csproj') -c Release --nologo -v q -p:NuGetAudit=false | Out-Host
        if ($LASTEXITCODE -ne 0) {
            & dotnet build (Join-Path $root 'tools\dr2-extract\Dr2Extract.csproj') -c Release --nologo -v q -p:NuGetAudit=false --no-restore | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'dr2-extract build failed' }
        }
    }
    elseif (-not (Test-Path $builtExe)) {
        throw "PREPARE_SKIP_BUILD=1 but missing $builtExe"
    }
    New-Item -ItemType Directory -Force $exeDir | Out-Null
    Copy-Item (Join-Path (Split-Path $builtExe) '*') $exeDir -Force
    if (-not (Test-Path (Join-Path $exeDir 'dr2-extract.runtimeconfig.json'))) {
        throw "dr2-extract.runtimeconfig.json missing next to $exe (incomplete build)"
    }
    Copy-Item (Join-Path $exeDir 'dr2-extract.exe') $exe -Force
    Copy-Item (Join-Path $exeDir 'dr2-extract.runtimeconfig.json') (Join-Path $exeDir 'stageprep.runtimeconfig.json') -Force
    $deps = Join-Path $exeDir 'dr2-extract.deps.json'
    if (Test-Path $deps) { Copy-Item $deps (Join-Path $exeDir 'stageprep.deps.json') -Force }

    $attempts = @{}
    if (Test-Path $attemptsPath) {
        $rawAttempts = Get-Content $attemptsPath -Raw | ConvertFrom-Json
        foreach ($p in $rawAttempts.PSObject.Properties) { $attempts[$p.Name] = [int]$p.Value }
    }

    $data = Read-StagesToml $Config
    $report = @()
    foreach ($st in $data.Stages) {
        if (-not $st.enabled) { continue }
        $id = $st.dr2
        if ($Only -and $id -notin $Only) { continue }
        $dir = Join-Path $root "work\ir\$id"
        $row = [ordered]@{
            stage = $id
            ok = $true
            render = $null
            bake = $null
            missing_files = 0
            missing_optional = 0
            missing = @()
            attempts = 0
            steps = @()
            error = $null
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $failedMarker = Join-Path $dir 'prepare-failed.json'
        try {
            $terrain = Join-Path $dir 'render\terrain.glb'
            $manifest = Join-Path $dir 'render\ground_bake\manifest.json'
            $skippedMarker = Join-Path $dir 'render\ground_bake\skipped.json'
            $texDir = Join-Path $dir 'textures'
            $stageJson = Join-Path $dir 'stage.json'

            if (-not $Force -and (Test-Path $failedMarker)) {
                $prev = Get-Content $failedMarker -Raw | ConvertFrom-Json
                $row.ok = $false
                $row.error = "failed (max attempts): $($prev.reason)"
                $row.attempts = if ($attempts.ContainsKey($id)) { $attempts[$id] } else { $MaxAttempts }
                $miss = Get-IrMissingFiles $dir -IncludeOptional
                $row.missing_files = $miss.All.Count
                $row.missing_optional = $miss.Optional.Count
                $row.missing = @($miss.All | Select-Object -First 40)
                $row.steps += 'failed_marker'
                Write-Host "== $id : skip (prepare-failed.json: $($prev.reason))"
                $row.seconds = 0
                $report += [pscustomobject]$row
                continue
            }

            if (-not $Force -and (Test-StagePrepared $dir)) {
                $miss = Get-IrMissingFiles $dir -IncludeOptional
                $row.render = 'ok'
                $row.bake = if (Test-Path $manifest) { 'ok' } else { 'skipped' }
                $row.missing_files = $miss.All.Count
                $row.missing_optional = $miss.Optional.Count
                $row.missing = @($miss.Optional | Select-Object -First 40)
                $row.steps += 'skip'
                Write-Host "== $id : skip (prepared, bake=$($row.bake), missing_optional=$($row.missing_optional))"
                $row.seconds = 0
                $report += [pscustomobject]$row
                continue
            }

            $nAttempt = if ($attempts.ContainsKey($id)) { [int]$attempts[$id] } else { 0 }
            if (-not $Force -and $nAttempt -ge $MaxAttempts) {
                $reason = "exceeded $MaxAttempts prepare attempts"
                Write-StageFailed $dir $reason
                throw $reason
            }
            $attempts[$id] = $nAttempt + 1
            $row.attempts = $attempts[$id]
            Save-Attempts $attempts
            Write-Host "== $id : attempt $($row.attempts)/$MaxAttempts"

            $needIr = $Force -or -not (Test-Path $terrain) -or -not (Test-Path $stageJson) -or (
                -not (Test-Path $manifest) -and -not (Test-Path $skippedMarker))
            if ($needIr) {
                Write-Host "== $id : stage-ir --render --bake-ground"
                $ir = Invoke-Dr2Extract @('stage-ir', $id, '--render', '--bake-ground')
                if ($ir.Code -ne 0) {
                    # Only soft-skip when bake cannot run for shader reasons — not disk/IO/AES failures.
                    if ((Test-Path $terrain) -and ($ir.Text -match 'no terrain_array triangles')) {
                        $hint = ($ir.Text -split "`n" | Where-Object { $_ -match 'no terrain_array' } | Select-Object -First 1)
                        $reason = if ($hint) { $hint.ToString().Trim() } else { 'ground bake failed' }
                        Write-BakeSkipped $dir $reason
                        $row.bake = 'skipped'
                        $row.steps += 'stage-ir'
                        Write-Warning "$id : ground bake skipped ($reason)"
                    }
                    else {
                        throw "stage-ir failed ($($ir.Code)): $(($ir.Text -split "`n" | Where-Object { $_ -match 'error:' } | Select-Object -First 1))"
                    }
                }
                else {
                    $row.steps += 'stage-ir'
                }
            }

            $texCheck = Get-IrMissingFiles $dir -IncludeOptional
            $needTex = $Force -or ($texCheck.Required | Where-Object { $_ -like 'textures/*' }).Count -gt 0 -or
                -not (Test-Path $texDir) -or -not (Get-ChildItem $texDir -File -ErrorAction SilentlyContinue)
            if ($needTex) {
                Write-Host "== $id : stage-textures"
                $tex = Invoke-Dr2Extract @('stage-textures', $id)
                if ($tex.Code -ne 0) { throw "stage-textures failed ($($tex.Code))" }
                $row.steps += 'stage-textures'
            }

            if (-not (Test-Path $terrain)) { throw "no render/terrain.glb after stage-ir" }
            if (-not (Test-Path $stageJson)) { throw "no stage.json after stage-ir" }
            $row.render = 'ok'
            if (Test-Path $manifest) {
                $row.bake = 'ok'
                if (Test-Path $skippedMarker) { Remove-Item $skippedMarker -Force }
            }
            elseif (Test-Path $skippedMarker) {
                $row.bake = 'skipped'
            }
            else {
                Write-BakeSkipped $dir 'no ground bake manifest after stage-ir'
                $row.bake = 'skipped'
                Write-Warning "$id : no ground bake manifest (bake_skipped)"
            }

            $miss = Get-IrMissingFiles $dir -IncludeOptional
            $row.missing_files = $miss.All.Count
            $row.missing_optional = $miss.Optional.Count
            $row.missing = @($miss.All | Select-Object -First 40)
            if ($miss.Required.Count -gt 0) {
                throw "still missing required files ($($miss.Required.Count)): $($miss.Required[0..([Math]::Min(4,$miss.Required.Count-1))] -join ', ')"
            }
            if ($miss.Optional.Count -gt 0) {
                Write-Warning "$id : missing_optional=$($miss.Optional.Count) (non-fatal: sponsor/lazerstart; DropIn skips)"
            }
            # success: clear attempt counter so Force re-runs stay clean
            if ($attempts.ContainsKey($id)) { $attempts.Remove($id); Save-Attempts $attempts }
            if (Test-Path $failedMarker) { Remove-Item $failedMarker -Force }
        }
        catch {
            $row.ok = $false
            $row.error = $_.Exception.Message
            try {
                $miss = Get-IrMissingFiles $dir -IncludeOptional
                $row.missing_files = $miss.All.Count
                $row.missing_optional = $miss.Optional.Count
                $row.missing = @($miss.All | Select-Object -First 40)
            } catch { }
            if (-not $row.attempts) { $row.attempts = if ($attempts.ContainsKey($id)) { $attempts[$id] } else { 0 } }
            if ($row.attempts -ge $MaxAttempts) {
                Write-StageFailed $dir $row.error
                Write-Warning "$id : FAILED after $($row.attempts) attempts: $($row.error)"
            }
            else {
                Write-Warning "$id : attempt $($row.attempts) failed: $($row.error)"
            }
        }
        $row.seconds = [math]::Round($sw.Elapsed.TotalSeconds)
        $report += [pscustomobject]$row
    }

    New-Item -ItemType Directory -Force $out | Out-Null
    $jsonPath = Join-Path $out 'prepare-data.json'
    $jsonTmp = Join-Path $out 'prepare-data.json.tmp'
    if (-not $NoReport) {
        $report | ConvertTo-Json -Depth 5 | Set-Content $jsonTmp -Encoding utf8
        Move-Item $jsonTmp $jsonPath -Force
    }

    $summary = foreach ($r in $report) {
        $status = if (-not $r.ok) { "FAILED: $($r.error)" }
        elseif ($r.bake -eq 'skipped') { 'render=ok bake=skipped' }
        elseif ($r.bake -eq 'ok') { 'render=ok bake=ok' }
        else { "render=$($r.render) bake=$($r.bake)" }
        "$($r.stage)`t$status`tmissing_files=$($r.missing_files)`tmissing_optional=$($r.missing_optional)"
    }
    $ts = (Get-Date).ToString('o')
    $nFail = @($report | Where-Object { -not $_.ok }).Count
    $nSkip = @($report | Where-Object { $_.ok -and $_.bake -eq 'skipped' }).Count
    $nBake = @($report | Where-Object { $_.ok -and $_.bake -eq 'ok' }).Count
    $donePath = Join-Path $out 'prepare.done'
    $doneTmp = Join-Path $out 'prepare.done.tmp'
    $done = @(
        "prepare.done $ts"
        "stages=$($report.Count) ok_bake=$nBake ok_bake_skipped=$nSkip failed=$nFail"
        $summary
    ) -join "`n"
    if (-not $NoReport) {
        [IO.File]::WriteAllText($doneTmp, $done + "`n")
        Move-Item $doneTmp $donePath -Force
    }

    Write-Host "prepared $($report.Count) stages: bake_ok=$nBake bake_skipped=$nSkip failed=$nFail"
    if (-not $NoReport) { Write-Host "wrote $donePath" }
    # Exit 0 once every stage is ok OR permanently failed (bounded). Retry only if some failed but under max attempts.
    $retryable = @($report | Where-Object {
        -not $_.ok -and [int]$_.attempts -lt $MaxAttempts -and
        -not (Test-Path (Join-Path $root "work\ir\$($_.stage)\prepare-failed.json"))
    })
    if ($retryable.Count -gt 0) { exit 2 }
    if ($nFail -gt 0) { exit 1 }
}
finally { Pop-Location }
