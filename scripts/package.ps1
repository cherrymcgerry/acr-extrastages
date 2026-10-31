<#
.SYNOPSIS
    Builds the release and packages it into dist\ACR-Extra-Stages-<version>.zip (+ dist\SHA256SUMS.txt) on Windows, or
    dist/ACR-Extra-Stages-<version>-linux-x64.tar.gz (+ dist/SHA256SUMS-linux-x64.txt) on Linux.

.DESCRIPTION
    The version comes from the VERSION file at the repository root (single source of truth; the .NET projects read it
    through tools\Directory.Build.props). It must match the newest "## [x.y.z]" heading in CHANGELOG.md.

    Zip layout (one top-level folder, ACR-Extra-Stages-<version>\):
        AcrExtraStages.exe                    the launcher (self-contained, single file, win-x64)
        config\                               stages.toml, launcher.toml, sky.toml, visuals.toml, pacenote-map.toml, ...
        tools\acr-ref\acr-ref.exe             self-contained single-file publishes, win-x64
        tools\acr-additive\acr-additive.exe
        tools\dr2-extract\dr2-extract.exe     (needs third_party\: scripts\bootstrap.ps1 runs automatically when missing)
        tools\retoc\retoc.exe                 built from a pinned commit (cargo install --git ... --rev)
        tools\acr-mappings\acr-mappings.exe   cargo build --release
        README.md                             the README without the "For developers" section
        LICENSE-MIT, LICENSE-APACHE, THIRD_PARTY_NOTICES.md, CHANGELOG.md, VERSION
        THIRD_PARTY_NOTICES-SkiaSharp.txt     Linux only: notices of the native libraries inside the launcher

    On Linux (PowerShell 7) the same layout is built for linux-x64, for running the game through Steam + Proton:
    executables have no .exe suffix, the .NET tools are published with invariant globalization (no libicu needed), and
    the archive is a .tar.gz so the executable bits survive. The package is always for the OS it is built on.

    Never in the zip (checked before zipping): Oodle (oo2core*, *oodle*; retoc downloads it on the user's PC), any
    .usmap, game containers or assets, work\, third_party\, .pdb files.

    The cargo target directory is read from `cargo metadata`, so a redirected CARGO_TARGET_DIR works.

.PARAMETER SkipHeavy
    Local smoke test: do not build retoc (a long cargo install from git). The zip is named
    ACR-Extra-Stages-<version>-partial.zip and must never be released.

.PARAMETER RetocExe
    Use this prebuilt retoc.exe instead of building the pinned commit (local testing only; prints a warning).

.PARAMETER SkipPublish
    Reuse the publish output of a previous run (work\package\publish\<name>) instead of publishing the .NET tools again.
#>
[CmdletBinding()]
param(
    [switch]$SkipHeavy,
    [string]$RetocExe,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$env:PATH += [IO.Path]::PathSeparator + (Join-Path $HOME '.cargo/bin')

# The package targets the OS it is built on (the Rust tools are built for the host).
$onLinux = $IsLinux -eq $true
if (-not $onLinux -and -not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'packaging is supported on Windows and Linux only' }
$rid = if ($onLinux) { 'linux-x64' } else { 'win-x64' }
$exe = if ($onLinux) { '' } else { '.exe' }
# Windows keeps debug info in .pdb files (never packaged); on Linux it would be inside the binaries.
if ($onLinux) { $env:CARGO_PROFILE_RELEASE_STRIP = 'debuginfo' }

# retoc (https://github.com/trumank/retoc, MIT only) is built from this exact commit: the one in THIRD_PARTY_NOTICES.md
# (master at the time of writing, 885a8dae740c). To update: change it here and in THIRD_PARTY_NOTICES.md together.
$RetocUrl = 'https://github.com/trumank/retoc'
$RetocCommit = '885a8dae740cb1ce1e41ff2e74f67f9f0c118237'

$root = Split-Path -Parent $PSScriptRoot
# Linux: build from the physical path. On Fedora Atomic (Bazzite, Silverblue) /home is a symlink to /var/home, and
# MSBuild's incremental clean deletes outputs it recorded under the other spelling of the same folder.
if ($onLinux) { $root = (& realpath -- $root).Trim() }
Push-Location $root
try {
    # ---- Version ---------------------------------------------------------------------------
    $version = ([IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "VERSION '$version' is not a SemVer version" }
    $changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
    if ($changelog -notmatch "(?m)^## \[$([regex]::Escape($version))\]") {
        throw "CHANGELOG.md has no '## [$version]' section: update VERSION and CHANGELOG.md together"
    }
    Write-Host "==> ACR Extra Stages $version ($rid)"

    $work = Join-Path $root 'work\package'
    $pubRoot = Join-Path $work 'publish'
    $name = "ACR-Extra-Stages-$version"
    $stage = Join-Path $work $name
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $work, $pubRoot, $dist | Out-Null
    if (Test-Path -LiteralPath $stage) { Remove-Item -Recurse -Force -LiteralPath $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    function Invoke-Checked([string]$what, [scriptblock]$cmd) {
        Write-Host "==> $what"
        & $cmd
        if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
    }

    # Self-contained, single-file publish for $rid; copies everything but .pdb into $dest (expects <exeName>$exe).
    function Publish-DotnetTool([string]$project, [string]$exeName, [string]$dest, [string[]]$extra = @()) {
        $out = Join-Path $pubRoot $exeName
        if (-not $SkipPublish -or -not (Test-Path -LiteralPath (Join-Path $out "$exeName$exe"))) {
            if (Test-Path -LiteralPath $out) { Remove-Item -Recurse -Force -LiteralPath $out }
            Invoke-Checked "dotnet publish $exeName" {
                dotnet publish (Join-Path $root $project) -c Release -r $rid --self-contained true `
                    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
                    -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
                    --nologo -v:minimal -o $out @extra
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $out "$exeName$exe"))) { throw "publish of $project did not produce $exeName$exe" }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        # Linux: no Windows DLLs (dr2-extract's libzstd.dll is Ego-Engine-Modding's; ZstdSharp is managed)
        $skip = if ($onLinux) { '.pdb', '.xml', '.dll' } else { '.pdb', '.xml' }
        foreach ($f in Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Extension -notin $skip }) {
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            Write-Host ("    + {0} ({1:N1} MB)" -f (Join-Path (Split-Path -Leaf $dest) $f.Name), ($f.Length / 1MB))
        }
    }

    # ---- .NET: launcher and tools ----------------------------------------------------------
    # Linux: no libicu dependency (the launcher is already invariant; the tools only handle file names and game data).
    $toolExtra = if ($onLinux) { @('-p:InvariantGlobalization=true') } else { @() }
    Publish-DotnetTool 'tools\launcher\AcrExtraStages.csproj' 'AcrExtraStages' $stage
    Publish-DotnetTool 'tools\acr-ref\AcrRef.csproj'          'acr-ref'        (Join-Path $stage 'tools\acr-ref') $toolExtra
    Publish-DotnetTool 'tools\acr-additive\AcrAdditive.csproj' 'acr-additive'  (Join-Path $stage 'tools\acr-additive') $toolExtra

    # dr2-extract builds against third_party\ (clones at pinned commits + our patches). NuGetAudit is off for the same
    # reason as in scripts\build.ps1 (EgoEngineLibrary pins ImageSharp 2.1.13; used offline on local files only).
    if (-not (Test-Path -LiteralPath (Join-Path $root 'third_party\ego.nefsedit\.git')) -or
        -not (Test-Path -LiteralPath (Join-Path $root 'third_party\Ego-Engine-Modding\.git'))) {
        Invoke-Checked 'scripts\bootstrap.ps1' { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
    }
    Publish-DotnetTool 'tools\dr2-extract\Dr2Extract.csproj' 'dr2-extract' (Join-Path $stage 'tools\dr2-extract') (@('-p:NuGetAudit=false') + $toolExtra)

    # ---- Rust: acr-mappings ----------------------------------------------------------------
    $mapManifest = Join-Path $root 'tools\acr-mappings\Cargo.toml'
    Invoke-Checked 'cargo build --release (acr-mappings)' { cargo build --release --locked --manifest-path $mapManifest }
    $mapMeta = cargo metadata --format-version 1 --no-deps --manifest-path $mapManifest | ConvertFrom-Json
    $mapExe = Join-Path $mapMeta.target_directory "release/acr-mappings$exe"
    if (-not (Test-Path -LiteralPath $mapExe)) { throw "missing $mapExe" }
    $mapDest = Join-Path $stage 'tools\acr-mappings'
    New-Item -ItemType Directory -Force -Path $mapDest | Out-Null
    Copy-Item -LiteralPath $mapExe -Destination $mapDest -Force
    Write-Host ("    + tools/acr-mappings/acr-mappings$exe ({0:N1} MB)" -f ((Get-Item -LiteralPath $mapExe).Length / 1MB))

    # ---- Rust: retoc at the pinned commit --------------------------------------------------
    $retocDest = Join-Path $stage 'tools\retoc'
    if ($RetocExe) {
        if (-not (Test-Path -LiteralPath $RetocExe)) { throw "-RetocExe not found: $RetocExe" }
        Write-Warning "using a prebuilt retoc ($RetocExe), not the pinned commit ${RetocCommit}: do not release this zip"
        $retocSrc = (Resolve-Path -LiteralPath $RetocExe).Path
    }
    elseif ($SkipHeavy) {
        Write-Warning '-SkipHeavy: retoc is not built; the zip is partial and must not be released'
        $retocSrc = $null
    }
    else {
        $retocRoot = Join-Path $work 'retoc-root'
        Invoke-Checked "cargo install retoc_cli @ $($RetocCommit.Substring(0, 12))" {
            cargo install --git $RetocUrl --rev $RetocCommit retoc_cli --root $retocRoot --locked
        }
        $retocSrc = Join-Path $retocRoot "bin/retoc$exe"
        if (-not (Test-Path -LiteralPath $retocSrc)) { throw "missing $retocSrc" }
    }
    if ($retocSrc) {
        New-Item -ItemType Directory -Force -Path $retocDest | Out-Null
        Copy-Item -LiteralPath $retocSrc -Destination (Join-Path $retocDest "retoc$exe") -Force
        Write-Host ("    + tools/retoc/retoc$exe ({0:N1} MB)" -f ((Get-Item -LiteralPath $retocSrc).Length / 1MB))
    }

    # ---- Config, documents -----------------------------------------------------------------
    Copy-Item -LiteralPath (Join-Path $root 'config') -Destination (Join-Path $stage 'config') -Recurse -Force
    Write-Host '    + config\'
    foreach ($f in 'LICENSE-MIT', 'LICENSE-APACHE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'VERSION') {
        $src = Join-Path $root $f
        if (-not (Test-Path -LiteralPath $src)) { throw "missing required file: $f" }
        Copy-Item -LiteralPath $src -Destination (Join-Path $stage $f) -Force
        Write-Host "    + $f"
    }

    # Linux: the launcher bundles SkiaSharp's native library (Skia, FreeType, HarfBuzz, libpng, ...), whose licenses
    # require their notices next to the binary. Taken from the restored package, at the version the csproj pins.
    if ($onLinux) {
        $csproj = [IO.File]::ReadAllText((Join-Path $root 'tools/launcher/AcrExtraStages.csproj'))
        $skiaVersion = [regex]::Match($csproj, 'Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="([^"]+)"').Groups[1].Value
        $nuget = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
        $skiaNotices = Join-Path $nuget "skiasharp.nativeassets.linux.nodependencies/$skiaVersion/THIRD-PARTY-NOTICES.txt"
        if (-not $skiaVersion -or -not (Test-Path -LiteralPath $skiaNotices)) { throw "SkiaSharp third-party notices not found ($skiaNotices)" }
        $noticesDest = Join-Path $stage 'THIRD_PARTY_NOTICES-SkiaSharp.txt'
        Copy-Item -LiteralPath $skiaNotices -Destination $noticesDest -Force
        & chmod 644 $noticesDest
        Write-Host "    + THIRD_PARTY_NOTICES-SkiaSharp.txt (SkiaSharp $skiaVersion)"
    }

    # README.md for players: the repository README up to its "For developers" section.
    $readme = [IO.File]::ReadAllText((Join-Path $root 'README.md')) -replace "`r`n", "`n"
    $cut = $readme.IndexOf("`n## For developers")
    if ($cut -lt 0) { throw "README.md has no '## For developers' section to cut at" }
    $readme = $readme.Substring(0, $cut)
    $repoUrl = 'https://github.com/cherrymcgerry/acr-extrastages'
    # "no release zip yet" note: meaningless inside the zip.
    $readme = [regex]::Replace($readme, '(?ms)^> No release zip has been published yet.*?(?=\n\n)\n\n', '')
    # Links to repository files that are not in the zip point at GitHub instead.
    $readme = $readme.Replace('](CONTRIBUTING.md)', "]($repoUrl/blob/main/CONTRIBUTING.md)")
    $readme = $readme.Replace('](SECURITY.md)', "]($repoUrl/blob/main/SECURITY.md)")
    $readme = $readme.Replace('](tools/launcher/README.md)', "]($repoUrl/blob/main/tools/launcher/README.md)")
    $readme = [regex]::Replace($readme, '(?s)<!--.*?-->', '')
    $readme = $readme.TrimEnd() + "`n`n## Source code`n`nACR Extra Stages is open source; the source code and developer documentation are on GitHub: $repoUrl`n"
    foreach ($bad in '<!--', 'user-attachments', '](CONTRIBUTING.md)', 'No release zip has been published') {
        if ($readme.Contains($bad)) { throw "player README.md still contains '$bad'" }
    }
    [IO.File]::WriteAllText((Join-Path $stage 'README.md'), $readme, [Text.UTF8Encoding]::new($false))
    Write-Host '    + README.md (player section)'

    # ---- Content guard ---------------------------------------------------------------------
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
    $forbidden = '(?i)(^|[\\/])([^\\/]*oo2core|[^\\/]*oodle)[^\\/]*$|\.(usmap|pak|ucas|utoc|uasset|uexp|ubulk|dds|pdb)$|(^|[\\/])(work|third_party)([\\/]|$)'
    $bad = foreach ($f in $files) {
        $rel = $f.FullName.Substring($stage.Length + 1)
        if ($rel -match $forbidden) { $rel }
    }
    if ($bad) { throw "forbidden files in the package (game data, Oodle, usmap, work/third_party):`n  $($bad -join "`n  ")" }

    $required = @("AcrExtraStages$exe", 'config\stages.toml', 'config\launcher.toml',
        "tools\acr-ref\acr-ref$exe", "tools\acr-additive\acr-additive$exe", "tools\dr2-extract\dr2-extract$exe",
        "tools\acr-mappings\acr-mappings$exe", 'README.md', 'LICENSE-MIT', 'LICENSE-APACHE',
        'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'VERSION')
    if ($onLinux) { $required += 'THIRD_PARTY_NOTICES-SkiaSharp.txt' }
    if ($retocSrc) { $required += "tools\retoc\retoc$exe" }
    elseif (-not $SkipHeavy) { throw "retoc$exe missing" }
    foreach ($r in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $r))) { throw "package is missing $r" }
    }

    # ---- Archive + checksums -----------------------------------------------------------------
    $partial = $SkipHeavy -and -not $RetocExe
    if ($onLinux) {
        foreach ($x in @("AcrExtraStages") + ('acr-ref', 'acr-additive', 'dr2-extract', 'acr-mappings', 'retoc' | ForEach-Object { "tools/$_/$_" })) {
            $p = Join-Path $stage $x
            if (Test-Path -LiteralPath $p) { & chmod 755 $p; if ($LASTEXITCODE -ne 0) { throw "chmod $p failed" } }
        }
        $archiveName = "$name-linux-x64$(if ($partial) { '-partial' }).tar.gz"
        $sums = Join-Path $dist 'SHA256SUMS-linux-x64.txt'
    }
    else {
        $archiveName = if ($partial) { "$name-partial.zip" } else { "$name.zip" }
        $sums = Join-Path $dist 'SHA256SUMS.txt'
    }
    $archive = Join-Path $dist $archiveName
    if (Test-Path -LiteralPath $archive) { Remove-Item -Force -LiteralPath $archive }
    if ($onLinux) {
        Invoke-Checked "tar $archiveName" { tar -C $work --owner=0 --group=0 -czf $archive $name }
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    }
    Write-Host "==> wrote $archive"

    $line = "$((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant())  $archiveName"
    [IO.File]::WriteAllText($sums, "$line`n", [Text.UTF8Encoding]::new($false))
    Write-Host "==> wrote $sums"

    $item = Get-Item -LiteralPath $archive
    Write-Host ("{0} ({1:N1} MB)" -f $item.Name, ($item.Length / 1MB))
    if ($onLinux) {
        foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File) {
            Write-Host ("    {0}  ({1:N1} MB)" -f $f.FullName.Substring($work.Length + 1), ($f.Length / 1MB))
        }
    }
    else {
        $z = [IO.Compression.ZipFile]::OpenRead($archive)
        try { $z.Entries | Where-Object { $_.Name } | ForEach-Object { Write-Host ("    {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) } }
        finally { $z.Dispose() }
    }
}
finally {
    Pop-Location
}
