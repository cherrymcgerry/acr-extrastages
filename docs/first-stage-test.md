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

## v2 test (after fixed v1 loads and drives)

`work\acr\dist-v2\zz_acrdirt_nz01r5_P.*` uses the **same container name** as v1 and overrides the same four host
packages. Install only one of them: remove the v1 files from `~mods` before copying v2 in (and back to switch).
Build: `.\mods\acrdirt-stage\build.ps1 -Version v2 -Verify` (offline: PASS 50 / FAIL 0). Sizes: ucas 230,347,525,
utoc 43,227, pak 347.

What v2 adds over v1:
- **Object collision (invisible):** 3189 DR2 objects become colliders; there are no visuals, so expect to hit things
  you cannot see (DR2 trees, fences, guardrails, rocks, poles). 1670 trunks + other simple shapes = 2205 new
  simple entries (375 boxes, 1830 capsules); 815 trunk cones + 169 guardrail cones = 2 small mesh colliders.
  Materials are PM_Wall* (all registered surfaces). Skipped: bushes/soft vegetation, light and phantom breakables.
  69 objects whose solid part would intrude into the road at car height were dropped (cattle-grid deck, start/finish
  gantries, scaffolding, a few roadside trees).
- **Pacenotes:** 73 rows from the 72 DR2 calls (71 mapped; `modifier_a_1` x2 unmapped and dropped), via
  config/pacenote-map.toml. Corners are positioned on the road geometry (53 of 63; 10 at the DR2 call distance).
  Finish call 50 m before the finish line, "stop at marshals" after it.
- **Respawn walls:** v1 left the host's 837 invisible respawn walls where they were (on Hafren's road, not
  the DR2 one). In v1, 8 of them overlap the DR2 road, 5 at road height, so an unexpected respawn there is a v1
  artefact. v2 re-lays 592 walls end to end along the DR2 out-of-bounds contour (10.06 km, min 3.19 m from the road)
  and sinks the rest.

What to check, in addition to the v1 list:
1. Drive into a few roadside trees/fences: the car should stop or bounce (not pass through).
2. No invisible obstacle on the road itself (report the distance on the stage if there is one).
3. Pacenotes: calls arrive before the corners and match their direction/severity; "Finish" before the line.
4. Going off the road far enough triggers a respawn; staying on the road never does.

Isolation switches if v2 misbehaves (each writes to work\acr\dist-v2; rebuild, then reinstall):
`-NoObjects` (no object collision), `-NoPacenotes` (emptied table as in v1), `-Respawn root|sink` (v1 walls / all
walls sunk).

## v1.1 test: visible road (fixed v1 + interim visuals)

`work\acr\dist-v1.1\zz_acrdirt_nz01r5_P.*` uses the **same container name** as v1 and v2, so install only one of
them: remove any other `zz_acrdirt_nz01r5_P.*` from `~mods` first.
Build: `.\mods\acrdirt-stage\build.ps1 -Version v1.1 -Verify` (offline: PASS 46 / FAIL 0).
Sizes: ucas 297,359,632, utoc 55,503, pak 347. It has the same collision, pacenotes and respawn walls as fixed v1
(no objects).

What changes (details in docs/decisions.md D6):
- **Lower placement:** the whole DR2 stage sits 47 m lower, so it is on average level with Hafren's own terrain
  instead of about 54 m above it. Hafren scenery will still poke through or hang above in places, and it has no
  collision.
- **Render-only geometry**, with no collision (it is drawn by the gameplay cell's start-trigger mesh):
  - DR2 ground in the engine's grey world-grid material;
  - the DR2 road surface in flat light blue;
  - magenta posts, 1.2 m tall, every 15 m on both nominal road edges. These sit about 6 m from the centreline,
    because the IR gate width is 12 m, so they are not the exact verge;
  - green start and finish gates (5 m posts plus a banner).

Check:
1. At the start you see the light-blue road, the grey ground and the magenta posts. Drive the stage between the posts.
2. Visuals match the physics: the wheels sit on the blue/grey surface, with no bumps where nothing is drawn.
3. Whether the geometry stays visible along the whole stage, or disappears beyond some distance (note the distance
   shown on the stage, or the time).
4. Frame rate (the mesh is about 1 M triangles in one component).

If nothing is drawn:
- Rebuild with `-NoVisuals` to check the lowered placement on its own.
- Rebuild with `-GroundOffset 0` to get v1 heights with the visuals.
- Report whether the start/finish *triggers* still work (timing starts and stops). The visuals replace the
  start-trigger's own mesh.

Offline verification (stage-verify plus a CUE4Parse re-read of the shipped container) covers:
- the component is visible, not hidden in game, and in world space;
- 5 sections, all without collision, with indices in range and the expected materials;
- 488 posts, each centred on its IR gate edge within 1 cm;
- ground covering the centreline.

**v1.1 result (in game):** the car drives, but none of the geometry is visible in gameplay. During loading a grey
track was briefly visible, so the mesh does render, then disappears when gameplay starts. See the v1.2 section below.

A stage-validate re-check of the v1 run (`work\validate\nz01r5-run1.csv`) now passes fall-through. The s = 108 m
event was the car resting 0.71 m above lower DR2 terrain 8.7 m left of the road, not a fall. Samples resting within
2 m of DR2 ground no longer count as "below-road".

## v1.2 test: visuals that stay visible in gameplay

v1.1 put the geometry on the start trigger's own ProceduralMeshComponent. That component is the trigger's
`MeshComponent`, which the native `StartEndSequenceTrigger` code drives at runtime (it holds a Default and a Triggered
material for it). The cause is suspected, not verified: the native code is not readable statically, and the Blueprints
contain no script.

v1.2 keeps the same geometry on the same component but points the start trigger's `MeshComponent` property at the end
trigger's small ring. The native code then hides, rebuilds or recolours that ring, and the visuals component is
referenced by nothing. Collision, placement (DR2 lowered 47 m) and materials are unchanged from v1.1.

Install `work\acr\dist-v1.2\zz_acrdirt_nz01r5_P.*` only (same container name: remove v1/v1.1/v2 first).
Build: `.\mods\acrdirt-stage\build.ps1 -Version v1.2 -Verify` (offline: 30 PASS / 0 FAIL in direct and round-trip
checks). Sizes: ucas 297,359,632, utoc 55,503, pak 347.

Check:
1. During loading and after the countdown: the grey ground, the light-blue road, the magenta edge posts every 15 m
   and the green start gate are visible, and they stay visible while you drive.
2. The stage timer still starts at the start line and stops at the finish. The triggers' detection uses their box
   and spline locations, not the mesh, but report it if timing breaks.
3. Note how far along the stage the geometry stays visible, and the frame rate.

If the geometry still vanishes, test the ordered set in `work\acr\visual-bisect\TESTS.md`:
- **C (`C-splinesactor`):** additionally re-parents the visuals component to the `SplinesActor`, for the case where
  the trigger actor itself is hidden at race start. This is a structural package change: if the stage fails to load,
  report it.
- **B (`B-end-trigger`):** visuals on the end trigger's own mesh, nothing retargeted (diagnostic).
