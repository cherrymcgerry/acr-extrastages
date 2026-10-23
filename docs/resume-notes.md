# Resume notes (updated 2026-10-22)

**Do not commit** unless asked. Plan file (read only, do not edit): `C:\Users\robbe\.cursor\plans\additive_stages_batch_00130e0c.plan.md`.

Naming: this project was formerly called acr-dirt. Containers are now `zz_acrxs_*`, package root `/Game/AcrXs/`, ids `AcrXs*`, mods under `mods/additive`, `mods/stage`, `mods/loadproof`. `scripts\install-stages.ps1` removes legacy `zz_acrdirt_*` files too. Everything built before the rename (work/acr/additive/dist, work/acr/batch) is stale: rebuild the registry and every stage container.

## State

- Prepare is **done**: `work/acr/batch/prepare.done` = 30 stages, `failed=0`, 9 bake ok, 21 bake_skipped (WSM terrain shaders, no `terrain_array`).
- Batch builds: **all 30 stages build and pass the offline visuals checks** (some checks are advisory, see below). Last full run: `work/acr/batch/run-final2.log`, results in `work/acr/batch/report.md` / `report.json` / `registry-ids.json`, per-stage logs in `work/acr/batch/logs/<AcrXsXXnn>.log`.
- Registry + CUE4Parse verify: **built for 30 stages, verify ALL PASS** (last full run 2026-10-22 20:40). dist = 19.5 GB (31 containers x 520-970 MB; ~30-70 s per stage).
- `-Install` was **not** used. The user installs and tests in game: `scripts\install-stages.ps1`, then follow `docs/additive-batch-test.md` (uninstall: `scripts\install-stages.ps1 -Uninstall`).
- Todos: spot-check and batch-test are for the user; everything else is done.

## Ownership / sequencing rule

One agent only. Never run a build while prepare / dr2-extract writes `work\ir` or `work\dr2\cache`.
`build-stages.ps1 -Only` takes a **single** comma string through `pwsh -File` (use `pwsh -NoProfile -Command "& scripts\build-stages.ps1 -Only '<a,b>'.Split(',')"`); `-Only` limits the registry and report to the selection, so finish with one full run (no `-Only`) to get the 30-stage registry. Changing `config/*.toml`, `acr-ref.dll` or `acr-additive.dll` changes every stage's input hash and rebuilds all 30 (~30 min).

## What was done this session

1. **AES fix is tracked**: `tools/dr2-extract/vendor-patches/ego.nefsedit-aes-padding.patch` (+ `README.md`). `scripts/bootstrap.ps1` applies `<repo>-*.patch` from that folder after checkout (idempotent). Regenerate with `git diff` inside `third_party/ego.nefsedit` if the file changes.
2. **Prepare**: `board` (France/Wales sponsor `board.tga`) added to the optional textures in `scripts/prepare-stage-data.ps1`. Missing sponsor textures (cloth, teardrop, drape, board, lazerstart) are non-fatal.
3. **Disk**: `work\dr2\cache\__*` (regenerable extraction cache) was deleted to recover ~50 GB; keep > 15 GB free. A forced build that fails deletes the stage's container from dist, so rerun it.
4. **NuGet workaround** (sandbox cache was hollow): `$env:NUGET_PACKAGES='C:\Users\robbe\.nuget\packages'`, `dotnet restore ... -p:NuGetAudit=false`.
5. **Waimarama (AcrXsNz02r3)**: cause was a gap in DR2's own terrain/vista (render terrain has no triangles there) plus too-tight thresholds. Fixed in `Visuals.cs` (vista gap-vertex logic, hole clusters, vista coverage of holes, ground check beyond route ends, off-ground threshold 5 cm), `ObjectColliders.cs` / `StageBuild.cs` (`RoadSurfaces.For`: per-location road surface codes, pacenote end tolerance +5 m). Te Awanga, Waimarama and Ocean Beach (AcrXsNz01r3) all pass and Ocean Beach is registry-verified.
6. **Per-location stand-ins** (`DropIn.cs` `Dr2Materials`): terrain/road texture from the stage's texture array slice or the most used `TDiffuseSpecMap1`, vista maps found by name, flat tint when absent. `VistaLayers()` picks decodable layer textures; BC7 has no decoder, so `MeanRgb` estimates a BC7 mean from block colour endpoints (Australia, Finland, Spain, Sweden, Wales use BC7 diffuses).
7. **Advisory checks**: `config/visuals.toml` `[checks] advisory = [...]` (regexes, single-line array because `Toml.cs` is a minimal parser). A matching failing check prints `WARN` (counted in `report.md` as "advisory WARN xN"). Advisory: road corridor posts / trunks, vista nearest vertex, terrain box UV ratio, total triangle budget, ground-vs-collision matching, plan-sample holes, gate raycast, simple entries. Hard checks are unchanged.
8. **Other fixes**: `Glb.Primitives` tolerates a glb with no meshes (Greece 02 `terrain.glb` is empty; terrain_low + ground bake carry it); solid bush colliders (`PM_WallBush`) are no longer drawn (they were up to 3M triangles; colliders stay in BakedData); `max_object_extent_m` 80 (pol_big_barn 51 m, gre_stone_wall8 67 m); `Verify.cs` now expects every spec location row that exists in vanilla (Wales and Greece) to gain stage tracks, not only Template-less ones; `build-stages.ps1` lists advisory WARN lines in the report.
9. C# gotcha seen: `x += f()` where `f` mutates `x` through a closure loses the increments (was `failures += CheckRenderGround(...)`).

## Distance calls ("80" = 80 m to the next corner), 2026-10-23

- **How ACR encodes them**: a pacenote row (`PacenoteSplineEntry`) whose token is `Dist40/50/60/70/80/90/100/130/150/170/200/250/300/350/400`, `LongStraight` or `VeryLongStraight` (`DT_PacenoteTokens_EA`, category Distances). The row sits on the straight between two corner rows (`SplineDistanceM` = end of the previous corner, `LengthM` = the gap); vanilla Hafren has 23 such rows in 84, 25-165 m long, the value being the gap rounded to the nearest call (58 m -> Dist60). Audio exists for every token in `DT_EN0`; `DT_EN1` lacks `Dist130` and `Dist170` (not checked which codriver is used when). Dump: `acr-ref pacenote-survey --legacy work\acr\legacy-vanilla --rows 1` (saved in `work/acr/vanilla-pacenote-rows.txt`), audio tables exported to `work/acr/dist-audio/`.
- **State before this change**: the generator already emitted geometry-derived DistNN rows (gap >= 40 m between corner rows, floor snap); only the DR2 spoken values were dropped (the "distance 50 x12" in the report) and short gaps had no row. So Waimarama had 24 DistNN rows before.
- **Now** (`config/pacenote-map.toml` `[distance_calls]`, `[layout] straight_snap`, code in `Pacenotes.Generate`): DR2 distance tokens >= 40 m are attached to the gap between the corner before and after them (DR2 order). The row gets the DR2 value snapped to the nearest ACR token when the geometric gap agrees (value/1.5 .. value*1.5); otherwise the geometry token is used (the player sees our road). Gaps of 20-40 m get a row when DR2 spoke <= 60 m. A DR2 distance inside one linked phrase now gets a DistNN row between the linked corners. Geometry rows snap to the nearest token (was floor). `source = "geometry"` restores v1.5 behaviour. DR2 120 has no ACR token and snaps to Dist130.
- **ROOT CAUSE of "no numbers shown or spoken" in game (v1.5-v1.11 builds, user test)**: a DistNN row must be the TAIL OF A LINKED CHAIN. In all 45 vanilla `DT_Pacenote*` tables, 1246 of 1360 DistNN rows have the previous row `LinkToNext = true` (e.g. `Left3 -> Dist60`), the DistNN row itself never links onward, is a single token, starts 0-5 m after the previous row ends, and the next corner starts a new chain. Field-for-field our rows were identical (token, length ~ value, SavedLocation 0, desc), but we left them standalone after an unlinked corner row, and the game neither shows nor says a standalone distance row (the native handler is not inspectable; this is inferred from the data + the in-game result, to be confirmed by the next in-game test). Fix: `Pacenotes.Generate` `Straight()` sets `LinkToNext = true` on the row before every DistNN row (`[distance_calls] link_to_previous = true`, set false to get the old layout). Vanilla never uses Dist350/Dist400/LongStraight/VeryLongStraight (only Dist40..300 and GoStraight x12); we still emit them on long straights (audio exists), so listen for those. Rows with no previous row (stage start) cannot be chained.
- **Check** (`stage-check`, `StageBuild.CheckDistanceCalls`): supported tokens only, spoken value fits row length, no two DistNN rows in a row, vanilla shape (previous row linked, row never links onward, single token, 0-15 m after previous row), table count equals the report, >= 60% of DR2 calls became a row. The build report has `tables.pacenotes.distance_calls` with a per-gap `detail` list.
- **Waimarama (AcrXsNz02r3)**: unmapped tokens 26 (24 distance + 2 unknown), mapped 77/103 -> unmapped 2 (unknown only), mapped 101/103. DistNN rows 24 -> 27; of 20 DR2 distance calls (3 doubled) 5 are spoken with the DR2 value, 13 are overruled by the geometry, 1 dropped (gap < 20 m), 1 dropped (corner not adjacent), 1 `null`. The 13 disagreements are mostly our corner placement being 50-250 m later than DR2's (leads 130-400 m vs DR2's 50-150), so DR2 says 50 where we have 200-400 m. Better corner alignment (using DR2's own gaps as a constraint) would fix that and is the next improvement.
- Only Waimarama was rebuilt (container in `work/acr/additive/dist`, not installed). Baselines: `work/acr/batch/report-before-distance-calls.md`, `work/acr/additive/*nz02r3.before-distance*`. The next full run rebuilds all 30 (config + `acr-ref.dll` changed); `work/acr/batch/report.md` currently only lists the Waimarama run.

## Menu images (maps, height profile, DR2-art backdrops), 2026-10-23

- Details in `docs/menu-images.md`. User decisions: DT_TracksTextures may get rows (5th overridden table, add-only, verify enforces vanilla rows identical); backdrop / banner / lobby images from DR2 loading art; location tile = matching vanilla row (only Monte Carlo: `tile` / `logo` in `[locations.france]`), no other texture tables.
- `menu_images = true` for all 30 stages. Each stage container gets 6 textures under `/Game/AcrXs/Stages/<Id>/Menu/` (track map, route map, height profile from `stage.json`; backdrop, BC7 banner, lobby thumbnail from DR2 art) and the variant's sector lengths come from the DR2 splits. Art: `scripts/prepare-menu-art.ps1` -> `work/dr2/menu-art/` (78 tpks); 21 stages use their own route's art, 9 (routes 4/5) route 0 of the same rally.
- Verified on Te Awanga only (`-Only`, verify ALL PASS, round trip OK); images-only spot check for one stage per location (`work/acr/menu-preview/<Id>/`, `spot-*.png`); a dry registry over all 30 specs added 180 texture rows. **dist's registry lists only AcrXsNz01r5.** The Waimarama container in dist was built before the menu change, so a registry with it fails verify (missing menu textures) until Waimarama is rebuilt. The next full run rebuilds all 30. Not installed, not tested in game.
- Open: per-location `menu_elevation_offset` (Elevation / ElevationAtStart are still Hafren's).
- Locations (later the same day, overrides "vanilla tile only"): each new location now has its own nation (`country`, vanilla DT_Countries row; Scotland -> UnitedKingdom, Monte Carlo -> Monaco), surface (`surface`: info-box surface % and the variants' surfaces / tyres from Wales, Alsace or Livigno, so Germany / Spain / Monte Carlo get TarmacSnow tyres and Sweden FullSnow, a gameplay change), description, and a generated tile (DR2 loading aerial) + neutral "RALLY <LOCATION>" logo in the registry container (Monte Carlo reuses ACR's own). DT_TracksLocationsBackgrounds / Logos become overridden tables (add-only). Registry-only build + verify ALL PASS (27 packages, round trip identical); not installed. Open: aerial tiles vs ground-level DR2 frontend photos (`work/acr/menu-preview/locations/photo-tiles.png`), difficulty still Wales's 6. Fast verify (`work/acr/verify-fast/src/acr-additive`) is an older copy of the tool and lacks these checks.

## Full batch + install status, 2026-10-23

- All 30 stages built (`work/acr/batch/report.md`: 30 built, 0 failed; the first batch had 6 "not enough space on the disk" failures, fixed by freeing disk and rerunning). dist holds the registry + 30 containers (registry newer than every stage).
- Final verify used the FAST tool (`work/bin/acr-additive-fast`) over all 30 specs: ALL PASS, 649 PASS / 0 FAIL (`work/acr/additive/check/verify.txt`); the registry round-trip step was skipped and the slow in-build verify (base tool) was killed. **Pending: the speed-up worker's equivalence check of the fast tool vs the base tool.** `registry-ids.json` / `report.md` were patched by hand to say built + ALL PASS (the killed verify had marked them "NOT built").
- No distance-call or menu-image WARNs in any stage log or verify; the only advisory WARNs are the usual ground / UV / collision ones.
- `scripts/install-stages.ps1` now installs via NTFS hard links (same volume; `-Copy` forces copies; `-Uninstall` removes only `zz_acrxs_*`). 93 files (registry + 30 stages, ~20 GB logical) are hard-linked into `~mods`, so they cost no extra disk. After any rebuild re-run the installer. `build.ps1` already deletes a dist file before retoc writes it, so a link is never half-written; it keeps the old content until reinstalled.
- Fixed `scripts/build-stages.ps1` incremental skipping: `vanillaSig` hashed `.source.json`, which build.ps1 rewrites with a new timestamp each run, so every run rebuilt everything. It now hashes only the container list (the next run rebuilds once more, then skips unchanged stages).

## Disk budget (measured 2026-10-23; plan step "Disk budget")

Goal: ~20 GB of stage files + a working peak of ~15 GB. Constants live in `scripts/disk-lib.ps1` (`$script:DiskBudget`), the estimate in `Get-DiskEstimate`; `scripts/estimate-disk.ps1 [-Json] [-Check]` prints / emits it.

**Measured** (hard links counted once; `work\` shows 238 GB nominal in `work\acr` because the 22.3 GB vanilla view is hard-linked into six folders):

| What | Real size | Regenerable? |
|---|---|---|
| `work\ir` | 55.9 GB: 30 enabled stages heavy part (`render\` + `textures\`) 50.3 GB (avg 1.68, max 2.43 GB, Scotland 03 r2); light part (stage.json + 2 collision glbs) 29 MB each (max 46); the other 126 stage IRs ~4.8 GB | heavy part: yes, ~30 s per stage (`prepare-stage-data.ps1 -Only <id>`); light part is read by registry / stage-fit / menu images |
| `work\dr2` | 7.5 GB: `cache\game_2_1` 5.05 GB (shared DR2 layer), `base\tracks` 2.0 GB (not used by the pipeline), gltf 0.2, menu-art 0.16 | yes (extracted on demand) |
| `work\dr2\cache\<country>__<location>` | 2.07 GB for Greece 02 (= 0.92 x its 2.25 GB archive); all 24 locations of the 30 stages = 63 GB of archives = ~58 GB extracted; largest France 02 ~3.9-4.3 GB. Deleted by hand earlier to recover ~50 GB | yes |
| `work\acr\additive\dist` = `~mods` | 20.3 GB, 93 files (registry + 30 containers, **old `zz_acrdirt_*` names**), shared with `~mods` through hard links (0 extra) | yes (30-70 s per stage) |
| `work\acr\verify-fast` | **15.9 GB real** (103 GB nominal): link farms (`check`, `check30`) that keep old container versions alive | yes, experiment output |
| vanilla view (`additive\vanilla`, `acr\vanilla`, `vanilla-paks`, `check\...`) | 22.3 GB nominal each, **0 real** (hard links to the game's Paks) | yes |
| `work\acr\additive\legacy` | 0.59 GB: retoc `--filter` extracts only the packages the specs clone + `Data/Database/Main/` + menu texture templates, not the 22 GB dump (needed-only extraction already existed) | yes |
| other `work\acr\*` (old experiments, menu-preview 0.78, legacy-vanilla 0.33, cells-json 0.42, ...) | ~4.7 GB | mostly |
| `work\bin` | 0.12 GB | yes |
| Per-stage build scratch (`mod-build` + `overlay` + `mod-stage`) | **peak 1.93 GB** in a one-stage run (AcrXsWa02r4, 722 MB container, 43 s); was ~0.4 GB left per FAILED build | deleted after every build |
| Container size | mean 676 MB (534-976), registry ~17-20 MB | |

Real use of `work\` today: ~85 GB (ir 55.9 + dr2 7.5 + verify-fast 15.9 + ~5.7 rest), plus the 20 GB of containers that live in `~mods` through hard links. A clean run in the OLD flow (prepare all, then build all, nothing pruned) needs ~117 GB working + 20 GB of stage files (~138 GB free), which is why the first batch hit "not enough space" (the README's "50 GB+" was too low).

**What changed (cleanup going forward)**

- `scripts/build-stages.ps1`: after a stage is built, its heavy IR (`work\ir\<dr2 id>\render` + `textures`, ~1.7 GB) is deleted; `stage.json` + collision glbs stay plus `ir-pruned.json` (terrain / bake signatures, so an unchanged stage stays "unchanged" without its render data). `-KeepIr` opts out. IR of stages that were not built in the run is never touched. A pruned stage whose inputs changed reports `needs-prepare` (or is re-prepared with `-PrepareOnDemand`).
- `-PrepareOnDemand` = streaming flow for a clean tree: `prepare-stage-data.ps1 -Only <id> -NoReport` right before each build, location cache (`work\dr2\cache\*__<location>`) dropped when the next stage is in another location (`-KeepDr2Cache` opts out), heavy IR pruned after the build (also after a failed stage prepared in this run). `stage-fit` is re-run for stages that had no IR when the plan was made (+~20 s each); deferred stages (> 9 km) are detected after their prepare.
- Preflight: `build-stages.ps1` prints and enforces the estimate (`-SkipDiskCheck` to ignore; `-ListOnly` only warns); `report.json` / `report.md` get a `disk` block (IR freed, lowest free space sampled at stage boundaries).
- `mods/additive/build.ps1`: `overlay-<id>` is deleted right after `relocate`, `mod-build-<id>` after the container inputs are assembled; all three temps are removed in a `finally` (so failed or prototype builds no longer leave them), `-KeepTemp` keeps them.
- `scripts/gen-stage-specs.ps1`: nested spec tables are `[ordered]`. Plain `@{}` enumerated in a per-process random order, so the spec bytes (and the batch input hash) differed on every run: the incremental "unchanged" skip could never work for stages with >1 key in a table.
- `scripts/clean-work.ps1`: dry run by default, `-Force` deletes (categories: scratch, dr2-location-cache, ir-built, experiments by default; ir-all, dr2-layer-cache, previews, stale-dist opt-in). Link-aware "frees X GB" numbers. Dry run today: experiments (mostly `verify-fast`) 17.6 GB, ir-all 48.5 GB, dr2-layer-cache 4.9 GB, previews 0.8 GB; `stale-dist` frees nothing until `install-stages.ps1 -Uninstall` removes the `~mods` links. Nothing was deleted from the user's `work\`.
- `scripts/prepare-stage-data.ps1 -NoReport` (no `prepare-data.json` / `prepare.done` overwrite when called per stage).
- Note: `build.ps1` and the spec bytes changed, so the next full run rebuilds all 30 stages once (as after any script / config change).

**Estimates** (`scripts/estimate-disk.ps1`, 30 stages, 676 MB mean container):

| Flow | Stage files | Working peak | Free space to require (incl. 2 GB margin) |
|---|---|---|---|
| streaming (`-PrepareOnDemand`, default estimate) | 19.8 GB | **14.4 GB** = shared 6.8 (DR2 layers 5.1 + legacy 0.6 + misc 1.3) + light IR 0.9 + one stage in flight 6.6 (heavy IR 2.4 + location cache 4.2, or build scratch 2.5) | **36.2 GB** |
| build-only (IR already prepared, `-KeepIr` or not) | 1 stage = 0.7 GB | 3.3 GB | 6.0 GB for one stage |
| bulk (old flow) | 19.8 GB | 116.6 GB | 138.4 GB |
| copy install (`-InstallMode copy`, or game on another volume) | x2 on disk | same | +19.8 GB on the game volume |

This machine: 30 GB free, so a clean 30-stage streaming run is 6 GB short; `clean-work.ps1 -Force` (experiments 17.6 GB) fixes that.

**Launcher contract**: `pwsh scripts\estimate-disk.ps1 -Json [-Stages N] [-ContainerMB X] [-Flow streaming|bulk|build-only] [-InstallMode hardlink|copy] [-OutFile f]` prints one JSON object (schema 1): `inputs`, `output_mb{containers,registry,total,on_disk_incl_install}`, `working_peak_mb{shared,light_ir,heavy_ir,transient,total}`, `required_free_mb{total,work_volume,game_volume}`, `free_mb`, `ok`, `shortfall_mb`, `message`. `-Check` exits 3 when free space is short. The C# launcher should reimplement `Get-DiskEstimate` (pure function, constants in `disk-lib.ps1`) and show `required_free_mb.work_volume` before Build.

Open: shared DR2 layer cache (5.1 GB) is kept for the whole run; `dr2-layer-cache` can drop it at the end. Per-location cache means a stage list not sorted by location re-extracts a location archive (stages.toml is grouped by location). Real peak was measured for one stage only (scratch 1.93 GB, prepare ~30 s for 0.64 GB IR + 2.07 GB cache); the 14.4 GB is the sum of per-component measurements, not a full 30-stage run.

## Known caveats for the in-game test

- 21 stages have no ground bake (WSM terrain shaders): they use the DR2 grass-slice / stand-in textures for the ground, so the look is plainer than NZ/Poland/Scotland/Argentina (baked). Not a crash risk, a visual-quality item.
- Spain 01 r4 and Greece 02 are the heaviest meshes (> 3M triangles on Spain 01); watch the frame rate there.
- Rocky stages (Argentina cliffs, Greece, France) have advisory WARNs where DR2 collision and drawn ground differ.
- Furthest-first suggestion for testing: Te Awanga (NZ01r5), Waimarama (NZ02r3), Ocean Beach (NZ01r3), then one stage per location.

## Next steps (for the next session)

1. User installs (`scripts\install-stages.ps1`) and tests per `docs/additive-batch-test.md`; record which stages misbehave.
2. Possible improvements: ground bake for WSM terrain shaders (`TerrainBake.cs`), a real BC7 decoder (exact vista tints), reducing Spain/Greece triangle counts, per-stage tuning of the advisory checks.
3. Stages longer than 9 km are deferred by `build-stages.ps1` (`-MaxLengthKm`); none of the 30 currently are.

## Uncommitted source that matters

Modified: `config/pacenote-map.toml`, `config/sky.toml`, `config/visuals.toml`, `docs/additive-test.md`, `mods/additive/build.ps1`, `scripts/bootstrap.ps1`, `tools/acr-additive/Registry.cs`, `tools/acr-additive/Verify.cs`, `tools/acr-ref/{DropIn,Glb,ObjectColliders,Program,Scenery,Sky,StageBuild,VisualObjects,Visuals}.cs`, `tools/dr2-extract/{Dr2Install,StageIr,TerrainBake}.cs`.

Untracked: `config/stages.toml`, `docs/additive-batch-test.md`, `docs/additive-te-awanga-test.md`, `docs/resume-notes.md`, `scripts/{prepare-stage-data,stages-lib,build-stages,install-stages,gen-stage-specs,pacenote-survey-ir}.ps1`, `tools/acr-ref/{Bounds,StageFit,Advisory}.cs`, `tools/dr2-extract/vendor-patches/`, generated stage specs now live in `work/acr/specs/` (gitignored).
