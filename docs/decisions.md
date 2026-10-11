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
| Physics (BakedData) | [V] byte-exact parser/writer exists. Appending colliders works offline. Replacing all colliders is the same code path. **Pending in-game test (b).** | **Needs Kunos' editor-only "Create Baked Colliders Asset"** (dmphysics editor module, not in the SDK dump). Stubs can't run it, so we would end up writing BakedData ourselves anyway. |
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
