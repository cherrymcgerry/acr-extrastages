# AcrExtraStages.exe (launcher)

The end-user front end: one self-contained, single-file Windows exe that replaces the PowerShell orchestration
(`scripts/prepare-stage-data.ps1`, `scripts/gen-stage-specs.ps1`, `scripts/build-stages.ps1`, `mods/additive/build.ps1`,
`scripts/menu-location-logos.ps1`, `scripts/prepare-menu-art.ps1`, `scripts/install-stages.ps1`). Users need no
PowerShell 7, .NET SDK, Rust or git. The scripts stay for developers.

It runs the bundled tools as child processes only: `acr-ref`, `acr-additive`, `dr2-extract`, `retoc` and `acr-mappings`.

## Publish

```powershell
dotnet publish tools/launcher/AcrExtraStages.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o work/bin/launcher
```

Output: `work/bin/launcher/AcrExtraStages.exe` (about 37 MB, compressed single file, no runtime needed).

## Layout it expects

Release zip (what `scripts/package.ps1` should produce):

```text
AcrExtraStages.exe
config\*.toml                     stages.toml, launcher.toml, visuals.toml, ...
tools\acr-ref\acr-ref.exe         self-contained publish of each .NET tool
tools\acr-additive\acr-additive.exe
tools\dr2-extract\dr2-extract.exe
tools\retoc\retoc.exe             retoc fetches oo2core_9_win64.dll next to itself on first use (never shipped)
tools\acr-mappings\acr-mappings.exe
work\                             created on first run (settings, caches, logs, built containers)
```

The root (the folder with `config\` and `work\`) is the first folder at or above the exe that has `config\stages.toml`,
else the current folder (`--root` overrides). Each tool is looked up in `tools\<name>\<name>.exe` (or `tools\`, `bin\`)
next to the exe and under the root, then in the developer builds: `tools\dr2-extract\bin\Release\net10.0`,
`work\bin\<name>`, `work\bin\bin` (retoc) and `tools\acr-mappings\target\release`. `--tools <dir>` adds a folder first.
In a repo checkout the published exe in `work\bin\launcher` therefore uses `work\` and the developer tool builds; it
does not build the tools itself (run `scripts\build-stages.ps1` once, or the `dotnet build` lines in CONTRIBUTING.md).

## What it does

Double-click: a console menu (Install / Update / Uninstall / Choose stages / Check / Game folders / Exit). The same
steps run non-interactively as commands; `AcrExtraStages.exe --help` lists them.

1. **Detect**: ACR (app 3917090) and DR2 (app 690790) from the Steam registry keys, `steamapps\libraryfolders.vdf` and
   `appmanifest_<id>.acf`; then `ACR_GAME_DIR` / `DR2_DIR` and the default folders; else asks. Saved in
   `work\launcher-settings.json` with the stage selection.
2. **Preflight**: free space on the work drive (estimate from `config\launcher.toml`), the work folder on the same
   drive as ACR (the build reads the game containers through hard links), build ids, ACR not running, tools and config
   present, mappings ready (path check only).
3. **Mappings** (tools/acr-mappings/README.md, "Launcher integration"): `acr-mappings path`, else confirm and
   `dump` + `validate --tool <acr-additive>`, else `--usmap` / `ACR_USMAP` / the saved file / a prompt for a Dumper-7 usmap.
   The result is passed to every tool as `ACR_USMAP`.
4. **Prepare** each selected stage (`dr2-extract stage-ir --render --bake-ground`, `stage-textures`), skipping
   complete ones, two attempts at most (`work\acr\batch\prepare-attempts.json`, `work\ir\<id>\prepare-failed.json`),
   and the DR2 loading art once.
5. **Specs** into `work\acr\specs` (same JSON as `gen-stage-specs.ps1`), the vanilla hard-link view, the legacy
   extractions (`work\acr\additive\legacy`, `work\acr\legacy-vanilla`, re-extracted after a game update) and
   `acr-ref stage-fit` (long stages are deferred).
6. **Build** each stage (stage-build `--additive --bounds`, Hafren overlay, `relocate`, Tex/Mat, `menu-assets`,
   `retoc to-zen`), one at a time, skipping stages whose inputs are unchanged (`work\acr\batch\state\<id>.json`) and
   continuing past failures (log per stage in `work\acr\batch\logs\<id>.log`).
7. **Registry** for the stages that passed (`registry`, generated location logos, `menu-locations`, `to-zen`), then
   **verify** with `acr-additive verify` (fast path; `--roundtrip` also converts the registry back and compares
   every table). Writes `work\acr\batch\report.md`, `report.json` and `registry-ids.json`.
8. **Install**: registry + built containers into `<ACR>\acr\Content\Paks\~mods` as hard links (copies across drives
   or with `--copy`). **Uninstall** removes `zz_acrxs_*` and the legacy `zz_acrdirt_*` files, nothing else.

Interrupting is safe: the next run skips prepared data and unchanged containers.

## Developer smoke test (no game install)

```powershell
work\bin\launcher\AcrExtraStages.exe preflight
work\bin\launcher\AcrExtraStages.exe mappings
work\bin\launcher\AcrExtraStages.exe install --only AcrXsNz01r5 --dry-run-mods $env:TEMP\acrxs-mods --yes
work\bin\launcher\AcrExtraStages.exe uninstall --dry-run-mods $env:TEMP\acrxs-mods
```

`--dry-run-mods <dir>` installs into / uninstalls from that folder instead of the game's `~mods`. Building still
writes to `work\` (the same files `scripts\build-stages.ps1` writes, including the registry and the batch report).
