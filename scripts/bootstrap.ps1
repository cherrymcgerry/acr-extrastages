# Clones the third-party libraries tools/dr2-extract builds against into third_party/ at pinned commits.
# Re-running is safe: existing clones are fetched and checked out at the pinned commit.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$thirdParty = Join-Path $root 'third_party'

$repos = @(
    @{ Name = 'ego.nefsedit';       Url = 'https://github.com/EgoEngineModding/ego.nefsedit';       Commit = '163c463e6e0c5792fef1a20eca5e08d8fb2128b9' },
    @{ Name = 'Ego-Engine-Modding'; Url = 'https://github.com/EgoEngineModding/Ego-Engine-Modding'; Commit = 'f3fe9ee0f6e8379c64bc4a29e1d1b586bba5d1c1' }
)

New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
foreach ($repo in $repos) {
    $dir = Join-Path $thirdParty $repo.Name
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        git clone $repo.Url $dir
        if ($LASTEXITCODE -ne 0) { throw "git clone $($repo.Url) failed" }
    }

    git -C $dir cat-file -e "$($repo.Commit)^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        git -C $dir fetch origin
        if ($LASTEXITCODE -ne 0) { throw "git fetch in $dir failed" }
    }

    git -C $dir -c advice.detachedHead=false checkout --quiet $repo.Commit
    if ($LASTEXITCODE -ne 0) { throw "checkout of $($repo.Commit) in $dir failed" }
    Write-Host "$($repo.Name) @ $($repo.Commit)"

    # Local fixes for upstream, tracked in tools/dr2-extract/vendor-patches/<name>-*.patch.
    # Idempotent: a patch that already applies in reverse is considered applied.
    $patchDir = Join-Path $root 'tools\dr2-extract\vendor-patches'
    foreach ($patch in (Get-ChildItem -Path $patchDir -Filter "$($repo.Name)-*.patch" -ErrorAction SilentlyContinue)) {
        git -C $dir -c core.autocrlf=false apply --check --ignore-whitespace -R $patch.FullName 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "  patch already applied: $($patch.Name)"; continue }
        git -C $dir -c core.autocrlf=false apply --ignore-whitespace $patch.FullName
        if ($LASTEXITCODE -ne 0) { throw "applying $($patch.Name) to $dir failed" }
        Write-Host "  patch applied: $($patch.Name)"
    }
}
