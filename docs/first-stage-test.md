# First DR2 stage in ACR: install & test checklist (~15 min)

**Stage:** DiRT Rally 2.0 `new_zealand_rally_01_route_5`, "Te Awanga Sprint Reverse" (Hawkes Bay, NZ),
4.69 km start to finish.
**Host:** Wales → **Hafren South**, variant **1/2 "Afon Bidno - Severn"** (FullForward). The DR2 road
replaces that variant's physics and route. The reverse variant 2/2 is **not** converted (see Caveats).

What this test answers: can a DR2 stage be driven start to finish in ACR on DR2's own physics ground,
with working start, splits and finish? **Nothing of DR2 is visible yet.** You drive on an invisible
road while Hafren South's scenery is drawn around you, often cutting through it.

## 0. Build (skip if `work\acr\dist\zz_acrdirt_nz01r5_P.*` exists)

```powershell
cd C:\Users\robbe\Projects\acr-dirt
pwsh -NoProfile -File mods\acrdirt-stage\build.ps1 -Verify      # ~1 min; ends with ALL PASS
```

Output is `work\acr\dist\zz_acrdirt_nz01r5_P.{utoc,ucas,pak}` (~230 MB). It needs the stage IR in
`work\ir\new_zealand_rally_01_route_5` (`dr2-extract stage-ir new_zealand_rally_01_route_5 --render`).

## 1. Install (manual, reversible)

Remove the load-proof containers first. They override the same BakedData and DataTable, and whichever
loads last wins.

```powershell
$paks = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally\acr\Content\Paks'
Remove-Item "$paks\~mods\zz_acrdirt_test_P.*", "$paks\~mods\zz_acrdirt_ramp_P.*" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$paks\~mods" | Out-Null
Copy-Item C:\Users\robbe\Projects\acr-dirt\work\acr\dist\zz_acrdirt_nz01r5_P.* "$paks\~mods\"
```

**Rollback:** `Remove-Item "$paks\~mods\zz_acrdirt_*"`. If anything still looks off, use Steam → Verify
integrity.

**Recommended:** have [ACR Rewind](../../acr-rewind/README.md) installed. With an invisible road you
will leave it sometimes. `R`, scrub back with `Left`, then `Enter` puts you back on the road without
restarting the stage. It works in offline modes only (free practice is fine).

## 2. Test

1. Stage selection: **Wales → Hafren South → variant 1/2 "Afon Bidno - Severn"**.
   - **Check A:** the info panel shows **4.69 km** (instead of 4.8).
2. Start a **free practice** (offline) stage with any car you know well.
   - **Check B:** the stage loads, and the car spawns on solid ground facing along the road.
     - The spawn point is ~30 m before the start line (DR2's run-up is short).
     - The car sits ~380–515 m high in Hafren South's coordinates, so expect to be inside or above the
       Welsh hills. That's normal.
3. Drive the whole stage at moderate speed. Use the ACR route/progress UI (HUD distance, any minimap)
   as your guide. The road is DR2's road surface plus 250 m of terrain either side.
   - **Check C:** the HUD route length reads about **4.85 km**: DR2's run-up + stage + run-out
     (Hafren's was ~5.1 km).
   - **Check D:** the car stays on an invisible but continuous surface. Expect:
     - gravel feel on the road;
     - grass grip on verges;
     - one metal cattle grid around 2/3 distance.
   - **Check E:** the timing sequence works: start line, split 1 after ~1.2 km, split 2 after ~3.5 km,
     finish at 4.69 km, then the car is stopped ~90 m after the finish.

## 3. What to report

For each of A–E: pass, fail, or "didn't get that far". Plus:

- Where it went wrong: HUD distance at that point, and a screenshot if possible.
- **Spawn:** facing the right way? On the ground, falling, or stuck?
- **Surface:** any holes, invisible bumps or walls, or a sudden drop?
  - If you leave the DR2 terrain (over 250 m from the road), you fall ~60 m onto a flattened layer.
    Did the game reset you, and to where?
- **Respawn / "return to track"** (if the game offers it): where does it put the car?
- **Timing:** did the start trigger the countdown or clock? Did splits and finish register? Was the
  final time plausible (DR2 pace ≈ 3 min for this stage)?
- **Pacenotes:** the co-driver should be silent (the table is empty). If the stage **crashes or hangs
  on load**, rebuild with `build.ps1 -KeepPacenotes` (Hafren's wrong calls, but a non-empty table) and
  retry. That tells us whether the empty table is the cause.
- **Direction sanity:** does the road "feel" like Te Awanga Sprint Reverse? It should be mostly
  downhill overall (DR2 gate heights fall from ~582 m to ~512 m). A mirror image would mean DR2's
  handedness assumption is wrong. Compare a distinctive corner sequence with a DR2 video if you can.

## 4. What exactly was changed

All four overridden packages are in one container.

| Package | Change |
|---|---|
| `DA_WalesS4HafrenSouthBakedData` | All 734 host mesh colliders flattened to a 12 m layer at 306–318 m; 17,180 tree and box colliders squashed to 306–315 m. Owners and stride maps are untouched. Then 253 DR2 colliders (1,006,007 triangles) were appended to the always-active stride set, chunked by ACR physical material on a 100 m grid. New imports: `PM_SurfaceGrass`, `PM_SurfaceMetal`, `PM_Fallback`. |
| `_Generated_/22VG0QIP9ATUQI633K86KWPFP` (forward gameplay cell) | Center/Ideal/Left/Right/Contour splines rebuilt from the DR2 gates (446 points). The start point, start trigger, 4 race sectors, stop zone and post-race camera were moved along the new route. Respawn walls were moved 1 km down. |
| `DT_TracksVariants` | `WelesS4HafrenSouthFullForward.Length` 4.8 → 4.689 |
| `DT_PacenoteHafrenSouthForward` | 84 rows → 0 |

**Placement.** DR2 world → ACR physics (metres, Y up): rotate 78° about Y around the DR2 route centre,
then translate so the route is centred in the host's usable area (≥ 297 m margin), with the collision
minimum at 380 m. The exact numbers are in `work\acr\stage-nz01r5.report.json` and
`docs/decisions.md` D6.

**Offline verification** (`build.ps1 -Verify`, also on a retoc round trip of the shipped container):

- Ground under all 446 gates is within 4.4 cm of the gate height (mean 0.8 cm).
- No gaps along the centreline at ≤ 1 m spacing (4,915 samples).
- 99.4 % of flat DR2 triangles face up, matching the host's convention.
- CUE4Parse resolves every collider's physical material, reads 446 spline points (4848.8 m), 0
  pacenote rows and Length 4.689.

## Caveats (known gaps)

- **Visuals:** none of DR2 is rendered. Hafren South's terrain, trees and road meshes are still drawn
  but have no collision. The route's minimap/HUD, if drawn from the spline, should follow DR2.
- **Objects / walls:** no DR2 trees, fences, rocks or buildings. DR2's object collision isn't decoded
  yet, and Hafren's trees and walls are disabled (flattened).
- **Pacenotes:** silent. DR2 calls aren't converted yet (the subcall meaning is not decoded).
- **Reverse variant 2/2** shares the same BakedData: its physics is now DR2 ground with Hafren's
  reverse route. **Don't drive it** with the mod installed.
- Replay cameras and minimap textures are Hafren's.
- [I] Assumed, not yet seen in-game:
  - Race sectors detect the car by spline distance (their trigger boxes are tiny).
  - Respawn uses the rebuilt splines.
  - An empty pacenote table is accepted.

## Optional: run the recorder

[`tools/stage-validate`](validation.md) logs the shared-memory telemetry while you drive (read-only). It then
checks the run against the IR: progress, lateral offset vs the road edges, fall-throughs, off-route stretches,
start/finish timing and speed. It also draws a top-down SVG of the route vs your path. A run CSV plus the
`check` output is the most useful thing to send back with the report in section 3.

```powershell
cd C:\Users\robbe\Projects\acr-dirt
cargo build --release --manifest-path tools\stage-validate\Cargo.toml
$sv = 'tools\stage-validate\target\release\stage-validate.exe'

# before or after launching ACR; stops on Ctrl+C or ~5 s after you leave the stage
& $sv record --out work\validate\nz01r5-run1.csv

# afterwards
& $sv check --run work\validate\nz01r5-run1.csv `
    --stage work\ir\new_zealand_rally_01_route_5\stage.json `
    --transform work\acr\stage-nz01r5.report.json
```

The check prints PASS/WARN/FAIL per item and writes `work\validate\nz01r5-run1.svg`. One recording per attempt
is easiest. If you use ACR Rewind, the rewinds show up as "teleports", not as falls.

## v1 crash on stage start (fixed; rebuilt v1 in work\acr\dist)

Symptom (first in-game test): menu showed 4.69 km, then starting the stage crashed with
`EXCEPTION_ACCESS_VIOLATION reading address 0xffffffffffffffff` ~7 s after `WalesS4HafrenSouth` loaded.

Diagnosis (from the minidump, `%LOCALAPPDATA%\acr\Saved\Crashes\UECC-Windows-C5A38F1D...\UEMinidump.dmp`, read with cdb):
- Verified: the faulting instruction is acr.exe RVA 0x4F64604 (`movsxd rcx, [r8+6Ch]`) in a function of the Kunos
  physics plugin; its assert strings are `dmphysics/Source/dmkunosengine/src/ksPhysicsAC/Track.cpp` and
  `trackData.surfaceDef != nullptr`. r8 (= surfaceDef) held 0x41023eae4e995c26, a garbage pointer (non-canonical
  addresses are reported as 0xffffffffffffffff), not null, so the assert did not fire. The caller (RVA 0x5049B43) fills
  trackData per track collider while building the physics track.
- Verified: v1 added 3 physical-material imports to BakedData (PM_SurfaceMetal, PM_SurfaceGrass, PM_Fallback). The game's
  only surface registry, DA_PhysMaterialsMappingCommon (38 DMPhysicalMaterials), lists the first two but not
  PM_Fallback. DR2's `DEFA` material (1 triangle in nz01r5) was mapped to PM_Fallback by config/surface-map.toml.
- Inferred (very likely, not yet confirmed in game): the collider with PM_Fallback has no Kunos surface, so surfaceDef is
  read from an unset slot -> crash.
- Not the cause: input contamination. The v1 host packages are byte-identical to a fresh extraction from the vanilla
  containers alone (DT_TracksVariants, BakedData, gameplay cell, pacenote table, scriptobjects).

Fix: stage-build now requires `--surface-registry` (build.ps1 exports DA_PhysMaterialsMappingCommon from the vanilla
view) and remaps any unregistered ground material to PM_SurfaceGrass (objects: PM_WallBasic); it refuses to import an
unregistered one, and stage-verify FAILs if any mesh or simple collider uses one. Builds also read only a vanilla view
of the Paks (`global` + `pakchunk*-Windows`, no `~mods`; mods/common/vanilla.ps1) from a stamped cache
`work\acr\legacy-vanilla`, and the offline check confirms the DT row Length equals the DR2 length.

Fixed v1 (`work\acr\dist`): ucas 229,533,612, utoc 43,083, pak 347; PASS 30 / FAIL 0.
Bisect set if it still crashes: `work\acr\bisect\` (A = DT only, Bfix = + BakedData, Cfix = + cell; B/C/D = the same
with the original v1 BakedData); test order in `work\acr\bisect\TESTS.md`.
