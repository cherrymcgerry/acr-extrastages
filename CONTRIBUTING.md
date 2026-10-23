# Contributing to ACR Extra Stages

Thanks for helping out! Bug reports, tooling fixes and stage-quality improvements are welcome.
End users do not need anything in this document: see [README.md](README.md).

## Policies

### No game files in the repository or in pull requests

ACR Extra Stages converts content from *DiRT Rally 2.0* and *Assetto Corsa Rally*. That content
is not ours and must never enter this repository:

- No DR2 or ACR assets: meshes, textures (including `.png`/`.dds` derived from game art such as
  loading screens), audio, `.pak`/`.ucas`/`.utoc`/`.uasset` files, extracted data, or screenshots
  that show game art at a size that could be reused.
- No `.usmap` mappings (they are generated from the game), no Oodle libraries (`oo2core*`), no
  memory dumps, and no logs containing personal paths.
- No generated stage files. `work/` and `third_party/` are git-ignored on purpose; the generated
  stage specs live in `work/acr/specs/`.

`scripts/check-repo-content.ps1` enforces this for tracked files (forbidden extensions, `.png`
outside `docs/media/`, any file over 1 MB). Run it before you open a pull request:

```powershell
pwsh -NoProfile -File scripts\check-repo-content.ps1
```

Stage names, location descriptions and pace-note mappings in `config/` are our own text and data;
keep them that way (don't paste descriptions or call lists from the games or their manuals).

### Offline-only policy for generated content

Generated stages are for **offline, single-player** play. Contributions must keep it that way:

- Don't add features aimed at multiplayer, leaderboards or competitive modes, or at hiding the
  stages from the game or from anti-cheat.
- Don't add tooling that redistributes generated paks (uploaders, share links, "stage pack"
  builds). The product is the converter; the output stays on the user's PC.
- When testing, start Steam in offline mode and stay out of online, Time Attack, Challenge,
  Event and leaderboard screens while generated stages are installed.

## Building from source

Requirements: Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), Git, a
Rust toolchain from [rustup](https://rustup.rs/) (to install retoc), PowerShell 7 (`pwsh`), and
your own installs of *Assetto Corsa Rally* (Steam) and *DiRT Rally 2.0*.

1. **Bootstrap the third-party clones** (pinned commits, plus our local patches in
   `tools/dr2-extract/vendor-patches/`):

   ```powershell
   pwsh -NoProfile -File scripts\bootstrap.ps1
   ```

   This clones `ego.nefsedit` and `Ego-Engine-Modding` into `third_party/` (git-ignored).

2. **Build the DR2 extractor** (`tools/dr2-extract`):

   ```powershell
   pwsh -NoProfile -File scripts\build.ps1
   ```

3. **Install retoc** (reads and writes ACR's IoStore containers) into `work\bin`:

   ```powershell
   $env:Path += ";$HOME\.cargo\bin"
   cargo install --git https://github.com/trumank/retoc retoc_cli --root work\bin
   ```

   The build scripts expect `work\bin\bin\retoc.exe`. retoc fetches the Oodle library it needs
   by itself on first run (`oo2core_9_win64.dll` next to `retoc.exe`); that file is Oodle's and
   must never be committed or redistributed.

4. **Generate a `.usmap` for ACR** from your own game with
   [Dumper-7](https://github.com/Encryqed/Dumper-7) (the UE 5.6 mappings file it writes under
   `Mappings\`). It is generated from the game, so it is never committed. Point the tools at it
   and at the game:

   ```powershell
   $env:ACR_USMAP = 'C:\Dumper-7\5.6.1-0+UE5-acr\Mappings\5.6.1-0+UE5-acr.usmap'
   $env:ACR_GAME_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally'
   $env:DR2_DIR = 'C:\Games\DiRT Rally 2.0'
   ```

   Dumper-7 injects into the running game: use it with Steam offline and never in an online
   session.

5. **Prepare, build and install stages**:

   ```powershell
   pwsh -NoProfile -File scripts\prepare-stage-data.ps1   # DR2 data -> work\ir (long)
   pwsh -NoProfile -File scripts\build-stages.ps1         # specs -> work\acr\specs, containers -> work\acr\additive\dist
   pwsh -NoProfile -File scripts\install-stages.ps1       # into <ACR>\acr\Content\Paks\~mods (-Uninstall removes)
   ```

   Which stages are built is chosen in `config/stages.toml`. The generated stage specs are
   written to `work/acr/specs/` by `scripts/gen-stage-specs.ps1`; they are build output, not
   source. The steps are documented in `docs/additive-batch-test.md`, `docs/additive-plan.md` and
   `docs/decisions.md`.

   **Disk.** All 30 stages need about 20 GB of stage files plus about 15 GB of working space at the
   peak, if you let the batch prepare each stage right before it is built (it deletes the stage's
   heavy IR and the DR2 location cache as it goes):

   ```powershell
   pwsh -NoProfile -File scripts\estimate-disk.ps1                          # the preflight numbers (-Json for the launcher)
   pwsh -NoProfile -File scripts\build-stages.ps1 -PrepareOnDemand          # prepare + build + prune, one stage at a time (~36 GB free)
   pwsh -NoProfile -File scripts\clean-work.ps1                             # dry run: what regenerable data could be deleted (-Force deletes)
   ```

   Preparing everything first (`prepare-stage-data.ps1`, as above) keeps ~50 GB of render data plus
   up to ~58 GB of extracted DR2 location archives on disk (about 115 GB working peak). Details and
   measurements: `docs/resume-notes.md`, "Disk budget".

## Repository layout

| Path | Purpose |
|---|---|
| `config/` | Our own stage list, location text, sky, visual and pace-note mappings. |
| `scripts/` | PowerShell build, prepare and install scripts. |
| `mods/` | Per-mod build scripts (`additive`, `stage`, `loadproof`); `mods/common` has shared helpers. |
| `tools/dr2-extract` | DR2 reader/exporter (uses `ego.nefsedit` and `Ego-Engine-Modding`). |
| `tools/acr-ref` | ACR reader and stage builder (CUE4Parse based). |
| `tools/acr-additive` | Additive registry and menu-table writer. |
| `tools/stage-validate` | Optional, development-only telemetry check (Rust; see below). |
| `docs/` | Design notes and test checklists. |
| `work/` | Git-ignored: all extracted data, build output and generated specs. |

## Optional: `tools/stage-validate`

A small Rust tool that records ACR's shared-memory telemetry while you drive a ported stage and
checks the run against the stage IR. It is **development-only**: it is not part of any release
and is not needed to build or install stages. It depends on the `acr_shm` crate from the public
[acr-rewind](https://github.com/cherrymcgerry/acr-rewind) repository through a pinned git
dependency, so no sibling checkout is required:

```powershell
cd tools\stage-validate
cargo build --release
```

To use a newer `acr_shm`, bump the `rev` in `tools/stage-validate/Cargo.toml` and commit the
updated `Cargo.lock`.

## Before you open a pull request

- Run `scripts\check-repo-content.ps1` (it must pass).
- Add a line under `[Unreleased]` in `CHANGELOG.md` for user-visible changes.
- If you add or update a dependency, update [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) with
  its license, verified from the upstream repository or package, not from memory.
- For in-game changes, describe how you tested (ACR build, stage, offline mode) and attach the
  relevant log lines without personal paths.

## License

By contributing, you agree that your contributions are dual-licensed under MIT OR Apache-2.0,
like the rest of the project.