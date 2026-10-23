<#
.SYNOPSIS
  Repository content guard: fails when tracked files contain game-derived or oversized content.

.DESCRIPTION
  Uses `git ls-files` (tracked files only; run from anywhere inside the repo). Fails on:
    - forbidden extensions: .usmap .pak .ucas .utoc .uasset .uexp .ubulk .dds
    - .png outside docs/media/
    - any tracked blob larger than 1 MB
  Files that are tracked but deleted in the working tree are checked by their index size.
  Exit code 0 = clean, 1 = violations (listed), 2 = git failure.
#>
param(
    [int]$MaxBytes = 1MB
)
$ErrorActionPreference = 'Stop'
$root = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $root) { Write-Error 'not inside a git repository'; exit 2 }
Set-Location $root

$forbidden = @('.usmap', '.pak', '.ucas', '.utoc', '.uasset', '.uexp', '.ubulk', '.dds')
$violations = [System.Collections.Generic.List[string]]::new()

# `-z` + split on NUL keeps unusual file names intact; `-s` gives "<mode> <object> <stage>\t<path>" so sizes come from the index.
$entries = (git ls-files -s -z) -split "`0" | Where-Object { $_ }
if ($LASTEXITCODE -ne 0) { Write-Error 'git ls-files failed'; exit 2 }

foreach ($e in $entries) {
    $tab = $e.IndexOf("`t")
    $meta = $e.Substring(0, $tab) -split ' '
    $path = $e.Substring($tab + 1)
    $norm = $path.Replace('\', '/')
    $ext = [IO.Path]::GetExtension($norm).ToLowerInvariant()

    if ($ext -in $forbidden) { $violations.Add("forbidden type ($ext): $norm") }
    if ($ext -eq '.png' -and -not $norm.StartsWith('docs/media/')) { $violations.Add("png outside docs/media: $norm") }

    $size = [long](git cat-file -s $meta[1])
    if ($size -gt $MaxBytes) { $violations.Add(('blob > {0:N0} bytes ({1:N0}): {2}' -f $MaxBytes, $size, $norm)) }
}

if ($violations.Count -gt 0) {
    Write-Host "check-repo-content: $($violations.Count) problem(s)" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "check-repo-content: OK ($($entries.Count) tracked files)"
exit 0