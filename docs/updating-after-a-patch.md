# After an ACR update

*Assetto Corsa Rally* is in Early Access and updates often. ACR Extra Stages builds against the
files of the ACR build you have installed, so an update can need a refresh. This page has two parts:

- [For players](#for-players): what to do when ACR updates.
- [For maintainers](#for-maintainers): bringing the tool itself up to date for a new ACR build
  (signatures, the `acr-mappings` pin, the mappings file, the stages).

Related: [troubleshooting.md](troubleshooting.md) and
[tools/acr-mappings/README.md](../tools/acr-mappings/README.md).

## What can break

| Layer | Depends on | Symptom after an ACR update |
|---|---|---|
| Mappings file (`.usmap`) | The engine data layout of the ACR build | The mappings tool refuses to run (`build differs from verified build`), or validation fails, or the other tools cannot read ACR's tables |
| `acr-mappings` signatures | `signatures.toml`: where the engine's globals (GObjects, FNamePool) are found, FProperty layout | As above; the dump finds nothing or reads garbage |
| ACR's own packages | The vanilla tables and levels the stages overlay (`DT_Levels`, `DT_Tracks`, `DT_TracksVariants`, `DT_TracksLocations`, `DT_TracksTextures`, the Hafren South host level) | The stages do not appear, or the game fails to load them. Needs a rebuild; if the package layout changed, the tools may need a code change |
| Our stage files | The above, baked in at build time | Stages built for an older build may not load; remove them |

The stage files are always rebuilt from your own installs. Nothing generated is shared.

## For players

1. **Update the game** through Steam, then update ACR Extra Stages to the latest release if one
   exists for the new ACR build. The releases say which ACR build they were verified on.
2. If the game fails to start or the stage menu is broken, **uninstall first** (menu: Uninstall, or
   `AcrExtraStages.exe uninstall`). That restores the unmodified game.
3. Run **Update** (`AcrExtraStages.exe update`):
   - it notices the new ACR build id and creates a new mappings file (ACR starts briefly, about a
     minute), cached in `work\mappings\<buildId>.usmap`;
   - it re-extracts the vanilla data it overlays;
   - it rebuilds the stages whose inputs changed and re-installs them.
4. To rebuild every stage regardless, run `AcrExtraStages.exe update --force`.
5. If the mappings step reports a build mismatch and no newer release exists yet, you can still
   proceed with a Dumper-7 mappings file for the new build (`--usmap <file>`; see
   [troubleshooting.md](troubleshooting.md#mappings-dump-fails-also-after-a-game-update)).
   Otherwise wait for an updated release.

Report problems with the stage id, the ACR build id (from `steamapps\appmanifest_3917090.acf`,
`buildid`) and the log files described in the troubleshooting page.

## For maintainers

### Ground rules

- Offline only: start Steam in offline mode, stay out of online, Time Attack, Challenge, Event and
  leaderboard screens while anything touches the game.
- `acr-mappings` is read-only (`PROCESS_VM_READ | PROCESS_QUERY_INFORMATION`). Dumper-7, if you use
  it, injects into the game: use it only offline, and never ship or commit its output.
- Do not commit `.usmap` files, memory dumps, or anything generated from the game.

### 1. Find the new build id and triage

```powershell
Select-String -Path "<Steam>\steamapps\appmanifest_3917090.acf" -Pattern '"buildid"'
AcrExtraStages.exe preflight        # shows the ACR build and whether a validated mappings file exists
acr-mappings dump --game "<ACR>"    # fails fast with a build mismatch if signatures.toml is old
```

`acr-mappings` compares the installed build with `verified_build` in its `signatures.toml`. The
possible outcomes:

| Outcome | Meaning | Next step |
|---|---|---|
| Dump and `validate` pass | Signatures still hold | Step 4 (update `verified_build` if you want it recorded) |
| Refuses: build mismatch | Signatures not verified for this build | Try `--allow-build-mismatch`; if it works and validates, go to step 2 to record it |
| Dump runs but finds nothing / wrong values | An engine global moved | Step 2 |
| Dump passes, `validate` fails | Type or property layout of ACR's tables changed | Step 3 |

### 2. Update the signatures (acr-rewind) and the `acr-mappings` pin

The engine-global signatures are shared with
[acr-rewind](https://github.com/cherrymcgerry/acr-rewind): `acr-mappings` builds on its `acr_ue`
crate and uses acr-rewind's `config/signatures.toml`.

1. In acr-rewind, bring `config/signatures.toml` up to date for the new build. Its
   [`docs/updating-after-a-patch.md`](https://github.com/cherrymcgerry/acr-rewind/blob/main/docs/updating-after-a-patch.md)
   is the full procedure (`acr-probe sig-test`, importing Dumper-7 offsets, `verified_build`).
   For the mappings tool, the engine globals (`[raw_offsets]`: GObjects, FNamePool, and the
   `uobject` / FField / FProperty layout) are what matter. The rewind-specific parts (rigid
   bodies, car UFunctions) are not needed by `acr-mappings`.
2. Commit that to acr-rewind and note the commit hash.
3. In this repo, bump the pin in `tools/acr-mappings/Cargo.toml`:

   ```toml
   acr_ue = { git = "https://github.com/cherrymcgerry/acr-rewind.git", rev = "<new commit>" }
   ```

4. Copy the file over `tools/acr-mappings/signatures.toml`. A unit test checks the copy matches a
   sibling `..\acr-rewind` checkout.
5. Rebuild and test:

   ```powershell
   cargo build --release --manifest-path tools/acr-mappings/Cargo.toml
   cargo test --manifest-path tools/acr-mappings/Cargo.toml
   ```

   To test against a local acr-rewind checkout without editing `Cargo.toml`, see "Local acr-rewind
   checkout" in the [acr-mappings README](../tools/acr-mappings/README.md).

If the FProperty layout itself changed (an engine upgrade, not just a patch), the fixed offsets in
`tools/acr-mappings/src/walk.rs` (`PropLayout`: `sizeof(FProperty)`, the array inner offset,
`UEnum::Names`) need checking against Dumper-7's `Basic.hpp`. That is a code change.

### 3. Regenerate the `.usmap` and validate it

```powershell
dotnet publish tools/acr-additive -c Release -o work/bin/acr-additive      # `validate` needs it
acr-mappings dump --game "<ACR>" --force                                    # new work\mappings\<buildId>.usmap
acr-mappings validate --game "<ACR>" --tool work\bin\acr-additive\acr-additive.exe
acr-mappings diff <old.usmap> <new.usmap>                                   # what changed in the types
```

- **`validate`** must finish with no exceptions and no CUE4Parse "could not read correctly" warnings.
  It loads the five vanilla DB tables and the Hafren South host packages from the unmodified game
  (the `~mods` folder is never read; use `--vanilla <dir>` for a mods-free copy).
- **`diff`** compares two usmaps (`--strict` makes differences an error). Differences that matter
  for us are in the types the tools read: `DBTrack`, `DBTrackVariant`, `BakedDataContainer`,
  `Actor`, `SoftObjectPath` (the key types the dump itself checks) and the row types of the other
  DB tables. If a property was added, removed
  or retyped there, the table writers in `tools/acr-additive` and `tools/acr-ref` need a code change.
- If the automatic route cannot be fixed yet, a Dumper-7 usmap for the new build still works for
  everything: `--usmap <file>` / `ACR_USMAP`.

### 4. Rebuild the stages if the package layout changed

The launcher rebuilds stages whose inputs changed. Rebuild **everything** when ACR changed the
packages the stages overlay (new or moved levels, tables or assets, a changed Hafren South host
level, a new container format, an engine version bump):

```powershell
AcrExtraStages.exe update --force                  # all selected stages, from scratch
AcrExtraStages.exe install --only AcrXsNz01r5 --force   # one stage while testing
```

Then check:

- `work\acr\batch\report.md`: stages built, failures, advisory WARN lines.
- `acr-additive verify` ran for the registry (the launcher does this; add `--roundtrip` to also
  round-trip every table).
- A game start: the stages appear in the stage menus, one loads, and the game stays up. Offline only.

If a stage fails the build with a hard `FAIL`, see
[troubleshooting.md](troubleshooting.md#a-stage-build-says-fail-advisory-versus-hard-failures). If the
failure is in a table or package round trip after an ACR update, the package layout changed and the
tool code in `tools/acr-ref` or `tools/acr-additive` needs updating. The notes in
[acr-stage-anatomy.md](acr-stage-anatomy.md) describe the layout the stage builder assumes.

### 5. Record the result

- Set `verified_build` in `signatures.toml` (both copies) to the new build id.
- Add a line under `[Unreleased]` in [CHANGELOG.md](../CHANGELOG.md): the ACR build now supported.
- Update the README's "Known limitations" and the release notes if behaviour changed.
- Run the [release checklist](release-checklist.md) before publishing a new release.
