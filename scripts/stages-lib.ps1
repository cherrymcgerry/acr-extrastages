# Shared helpers for config/stages.toml (dot-source: . "$PSScriptRoot\stages-lib.ps1").
# Minimal TOML reader for our schema: [locations.x] tables and [[stages]] arrays of key = value lines.

function Read-StagesToml([string]$path) {
    $locs = @{}
    $stages = [System.Collections.Generic.List[hashtable]]::new()
    $section = $null
    $locKey = $null
    $cur = $null
    foreach ($raw in Get-Content $path -Encoding UTF8) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        if ($line -match '^\[locations\.([^\]]+)\]$') {
            if ($cur -and $section -eq 'stage') { $stages.Add($cur) }
            $section = 'loc'; $locKey = $Matches[1]; $locs[$locKey] = @{}; $cur = $null; continue
        }
        if ($line -eq '[[stages]]') {
            if ($cur -and $section -eq 'stage') { $stages.Add($cur) }
            $section = 'stage'; $cur = @{}; continue
        }
        if ($line -match '^([A-Za-z0-9_]+)\s*=\s*(.+)$') {
            $k = $Matches[1]; $v = $Matches[2].Trim()
            if ($v -match '^"(.*)"$') { $v = $Matches[1] }
            elseif ($v -eq 'true') { $v = $true }
            elseif ($v -eq 'false') { $v = $false }
            elseif ($v -match '^-?\d+(\.\d+)?$') { $v = [double]::Parse($v, [Globalization.CultureInfo]::InvariantCulture) }
            if ($section -eq 'loc') { $locs[$locKey][$k] = $v }
            elseif ($section -eq 'stage') { $cur[$k] = $v }
        }
    }
    if ($cur -and $section -eq 'stage') { $stages.Add($cur) }
    return @{ Locations = $locs; Stages = $stages }
}

# AcrXs<LocCode><Rally><Route> e.g. new_zealand_rally_01_route_5 + Nz -> AcrXsNz01r5
function Get-AcrId([string]$dr2, [string]$code) {
    if ($dr2 -notmatch '^([a-z_]+)_rally_(\d+)_route_(\d+)$') { throw "bad dr2 id: $dr2" }
    $rally = [int]$Matches[2]
    $route = [int]$Matches[3]
    return "AcrXs${code}$('{0:D2}' -f $rally)r$route"
}
