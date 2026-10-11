# Decisions (ACR side)

Each entry: decision · evidence ([V] verified / [I] inferred) · consequences. Newest last.

## D1. Read ACR content with CUE4Parse + the Dumper-7 usmap (`tools/acr-ref`)

- **Decision:** a small .NET 10 CLI (`acr-ref`) on CUE4Parse 1.2.2, with mappings from
  `C:\Dumper-7\5.6.1-0+UE5-acr\Mappings\5.6.1-0+UE5-acr.usmap`.
- **Evidence:**
  - [V] Every asset needed so far exports cleanly with `EGame.GAME_UE5_6`, an empty AES key and Oodle
    downloaded at runtime: DataTables, the persistent umap (2982 exports), 163 WP cells, and the
    BakedData asset (936 MB JSON).
  - [V] 51,986 package paths are listed.
- **Consequence:** reading is solved. `acr-ref` stays read-only against the install. Edits operate on
  legacy files under `work/`.

## D2. Pack with retoc; edit legacy packages with our own byte-level writers

- **Decision:** `retoc to-legacy` → edit → `retoc to-zen --version UE5_6` into `~mods/<name>_P`.
  Edits are done by purpose-built writers (`patch-f32`, `baked-add-ramp`) rather than a generic
  property serializer.
- **Evidence:**
  - [V] The retoc round trip leaves the `.uexp` byte-identical; the `.uasset` gains one name-map entry.
  - [V] The BakedData writer reproduces the original stride-map bytes exactly before editing, and
    CUE4Parse re-reads the edited asset (735 colliders, new stride present).
  - [I] UAssetAPI could do generic property edits, but its UE 5.6 unversioned support is unproven here.
    BakedData is ~170 MB of native arrays, where a streaming writer is simpler and safer.
- **Consequence:** writers are format-specific; each new asset type needs a parser/writer plus a
  round-trip test.

## D3. Physics coordinate convention

- **Decision:** baked physics space = metres, Y up: `(x, y, z)_phys = (X, Z, Y)_UE / 100`.
- **Evidence:** [V] the start trigger's height matches `ElevationAtStart` (478.29) and lies inside the
  road colliders; the mirrored mapping hits nothing.
- **Consequence:** the DR2 → ACR converter must emit BakedData positions in this space. Simple
  colliders (capsules/boxes) stay in UE units/rotators.

## D4. Load proof first, before any route work

- **Decision:** ship two tiny, independent containers (a DT value patch and a single extra BakedData
  ramp) and let the user test them in-game (`docs/load-proof.md`).
- **Rationale:** they answer the two blocking questions:
  1. Do `~mods` IoStore overrides load at all?
  2. Does BakedData drive driving physics?

  Static analysis cannot settle the second: [V] runtime collider gathering also exists in shipping (see
  `docs/acr-stage-anatomy.md` §3).
- **Result (2026-10-11, in-game, user):**
  - [V] `~mods/zz_*_P` IoStore overrides load (the DT length showed 42 km).
  - [V] The appended BakedData ramp launched the car, so **baked collision drives physics** in shipping.
  - [V] The HUD route length comes from the route spline, not the DT row.

## D5. Authoring route — recommendation: **hybrid**

Routes compared:

- **A.** UE 5.6 editor driven headlessly, with stub classes generated from the Dumper-7 SDK.
- **B.** Editor-less: clone a template stage and swap BakedData, splines and meshes through asset
  editing plus retoc.

The recommendation: **use B for everything except new render geometry and new level packages, and A
only for those**. Build B first, because it is already working and unblocks physics testing of DR2
roads.

| Concern | Route B (editor-less) | Route A (headless UE 5.6 + stubs) |
|---|---|---|
| DataTable registration (new rows in 7+ tables, string tables) | [V] works (value patch). Row insertion needs a DT row writer; the formats are known from the exports. Medium effort. | Possible, but needs the stub row structs to match exactly. |
| Physics (BakedData) | [V] byte-exact parser/writer exists. Appending colliders works offline. Replacing all colliders is the same code path. [V] Used by the game for physics (load proof b). | **Needs Kunos' editor-only "Create Baked Colliders Asset"** (dmphysics editor module, not in the SDK dump). Stubs can't run it, so we would end up writing BakedData ourselves anyway. |
| Splines, triggers, pacenotes (actor properties in WP cells) | [I] feasible: properties of existing actors in cooked cells (DMSpline points, trigger transforms) are plain unversioned arrays and structs, like BakedData. Same-count edits are easy; growing arrays means rewriting the export and patching the header, which we already do. | Natural in editor, but every BC_* blueprint and dmphysics component needs a faithful stub (Dumper-7 gives layouts, not behaviour). |
| Render meshes (terrain, road, objects) | **[I] Very hard / unproven.** Means writing cooked `UStaticMesh` render data (LOD vertex/index buffers; Nanite resources if enabled; distance fields optional) for UE 5.6 with no existing writer. Re-using the template's meshes only gives Wales visuals over DR2 physics. | **Natural:** import FBX/glTF and cook. Stubs are only needed for the *referenced* classes; meshes and materials are engine types. |
| New level / World Partition | **[I] Hard.** A new streaming grid means generating cell packages, runtime hash and HLOD references; cloning keeps the template's 163-cell layout and geography. | Natural: WP generation and cook in editor. Risk: cooked actors must resolve to the game's `/Script/acr`, `/Script/dmphysics` and `/Game/...BC_*` classes, so stub module and package names must match. |
| Setup cost | [V] none beyond .NET and retoc (both installed) | UE 5.6 install ~60 GB (not installed, per brief), a stub project generated from the SDK, and cook settings matching ACR (IoStore, Oodle, ZenLoader, same plugin set). |
| Iteration | seconds to a minute per rebuild (`build.ps1`: ~50 s including verify) | minutes per cook |

**Evidence summary:**

- [V] Cooked levels are fully readable. The stage is a WP level with 163 cells, 87 TrackTiles, 160
  StaticMeshActors, Nanite meshes, PCG and HLODs.
- [V] BakedData edits are possible and repack with retoc.
- [V] `bForceUseBakedData` is not read in shipping code; TrackAvatar logs a missing BakedData but
  continues; runtime collider gathering exists.
- [I] Producing new cooked static meshes and new WP levels without the editor has no existing tooling
  and a large format surface. This is the dominant risk of a pure route B.

**Plan this implies:**

1. **B1, now:** use Hafren South as the template.
   - Replace the BakedData with collision generated from a DR2 stage (from the other worker's stage IR),
     aligned to the template start.
   - Replace the forward variant's splines, triggers and pacenotes, and patch the DT rows.
   - Result: a DR2 road drivable (invisibly) inside the Wales level. This validates physics scale,
     surfaces, timing and pacenotes end to end without any engine install.
2. **A1, after B1 succeeds:** a minimal UE 5.6 stub project that cooks one static mesh plus one
   non-WP level containing `BC_TrackAvatar_C` / `BC_PhysicsAvatar_C` references. Check that the game
   loads it via `DT_Levels`.
   - If yes, use A for render meshes and level packages, and keep B tools for BakedData, DT rows and
     pacenotes.
   - If loading cooked stub content fails, fall back to a B-only "re-skin" scope.
3. ~~Decide once `docs/load-proof.md` results are in.~~ Done: both load-proof tests passed, so B1 is
   unblocked. Physics can be authored entirely by writing BakedData.

## D6. B1 stage port: Hafren South forward as host; neutralise rather than delete; route-relative actors

First stage: `new_zealand_rally_01_route_5` → `zz_acrdirt_nz01r5_P`. Built by `acr-ref stage-build` via
`mods/acrdirt-stage/build.ps1`; test plan in `docs/first-stage-test.md`.

- **Host:** variant 1/2 FullForward. Overridden packages: BakedData, forward gameplay cell
  `22VG0QIP9ATUQI633K86KWPFP`, `DT_TracksVariants` and `DT_PacenoteHafrenSouthForward`. The persistent
  level is untouched: [V] `BC_PlayerStartsManager_C` holds no transforms.
- **Placement transform:**
  - Formula: `physics = R_y(θ)·(dr2 − c) + t`, then `UE cm = (px, pz, py)·100`. [V] Both frames are
    right-handed with Y up, so this is a proper rotation with no mirroring.
  - θ is searched in 1° steps to maximise the margin of the gate corridor inside the host's safe XZ
    region. That region is the forward cell's content bounds grown by its 640 m loading range,
    intersected with `PhysicsWorldBounds`: X −1950..802 m, Z −964..2048 m.
  - The DR2 collision minimum goes to y = 380 m, which keeps everything inside the physics bounds' y
    range of 305..534 m.
  - NZ route 5 result: θ = 78°, c = (−3918.19, −3256.5), t = (−560.452, −118.144, 525.77), margin
    297 m.
- **Host colliders are flattened, not deleted:**
  - Mesh colliders go to y 306..318 m; simple-collider centres to 306..315 m.
  - Owners, counts and data-layer stride maps stay intact, because [I] runtime collider bookkeeping is
    keyed by owner, and removing entries would shift every stride index.
  - DR2 collision is appended as one stride in the `None` set: 253 colliders, chunked by ACR physical
    material on a 100 m grid, ≤ 60k vertices each. Template transform, colors and owner come from host
    collider #10.
  - Missing physical materials become new imports, resolved by CUE4Parse against the game: [V]
    `PM_SurfaceGrass`, `PM_SurfaceMetal`, `PM_Fallback`.
- **Winding:** kept CCW-from-above. [V] The host's flat triangles are 99.9 % +Y under right-handed
  normals (4.74 M vs 3.6 k), and the DR2 output is 99.4 % +Y.
- **Splines:**
  - Center and Ideal are rebuilt from the DR2 gate crossings; Left/Right from the gate edges.
  - Contour is a closed loop 20 m outside the edges.
  - All use auto tangents, identity rotations, and UE's reparam algorithm (validated against the
    host's table).
  - Ideal-spline metadata = constant curves holding the host's first values.
  - Points are in driving order with `bIsReverse` untouched, exactly like the host.
- **Gameplay actors** are re-anchored by route role:
  - Each host component is projected onto the host CenterSpline to get its along-track distance,
    lateral offset, height and yaw relative to the road heading.
  - It is then re-placed with the same offsets relative to the DR2 route:
    - The start group is anchored on the DR2 start gate. CompleStart is clamped into the run-up.
    - Sectors 1/2 go to the DR2 splits nearest 1/3 and 2/3 of the stage.
    - Sector 3 goes to the finish gate.
    - The stop zone goes to DR2's own `EndSequenceTriggerBox`, and the post-race camera follows it.
  - Respawn walls are moved 1 km down.
  - Transforms are patched in place by locating the doubles whose float casts equal CUE4Parse's values
    (unique hit required).
  - Zero-valued properties that aren't serialized (e.g. sectors 1/2 have no rotation) stay
    unserialized.
- **Tables:** the FullForward row's Length becomes the DR2 length in km. The pacenote table is emptied
  (`-KeepPacenotes` keeps Hafren's as a crash-isolation fallback).
- **Verification [V] (offline only):**
  - Byte-exact round trips of all four host packages and their parsers.
  - Ground raycast at all 446 gates is within 4.4 cm of the IR gate height, with 0 gaps at ≤ 1 m
    spacing.
  - The same checks pass again on a retoc round trip of the shipped container.
  - The CUE4Parse re-read resolves every material and sees 446 spline points.
  - In-game result: pending (`docs/first-stage-test.md`).
- **Consequences:** the reverse variant becomes unusable while the mod is installed (shared
  BakedData). Visuals stay Hafren's until route A1 or a mesh writer exists.
- **Status:** v1.2 (ground, road, markers, start/finish visible; placement −47 m) is verified in game; see the
  v1.2 bullet below and `docs/first-stage-test.md`.
- **In-game result (fixed v1) [V]:** the stage loads, the menu shows 4.69 km, and the car drives on DR2
  collision. stage-validate shows 100 % of loaded wheel contacts on DR2 ground. But nothing is visible: the
  DR2 road is invisible and floats over Hafren's scenery.
- **Placement height (v1.1) [V offline]:**
  - `acr-ref height-survey` compares the DR2 gate heights with the vanilla host's up-facing collision at
    the same XZ. The host data covers only 202 of 446 gates (s ≈ 0.7–2.7 km); elsewhere the route lies
    outside Hafren's terrain footprint.
  - With v1's placement, over those gates the DR2 road sits a median +54 m above the host ground (min −55,
    max +157), and 72 gates are below it. That explains "floating in the sky": DR2 relief varies far more
    than Hafren's, so no single offset fits everywhere.
  - v1.1 lowers the placement by 47 m (`--ground-offset -47`, θ/XZ unchanged). That makes the mean
    road-minus-host difference ≈ 0 (88 of 202 gates then lie below Hafren's visual terrain; it has no
    collision there, because the host colliders are flattened). The DR2 collision minimum becomes 333 m,
    still above the flattened host colliders (≤ 318 m) and inside the physics bounds. All stage-verify
    collision checks pass unchanged.
- **Interim visuals (v1.1) [V offline, in-game pending]:**
  - These use only data edits; no editor and no new asset types. The forward gameplay cell already contains
    2 ProceduralMeshComponents: the MeshComponent of `BC_StartSequenceTrigger_C` and of
    `BC_EndSequenceTrigger_C`, both small rings with no collision. Their ProcMeshSections are a plain
    UPROPERTY array of vertices and indices. `ProcMesh.cs` parses and writes it, with byte-exact round trips
    on the host.
  - v1.1 rewrites the start-trigger component in world space (absolute location, rotation and scale; visible;
    not hidden in game) with 5 sections, all without collision:
    - 0: a dummy triangle 50 m below the start, in case the trigger code sets slot 0's material.
    - 1: DR2 non-road ground, using the engine default world-grid material.
    - 2: DR2 road surfaces (`GD0+`, `CATG`), light-blue `MI_SurfaceGravelFine`.
    - 3: edge posts every 15 m at the IR gate's left and right edges, 0.3 × 0.3 × 1.2 m, magenta
      `MI_WallMetalPole`.
    - 4: start and finish gates (5 m posts plus a banner), green `MI_SurfaceGrass`.
  - The `MI_Surface*` materials are Kunos' unlit physical-surface debug colours. They are cooked, always
    present, and need no textures.
  - Config: `config/visuals.toml`. Size: the start-trigger export grows from 1.7 kB to 67.8 MB (390 k
    terrain and 167 k road vertices). Car physics still comes only from BakedData.
  - Risks: native trigger code could hide or rebuild the mesh at runtime. The gameplay cell is not spatially loaded
    (data-layer gated only), so it stays loaded for the whole variant.
- **v1.1 in game, and the v1.2 fix:**
  - [V] v1.1's mesh renders while loading and is gone in gameplay.
  - [V] `BC_StartSequenceTrigger`, `BC_EndSequenceTrigger` and their parent `BC_StartEndSequenceTrigger` contain no
    script. Their parent is the native `StartEndSequenceTrigger` (properties MeshComponent, DefaultMaterial,
    TriggeredMaterial, MaxTriggerAngle), so the native code owns the mesh.
  - [I] That native code hides, clears or rebuilds its MeshComponent when gameplay starts.
  - [V] The forward cell is not spatially loaded (data-layer gated only), so it does not stream out during the stage.
  - v1.2 points the start trigger's serialized `MeshComponent` reference, and its CreateBeforeSerialization preload
    dependency, at the end trigger's ring. Both triggers then drive that ring, and nothing references the visuals
    component.
  - Fallbacks: `-VisualsOwner SplinesActor` re-parents the component (outer, flags, CreateBeforeCreate dependency,
    AttachParent → SplinesActor Root); `-VisualsHost end -NoRetarget` is diagnostic only.
  - [V offline] Both survive retoc's zen round trip, and CUE4Parse resolves the new owners and references.
  - Rejected: the respawn walls (hidden overlap triggers that respawn needs), and the 2 baked cable meshes in cell
    `1HVWF…` (spatially loaded, so they would stream out).
  - **[V in game] v1.2 confirmed:** the visuals stay visible for the whole drive, and terrain collision, start, timer
    and finish all work, with good frame rate. The retarget is the fix. The exact native behaviour (hide vs. clear
    vs. rebuild) remains unknown, and the fallbacks were not needed.
- **Real DR2 visuals: assessment [inferred, not implemented]:**
  - **Geometry without the editor is feasible** if v1.1 renders. ProcMeshSections take arbitrary vertices, normals,
    tangents, UV0–3 and colours, so DR2 terrain tiles and object glb meshes can be baked in with their own UVs.
  - The limits are a PMC's CPU-side, uninstanced, LOD-less data. Thousands of trees would mean duplicated
    vertices. The cell has only 2 PMCs; more components means writing new exports, which is a larger but
    similar job.
  - **Textures are the hard part.** PMC materials must be cooked material instances. The options are:
    - (a) Overwrite the bulk data of existing cooked Texture2D assets (BCn encode plus mips, same format and size)
      behind an MI used only on Hafren. This needs a texture writer, and it changes those textures wherever the
      host uses them.
    - (b) Write new Texture2D and MaterialInstanceConstant packages. This is more serializer work.
  - Either way, DR2's multi-layer terrain blending won't map onto a single MI without a matching master material.
  - **The UE 5.6 editor** gives proper StaticMeshes, LODs, instancing and materials. But it is about 60 GB, and
    cooking against Kunos' engine build risks compatibility problems.
  - **Recommendation:**
    1. First confirm in game that v1.1's PMC renders.
    2. Then go editor-less: textured terrain via (a) (about 2–4 days, medium risk), and objects merged per material
       into PMC sections with simplified LODs (about 2–3 days, risk to frame rate).
    3. Move to the editor only for full fidelity (foliage, blended terrain, LODs): 1–2 weeks plus the install, with
       high risk from cook compatibility.

## D7. Single-stage port v2: surfaces, objects, pacenotes, respawn walls (nz01r5)

- **Surfaces [V]:**
  - Every collider's physical material must be listed in `DA_PhysMaterialsMappingCommon`, the only Kunos surface
    registry (38 DMPhysicalMaterials). Otherwise the track-physics setup reads an unset `trackData.surfaceDef`
    (`ksPhysicsAC/Track.cpp`, acr.exe RVA 0x4F64604) and the stage load crashes. This happened to v1 via
    DEFA -> PM_Fallback.
  - Enforced three ways:
    - `surface-map-check` validates config/surface-map.toml against the exported vanilla registry on every build.
    - stage-build remaps any unregistered ground material to PM_SurfaceGrass and refuses to import an
      unregistered one.
    - stage-verify fails if any collider uses one.
- **Vanilla inputs [V]:**
  - All build inputs come from a hard-link view of the shipped containers (`global`, `pakchunk*-Windows`), never
    `~mods`, through the stamped cache `work/acr/legacy-vanilla`.
  - The offline check asserts that the forward row's Length equals the DR2 length.
- **Objects:** DR2 objects become invisible colliders in BakedData. Simple shapes (box, capsule) are appended to
  BakedSimpleCollisionData under the always-active None stride, reusing the host foliage actor as owner. Cones become
  small mesh colliders. Materials map to PM_Wall*. Soft vegetation and light or phantom breakables are skipped.
  Shapes that intrude more than 0.3 m into the road at car height (over a road surface code) are dropped and
  reported. Visible from v1.3 (below). From v1.4 the intrusion is measured on the shape's hull triangles, not only
  on footprint samples: a leaning trunk cone has its base ring below and its apex above the car-height band, so
  sampling only those let its body cross the road at car height. This drops 15 more shapes (12 pohutukawa trunks, 3
  scaffolding frames; 84 in total).
- **Pacenotes [V offline]:**
  - The ACR table is a list of rows: SplineDistanceM (corner start along the CenterSpline), LengthM, and tokens.
    Severity 6 is the fastest, as in DR2. Right means a positive heading change in UE XY.
  - DR2 calls map through config/pacenote-map.toml.
  - Each corner is located on the rebuilt spline by curvature, searching −30 to +300 m from the call distance with
    the direction required to match. If that fails, it is placed 80 m after the call.
  - Rows link (Into/And) only within one DR2 call. A "Finish" row sits 50 m before the line.
  - New FNames must be inserted at `NamesReferencedFromExportDataCount`: retoc keeps only that prefix for export
    data, and appended names are lost.
- **Respawn walls [V in game, v1.3]:**
  - The host's 837 respawn walls are unattached spline meshes in world space, so moving the root (v1) leaves
    them on Hafren's road; 8 of them overlap the DR2 road.
  - v2 rigidly re-lays them end to end along the DR2 out-of-bounds contour. Inserted RelativeLocation/Rotation and
    Z scale give a span from 30 m below to 15 m above the contour. Pieces closer than 3 m to the road, and spares,
    are sunk.
  - The game respawns from these walls alone: in v1.3, leaving the road put the car back on the DR2 road.
- **v1.3 = v1.2 + the features above, with visible objects [V in game, except the vegetation proxies]:**
  - In game: trunks are visible and solid, co-driver calls play, and respawn works. The dark-green non-solid
    proxies flooded the view, so v1.4 removes them (below).
  - The start-trigger mesh retarget from D6 (v1.2) is the confirmed visuals fix, and v1.3 keeps it.
  - The kept object colliders are also drawn, as extra sections of the same visuals mesh, one flat-colour
    `MI_Wall*` per class (`config/visuals.toml` `[objects]`, `[object_materials]`; `VisualObjects.cs`):
    - Trees: the trunk colliders themselves (capsule → 6-sided prism, cone, box), plus a non-solid crown proxy.
    - Other solid objects: DR2's render mesh from `render/objects.glb`, placed by the instance TRS. The glb holds one
      mesh per template in template space, the same frame as the collider shapes. Offline, 445 of 445 collider
      centres lie within the placed render bounds (+1 m). Meshes over 4,000 triangles are drawn as their colliders.
    - Anything not solid: an octahedron over the render bounds in dark green.
  - Budget: instancing every render mesh would cost 13.4 M triangles, 90 % of it vegetation, so vegetation is drawn as
    proxies. v1.3 totals 1.38 M triangles (objects 366 k), and the offline check enforces ≤ 3 M.
  - Data quirk: `maple_02_e`'s trunk cone is tagged `sArmco` (guardrail). `config/object-collision.toml`
    `[template_wall]` gives it tree-trunk physics. This also changes v2 if rebuilt.
  - Pacenotes are unchanged from v2. `modifier_a_1` (2×) stays unmapped, because the code is not decoded.
- **v1.4: no non-solid geometry, and nothing drawn in the road unless it is solid there [V offline]:**
  - Cause of the v1.3 green flood: about 7,700 soft-vegetation clumps (pohutukawa bushes, large ferns, harakeke,
    pampas, pohutukawa roots). Their DR2 render bounds are 9–14 m wide but only 1.5–3.4 m tall, with instance scales
    up to 2.8.
    - Each clump was drawn as an octahedron over the world-aligned box of its rotated bounds: up to about 17 m
      across.
    - Many stand on the road edges, so they read as flat green panels over the road.
    - The 1,487 tree crowns (the upper 70 % of the bounds) were 10–15 m wide as well.
  - Fix: non-solid objects are not drawn at all.
    - `-Vegetation crowns` (`--visuals-vegetation`) adds a small octahedron per tree, high up:
      - radius at most 2.5 m, and at most a quarter of the smaller plan extent of the DR2 crown;
      - it spans the top 40 % of the tree;
      - it is shrunk to stay 1.5 m from the road edges, or dropped.
  - Render meshes that reach into the road deeper than the collider limit + 0.2 m (car height −1 to 6 m) are drawn
    as their colliders instead. This affects 4 objects, among them the start timer and a cattle-grid sign.
  - The offline check samples every drawn object triangle (50 cm grid) against the road corridor over road
    surface:
    - At car height, objects may reach at most 0.5 m in; edge posts, centred on the gate edges, at most 0.23 m.
    - Above car height up to 6 m, a triangle in the corridor must lie inside one of the kept colliders that the
      build reports there: 48 colliders, mostly leaning pohutukawa trunks, plus the start gantry beam.
    - Crowns must not enter the corridor in plan at any height.
    - Two size checks: the largest drawn object must be ≤ 40 m in plan (the biggest is the 31 m building), and no
      object may be drawn wider than 1.15 × its DR2 render bounds + 1 m.
  - Pacenote report (`--pacenote-report`, written to `work/acr/pacenotes-<short>.md`): each row with its
    start-line distance, the call, and the strongest geometric corner within ±20 m. A corner call is flagged when
    the direction is wrong, or when a tight call has no corner there. A severity hint compares the corner's minimum
    radius with the host's median for that severity.
