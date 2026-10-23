<#
.SYNOPSIS
  Installs (or removes) the additive stage mods in the game's ~mods folder: the registry plus the stage containers named in
  work/acr/batch/registry-ids.json (written by scripts/build-stages.ps1). Only vanilla-based files from work/acr/additive/dist are installed.

  Files are installed as NTFS hard links (no extra disk space) when dist and ~mods are on the same volume; otherwise they are copied.
  NOTE: after rebuilding stages (scripts\build-stages.ps1 / mods\additive\build.ps1) re-run this script. The build deletes a
  dist file before writing its replacement, so an installed link keeps the OLD content (never a half-written file) until reinstalled.

.PARAMETER Uninstall  remove every zz_acrxs_* file from ~mods AND the legacy zz_acrdirt_* files of earlier installs (so old installs migrate); other files are never touched
.PARAMETER Copy       force Copy-Item instead of hard links
.PARAMETER Game       ACR install dir (default $env:ACR_GAME_DIR or the Steam path)
#>
param(
    [switch]$Uninstall,
    [switch]$Copy,
    [string]$Game = $(if ($env:ACR_GAME_DIR) { $env:ACR_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$mods = Join-Path $Game 'acr\Content\Paks\~mods'
$dist = Join-Path $root 'work\acr\additive\dist'
$ids = Join-Path $root 'work\acr\batch\registry-ids.json'

if (Test-Path $mods) {
    # current containers plus the legacy zz_acrdirt_* name (pre-rename installs), so reinstalling migrates cleanly
    $old = @('zz_acrxs_*', 'zz_acrdirt_*' | % { Get-ChildItem $mods -File -Filter $_ })
    if ($old) { Write-Host "removing $($old.Count) existing zz_acrxs_* / legacy zz_acrdirt_* file(s) from $mods"; $old | Remove-Item -Force }
}
if ($Uninstall) { Write-Host 'uninstalled'; return }

if (-not (Test-Path $ids)) { throw "no $ids - run scripts\build-stages.ps1 first" }
$reg = Get-Content $ids -Raw | ConvertFrom-Json
if (-not $reg.built -or -not @($reg.ids).Count) { throw "the last batch has no usable registry ($($reg.error))" }
New-Item -ItemType Directory -Force $mods | Out-Null
$files = @(Get-ChildItem $dist -File -Filter 'zz_acrxs_registry_P.*')
foreach ($id in $reg.ids) { $files += Get-ChildItem $dist -File -Filter "zz_acrxs_s_$($id.ToLowerInvariant())_P.*" }
if (-not $files) { throw "nothing to install in $dist" }
$sameVolume = (Split-Path -Qualifier (Resolve-Path $dist).Path) -eq (Split-Path -Qualifier (Resolve-Path $mods).Path)
$mode = 'copied'
if ($sameVolume -and -not $Copy) {
    $mode = 'hard-linked'
    try {
        foreach ($f in $files) { New-Item -ItemType HardLink -Path (Join-Path $mods $f.Name) -Target $f.FullName | Out-Null }
    }
    catch {
        Write-Warning "hard link failed ($($_.Exception.Message)); falling back to copy"
        $mode = 'copied'
        $files | % { Remove-Item (Join-Path $mods $_.Name) -Force -ErrorAction SilentlyContinue }   # drop any partial links first
        $files | Copy-Item -Destination $mods -Force
    }
}
else { $files | Copy-Item -Destination $mods -Force }
$mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "installed ($mode) registry + $(@($reg.ids).Count) stage container(s), $mb MB, into $mods"
if ($mode -eq 'hard-linked') { Write-Host 'note: after rebuilding stages, re-run this script (links keep the old content until reinstalled)' }
Write-Host 'stages: ' ($reg.ids -join ', ')
Write-Host 'rollback: scripts\install-stages.ps1 -Uninstall'
