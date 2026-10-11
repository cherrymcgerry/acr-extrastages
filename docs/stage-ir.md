# Stage IR (intermediate representation), schema version 1

The stage IR is the hand-off between the DR2 side (extraction) and the ACR side (mod building). One directory per
stage, produced end-to-end from a DR2 install by:

```powershell
pwsh -NoProfile -File scripts\bootstrap.ps1     # once: third_party/ at pinned commits
pwsh -NoProfile -File scripts\build.ps1
tools\dr2-extract\bin\Release\net10.0\dr2-extract.exe stage-ir new_zealand_rally_01_route_5 --render
```

Options: `--render` (also export render meshes), `--corridor <m>` (crop radius around the route, default 250),
`--out <dir>` (default `work/ir`), `--dr2 <install>` (default `%DR2_DIR%`, else `C:\Games\DiRT Rally 2.0`),
`--cache <dir>` (extracted DR2 files, default `work/dr2/cache`), `--surface-map <toml>` (default
`config/surface-map.toml`). All output is generated from game data and lives under the gitignored `work/`.

`dr2-extract catalog` lists every valid stage id (156 rally stages); see docs/dr2-formats.md.

## Layout

```
work/ir/<stage_id>/
  stage.json                     everything except geometry
  collision.glb                  physics ground, one primitive per DR2 surface code
  render/terrain.glb             full-detail terrain        (only with --render)
  render/terrain_decals.glb      road decals over it
  render/terrain_low.glb         low-LOD terrain of the same area (alternative to terrain.glb, not an addition)
  render/vista.glb               distant scenery ring, not cropped
  render/<name>.materials.json   shader + texture names for each glTF material in <name>.glb
```

`<stage_id>` is `<location>_route_<n>`, where `<location>` is the DR2 location folder (`new_zealand_rally_01`)
and `n` is the route index 0-5.

## Coordinate convention

- **Frame:** DR2 native world coordinates, copied unchanged from the game files. Metres, **+Y up**.
- **Handedness:** treated as **right-handed** (the glTF convention). For travel direction `f`,
  `normalize(cross(f, +Y))` points at the gate's `right` point. That only holds if the game's own "right" is the
  driver's right under a right-handed reading, so this is strong evidence but not proof. It has not been checked
  against a real-world map; see "Open questions" in docs/dr2-formats.md.
- All files share one frame: `progress_track.xml` gates, triggers, the physics ground and the render meshes. Physics
  ground height at all 446 gate crossings of the reference stage matches the gate Y within 4.4 cm.
- **glTF files are in the same frame.** No axis swap, no scale and no re-centering. Coordinates are large (about
  -5000..-2900 in X for NZ), so apply an offset on the ACR side if float precision matters. Triangle winding is
  counter-clockwise from the front, as glTF expects.
- Arrays `[x, y, z]` in stage.json are rounded to 1 mm, and direction vectors to 1e-5.

## stage.json

Snake_case keys; `null` where a value is unavailable. Top level:

| key | type | meaning |
|---|---|---|
| `schema`, `version` | string, int | `"acr-dirt/stage-ir"`, `1` |
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
| `collision` | object | see below |
| `surfaces` | array | see below |
| `render` | array | render mesh files (empty without `--render`) |
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
{ "source": "<archive path>", "distance_reference": "...",
  "shakedown_cutoff_m": 1127, "finish_call_distance_m": 1230,
  "calls": [ { "distance": 50, "name": null, "audio": "cd_nz_01_med_rvs_062",
               "subcalls": [ { "type": 2, "delay_ms": 0, "angle": 60, "modifier_a": 1, "modifier_b": 0,
                               "can_concat_mod_a": true, "can_concat_mod_b": true, "distance_link": 12 } ] } ] }
```

These are copied raw from DR2's codriver file. Each call is one spoken phrase: `audio` names the sound in the
location's codriver soundbank (the `.bnk` files are not exported), and the 3 subcalls carry the structured content.
**Not decoded:** the meaning of subcall `type`/`angle`/`modifier_*` (the corner severity and modifier tables), and
whether `distance` is measured from the progress-track origin or from the start line.

### triggers[]

`{ type, name, position, forward, size_whl }` for box-shaped triggers in `triggers_game_object.xml`, converted
from DR2's inverse transforms to world space. `size_whl` is width/height/length in metres along the box's local
X/Y/Z, and `forward` is its local +Z. Useful types:
- `VehicleBox` `EndSequenceTriggerBox*`: where the car is stopped after the finish.
- `CattleGridTrigger`: a cattle grid.
- `fe_hazard_approach_*` / `fe_hazard_cleared_*`: hazard-warning boxes.

Point-like triggers without a box are omitted.

### collision

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
- **Not included:** collision for objects such as trees, fences, rocks and buildings. DR2 stores those separately
  and that format is not decoded yet.
- `flags` is a histogram of the 3-bit per-triangle flag field in DR2's ground format. Its meaning is unknown, and it
  is kept for future use.

### surfaces[]

`{ code, acr_physical_material, triangles, dr2 }` for every surface code in the collision mesh. `dr2` holds the
resolved DR2 definition: `base_chain`, `friction`, `mechanics`, `audio`, `particle`, `mud`. The mapping comes from
`config/surface-map.toml`, and `stage-ir` **fails** (exit code 1, no output written) if any code in the corridor is
unmapped.

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

## Reference output: `new_zealand_rally_01_route_5`

"Te Awanga Sprint Reverse", Hawkes Bay, New Zealand: 4688.5 m start to finish, 446 gates, 45 pacenote calls, 6
timing lines.

- **Collision (250 m corridor):** 1,006,007 triangles. GR3+ grass 402,170; GD0+ gravel 316,041; DB3+ dirt bank
  287,699; CATG cattle grid 96; DEFA 1.
- **Render:** terrain detail 1,302,308 triangles; decals 24,691; low LOD 25,996; vista 223,300.
- **Validation:** gate ground height max |dy| 0.044 m, mean 0.008 m.

Wales `wales_rally_01_route_2` "Pant Mawr", whose pacenotes come from the `game_2_1.dat` patch, also passes
(786,135 collision triangles, max |dy| 0.142 m).
