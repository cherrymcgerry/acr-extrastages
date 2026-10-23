# Troubleshooting

For `AcrExtraStages.exe` (the launcher). The basics are in the [README](../README.md); this page
covers what usually goes wrong and how to get unstuck.

**Safe to retry.** You can stop the launcher at any time, or run it again after an error. It skips
data that is already prepared and containers whose inputs have not changed, so a re-run continues
where it stopped.

**First, look here:**

| File (relative to the launcher folder) | What it has |
|---|---|
| `work\acr\batch\report.md` | Per-stage result of the last run (built, failed, WARN lines) |
| `work\acr\batch\logs\<id>.log` | Build log for one stage, e.g. `AcrXsNz01r5.log` |
| `work\acr\batch\logs\prepare-<id>.log` | The DR2 data extraction for one stage |
| `work\acr\batch\logs\registry.log` | The stage-menu registry step |
| `work\acr\batch\logs\launcher.log` | The launcher's own log |

Run `AcrExtraStages.exe preflight` for a read-only health check (it never starts a game), and add
`--verbose` to any command to see the helper tools' output live.

When you report a problem, include the stage id, the ACR build id and the relevant log lines, with
personal paths removed. Never attach game files or generated stage files.

---

## Not enough disk space

Symptom: preflight stops with `not enough free space on X: ... GB free, about N GB needed`.

- The estimate comes from `config\launcher.toml`. Building all 30 stages needs roughly **36 GB free**
  (about 20 GB for the finished stage files, about 15 GB working peak, 2 GB margin). Fewer stages
  need proportionally less: use **Choose stages**, or `--only <id,...>`.
- The check is made on the drive that holds the launcher's `work\` folder. That must be the
  **same drive as the game** (see [hard links](#hard-links-and-the-same-drive-requirement)).
- Free space by deleting regenerable data from an earlier run: the `work\` folder (extracted DR2
  data and intermediate files). The built containers and the mappings cache under `work\` are
  needed only for the next **Update**; they can be rebuilt. Developers: `scripts\clean-work.ps1`
  prints what could be deleted (`-Force` deletes).
- `--ignore-disk-space` turns the error into a warning. Use it only if you know the estimate is
  too high for your selection; a full disk mid-build leaves a partial stage that the next run
  rebuilds.
- Space is released stage by stage while building, so a failed or interrupted run does not need
  the full peak again.

## Steam and game folders not found

Symptom: `Assetto Corsa Rally: not found` or `DiRT Rally 2.0: not found`, or the wrong copy picked.

- The launcher looks (in order) at: a folder given on the command line, the saved setting in
  `work\launcher-settings.json`, then Steam: the registry keys, `steamapps\libraryfolders.vdf`
  and the `appmanifest_<id>.acf` of ACR (app 3917090) and DR2 (app 690790), then the default folders.
- Use **Game folders** in the menu, or pass `--acr "<dir>"` and `--dr2 "<dir>"`. You can also set
  `ACR_GAME_DIR` and `DR2_DIR`.
- What counts as a valid folder:
  - ACR: the folder that contains `acr\Content\Paks` (usually `...\steamapps\common\Assetto Corsa Rally`);
  - DR2: the folder that contains `dirtrally2.exe` and `game\game.dat`.
- A non-Steam DR2 copy works if you point to it. It will show `DR2 build: n/a`; that is normal.
- Only the Steam release of ACR is tested. Without `steamapps\appmanifest_3917090.acf` next to the
  game, the ACR build id is unknown and the mappings tool cannot cache by build (warning in preflight).
- If you moved a Steam library, delete `work\launcher-settings.json` (or fix the folders in the
  menu) so a stale path is not reused.
- "Assetto Corsa Rally is running: close the game first": close it, and check Task Manager for a
  leftover `acr-Win64-Shipping.exe` or `acr.exe` process.

## Mappings dump fails (also after a game update)

The first install (and the first one after each ACR update) creates a mappings file, the `.usmap`
that tells the tools how ACR's data is laid out. `acr-mappings.exe` starts ACR through Steam, reads
its engine data from outside the process (read-only, nothing is injected or written to the game),
and closes the game. It takes about 30 to 60 seconds, mostly game start-up. The result is cached in
`work\mappings\<buildId>.usmap`.

Common failures:

- **"build ... differs from the verified build" / refuses to run.** After an ACR update the tool's
  built-in signatures no longer match the new build. Update to a newer release of ACR Extra
  Stages, which carries updated signatures. Until then, use the Dumper-7 fallback below. Developers
  and testers: see [updating-after-a-patch.md](updating-after-a-patch.md).
- **The game did not start / timed out.** Steam must be running, logged in and able to start ACR
  unattended (no launcher dialog, no pending update, no "play offline?" prompt). Start Steam and
  the game once by hand, quit, and retry. Anti-cheat or overlay tools that block external readers
  can also stop it.
- **Validation failed.** The tool checks that CUE4Parse can read ACR's tables and the Hafren South
  packages with the new file. A failure here usually means a mismatched game build or a game
  update in progress. Let Steam finish updating, then retry with `mappings --dump` (it asks before
  starting ACR; `--force` is in the `acr-mappings` tool, see
  [tools/acr-mappings/README.md](../tools/acr-mappings/README.md)).
- **Close ACR yourself if the tool could not.** If ACR is already running, the tool attaches to
  it and leaves it running. Otherwise it closes the game after the dump (a forced kill after 60 s).
- **Fallback: supply your own file.** Generate a `.usmap` with
  [Dumper-7](https://github.com/Encryqed/Dumper-7) against the running game (offline only, never in
  an online session) and pass it with `--usmap <file>`, or set `ACR_USMAP`. The launcher also
  prompts for it when the automatic route fails. It is saved in `work\launcher-settings.json`.

If the stages stopped appearing in the menu after an ACR update, the likeliest cause is an old
mappings file or old stage files: run **Update** (new mappings are made automatically when the build
id changed), or uninstall and reinstall.

## Antivirus flags `acr-mappings.exe`

`acr-mappings.exe` opens the running game's process to read memory (`PROCESS_VM_READ` and
`PROCESS_QUERY_INFORMATION` only), scans it for engine structures and starts and stops a game.
That behaviour looks like a game cheat to some antivirus products, so a false positive is
possible, particularly with heuristic or reputation-based scanners.

- It is **read-only**: no injection, no DLL, no thread creation, nothing written to the game. The
  source is in `tools\acr-mappings` in the repository, and you can build the exe yourself
  (`cargo build --release --manifest-path tools/acr-mappings/Cargo.toml`).
- Each release lists the SHA-256 of the zip in `SHA256SUMS.txt`; compare it with
  `Get-FileHash .\ACR-Extra-Stages-<version>.zip`. Release notes link a VirusTotal report for the
  executables.
- If your antivirus quarantines it, restore it and add an exclusion for the launcher folder (or
  just the `tools\acr-mappings` folder), or skip the tool and use the Dumper-7 route above
  (`--usmap`). Do not disable your antivirus globally.
- Preflight shows `acr-mappings.exe not found` when it was quarantined: the launcher then needs a
  `--usmap` file.
- Anti-cheat software for other games running at the same time can also block the read. Close it.

## Hard links and the same-drive requirement

Symptom: preflight error `the work folder (...) must be on the same drive as ACR`.

The build reads the game's containers through **hard links** (no copy of the game is made) and the
installer links the finished stage files into `~mods`, so they take no extra space. Hard links only
work inside one NTFS volume.

- Move or re-extract the launcher folder to the **same drive as the game**, for example
  `D:\ACR-Extra-Stages` when ACR is on `D:`. The `work\` folder lives inside the launcher folder.
- The game's drive must be **NTFS**. exFAT or FAT32 drives, network shares and some synced or
  virtual folders do not support hard links.
- The `~mods` folder on the game's drive and the launcher folder must be on the same volume, or the
  installer falls back to **copying** (preflight says `copies` instead of `hard links (no extra
  space)`). Copies need the extra ~20 GB. `--copy` forces copying.
- Junctions or symlinks that move the game to another disk count as the other volume. Use the real
  path of the game folder.

## Oodle download fails

Unreal Engine 5 packages are Oodle-compressed, so the helper tools need the Oodle library. It is
**not** shipped in the release (see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)), so the
tools fetch it on first use. That is the only step that needs internet.

Symptoms: `could not download the Oodle runtime` (`acr-ref` or `acr-additive`), or `retoc`
failing to fetch `oo2core_9_win64.dll`, usually at the first build step or at validation.

- Check that the PC is online (once), and that a firewall, proxy or antivirus does not block the
  tools from connecting to GitHub (the library comes from
  [WorkingRobot/OodleUE](https://github.com/WorkingRobot/OodleUE)).
- The libraries are saved **next to each tool**: `oo2core_9_win64.dll` next to `retoc.exe`, and
  `oodle-data-shared.dll` next to `acr-ref.exe` and `acr-additive.exe`. A partial or empty download
  of one of them can cause repeated failures: delete the file and retry.
- Offline or blocked: put your own copy of the library next to each tool, under the file name above.
  The tools use it instead of downloading. Do not share or commit it; it is Oodle's software.
- Permissions: install the launcher in a folder you can write to (not `C:\Program Files`).

## A stage build says FAIL (advisory versus hard failures)

Every built stage is re-read and checked before installing; the build log (`work\acr\batch\logs\<id>.log`)
lists each check as `PASS`, `WARN` or `FAIL`.

- **`WARN`** is an *advisory* result: a check about how closely the drawn ground and decoration
  follow DR2's collision mesh (visual quality: road corridor posts, drawn ground versus collision
  ground, ground raycasts at gates, vista sections, triangle counts and similar). These checks were
  tuned on New Zealand and fail on rocky stages without a real defect. A WARN does **not** fail the
  build. The stage is installed and listed with its warnings in `work\acr\batch\report.md`. The
  list lives in `config\visuals.toml`, `[checks] advisory`.
- **`FAIL`** is a *hard* failure: package round trips, indices, sections, tables, or collision
  physics. The stage is **skipped** (not installed), the run goes on with the other stages, and the
  report lists it. Hard failures are kept strict on purpose, because they can make the game fail to
  load a stage.
- What to do:
  1. Read the last lines of `work\acr\batch\logs\<id>.log` for the failing check.
  2. Re-run the stage alone: `AcrExtraStages.exe install --only <id> --force` (`--force` rebuilds and
     re-prepares even when inputs are unchanged).
  3. A stage whose DR2 data could not be prepared is tried twice at most, then marked in
     `work\ir\<id>\prepare-failed.json`; `--force` clears that and tries again.
  4. If it still fails, deselect the stage and report the id with the log lines.
- Very long stages are deferred by default (`--max-length-km`, default 9). Raise it if you want them.
- A stage that passes with warnings may look plainer or have small ground differences; see
  Known limitations in the [README](../README.md).

## Uninstall

- Menu: **Uninstall**; or `AcrExtraStages.exe uninstall`. It deletes only the `zz_acrxs_*` files
  (and legacy `zz_acrdirt_*` files, see below) from `<ACR>\acr\Content\Paks\~mods\`. Nothing else
  is touched, and the game's own files are never modified.
- By hand: delete every file starting with `zz_acrxs_` from that folder. If `~mods` is then empty
  you can leave it.
- To get the disk space back, delete the launcher folder's `work\` folder (or the whole launcher
  folder). Keep `work\mappings` if you want to avoid regenerating the mappings file.
- If the game fails to start after an update, remove the `zz_acrxs_*` files first: that restores the
  unmodified game. Verify the game's files in Steam if in doubt.
- `AcrExtraStages.exe uninstall --dry-run-mods <dir>` runs against another folder instead (for
  testing).

## Legacy `zz_acrdirt_*` files

Earlier development builds used the names `zz_acrdirt_*` (and a different internal package path)
instead of `zz_acrxs_*`. If you used one of those, remove them so ACR does not load two versions of
the same stages:

- Run **Uninstall** (or `AcrExtraStages.exe uninstall`): it removes `zz_acrxs_*` **and** the legacy
  `zz_acrdirt_*` files. A fresh **Install** also removes the legacy files first.
- By hand: delete every `zz_acrdirt_*` and `zz_acrxs_*` file from
  `<ACR>\acr\Content\Paks\~mods\`.
- The old stages use different ids than the current ones (`AcrXs...`), so there is nothing to
  migrate; rebuild the stages with the launcher.

## Still stuck

Open an [issue](https://github.com/cherrymcgerry/acr-extrastages/issues) with the stage id, ACR
build, what you did and the log lines (see the top of this page). Do not attach game files or
generated stage files.
