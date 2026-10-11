# Stage IR (intermediate representation), schema version 2

The stage IR is the hand-off between the DR2 side (extraction) and the ACR side (mod building). One directory per
stage, produced end-to-end from a DR2 install by:

```powershell
pwsh -NoProfile -File scripts\bootstrap.ps1     # once: third_party/ at pinned commits
pwsh -NoProfile -File scripts\build.ps1
tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe stage-ir new_zealand_rally_01_route_5 --render
tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe stage-ir --all            # every stage, no render meshes
```

Options:
- `--render`: also export render meshes (terrain tiers and object prototypes).
- `--all`: build every stage in the install, then write `work/ir/report.md` (see below).
  - `--filter <regex>` limits it to matching location archives (default `_rally_`).
  - A stage that fails is reported, and the batch continues.
- `--corridor <m>`: crop radius around the route, default 250. Applies to ground and objects.
- `--out <dir>`: output root, default `work/ir`.
- `--dr2 <install>`: default `%DR2_DIR%`, else `C:\Games\DiRT Rally 2.0`.
- `--cache <dir>`: extracted DR2 files, default `work/dr2/cache`.
- `--surface-map <toml>`: default `config/surface-map.toml`.

All output is generated from game data and lives under the gitignored `work/`.

`dr2-extract catalog` lists every valid stage id (156 rally stages); see docs/dr2-formats.md.

Version 2 (changes from v1):
- adds `objects`, `respawn`, normalized `pacenotes.tokens` (raw calls move to `pacenotes.raw`), and
  `validation.route_map_fit` / `validation.object_shapes_vs_render`;
- states the handedness as verified.

## Layout

```
work/ir/<stage_id>/
  stage.json                     everything except geometry
  collision.glb                  physics ground, one primitive per DR2 surface code
  objects_collision.glb          object collision primitives (trees, fences, posts, rocks, ...), one primitive per ACR wall material
  render/terrain.glb             full-detail terrain        (only with --render)
  render/terrain_decals.glb      road decals over it
  render/terrain_low.glb         low-LOD terrain of the same area (alternative to terrain.glb, not an addition)
  render/vista.glb               distant scenery ring, not cropped
  render/objects.glb             one render prototype per object template, in template space
  render/<name>.materials.json   shader + texture names for each glTF material in <name>.glb
work/ir/report.md                per-stage summary of the last `stage-ir --all`
```

`<stage_id>` is `<location>_route_<n>`, where `<location>` is the DR2 location folder (`new_zealand_rally_01`)
and `n` is the route index 0-5.

## Coordinate convention

- **Frame:** DR2 native world coordinates, copied unchanged from the game files. Metres, **+Y up**.
- **Handedness:** **right-handed** (the glTF convention), verified three independent ways (details in docs/dr2-formats.md):
  1. **Route maps.** The gate polyline matches the stage-select route map by a plain rotation, not a mirror image,
     on 154 of 156 stages. The other two fit badly either way.
  2. **Pacenotes.** Calls transcribed by players as "left" are followed by a turn towards `cross(+Y, f)` in 197 of
     201 sharp corners; calls heard as "right" turn towards `cross(f, +Y)` in 233 of 246.
  3. **Gates.** Each gate's `right` point lies at `normalize(cross(f, +Y))` for travel direction `f`.
- All files share one frame: `progress_track.xml` gates, triggers, the physics ground, object instances and the
  render meshes. On the reference stage:
  - physics ground height at all 446 gate crossings matches the gate Y within 4.4 cm;
  - object origins sit a median 0.2 m from the ground.
- **glTF files are in the same frame.** No axis swap, no scale and no re-centering. Coordinates are large (about
  -5000..-2900 in X for NZ), so apply an offset on the ACR side if float precision matters. Triangle winding is
  counter-clockwise from the front, as glTF expects.
- Arrays `[x, y, z]` in stage.json are rounded to 1 mm, and direction vectors to 1e-5.
- **Transforms** (objects) are `{ position, rotation, scale, matrix }`:
  - `rotation` is a unit quaternion `[x, y, z, w]` in the same frame.
  - `scale` is `null` when it is 1.
  - `matrix` is `null` unless the transform could not be decomposed. It is then the raw 16 floats, row-major,
    applied to row vectors (`p' = p * M`, translation in the last row; this is the System.Numerics convention).
  - Composition: `world(shape) = shape.transform * instance.transform`.

## stage.json

Snake_case keys; `null` where a value is unavailable. Top level:

| key | type | meaning |
|---|---|---|
| `schema`, `version` | string, int | `"acr-dirt/stage-ir"`, `2` |
| `id` | string | stage id, e.g. `new_zealand_rally_01_route_5` |
| `name` | string | English stage name (`language_eng.lng` key `lng_<location>_route_<n>`) |
| `location` | object | `id` (location folder), `country` (archive prefix), `name` (e.g. "Hawkes Bay, New Zealand"), `archive`, `route_index` |
| `length_m` | number | competitive length: `distance(finish gate) - distance(start gate)` |
| `total_distance_m` | number | `progress_track` total, which includes run-up and run-out |
| `track_type`, `direction` | string | as stored (`point_to_point`, `forwards`; every DR2 rally route says `forwards`, reverse stages are separate routes) |
| `coordinates` | object | the convention above, stated in the file |
| `centreline` | `[[x,y,z]]` | route polyline = gate crossing points in driving order, about 10-15 m apart, from the run-up before the start through the run-out after the finish |
| `gates` | array | see below |
| `timing` | array | start, splits and finish, see below |
| `pacenotes` | object/null | see below |
| `triggers` | array | box triggers along the route, see below |
| `respawn` | object | see below |
| `collision` | object | physics ground, see below |
| `surfaces` | array | see below |
| `objects` | object/null | object placements and collision, see below (`null` if the route has no objects.ens) |
| `render` | array | terrain render mesh files (empty without `--render`) |
| `validation` | object | self-checks, see below |
| `sources` | object | archive path -> DR2 archive it was read from, after patch layering |

### gates[]

`{ id, distance, crossing, left, right, width }`. `distance` is metres along the route from the progress-track
origin (gate 0 is at about 5 m). `crossing` is the centreline point. `left` and `right` are the road-edge points
across the gate (the drivable width as authored, not the collision extent). `width = |right - left|`.

### timing[]

One entry per DR2 split, in route order:
`{ type, split_id, gate, distance, distance_from_start, position, forward, left, right }`.

- `type` is one of:
  - `start`: the start line; place the car here facing `forward`.
  - `time`: an intermediate split.
  - `shake_finish`: the end of the shakedown (practice) section.
  - `finish`: the finish line.
- `position` is the gate crossing point and `forward` the unit direction of travel there (central difference of the
  neighbouring crossings). `left`/`right` give the line's extent.
- `distance_from_start` is `distance - start.distance`.

### pacenotes

```json
{ "source": "<archive path>", "distance_origin": "start_line", "distance_reference": "...",
  "start_gate_distance": 40.52, "shakedown_cutoff_m": 1127, "finish_call_distance_m": 1230,
  "tokens": [
    { "call": 2, "subcall": 1, "distance_m": 160, "delay_ms": 2700, "kind": "corner", "direction": "right",
      "severity": "2", "angle": 60, "modifiers": ["dont_cut"], "link_code": 12,
      "gate_distance": 200.52, "position": [-4823.16, 584.831, -2106.28] },
    { "call": 4, "subcall": 1, "distance_m": 370, "delay_ms": 1500, "kind": "distance", "distance_call_m": 30,
      "modifiers": [], "gate_distance": 410.52, "position": [..] } ],
  "raw": [ { "distance": 160, "name": null, "audio": "cd_nz_01_med_rvs_063",
             "subcalls": [ { "type": 1, "delay_ms": 0, "angle": 25, "modifier_a": 0, "modifier_b": 0,
                             "can_concat_mod_a": true, "can_concat_mod_b": true, "distance_link": 13 } ] } ] }
```

Each DR2 call is one recorded phrase (`raw[].audio`, a sound in the location's codriver soundbank; the `.bnk` files
are not exported) with up to 3 subcalls. `tokens` has one entry per non-empty subcall, in order. Field semantics and
the evidence for each are in docs/dr2-formats.md ("Codriver calls"). In short:

- `distance_m`: metres along the route **from the start line**. This is verified across 153 stages. The note is
  read when the car passes this point; the corner it describes typically lies 50-150 m further on.
  - `gate_distance` is the same point on the `gates[].distance` scale (`start_gate_distance + distance_m`).
  - `position` is the centreline point there.
- `delay_ms`: the raw subcall delay within the phrase. Its exact meaning is unverified.
- `kind` is one of:
  - `corner`: has `direction` (`left`/`right`), `severity` and the raw `angle`. `severity` is DR2's corner scale:
    `"6"` (fastest) through `"1"`, then `"square"` and `"hairpin"`.
  - `distance`: has `distance_call_m`, the spoken distance (e.g. "30"). It is `null` with a `link_code` when the
    code is not in the verified table.
  - `finish`.
  - `unknown`: has the raw `type` (types 5, 7, 8 and 9: 1,432 of 18,424 tokens across all stages).
- `modifiers`: `"dont_cut"` (modifierA 3) or raw `"modifier_a_<n>"` / `"modifier_b_<n>"`.
- `then_distance_m`: present when a non-distance subcall links to a distance ("... 100").
- `link_code`: the raw distanceLink otherwise (e.g. 12/13 on corners, believed to encode "into"/"long"-type links;
  not verified).
- `raw` is the unmodified DR2 data (schema v1's `calls`).

Mapping to ACR pacenote tokens is left to the ACR side.

### triggers[]

`{ type, name, position, forward, size_whl }` for box-shaped triggers in `triggers_game_object.xml`, converted
from DR2's inverse transforms to world space. `size_whl` is width/height/length in metres along the box's local
X/Y/Z, and `forward` is its local +Z. Useful types:
- `VehicleBox` `EndSequenceTriggerBox*`: where the car is stopped after the finish.
- `CattleGridTrigger`: a cattle grid.
- `fe_hazard_approach_*` / `fe_hazard_cleared_*`: hazard-warning boxes.

Point-like triggers without a box are omitted.

### respawn

```json
{ "method": "gates", "notes": "...", "median_gate_spacing_m": 11,
  "resetlines": { "source": "resetlines.cqtc", "bytes": 210657, "header_word": 3,
                  "chunks": [ { "tag": "REST", "offset": 148, "size": 22630 }, { "tag": "CRES", ... }, { "tag": "SCRS", ... } ],
                  "rest_bounds": { "min": [..], "max": [..] } } }
```

If the file cannot be read, `resetlines` is `{ "error": "..." }` (only `scotland_rally_04_route_2`, whose entry
uses a compression method the NeFS reader does not support).

DR2's own reset data (`route_N/resetlines.cqtc`) is only decoded down to its chunk table. Use the gates as respawn
points:
- position: `crossing`, which is on the road (the ground there is within a few cm, see validation);
- heading: `crossing[i+1] - crossing[i-1]`;
- usable width: `left`..`right`.

Gates are about 11 m apart.

### collision (physics ground)

```json
{ "file": "collision.glb", "source": ".../route_5/track.jpk", "corridor_m": 250, "triangles": 1006007,
  "bounds": { "min": [..], "max": [..] },
  "meshes": [ { "mesh": "collision", "gltf_material": "GD0+", "dr2_surface": "GD0+",
                "acr_physical_material": "PM_SurfaceGravelHard",
                "acr_asset_path": "/Game/Data/Gameplay/PhysicalMaterials/PM_SurfaceGravelHard", "triangles": 316041 } ],
  "flags": { "0": 974, "7": 963177 } }
```

- `collision.glb` holds a single node and mesh named `collision`, with one primitive per DR2 surface code. The glTF
  material name is the code (`meshes[].gltf_material`). The geometry is positions only, with no normals or UVs.
- The source is the DR2 physics ground: road, verges and terrain as one continuous surface. Triangles whose centroid
  lies within `corridor_m` (XZ distance) of the centreline are kept.
- Object collision (trees, fences, ...) is separate, in `objects`.
- `flags` is a histogram of the 3-bit per-triangle flag field in DR2's ground format. Its meaning is unknown, and it
  is kept for future use.

### surfaces[]

`{ code, acr_physical_material, triangles, dr2 }` for every surface code in the collision mesh. `dr2` holds the
resolved DR2 definition: `base_chain`, `friction`, `mechanics`, `audio`, `particle`, `mud`. The mapping comes from
`[surfaces]` in `config/surface-map.toml`, and `stage-ir` **fails** (exit code 1, no stage.json written) if any code
in the corridor is unmapped.

### objects

Everything DR2 places along the route: trees, bushes, fences, posts, signs, rocks, buildings, barriers, parked cars
and so on. The source is `route_N/objects.ens`, with templates from `route_N/route_objecttypes.pssg`. Instances whose
origin lies within `corridor_m` of the centreline are kept.

```json
{ "sources": { "instances": ".../route_5/objects.ens", "templates": [".../route_5/route_objecttypes.pssg"],
               "shape_classes": "tracks/generic/physparams.xml" },
  "corridor_m": 250, "instances_total": 23025, "instances_in_corridor": 10609, "cloth_instances_skipped": 131,
  "unknown_shape_kinds": [],
  "collision": { "file": "objects_collision.glb", "triangles": 554192,
                 "meshes": [ { "gltf_material": "PM_WallWoodTreeTrunks", "acr_physical_material": "PM_WallWoodTreeTrunks",
                               "acr_asset_path": "/Game/Data/Gameplay/PhysicalMaterials/PM_WallWoodTreeTrunks", "primitives": 2491 } ],
                 "mesh_shapes": [ { "instance": "...", "template": "core_padd_tarp_01_driveable", "file": "xxx.vcqtc",
                                    "acr_wall": "PM_WallPlasticBarrier", "transform": {..} } ] },
  "render": { "file": "render/objects.glb", "materials": "render/objects.materials.json", "prototypes": 99,
              "triangles": 212368, "missing_renderables": [], "bounds": { "<template>": { "min": [..], "max": [..] } } },
  "wall_classes": [ { "shape_class": "sSolidTreeLeafy", "dr2_material": "TREL", "dr2_core_material": null,
                      "acr_wall": ["PM_WallWoodTreeTrunks"], "templates": 7 } ],
  "templates": { "pohutukawa_01_a": {
      "source": ".../route_objecttypes.pssg", "renderables": ["trees.pssg#default"],
      "inactive_subentities": 0, "constraints": 0,
      "bodies": [ { "sub_entity": "...", "static": true, "renderable": "trees.pssg#default",
                    "shapes": [ { "id": "pohutukawa_01_a_col_shape", "kind": "cylinder",
                                  "dimensions": { "height": 2.501893, "radius": 0.2190365, "shell": 0.02 },
                                  "transform": { "position": [0.076, 0.953, 0.033], "rotation": [..], "scale": null, "matrix": null },
                                  "shape_class": "sSolidTreeLeafy", "dr2_material": "TREL",
                                  "acr_wall": "PM_WallWoodTreeTrunks", "breaks_on_impulse": null, "mesh_file": null } ] } ] } },
  "instances": [ { "id": "...", "template": "pohutukawa_01_a", "kind": "basic", "tag": null, "transform": {..} } ] }
```

- **instances[]**: one placed object each. `template` keys into `templates`. `transform` is the instance's world
  transform; the origin is at the object's base, on the ground.
- **templates**: only the templates used in the corridor are listed. Each has one entry per rigid body in the
  object's **initial (undamaged) state**. DR2 swaps in broken pieces on impact; `inactive_subentities` counts those
  omitted pieces.
  - `static: true` means infinite mass (trees, rocks, buildings).
  - `static: false` means a movable or breakable body (fence rails, signs, cones), often anchored to the world by
    `constraints`.
- **shapes[]**: a collision primitive in template space; `world = shape.transform * instance.transform`. Per `kind`:
  - `box`: `dimensions.width/height/length` are full extents along local x/y/z, centred.
  - `cylinder`: `radius`, `height`, axis = local y, centred.
  - `cone`: `baseRadius`, `fullHeight`, axis = local +y pointing at the apex; the origin is the cone's centroid,
    i.e. the base is `fullHeight/4` below it. Tree canopies are inverted cones (local y points down).
  - `sphere`: `radius`, centred.
  - `vcquadtree`: a triangle mesh in DR2's ground format (`mesh_file`, e.g. drivable tarps and podiums). It is
    listed under `collision.mesh_shapes` and not tessellated.
  - `shell` is a DR2 collision margin (0.02 m).
- **shape fields**:
  - `shape_class`: the physparams.xml class.
  - `dr2_material`: that class's 4-char object material.
  - `acr_wall`: the ACR material, from `[walls]` / `[wall_templates]` in `config/surface-map.toml`; `"none"` means
    not collidable (DR2 spawn markers).
  - `breaks_on_impulse`: the physparams impulse class (`bLightest` < `bLight` < `bMedium` < `bHeavy`) at which DR2
    breaks the object into its damage state.
- **collision**: `objects_collision.glb` holds every non-`none` primitive of every instance, tessellated in world
  space (8 segments), with one glTF primitive per ACR wall material (the glTF material name).
- **render** (only with `--render`): `render/objects.glb` has one node + mesh per template, named by template id,
  in template space (LOD0). Instance it with `instances[].transform`. The glTF materials work like the terrain's,
  with shader and textures listed in `render/objects.materials.json`.
- **Fails loudly:** `stage-ir` fails if a shape class used in the corridor is unmapped, or an instance references a
  missing template.
- **Not included:**
  - cloth (power-line cables, tape between poles), only counted;
  - `trees.bin` / `ornaments.bin` / `crowd_*.bin` (render-only instance lists, e.g. distant trees, small props,
    spectators), not decoded.

### render[]

`{ file, kind, materials, source, nodes, corridor_m, triangles, winding_matches_normals, bounds }`.

- `kind` is one of `terrain_detail`, `terrain_decals`, `terrain_low_lod` or `vista`.
- The glb files have one node and mesh per DR2 render tile, with transforms baked into the vertices. Vertex
  attributes are POSITION, NORMAL, COLOR_0 and TEXCOORD_0/1.
  - On terrain, COLOR_0 is DR2's per-vertex colour stream, most likely terrain layer blend weights (not verified).
  - TEXCOORD_0/1 are the first two UV sets.
- glTF materials are placeholders named after the DR2 shader instance. `<name>.materials.json` lists per material
  the `shader_group` (`terrain_array.fx`, `decal_ao.fx`, ...) and `textures[]` as `{parameter, texture}`, e.g.
  `TColourMap = #nz_01_a_01_d.tga`. Textures are not embedded. `pssg-to-gltf --textures` can dump them as DDS.

### validation

- `ground_height_at_gates`: `{checked, missing, max_abs_dy_m, mean_abs_dy_m}`, i.e. physics ground height under each
  gate crossing vs the crossing's Y. It is a frame/extraction sanity check; stage-ir prints a warning when a gate has
  no ground or `|dy| > 2 m`.
- `collision_ccw_from_above`: fraction of collision triangles that are counter-clockwise seen from +Y (0.97 for
  the reference stage; the rest are steep banks and the like).
- `route_map_fit`: the handedness check against the stage-select route map,
  `frontend/streamed_textures/splines/<stage>_sa.tpk`. Fields:
  - `right_handed`;
  - `unmirrored_cost_px` / `mirrored_cost_px`: mean distance in map pixels from the fitted polyline to the drawn
    route;
  - the best rotation for each.
- `object_shapes_vs_render`:
  - `origin_vs_ground`: |instance Y - ground height| over up to 2000 instances.
  - `bounds_vs_render` (with `--render`): median overlap (IoU) of each template's collision bounds with its render
    bounds, under the chosen primitive conventions and the alternatives.

## Batch: `stage-ir --all` and report.md

`stage-ir --all` builds every stage (no render meshes unless `--render`). It writes `work/ir/report.md` with:
- the handedness tally and totals;
- one row per stage: name, length, gates, ground triangles, surface count, objects, object shapes, object collision
  triangles, render triangles, pacenote calls/tokens (unknown), route-map fit, max gate dy, time, and
  status/failure message.

Unmapped surface codes or shape classes appear as failures naming the codes. The exit code is 1 if any stage failed.

Last full run (render off, about 6.5 minutes): **156/156 stages built**, with no unmapped surface codes or shape
classes.
- Totals: 169.9M ground triangles, 1,977,175 object instances in the corridors (2,332,699 collision shapes),
  18,424 pacenote tokens (1,432 of unknown kind).
- Handedness: 154/156 fit the route map better unmirrored. The two exceptions (`greece_rally_01_route_5` ratio 0.7,
  `france_rally_02_route_3` ratio 0.9) fit badly either way.
- Gate ground height is within 0.7 m on every stage except 8 Monte Carlo stages (max 15.8 m on
  `france_rally_01_route_0/1/2/5`, 22.8 m on `france_rally_02_route_0/1/3/4`). Their mean is still about 0.14 m, so
  only a few gates are affected, probably where the road passes under another surface (the height lookup takes the
  highest one). Not yet inspected.
- Argentina has very few objects (about 400-3,600 per stage), compared with 4,000-35,000 elsewhere.
- `scotland_rally_04_route_2`: `resetlines.cqtc` is unreadable (unsupported compression method); everything else
  builds.

## Reference output: `new_zealand_rally_01_route_5`

"Te Awanga Sprint Reverse", Hawkes Bay, New Zealand: 4688.5 m start to finish, 446 gates, 45 pacenote calls
(72 tokens: 63 corners, 8 distances, 1 finish), 6 timing lines.

- **Ground collision (250 m corridor):** 1,006,007 triangles. GR3+ grass 402,170; GD0+ gravel 316,041; DB3+ dirt bank
  287,699; CATG cattle grid 96; DEFA 1.
- **Objects:**
  - Instances: 10,609 of 23,025 within the corridor, from 99 templates, making 11,768 collision primitives
    (554,192 triangles).
  - By ACR material: bushes 7,114, tree trunks 2,491, fences 1,380, poles 253, guardrail 169, rocks 107, other 254.
  - 3 drivable vcqtc mesh shapes.
- **Render:** terrain detail 1,302,308 triangles; decals 24,691; low LOD 25,996; vista 223,300; 99 object
  prototypes 212,368.
- **Validation:**
  - Gate ground height: max |dy| 0.044 m, mean 0.008 m.
  - Route map: fits unmirrored (cost 3.4 px vs 5.6 px mirrored).
  - Object origins vs ground: median 0.21 m, p90 1.10 m.
  - Collision vs render bounds: box IoU 0.72 (0.08 with width/length swapped); cylinder 0.57 (0.22 if based at
    the origin).
