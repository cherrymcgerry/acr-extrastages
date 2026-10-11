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

**v1.2 result (in game, verified by the user):**
- The road, edge markers and terrain are visible and stay visible for the whole drive.
- Terrain collision works: hitting a side wall causes damage and pushes the car back.
- Start, timer and finish all work. Frame rate is good, and the surface feels like gravel.
- So the start-trigger `MeshComponent` retarget is the confirmed fix, and the visual-bisect variants were not needed.

## v1.3 test: v1.2 + solid DR2 objects (visible) + pacenotes + respawn walls

`work\acr\dist-v1.3\zz_acrdirt_nz01r5_P.*`: same container name, so install only this one.
Build: `.\mods\acrdirt-stage\build.ps1 -Version v1.3 -Verify` (offline: CUE4Parse 11 PASS / 0 FAIL, round trip
30 PASS / 0 FAIL). Sizes: ucas 349,199,620, utoc 64,995, pak 347.

What v1.3 adds to v1.2 (placement, ground and the visuals fix are unchanged):
- **Object collision (as v2):** 3,189 collider shapes from 1,843 DR2 objects, as 2,205 simple entries (boxes and
  capsules) plus 1 mesh collider for cones (trunk bases), all with registered `PM_Wall*` materials.
  - 69 shapes that would intrude into the road at car height are dropped.
  - Skipped as drive-through: soft vegetation (7,114 shapes) and light, lightest and phantom breakables (1,396).
  - New: `maple_02_e`'s trunk is tagged as guardrail in the DR2 data. It now gets tree-trunk physics
    (`config/object-collision.toml` `[template_wall]`).
- **Objects drawn in flat colours:** every solid object is visible (1,843 of 1,843):
  - tree trunks: orange prisms on exactly the trunk colliders, with a dark-green crown that is NOT solid;
  - wood (fences, gates, posts, sheds): yellow DR2 meshes;
  - metal (fences, signs, the timing gantry): violet;
  - concrete (building): blue; rock: cyan; plastic: bright green;
  - cars and vans: black boxes, drawn as their colliders;
  - **dark green = drive-through:** bushes, ferns, grass, cloth, light breakables, and objects dropped from the road
    corridor (8,766 low-poly octahedra).

  Render meshes are DR2's own meshes up to 4,000 triangles per object. Heavier ones (vehicles, scaffolding banners,
  the start gantry) are drawn as their collider shapes. Offline, every collider centre lies within its object's
  render bounds (445 of 445 checked).
- **Triangles:** 1,377,177 in total (ground 1,006,007, markers and gates 4,928, objects 366,241; v1.2 had 1.01 M).
  The visuals export is 118.9 MB (v1.2: 67.8 MB).
- **Pacenotes (as v2):** 73 rows from the 72 DR2 calls.
  - The build reports 71 of 72 calls as mapped. Two calls carry the undecoded DR2 modifier `modifier_a_1`, which is
    dropped; both corners are still called.
  - 53 of 63 corners are positioned on the road geometry; the other 10 sit at the DR2 call distance.
  - The finish call comes 50 m before the line (at 4,671 m).
- **Respawn walls (as v2):** fixes v1's walls on Hafren's road, 8 of which overlapped the DR2 road.
  - 592 walls are re-laid end to end along the DR2 out-of-bounds contour (9.5 of 10.1 km covered), at least 3.19 m
    from the road edges.
  - 6 walls that were too close to the road are sunk, along with 239 spares.
  - Where the car is put back is decided by the game. Its race splines (CenterSpline, 446 points, 4,848.8 m) are
    rebuilt from the DR2 gates, and the ground raycast hits DR2 ground at all 446 gate crossings (max 4.4 cm off), so
    a respawn should land on the DR2 road. This is inferred until tested.

Check:
1. Objects are visible and solid: drive gently into an orange trunk, a yellow fence and a cyan rock; the car should
   stop or bounce. Dark-green shapes should let you through. Note anything solid that you cannot see, or anything
   coloured (not dark green) that you pass through, with the stage distance.
2. Co-driver: calls come before the corners and match their direction and severity; "Finish" comes before the line.
3. Respawn: leave the road far enough to trigger a respawn, and also use the manual reset. Both should put you back
   on the road (report where if not). Staying on the road should never trigger one.
4. Frame rate compared with v1.2, especially in the forest sections.
5. As in v1.2: timer start and finish, and the visuals stay visible.

Isolation switches if v1.3 misbehaves:
- `-NoObjects`: no object collision or object visuals.
- `-NoPacenotes`.
- `-Respawn root|sink`.

Optional texture experiment (docs/visuals-plan.md): `work\acr\tex-proof\dist` is v1.3 with Hafren's own terrain blend
and gravel road materials on the ground sections instead of grid and light blue. Install it like v1.3 and report how
the ground looks (screenshot): textured, stretched, or a flat or black colour.

**Result (user, in game, screenshot at 0.6 km, 03:01):**
- Works:
  - Orange trunks are visible and solid (collision confirmed).
  - Co-driver calls play.
  - Leaving the road respawns the car on the DR2 road (respawn confirmed).
- Problem: dark-green non-solid shapes flood the view as huge flat panels. The road is hard to see, so fences and
  signs were not found and the pacenotes could not be checked.
- Cause: the soft-vegetation clumps have bounds 9–14 m wide but 1.5–3.4 m tall, and they were drawn as octahedra
  over their rotated world box (up to about 17 m), many of them on the road edges. Fixed in v1.4.

## v1.4 test: v1.3 without the green, nothing in the road unless solid, pacenote report

`work\acr\dist-v1.4\zz_acrdirt_nz01r5_P.*`: same container name, so install only this one (remove v1.3's three files
first). Build: `.\mods\acrdirt-stage\build.ps1 -Version v1.4 -Verify`.
- Offline: CUE4Parse 11 PASS / 0 FAIL, round trip 42 PASS / 0 FAIL.
- Sizes: ucas 327,522,107, utoc 61,023, pak 347.

What changed from v1.3:
- **No non-solid geometry:** 8,769 bushes, ferns, grass, cloth, light breakables and road-dropped objects are not
  drawn at all. Tree crowns are off by default.
- **Solid objects unchanged:** all 1,840 are drawn in their class colour. Trunks are orange; wood yellow; metal
  violet; concrete blue; rock cyan; plastic bright green; cars black.
- **No visuals in the road unless solid there (offline check):**
  - At car height, nothing drawn reaches more than 0.5 m into the road over road surface.
  - Above car height, up to 6 m, only real colliders reach in: 48 leaning pohutukawa trunks over the road and the
    start gantry.
  - 4 render meshes that reached in are drawn as their colliders: the start timer, a cattle-grid sign, a high
    fence and a place sign.
- **Collision fix:** 15 more collider shapes are dropped from the road (84 in total): 12 leaning pohutukawa trunks
  and 3 scaffolding-banner frames. Their bodies crossed the road at car height between the points that v1.3
  sampled. If something orange or violet stood in the road in v1.3 and was solid, this was probably it.
- **Largest drawn object:** 31 m (the building); no object is drawn larger than its DR2 bounds.
- **Triangles:** 1,289,685 (objects 278,749); the visuals export is 97.2 MB.
- **Pacenote report:** `work\acr\pacenotes-nz01r5-v1.4.md` has every call with its distance from the start line and the
  DR2 corner measured there.
  - 61 corner calls, of which 2 have the wrong direction:
    - Left4 at 1,238 m: a right corner of 60° is there.
    - Left4 at 2,146 m: a right corner of 83° is there.
  - 15 severity hints, i.e. the radius is far off the host's typical radius for that number. Watch especially:
    - Left5 at 380 m (75°, R 14 m);
    - RightHP at 1,578 m (41°, R 45 m);
    - Left6 at 1,656 m (183°, R 14 m);
    - Left6 at 2,582 m (146°, R 17 m).

Check:
1. The road is clearly visible, with no green anywhere (default build).
2. Fences, signs and gates: yellow and violet objects at the roadside. Touch one gently; it should be solid. Note
   anything you can see on the road but drive through, or anything solid you cannot see, with the stage distance.
3. Pacenotes: with the report open, check the 2 flagged Left4 calls and the 4 rows above. Is each call wrong, or
   is the report wrong?
4. As in v1.3: respawn, the timer, and frame rate (it should be the same or better: 87 k fewer triangles).

Variant: `work\acr\dist-v1.4-crowns` (`-Vegetation crowns -Tag nz01r5-v1.4-crowns -Out work\acr\dist-v1.4-crowns`).
- Adds 1,030 small dark-green crowns (8,240 triangles), at most 5 m wide, in the top 40 % of each tree and at least
  1.5 m from the road edges. 457 crowns are dropped as too low, too small, or too close to the road.
- Offline: 11 / 0 and 43 / 0. Sizes: ucas 329,648,179, utoc 61,407, pak 347.
- Try it only if the default build looks good, and report whether the crowns help or clutter.

**Result (in game, default build):**
- The road is clearly visible, with no green.
- Collision on the yellow and violet fences is fine.
- Respawn works; the timer and frame rate are unchanged.
- Two faults:
  - The pacenotes drift: calls come progressively too late along the stage.
  - The orange "trunks" show as huge flat triangular panels on both sides of the road.

## v1.5 test: calls on their own corners, canopies drawn at car height only

`work\acr\dist-v1.5\zz_acrdirt_nz01r5_P.*`: same container name, so remove v1.4's three files first. Build:
`.\mods\acrdirt-stage\build.ps1 -Version v1.5 -Verify`.
- Offline: CUE4Parse 11 PASS / 0 FAIL, round trip 44 PASS / 0 FAIL.
- Sizes: ucas 329,207,724, utoc 61,335, pak 347.
- Triangles: 1,296,219 (objects 285,283).

What changed from v1.4 (details in docs/decisions.md, D7 v1.5):
- **Pacenotes:** the drift was a one-corner offset, not a distance-scale error. The scale checks out: ACR's
  SplineDistanceM is CenterSpline metres (the host fits at shift 0, scale 1.000), and DR2's distances agree with
  ours within 0.33 %.
  - DR2's first call ("Right2 into Right1") is one merged corner. v1.4 gave its Right1 the next right turn, so every
    later call was one corner late up to about 2 km: 112 m late on average in the first 500 m, rising to 181 m by
    1,500–2,000 m. After 2 km v1.4 was right.
  - v1.5 aligns all calls to the corners in order. DR2's read point now sits 64–93 m before each called corner on
    average (DR2 itself reads 50–150 m ahead). After 2 km the rows are identical to v1.4.
  - Report: `work\acr\pacenotes-nz01r5-v1.5.md`, with each call's v1.4 and v1.5 distance and the drift table.
    0 mismatches and 2 severity hints, down from 2 and 15.
- **Orange panels:** these were DR2's tree canopy colliders, not trunks. They are upside-down cones about 5.5 m
  wide and 6.4 m tall, sitting on the trunk.
  - The collider itself is that big in DR2 too; it is unchanged. At car height it is only the narrow tip near the
    trunk, which is why collision felt fine.
  - v1.5 draws only the part below 2.5 m above the tree base: a small orange cone tip on the trunk, about 0.9 m
    wide at the top. That part is what the car can hit.
  - 11 canopies whose tip is above 2.5 m are not drawn.
- **New offline check:** every drawn shape (2,722) stays inside its collider's own bounds.

Check (distances are metres from the start line; with Normal anticipation a call should come about 5–6 s before its
corner):
1. **Start to 400 m:** "Right2 into Right1" for the long right from 156 m, then "Left5" at 230 m and "Right2" at 328 m.
   In v1.4 these were all one corner late.
2. **380 m Left2** (75°, tight), **434 m Right3**, then **576 m Left5** and **614 m Right2 and Left1** (664 m).
3. **1,098 m Left4** (a gentle left): in v1.4 this call landed on a right turn at 1,238 m.
4. **1,412 m RightHP** (160°), then **1,656 m Left3 into LeftHP** (183° over 1,656–1,772 m).
5. **1,864 m Left4, 1,950 m Right4, 2,056 m Left4**: the end of the former drift. v1.4's third Left4 sat on a right
   turn at 2,146 m.
6. **2,140 m Right2** and everything after it is unchanged from v1.4, as a control. For example: **3,370 m LeftHP**,
   **3,918 m RightHP**, **4,262 m LeftHP**.
7. Still flagged, worth a listen:
   - **2,582 m Left6 then Left3** (2,620 m) on one long 146° left that tightens;
   - **3,118 m Right4 into Right3** (3,158 m) on a 74° right.
8. The one call not placed on a corner: **Right6 at 515 m** (no right turn found there; placed 80 m after DR2's read
   point).
9. **Trees:** no large orange panels; trunks with a small orange cone tip at about 1.5–2.5 m. Tree collision should
   feel the same as in v1.4.
10. As before: road visible, fences solid, respawn, timer, frame rate.

Report per checkpoint: whether the call came too early, on time, or too late, and whether the severity sounded right.
If it's still late everywhere, try Pacenote Anticipation "Early" once and say whether that fixes it.

**Result (in game):**
- The pacenote calls now seem correct.
- The trunks look OK.
- With that, the single stage is functionally complete: physics, objects, timing, respawn and pacenotes. The rest
  is presentation: Hafren South's scenery is still visible, and the menu still names the Welsh stage.

## v1.6 test: Hafren South's scenery hidden, DR2 vista, "Te Awanga Sprint Reverse"

`work\acr\dist-v1.6\zz_acrdirt_nz01r5_P.*`: same container name, so remove v1.5's three files first. Build:
`.\mods\acrdirt-stage\build.ps1 -Version v1.6 -Verify`.
- Offline: 118 PASS / 0 FAIL (CUE4Parse on the mounted container 11, round trip 53).
- Sizes: ucas 397,253,138, utoc 74,132, pak 347. The container is 68 MB bigger than v1.5, because it now also carries
  the persistent level (WalesS4HafrenSouth.umap, with its 20 MB .ubulk) and ST_Track.
- Triangles: 1,519,474 (v1.5's 1,296,219 plus 223,255 for the vista).

What changed from v1.5 (details in docs/decisions.md, D7 v1.6). Everything is the same as v1.5 except:
- **Hafren streaming cells:** 128 of the persistent level's 163 streaming cells no longer load in this variant. They
  are retagged to the reverse variant's data layers only (config/scenery.toml).
  - These cells hold all of Hafren's terrain tiles, foliage, PCG grass, tapes, crowds, marshals, TV cameras and
    physics props.
  - The only forward cell left is 22VG, the gameplay cell with our visuals, triggers, pacenote and respawn edits.
- **Persistent-level visuals:** 107 actors (338 components) are set invisible:
  - all 87 HLODs, which UE draws in place of unloaded cells;
  - the 4 Hafren terrain tiles (about 2 km each) and the 2 background meshes;
  - 7 placed trees, the treelines container and the 6 wind turbines.
- **Horizon:** DR2's own distant vista ring (render/vista.glb, out to about 10 km) is drawn in the same default
  grey-grid material as the terrain. Its nearest vertex is 83 m from the road.
- **Names:** the variant's string-table entries are "Te Awanga Sprint Reverse" (stage name) and "Te Awanga Sprint"
  (short name), replacing "Afon Bidno - Severn" and "Afon Bidno".
  - The track name "Hafren South" and the country "Wales" are unchanged, because other variants share them.
  - The map, elevation and lobby images are unchanged (they are textures).
- **Unchanged:** sky, weather, time of day, lighting, post process, reflection captures, audio volumes, the managers
  and the BakedData physics.
  - The service park's own persistent actors also stay: its ground, decals, barriers and props (level X 3.2–3.3 km,
    Y 0.75–1.26 km), and the radio tower next to it.

Check:
1. **Stage select and loading screen:** the stage reads "Te Awanga Sprint Reverse" (or "Te Awanga Sprint" where a
   short name is shown).
   - The location and country still say Hafren South / Wales, and the map image is Hafren's.
   - Note where a Welsh name still appears, including the HUD and results screen.
2. **Loads, and the start works:** the countdown, the start line, the timer and the finish, as before.
3. **Scenery:**
   - no Hafren terrain, trees, grass, hedges, tapes, crowds, marshals or wind turbines anywhere;
   - only the DR2 ground, the coloured DR2 objects, the edge posts and gates, and the grey vista hills on the horizon;
   - nothing floating or cut off where Hafren used to be.
   - Note anything that still looks Welsh, and where.
4. **Horizon:** grey hills all round at a distance. Note whether any of them cut through the near DR2 ground or hang
   in the air.
5. **Frame rate:** compared with v1.5 it should be the same or better. This is inferred, not measured: Hafren's
   streamed terrain, foliage, HLODs and crowds are gone, and the vista adds one 223k-triangle mesh section. Please
   give rough numbers or impressions for both versions if you can.
6. **Gameplay as in v1.5:** pacenotes, solid objects, respawn (drive off the road once), the trunks.
7. **Sky:** unchanged, the Welsh November afternoon at 15:59. This is expected; sky changes are a later step.
8. Optionally, the reverse variant (Afon Bidno reverse) still loads Hafren's own cells. It was already unusable,
   because of our BakedData physics.

Report:
- the names you see in each place;
- anything Welsh that's still visible, and where;
- how the horizon looks;
- the frame rate;
- anything that broke compared with v1.5, such as audio, replay cameras or the start sequence.
