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
