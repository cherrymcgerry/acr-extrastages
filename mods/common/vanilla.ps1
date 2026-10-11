<#
  Vanilla-only game inputs for the build scripts (dot-source this file).

  The game's Paks folder also holds ~mods (and possibly other mod containers); reading it directly lets an installed mod
  leak into extracted "host" packages. Builds therefore read a hard-link view that holds only the shipped containers:
  global.* and pakchunk<N>[optional]-Windows.*  (Steam build 25170642: 1 + 53 containers).
#>

$script:VanillaContainer = '^(global|pakchunk\d+(optional)?-Windows)$'

# Creates (or refreshes) <Dest>\acr\Content\Paks as hard links to the vanilla containers of <Game>; returns <Dest>
# (usable as acr-ref --game) and lists what was left out.
function New-VanillaView([string]$Game, [string]$Dest) {
    $paks = Join-Path $Game 'acr\Content\Paks'
    if (-not (Test-Path (Join-Path $paks 'global.utoc'))) { throw "no ACR Paks at $paks" }
    $view = Join-Path $Dest 'acr\Content\Paks'
    Remove-Item -Recurse -Force $Dest -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $view | Out-Null
    $skipped = @()
    foreach ($f in Get-ChildItem $paks -File) {
        if ($f.Extension -notin '.utoc', '.ucas', '.pak', '.sig') { continue }
        if ($f.BaseName -notmatch $script:VanillaContainer) { $skipped += $f.Name; continue }
        New-Item -ItemType HardLink -Path (Join-Path $view $f.Name) -Target $f.FullName | Out-Null
    }
    Assert-VanillaPaks $view
    $dirs = @(Get-ChildItem $paks -Directory | % Name)
    if ($skipped -or $dirs) { Write-Host "vanilla view: ignoring $(@($skipped).Count) non-vanilla files at top level ($($skipped -join ', ')) and folders: $($dirs -join ', ')" }
    $Dest
}

# Fails if a Paks folder used as a build input holds anything but vanilla containers (or any subfolder, e.g. ~mods).
function Assert-VanillaPaks([string]$Paks) {
    $bad = @(Get-ChildItem $Paks -File | ? { $_.BaseName -notmatch $script:VanillaContainer } | % Name)
    $bad += @(Get-ChildItem $Paks -Directory | % { "$($_.Name)\" })
    if ($bad) { throw "non-vanilla build input in ${Paks}: $($bad -join ', ')" }
    if (-not (Test-Path (Join-Path $Paks 'global.utoc'))) { throw "no global.utoc in $Paks" }
}

# Legacy extraction caches carry a stamp naming the vanilla view they came from; a cache without one (or from another
# source) is not trusted.
function Test-VanillaStamp([string]$Legacy) {
    $s = Join-Path $Legacy '.source.json'
    if (-not (Test-Path $s)) { return $false }
    (Get-Content $s -Raw | ConvertFrom-Json).vanilla -eq $true
}

function Write-VanillaStamp([string]$Legacy, [string]$ViewPaks) {
    New-Item -ItemType Directory -Force $Legacy | Out-Null
    $list = Get-ChildItem $ViewPaks -File -Filter '*.utoc' | % { "$($_.BaseName) $($_.Length)" }
    @{ vanilla = $true; containers = $list; created = (Get-Date).ToString('s') } | ConvertTo-Json | Set-Content (Join-Path $Legacy '.source.json')
}
