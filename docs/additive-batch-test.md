# Additive DiRT stages: batch build, spot-check and per-location test

Status: the batch tooling is written; the stage list below can only be built once the DR2 data preparation is finished
(`work\acr\batch\prepare.done`). Until a stage is built and listed `built` in `work\acr\batch\report.md`, it cannot be tested.

## 1. Build (after prepare is green)

```powershell
cd C:\Users\robbe\Projects\acr-dirt
# everything (30 enabled stages; incremental: unchanged stages are skipped, failures never stop the batch)
pwsh -NoProfile -File scripts\build-stages.ps1
# a subset (acr ids or DR2 ids); the registry then lists ONLY the stages that passed in this run
pwsh -NoProfile -File scripts\build-stages.ps1 -Only AcrXsDe01r4,AcrXsSe01r2,AcrXsMc01r2,AcrXsNz02r3
```

Useful switches: `-ListOnly` (plan: needs prepare / deferred / todo), `-Force` (rebuild unchanged stages), `-NoVerify`,
`-FailOnAnyError` (exit 1 when any stage failed; by default exit 0 if at least one stage passed), `-Install`.

Results: `work\acr\batch\report.md` / `report.json` (status, reason, placement, world size, MB, pacenote tokens mapped, unmapped
items), per-stage logs `work\acr\batch\logs\<id>.log`, fit analysis `work\acr\fit-report.txt`.
Statuses: `built`, `unchanged`, `failed` (reason in report), `needs-prepare` (DR2 render data / textures missing: never extracted by the
batch), `deferred` (route over 9 km).

A stage whose ground bake manifest is missing still builds (warning in the report: DR2 grass-slice ground instead of the bake).

## 2. Install / uninstall

```powershell
pwsh -NoProfile -File scripts\install-stages.ps1             # registry + the stages of the last batch -> Paks\~mods
pwsh -NoProfile -File scripts\install-stages.ps1 -Uninstall  # removes every zz_acrxs_* and legacy zz_acrdirt_* file (rollback)
```

- It first removes existing `zz_acrxs_*` files, so the old `zz_acrxs_nz01r5_P` override can never coexist with the registry
  (both ship `DT_TracksVariants`).
- The registry reflects the last batch's selection. Re-run the build with the selection you want to test, then install again.
- Each stage container is about 0.7 GB (`.ucas`), so 4 stages ~3 GB, all 30 ~21 GB. The game loads a container only when its stage is picked.
- Close the game before installing.

## 3. Spot-check (4 stages, ~40 min)

Build and install:

```powershell
pwsh -NoProfile -File scripts\build-stages.ps1 -Only AcrXsDe01r4,AcrXsSe01r2,AcrXsMc01r2,AcrXsNz02r3 -Install
```

| Stage | Location | What it exercises | Fit (stage-fit) |
|---|---|---|---|
| `AcrXsDe01r4` Kreuzungsring | Germany | 6.1 km, placed inside the host region (no enlarged world) | host-region (no enlarged world), 8.6 km2 |
| `AcrXsSe01r2` Älgsjön Sprint | Sweden | enlarged physics world, winter sky, may lack the ground bake | enlarged, 5.2 km, 14.8 km2, Y span 340 m |
| `AcrXsMc01r2` | Monte Carlo | tall terrain (Y span ~578 m, taller than the host's 229 m span), enlarged world | enlarged, 5.2 km, 8.6 km2 |
| `AcrXsNz02r3` Waimarama Sprint | New Zealand | longest of the set, 8.8 km, enlarged world | enlarged, 15.3 km2 |

For each stage (free practice / test stage, any car):

1. The location appears in the location list (New location rows: Germany, Sweden, Monte Carlo, New Zealand; Wales still shows its own stages).
2. The stage appears with the expected name and length (km above).
3. It loads (no crash, no black screen) and the car sits on the road at the start line, not falling or floating.
4. Drive the whole stage: no invisible walls on the road, no falling out of the world (this is the enlarged-bounds check: the car must not hit
   the physics world edge and get respawned), no road holes, no big ground holes close to the road.
5. Pacenotes are spoken, roughly in the right place before corners and with the right direction/severity. Items DR2 has that ACR can't say
   (raw call types 5/7/8/9, modifiers `modifier_a_1`, `modifier_b_1`, `modifier_b_3`) are dropped on purpose; they are listed per stage in the report.
6. Sky/time of day matches the location (config\sky.toml: Sweden winter, Monte Carlo January).
7. Wales -> Hafren South still loads forward and reverse (vanilla untouched).

Report per stage: `OK` / `problem` + the checks (3, 4, 5) that failed, where on the road (km), a screenshot if possible.

## 4. Per-location test (one drive per location, 13 locations)

One representative stage per location (host-region placements preferred, because they are the lowest-risk):

| Location | Stage | Km | Placement |
|---|---|---|---|
| Argentina | `AcrXsAr01r2` | 5.3 | enlarged |
| Australia | `AcrXsAu02r3` | 7.0 | enlarged (no ground bake) |
| Finland | `AcrXsFi01r2` | 7.5 | enlarged |
| Monte Carlo | `AcrXsMc01r2` | 5.2 | enlarged |
| Germany | `AcrXsDe01r4` | 6.1 | host-region |
| Greece | `AcrXsGr01r2` | 5.1 | enlarged |
| New Zealand | `AcrXsNz01r5` (Te Awanga) | 4.7 | host-region |
| Poland | `AcrXsPl02r2` | 6.6 | enlarged |
| Scotland | `AcrXsSc03r2` | 5.8 | enlarged |
| Spain | `AcrXsEs02r2` | 4.6 | host-region |
| Sweden | `AcrXsSe01r2` | 5.2 | enlarged |
| USA | `AcrXsUs02r2` | 6.2 | host-region |
| Wales (DiRT) | `AcrXsWa01r2` | 4.8 | host-region |

```powershell
pwsh -NoProfile -File scripts\build-stages.ps1 -Only AcrXsAr01r2,AcrXsAu02r3,AcrXsFi01r2,AcrXsMc01r2,AcrXsDe01r4,AcrXsGr01r2,AcrXsNz01r5,AcrXsPl02r2,AcrXsSc03r2,AcrXsEs02r2,AcrXsSe01r2,AcrXsUs02r2,AcrXsWa01r2 -Install
```

~9 GB of containers. If disk or load time is a problem, test in groups of 4: pass a shorter `-Only` list and `-Install` again.

Per location: (1) location row exists with the right name, (2) the stage is listed under it, (3) it loads and you can drive the first 2 km without falling
through the ground or hitting a wall, (4) pacenotes play. Mark the stage `OK` / `problem` / `crash on load`. A failed location row or a
missing stage means a registry problem (report which); the offline verify says ALL PASS when the registry was built.

## 5. Risks to keep in mind

- Enlarged physics worlds reach 15-25 km2 (vanilla Hafren: 8.6 km2) and Y spans up to ~790 m. The in-game behaviour (physics cost, the car
  respawning at the world edge) is not verified by the offline checks; the spot-check above is the first real test of it.
- Stages without a ground bake (Australia, Sweden, Poland route 2 at the time of writing) use the DR2 grass-slice ground: they look flatter.
- Texture/material data come from the prepare step; a stage whose textures were incomplete is reported `needs-prepare`, not built.
- Stages over 9 km are deferred (`fit-report.txt`), they are not in the 30.

## Status notes (batch validated 03:54)

- **Waimarama (AcrXsNz02r3) currently fails the stage-build visuals gates** and is therefore *not built / not in the registry*: vista
  section 34.8 m from the centreline (min 50 m), 521 of 421882 road vertices off the collision ground by > 0.5 cm (bridge-like
  higher layer), 1170 of 272781 plan samples within 60 m of the road with no drawn ground (log: `work\acr\batch\logs\AcrXsNz02r3.log`).
  It needs a visuals fix, it is not a prepare problem. Until then use `AcrXsNz01r3` (Ocean Beach Sprint Forward, 6.6 km, enlarged
  13.1 km2) as the enlarged-world test: it is built, and the registry verify (incl. the enlarged PhysicsWorldBounds check) is ALL PASS.
- Argentina and Poland 02 report `needs-prepare: incomplete IR data` (texture `nz_ral_set_1_01_d1049518591_slice2.dds` referenced by
  the render materials but never extracted).
- `install-stages.ps1` installs exactly the stages the last batch put in the registry (`work\acr\batch\registry-ids.json`); `-Uninstall` rolls back.
