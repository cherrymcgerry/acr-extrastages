# DiRT Rally 2.0 data formats (as used by tools/dr2-extract)

Findings from the Steam build of DR2 installed at `C:\Games\DiRT Rally 2.0`. Everything below was read with
`tools/dr2-extract`, which uses NefsLib (ego.nefsedit) for archives and EgoEngineLibrary for binary XML, JPK,
PSSG, LNG and textures, plus our own readers where those libraries don't handle DR2's variants.

Legend: **[V]** verified on game data; **[I]** inferred; **[?]** unknown.

## 1. Install layout and archives

| path | what | how to open |
|---|---|---|
| `locations\<country>__<location>.nefs` | one archive per location, 40 in total: 26 rally (`*_rally_NN`) and 14 rallycross/other | NeFS; `dr2-extract list/extract` [V] |
| `game\game.nefs` | 1290 generic files (UI, cars, shared data) | NeFS [V] |
| `game\game.dat`, `game_1.dat`, `game_2.dat`, `game_2_1.dat` | **headless** NeFS: the headers are embedded in `dirtrally2.exe` | `NefsExeHeaderFinder` over the exe, then read the archive from the matching header [V] |
| `dirtrally2.exe` | holds the `.dat` headers | |

Contents of the `.dat` archives [V]:
- `game.dat` (390 files): `tracks/generic/physparams.xml`, catalogues and spline textures.
- `game_1.dat` (2082 files): `language/language_<lang>.lng`, **`surface_materials.xml`**, catalogues, system files.
- `game_2.dat` (144 files) and `game_2_1.dat` (629 files): **patch layers** with replacement location files. Most
  common are `route_N/resetlines.cqtc` (48), codriver files `route_N/<loc>_codriver_1_pro_N_data.xml` (30),
  `sponsor_grouping_*.xml`, soundbanks, `route_objecttypes.pssg` and `route_patchup_ot.pssg`. The two patch
  archives share no paths.

**Lookup order** used by `stage-ir` and `catalog` (`Dr2Install`): `game_2_1.dat`, `game_2.dat`, the location
archive, `game_1.dat`, `game.dat` [I]. The patch layers can only sensibly override the location files, but the
engine's real search order was not observed. stage.json records which archive each file came from. In the catalogue,
30 of 156 stages take their codriver file from a patch layer.

### Encrypted-archive status

[V] **All 40 location archives and all 5 `game` archives open.**
- 20 location archives have a plain `NeFS` magic. The other 20 have an RSA-scrambled header intro, which NefsLib
  descrambles automatically with the public key built into it.
- Among the 26 rally archives, 12 are plain: Australia, New Zealand, Poland, Argentina, Spain and USA (2 each).
- The other 14 are scrambled: Germany, Greece, Sweden, Finland, France, Wales and Scotland (2 each).
- Payload data is not encrypted beyond NeFS compression. `dr2-extract scan` reports `ok` for all 40.

## 2. Location archive layout

Every location archive holds `tracks/locations/<country>/<location>/`. Rally locations have 6 routes,
`route_0`..`route_5`. Each DR2 "rally" is two archives (`_rally_01`/`_02`, or `scotland_rally_03`/`_04`), so 12
stages per country. Reverse stages are separate routes.

Location level (shared by the 6 routes) [V unless noted]:

| file | content |
|---|---|
| `tracksplit.pssg` | **terrain render meshes**, about 770 MB for NZ01 (§5) |
| `patchup_ot.pssg` | 731 textures only (about 1 GB) |
| `objects.pssg`, `objectstextures.pssg` | object prototypes (146 for NZ01) and their textures |
| `trees.pssg`, `treestextures.pssg`, `ground_cover.pssg`, `grass.grs` | vegetation |
| `niwater.pssg` | water |
| `landscape.heightfield` | [?] coarse heightfield, not decoded |
| `surface_deg.xml`, `mud*.xml`, `lighting_*.xml`, `weather.xml`, `*_references.xml` | config (binary XML) |
| `banks/**.bnk` | audio, including codriver soundbanks per language (`d_cd_<loc>_fwd/_rvs.bnk`) |

Per route (`route_N/`):

| file | content |
|---|---|
| `progress_track.xml` | **stage definition**: gates, splits, total distance (§3.2) [V] |
| `<location>_codriver_1_pro_N_data.xml` | **pacenotes** (§3.3) [V] |
| `track.jpk` | **physics ground** (§4) [V]. Identical in all 6 routes of an archive (same SHA-1 in all 26 archives) |
| `track_spline.xml` | `TrackSplines version=2`: `CentralSplineOriginal/spline/cp` points with `pos*`, `for*` and `up*` attributes [V]. Covers the location's road network, not just this route [I] |
| `triggers_game_object.xml` | triggers (§3.4) [V] |
| `start_lights.xml` | start-light sequence (textures and timings) [V] |
| `resetlines.cqtc` | [?] reset/recovery lines; quad-tree format not decoded |
| `ai_track.xml`, `ai_vehicle_track.xml`, `vehicle_track_progress_data.xml` | AI racing line / progress [?] |
| `route_objects.pssg`, `route_objecttypes.pssg`, `route_objectstextures.pssg` | route-specific object prototypes (e.g. 12 for NZ01 route 5: signs, arrows, parked vehicles) and textures [V] |
| `objects.ens`, `ornaments.bin`, `trees.bin`, `crowd_*.bin` | [?] **object placements** (instances of the prototypes); not decoded |
| `drivable_entities.jpk`, `grids.pssg`, `track.vis`, `replay_camera_config.xml`, `pfx*.xml`, `light_*` | [?] |

## 3. Text/XML data

### 3.1 Binary XML

Most `.xml` files are binary. `"\x1A\"Rr"` files are BinXml and the others are BXML (shown as `<!--BinXml-->` /
`<!--BxmlBig-->` after conversion). EgoEngineLibrary's `XmlFile` decodes both; `dr2-extract xml <file|dir>`
converts them to text.

### 3.2 progress_track.xml (stage definition) [V]

```xml
<progress_track_data exporter_version="3.0.0">
  <routes num_routes="1"><route id="0" direction="forwards" num_splits="6">
    <split id="0" type="start" gate="3"/> ... <split id="5" type="finish" gate="433"/></route></routes>
  <gates num_gates="446">
    <gate id="0" distance="4.86">
      <left format="float3">x y z</left><right format="float3">x y z</right><crossing format="float3">x y z</crossing>
    </gate> ...
  </gates>
  <lines num_lines="1"><line/></lines>
  <track type="point_to_point" total_distance="4857.605"><track_bounds>...</track_bounds></track>
</progress_track_data>
```

- Gates are ordered in the driving direction, about 10-15 m apart, and run from before the start to after the finish.
  `distance` is metres along the route. `crossing` is the centreline point, and `left`/`right` the road edges.
- Split `type` is one of:
  - `start`
  - `time`: intermediate split.
  - `shake_finish`: end of the shakedown section.
  - `finish`
- **Stage length** = `distance(finish gate) - distance(start gate)`. `total_distance` also counts the run-up and
  run-out.
- `track_bounds` holds unreliable values (max = 0, 613, 0 for NZ01 route 5) and is ignored.
- Every rally route has `direction="forwards"`.

### 3.3 Codriver / pacenotes [V structure, ? semantics]

```xml
<root>
  <shakedown_info cutoffDistance="1127" finishCallDistance="1230"/>
  <codriver_calls>
    <call name="" audioName="cd_nz_01_med_rvs_062" distanceAlongTrack="50">
      <subcall type="2" delay="0" angle="60" modifierA="1" modifierB="0" canConcatModA="True"
               canConcatModB="True" distanceLink="12"/>   <!-- always 3 subcalls -->
    </call> ...
```

- Each call is one pre-recorded phrase (`audioName` in the codriver soundbank) triggered at `distanceAlongTrack`.
  `name` is an authoring comment.
- Not yet decoded:
  - The subcall fields describing the note (corner type, angle/severity, modifiers, links).
  - Whether `distanceAlongTrack` uses the gate-distance origin or the start line. The first call is at 0.1 while the
    start gate is at 40.5 m.

### 3.4 triggers_game_object.xml [V]

`<trigger type="...">` with `base_params name id layer_mask` and type-specific `params`. Box triggers carry
`length`/`width`/`height` plus an **inverse** world transform (`inverse_transform` attribute, or `inv_transform` value).
The transform is a row-major 4×4 in System.Numerics convention, with translation in the last row.

- Types seen: `VehicleBox` (incl. `EndSequenceTriggerBox`, `fe_hazard_*`), `VehicleGate`, `CattleGridTrigger`,
  `TriggerETA`, `SessionTime` and `DummyTrigger`.
- `DummyTrigger` holds `SpareWheelData` transforms; the NZ01 route 5 file has them at a service area off the route.

### 3.5 surface_materials.xml (game_1.dat) [V]

`MATERIALS_V2/MATERIAL` entries (316 of them):
- Attributes: `name` (4-char code), `base_name` (single inheritance), `friction`, `dynamicFriction*`, `remap_name`
  and an RGB debug colour.
- Children: `MECHANICS name bumpsmagnitude depth ...`, `AUDIO`, `PARTICLE`, `MUD2` and `SCRATCH`.

Codes ending in `+` are the ones used by physics grounds and inherit from base classes ending in `_`, e.g.
`GD0+ > GLD+ > GLD_ > PBSE`. The resolved definitions go into stage.json `surfaces[].dr2`.

### 3.6 Localisation (game_1.dat `language/language_eng.lng`) [V]

Key → string table, read with EgoEngineLibrary `LngFile`. Useful keys:
- `lng_<location>_route_<n>`: stage name.
- `lng_<location>_r_<n>_caps`: upper-case stage name.
- `lng_location_full_<key>`: location name. `<key>` is the location minus `_rally_NN`, except `usa` uses
  `new_england` and `scotland` uses `scotland_gravel`.

## 4. Physics ground: `route_N/track.jpk` [V]

A JPK archive (EgoEngineLibrary `JpkFile`) holding `qt.info` (overall bounds) plus one `qt_*.vcqtc` entry per
quad-tree leaf cell (5936 cells for NZ01). EgoEngineLibrary's `VcQuadTreeFile` targets older EGO games (negative
counts, 8-byte packed vertices) and cannot read DR2's variant, so `Dr2Ground.cs` implements it:

```
little endian
0x00 float3 boundsMin, float3 boundsMax
0x18 int32 numTriangles, numVertices, numMaterials      (positive; numMaterials <= 8)
0x24 uint32 verticesOffset, nodesOffset, trianglesOffset, triangleRefsOffset
0x34 char[4] material code x numMaterials               (e.g. "GR3+")
     ... verticesOffset-48: "VCQT", uint32 version 1, 8 zero bytes,
         float4 offset (minX, 0, minZ, 1), float4 scale (sizeX, 1, sizeZ, 1)
verticesOffset:  numVertices x { float x01, float yWorld, float z01, uint32 pad }
                 world = (x01*scale.x + offset.x, yWorld, z01*scale.z + offset.z)
nodesOffset:     uint16 big-endian quad-tree nodes (not needed for extraction)
trianglesOffset: numTriangles x 4 bytes
                 v0 = b0<<2 | b1>>6;  flags = (b1>>3)&7;  material = b1&7;  v1 = v0+b2;  v2 = v0+b3
```

- **Contents:** one continuous surface covering road, verges, banks and terrain.
  - NZ01: 2,141,369 triangles. The `qt.info` bounds are (-5547.7, 422.8, -4554.5) .. (-2921.1, 630.7, 1881.2).
  - Materials over the whole ground: GR3+ 849,631; GD0+ 659,045; DB3+ 632,470; CATG 222; DEFA 1.
- **Not in track.jpk:** objects (trees, fences, rocks, buildings). Their collision is presumably in the object
  placements and prototypes (`objects.ens`, `*.bin`, `drivable_entities.jpk`), which are not decoded.
- **Winding:** 97% of triangles are counter-clockwise seen from +Y. The rest are steep banks and the like.
- **`flags`:** [?] a 3-bit per-triangle value; NZ01 route 5 has 96% of triangles at 7.
- **Commands:** `dr2-extract track-to-gltf <track.jpk> out.glb` gives one primitive per surface code.
  `ground-height <track.jpk> x z` probes the surface.

## 5. Render meshes: PSSG [V]

PSSG scenes are read with EgoEngineLibrary (`PssgFile`); `dr2-extract pssg-info` prints the element tree.
`pssg-to-gltf` (`PssgMeshExport.cs`):
- walks the `NODE` library and bakes node transforms into the vertices;
- takes LOD 0 of `LODVISIBLERENDERNODE`s;
- writes one glTF material per shader instance, plus a `.materials.json` with the texture names.

**`tracksplit.pssg`** (location terrain) has 4 tiers of tiles under `Scene Root/surface`. Each tile is a
`RENDERNODE` with a world-identity transform:

| node | shader(s) | role | NZ01 tiles / triangles |
|---|---|---|---|
| `SHADOWCASTING_i_j` (under `ROOT_i_j`) | `terrain_array.fx` (instances `terrain_array!N`, `Corner Cutting!N`) | **full-detail terrain** (the name is misleading) | 37 / 2,905,443 |
| `DECAL_i_j` | `decal_ao.fx` | road decals | 25 / 36,558 |
| `LOW_i_j` | `terrain_lod.fx` | low-LOD terrain of the same area | 37 / 50,649 |
| `NONLOD_i_j` (under `LAND_i_j`, 3×3 grid, ±10 km) | `terrain_track_vista_d4.fx` | distant vista | 9 / 223,300 |
| `*BATCH_i_j` twins of each | `batched_track.fx` | the same surface drawn again for another pass | skipped |

- Detail-terrain vertices are `Vertex float3`, `Color uint_color_argb`, two `ST half4` streams (4 UVs), an
  `ST uint_color_argb` stream, `Normal half4` and `Tangent half4`.
- The extra `uint_color_argb` ST stream makes EgoEngineLibrary's `RenderDataSourceReader` throw, so `RdsReader.cs`
  reads it instead and ignores that stream. That stream is probably packed layer/material indices [I].
- `terrain_array.fx` material textures:
  - `TColourMap`: tile colour map.
  - `TGlobalNormal`.
  - `TAmbientOcclusion`: section lightmap.
  - `TDiffuse`/`TNormal`: texture arrays, `*.dds` ids.
  - `TSkidMap*`: skid marks.

Texture bodies are in the same file, `patchup_ot.pssg`, `objectstextures.pssg` or the route `*textures.pssg`; a
reference reads `file.pssg#id` or `#id`. `PssgTexture.ToDdsFile()` writes them out as DDS.

**Object prototypes** (`objects.pssg`, `route_objects.pssg`) are `ROOTNODE`s at the origin, e.g.
`core_game_rcsign_h Root` > `LODVISIBLERENDERNODE` > `LODRENDERINSTANCES`. The placements live elsewhere (§2,
not decoded), so prototypes export at the origin.

The stage-map textures (`*.tpk` in `game.dat`) use TPK texture format 38, which EgoEngineLibrary does not support
(`tpk-to-png` fails on them).

## 6. Coordinate system

- **Units and up axis:** metres, **+Y up** [V]. All of the following share one world frame [V]: `progress_track`
  gates, `track_spline`, triggers, the physics ground and `tracksplit.pssg`.
  - Physics-ground height at the 446 gate crossings of NZ01 route 5 matches the gate Y within 4.4 cm (mean 8 mm).
  - The render-terrain and physics bounds agree to 1 mm at the shared edge.
- **No conversion:** EgoEngineLibrary's converters apply no axis conversion, and neither do we.
- **Handedness:** right-handed is the working assumption [I], based on the gate labels.
  - For travel direction `f`, `normalize(cross(f, +Y))` points at the gate's `right` point on all 853 interior
    gates of NZ01 route 5 and Wales01 route 2.
  - That is the driver's right only in a right-handed reading. A left-handed reading would put the game's "right"
    label on the driver's left.
  - Render winding agrees with the stored normals 99.98% of the time. That doesn't distinguish right-handed with
    counter-clockwise front faces from left-handed with clockwise.
  - Not yet checked against a real-world map; see the open questions in §9.

## 7. Tool commands

```
dr2-extract scan <dr2-root>                       open every location archive
dr2-extract list|extract <archive.nefs|.dat> ...   (.dat via the exe headers)
dr2-extract xml <file|dir>    lng <file.lng>    tpk-to-png <file.tpk>
dr2-extract track-to-gltf|track-info|ground-height <track.jpk> ...
dr2-extract pssg-info|pssg-to-gltf <file.pssg> ...
dr2-extract catalog                               all rally stages + surface survey (work/dr2/catalog.{json,md})
dr2-extract stage-ir <stage_id> [--render]        stage IR (docs/stage-ir.md)
```

## 8. Catalogue of rally stages [V]

26 rally archives with 6 routes each give **156 stages**, about 1258 km of competitive distance in total. The
lengths below are start to finish. `pacenotes` is the number of codriver calls. Generated by `dr2-extract catalog`
(names from `language_eng.lng`).

| stage id | name | location | length (m) | gates | pacenotes |
|---|---|---|---:|---:|---:|
| `australia_rally_01_route_0` | Mount Kaye Pass | Monaro, Australia | 13304.1 | 1956 | 105 |
| `australia_rally_01_route_1` | Mount Kaye Pass Reverse | Monaro, Australia | 13301.1 | 2003 | 94 |
| `australia_rally_01_route_2` | Rockton Plains | Monaro, Australia | 6951.2 | 887 | 51 |
| `australia_rally_01_route_3` | Yambulla Mountain Descent | Monaro, Australia | 6221.5 | 928 | 52 |
| `australia_rally_01_route_4` | Yambulla Mountain Ascent | Monaro, Australia | 6398.9 | 1093 | 55 |
| `australia_rally_01_route_5` | Rockton Plains Reverse | Monaro, Australia | 7116.2 | 955 | 42 |
| `australia_rally_02_route_0` | Chandlers Creek | Monaro, Australia | 12341.2 | 1039 | 93 |
| `australia_rally_02_route_1` | Chandlers Creek Reverse | Monaro, Australia | 12305.0 | 1064 | 97 |
| `australia_rally_02_route_2` | Noorinbee Ridge Ascent | Monaro, Australia | 5277.0 | 522 | 44 |
| `australia_rally_02_route_3` | Taylor Farm Sprint | Monaro, Australia | 7007.0 | 543 | 50 |
| `australia_rally_02_route_4` | Bondi Forest | Monaro, Australia | 7052.3 | 548 | 48 |
| `australia_rally_02_route_5` | Noorinbee Ridge Descent | Monaro, Australia | 5236.9 | 537 | 47 |
| `finland_rally_01_route_0` | Kontinjärvi | Jämsä, Finland | 14929.8 | 1014 | 69 |
| `finland_rally_01_route_1` | Hämelahti | Jämsä, Finland | 14866.1 | 1025 | 74 |
| `finland_rally_01_route_2` | Kailajärvi | Jämsä, Finland | 7515.4 | 530 | 36 |
| `finland_rally_01_route_3` | Jyrkysjärvi | Jämsä, Finland | 7340.4 | 511 | 39 |
| `finland_rally_01_route_4` | Naarajärvi | Jämsä, Finland | 7310.5 | 496 | 34 |
| `finland_rally_01_route_5` | Paskuri | Jämsä, Finland | 7461.7 | 511 | 36 |
| `finland_rally_02_route_0` | Kakaristo | Jämsä, Finland | 16205.2 | 1152 | 85 |
| `finland_rally_02_route_1` | Pitkäjärvi | Jämsä, Finland | 16205.3 | 1152 | 79 |
| `finland_rally_02_route_2` | Iso Oksjärvi | Jämsä, Finland | 8042.5 | 584 | 40 |
| `finland_rally_02_route_3` | Järvenkylä | Jämsä, Finland | 8147.4 | 616 | 43 |
| `finland_rally_02_route_4` | Kotajärvi | Jämsä, Finland | 8147.6 | 610 | 45 |
| `finland_rally_02_route_5` | Oksala | Jämsä, Finland | 8057.5 | 581 | 37 |
| `france_rally_01_route_0` | Pra d’Alart | Monte Carlo, Monaco | 9831.4 | 805 | 59 |
| `france_rally_01_route_1` | Col de Turini Départ | Monte Carlo, Monaco | 9832.0 | 795 | 62 |
| `france_rally_01_route_2` | Gordolon - Courte montée | Monte Carlo, Monaco | 5175.9 | 428 | 31 |
| `france_rally_01_route_3` | Col de Turini - Sprint en descente | Monte Carlo, Monaco | 4730.0 | 405 | 30 |
| `france_rally_01_route_4` | Col de Turini sprint en Montée | Monte Carlo, Monaco | 4729.5 | 403 | 29 |
| `france_rally_01_route_5` | Col de Turini - Descente | Monte Carlo, Monaco | 5175.9 | 424 | 32 |
| `france_rally_02_route_0` | Vallée descendante | Monte Carlo, Monaco | 10866.9 | 1018 | 77 |
| `france_rally_02_route_1` | Route de Turini | Monte Carlo, Monaco | 10805.2 | 1013 | 78 |
| `france_rally_02_route_2` | Col de Turini - Départ en descente | Monte Carlo, Monaco | 6846.8 | 631 | 48 |
| `france_rally_02_route_3` | Approche du Col de Turini - Montée | Monte Carlo, Monaco | 3952.2 | 419 | 29 |
| `france_rally_02_route_4` | Route de Turini Descente | Monte Carlo, Monaco | 4015.4 | 419 | 30 |
| `france_rally_02_route_5` | Route de Turini Montée | Monte Carlo, Monaco | 6843.3 | 631 | 51 |
| `germany_rally_01_route_0` | Oberstein | Baumholder, Germany | 11487.2 | 775 | 68 |
| `germany_rally_01_route_1` | Frauenberg | Baumholder, Germany | 11551.2 | 777 | 67 |
| `germany_rally_01_route_2` | Waldaufstieg | Baumholder, Germany | 5361.9 | 414 | 33 |
| `germany_rally_01_route_3` | Kreuzungsring Reverse | Baumholder, Germany | 5666.2 | 345 | 32 |
| `germany_rally_01_route_4` | Kreuzungsring | Baumholder, Germany | 6121.9 | 496 | 36 |
| `germany_rally_01_route_5` | Waldabstieg | Baumholder, Germany | 5882.2 | 511 | 36 |
| `germany_rally_02_route_0` | Hammerstein | Baumholder, Germany | 10805.2 | 1356 | 71 |
| `germany_rally_02_route_1` | Ruschberg | Baumholder, Germany | 10700.0 | 1329 | 71 |
| `germany_rally_02_route_2` | Verbundsring | Baumholder, Germany | 5855.7 | 484 | 39 |
| `germany_rally_02_route_3` | Innerer Feld-Sprint | Baumholder, Germany | 5129.0 | 413 | 31 |
| `germany_rally_02_route_4` | Innerer Feld-Sprint (umgekehrt) | Baumholder, Germany | 4937.8 | 419 | 32 |
| `germany_rally_02_route_5` | Verbundsring Reverse | Baumholder, Germany | 5550.8 | 467 | 41 |
| `greece_rally_01_route_0` | Anodou Farmakas | Argolis, Greece | 9666.5 | 839 | 69 |
| `greece_rally_01_route_1` | Kathodo Leontiou | Argolis, Greece | 9666.0 | 857 | 74 |
| `greece_rally_01_route_2` | Pomona Ékrixi | Argolis, Greece | 5086.8 | 454 | 36 |
| `greece_rally_01_route_3` | Fourkéta Kourva | Argolis, Greece | 4515.4 | 416 | 35 |
| `greece_rally_01_route_4` | Koryfi Dafni | Argolis, Greece | 4582.0 | 416 | 33 |
| `greece_rally_01_route_5` | Ampelonas Ormi | Argolis, Greece | 4860.2 | 447 | 37 |
| `greece_rally_02_route_0` | Perasma Platani | Argolis, Greece | 10487.1 | 858 | 70 |
| `greece_rally_02_route_1` | Tsiristra Théa | Argolis, Greece | 10357.9 | 857 | 73 |
| `greece_rally_02_route_2` | Ourea Spevsi | Argolis, Greece | 5739.1 | 490 | 40 |
| `greece_rally_02_route_3` | Pedines Epidaxi | Argolis, Greece | 6595.3 | 551 | 45 |
| `greece_rally_02_route_4` | Abies Koiláda | Argolis, Greece | 6888.4 | 563 | 45 |
| `greece_rally_02_route_5` | Ypsona tou Dasos | Argolis, Greece | 5383.0 | 462 | 41 |
| `new_zealand_rally_01_route_0` | Te Awanga Forward | Hawkes Bay, New Zealand | 11507.4 | 1097 | 127 |
| `new_zealand_rally_01_route_1` | Ocean Beach | Hawkes Bay, New Zealand | 11437.1 | 1072 | 105 |
| `new_zealand_rally_01_route_2` | Te Awanga Sprint Forward | Hawkes Bay, New Zealand | 4799.8 | 471 | 59 |
| `new_zealand_rally_01_route_3` | Ocean Beach Sprint Forward | Hawkes Bay, New Zealand | 6624.0 | 606 | 60 |
| `new_zealand_rally_01_route_4` | Ocean Beach Sprint Reverse | Hawkes Bay, New Zealand | 6584.1 | 613 | 66 |
| `new_zealand_rally_01_route_5` | Te Awanga Sprint Reverse | Hawkes Bay, New Zealand | 4688.5 | 446 | 45 |
| `new_zealand_rally_02_route_0` | Waimarama Point Forward | Hawkes Bay, New Zealand | 16057.8 | 1219 | 96 |
| `new_zealand_rally_02_route_1` | Waimarama Point Reverse | Hawkes Bay, New Zealand | 15844.5 | 1243 | 84 |
| `new_zealand_rally_02_route_2` | Elsthorpe Sprint Forward | Hawkes Bay, New Zealand | 7137.8 | 547 | 46 |
| `new_zealand_rally_02_route_3` | Waimarama Sprint Forward | Hawkes Bay, New Zealand | 8807.5 | 708 | 46 |
| `new_zealand_rally_02_route_4` | Waimarama Sprint Reverse | Hawkes Bay, New Zealand | 8734.0 | 710 | 50 |
| `new_zealand_rally_02_route_5` | Elsthorpe Sprint Reverse | Hawkes Bay, New Zealand | 6643.5 | 542 | 36 |
| `poland_rally_01_route_0` | Zaróbka | Łęczna County, Poland | 16475.0 | 1146 | 98 |
| `poland_rally_01_route_1` | Zagórze | Łęczna County, Poland | 16615.0 | 1151 | 95 |
| `poland_rally_01_route_2` | Kopina | Łęczna County, Poland | 7840.2 | 571 | 50 |
| `poland_rally_01_route_3` | Marynka | Łęczna County, Poland | 9254.9 | 670 | 55 |
| `poland_rally_01_route_4` | Borysik | Łęczna County, Poland | 9194.3 | 669 | 54 |
| `poland_rally_01_route_5` | Józefin | Łęczna County, Poland | 8159.8 | 577 | 45 |
| `poland_rally_02_route_0` | Jezioro Rotcze | Łęczna County, Poland | 13295.7 | 1698 | 71 |
| `poland_rally_02_route_1` | Zienki | Łęczna County, Poland | 13180.4 | 1603 | 67 |
| `poland_rally_02_route_2` | Czarny Las | Łęczna County, Poland | 6622.1 | 484 | 34 |
| `poland_rally_02_route_3` | Lejno | Łęczna County, Poland | 6698.8 | 489 | 34 |
| `poland_rally_02_route_4` | Jagodno | Łęczna County, Poland | 6655.5 | 479 | 38 |
| `poland_rally_02_route_5` | Jezioro Lukie | Łęczna County, Poland | 6437.8 | 459 | 33 |
| `argentina_rally_01_route_0` | Las Juntas | Córdoba, Argentina | 8256.9 | 840 | 72 |
| `argentina_rally_01_route_1` | Camino a La Puerta | Córdoba, Argentina | 8266.0 | 829 | 64 |
| `argentina_rally_01_route_2` | Camino de acantilados y rocas | Córdoba, Argentina | 5303.8 | 512 | 47 |
| `argentina_rally_01_route_3` | El Rodeo | Córdoba, Argentina | 2845.6 | 339 | 23 |
| `argentina_rally_01_route_4` | La Merced | Córdoba, Argentina | 2779.5 | 328 | 24 |
| `argentina_rally_01_route_5` | Camino de acantilados y rocas inverso | Córdoba, Argentina | 5294.8 | 557 | 40 |
| `argentina_rally_02_route_0` | Valle de los puentes | Córdoba, Argentina | 7667.3 | 773 | 47 |
| `argentina_rally_02_route_1` | Valle de los puentes a la inversa | Córdoba, Argentina | 7929.2 | 782 | 53 |
| `argentina_rally_02_route_2` | Miraflores | Córdoba, Argentina | 3353.0 | 389 | 25 |
| `argentina_rally_02_route_3` | San Isidro | Córdoba, Argentina | 4171.5 | 364 | 22 |
| `argentina_rally_02_route_4` | Camino a Coneta | Córdoba, Argentina | 4082.3 | 378 | 20 |
| `argentina_rally_02_route_5` | Huillaprima | Córdoba, Argentina | 3494.0 | 413 | 30 |
| `spain_rally_01_route_0` | Comienzo De Bellriu | Ribadelles, Spain | 14348.4 | 1354 | 86 |
| `spain_rally_01_route_1` | Final de Bellriu | Ribadelles, Spain | 13164.3 | 1340 | 84 |
| `spain_rally_01_route_2` | Ascenso por valle el Gualet | Ribadelles, Spain | 7005.7 | 671 | 44 |
| `spain_rally_01_route_3` | Viñedos dentro del valle Parra | Ribadelles, Spain | 6815.4 | 627 | 42 |
| `spain_rally_01_route_4` | Ascenso bosque Montverd | Ribadelles, Spain | 7297.3 | 626 | 43 |
| `spain_rally_01_route_5` | Salida desde Montverd | Ribadelles, Spain | 6143.6 | 670 | 42 |
| `spain_rally_02_route_0` | Centenera | Ribadelles, Spain | 10568.4 | 1687 | 65 |
| `spain_rally_02_route_1` | Camino a Centenera | Ribadelles, Spain | 10584.7 | 1493 | 60 |
| `spain_rally_02_route_2` | Descenso por carretera | Ribadelles, Spain | 4562.8 | 591 | 28 |
| `spain_rally_02_route_3` | Viñedos Dardenyà | Ribadelles, Spain | 6547.4 | 1018 | 37 |
| `spain_rally_02_route_4` | Viñedos Dardenyà inversa | Ribadelles, Spain | 6194.7 | 971 | 40 |
| `spain_rally_02_route_5` | Subida por carretera | Ribadelles, Spain | 4380.7 | 552 | 26 |
| `sweden_rally_01_route_0` | Ransbysäter | Värmland, Sweden | 11922.6 | 931 | 75 |
| `sweden_rally_01_route_1` | Norraskoga | Värmland, Sweden | 11503.5 | 866 | 72 |
| `sweden_rally_01_route_2` | Älgsjön Sprint | Värmland, Sweden | 5248.4 | 422 | 37 |
| `sweden_rally_01_route_3` | Stor-jangen Sprint Reverse | Värmland, Sweden | 6693.4 | 487 | 41 |
| `sweden_rally_01_route_4` | Stor-jangen Sprint | Värmland, Sweden | 6666.9 | 492 | 39 |
| `sweden_rally_01_route_5` | Skogsrallyt | Värmland, Sweden | 4804.0 | 441 | 32 |
| `sweden_rally_02_route_0` | Hamra | Värmland, Sweden | 12123.7 | 1058 | 66 |
| `sweden_rally_02_route_1` | Lysvik | Värmland, Sweden | 12123.6 | 1033 | 67 |
| `sweden_rally_02_route_2` | Elgsjön | Värmland, Sweden | 7058.5 | 544 | 38 |
| `sweden_rally_02_route_3` | Björklangen | Värmland, Sweden | 4932.0 | 362 | 27 |
| `sweden_rally_02_route_4` | Östra Hinnsjön | Värmland, Sweden | 4911.7 | 341 | 28 |
| `sweden_rally_02_route_5` | Älgsjön | Värmland, Sweden | 7056.0 | 649 | 41 |
| `scotland_rally_03_route_0` | South Morningside | Perth and Kinross, Scotland | 12583.4 | 943 | 202 |
| `scotland_rally_03_route_1` | South Morningside Reverse | Perth and Kinross, Scotland | 12764.6 | 962 | 212 |
| `scotland_rally_03_route_2` | Old Butterstone Muir | Perth and Kinross, Scotland | 5822.8 | 469 | 95 |
| `scotland_rally_03_route_3` | Rosebank Farm | Perth and Kinross, Scotland | 7144.7 | 530 | 117 |
| `scotland_rally_03_route_4` | Rosebank Farm Reverse | Perth and Kinross, Scotland | 6967.9 | 522 | 111 |
| `scotland_rally_03_route_5` | Old Butterstone Muir Reverse | Perth and Kinross, Scotland | 5659.8 | 453 | 97 |
| `scotland_rally_04_route_0` | Newhouse Bridge | Perth and Kinross, Scotland | 12857.1 | 955 | 203 |
| `scotland_rally_04_route_1` | Newhouse Bridge Reverse | Perth and Kinross, Scotland | 12969.2 | 980 | 222 |
| `scotland_rally_04_route_2` | Glencastle Farm | Perth and Kinross, Scotland | 5245.4 | 400 | 82 |
| `scotland_rally_04_route_3` | Annbank Station | Perth and Kinross, Scotland | 7703.7 | 574 | 130 |
| `scotland_rally_04_route_4` | Annbank Station Reverse | Perth and Kinross, Scotland | 7587.6 | 579 | 121 |
| `scotland_rally_04_route_5` | Glencastle Farm Reverse | Perth and Kinross, Scotland | 5238.4 | 405 | 90 |
| `wales_rally_01_route_0` | Sweet Lamb | Powys, Wales | 9911.7 | 844 | 77 |
| `wales_rally_01_route_1` | Geufron Forest | Powys, Wales | 10063.6 | 852 | 76 |
| `wales_rally_01_route_2` | Pant Mawr | Powys, Wales | 4788.7 | 411 | 36 |
| `wales_rally_01_route_3` | Bidno Moorland Reverse | Powys, Wales | 5166.0 | 453 | 37 |
| `wales_rally_01_route_4` | Bidno Moorland | Powys, Wales | 4960.1 | 443 | 41 |
| `wales_rally_01_route_5` | Pant Mawr Reverse | Powys, Wales | 4821.6 | 411 | 39 |
| `wales_rally_02_route_0` | River Severn Valley | Powys, Wales | 11435.5 | 923 | 78 |
| `wales_rally_02_route_1` | Bronfelen | Powys, Wales | 11435.5 | 923 | 75 |
| `wales_rally_02_route_2` | Fferm Wynt | Powys, Wales | 5717.4 | 476 | 39 |
| `wales_rally_02_route_3` | Dyffryn Afon Reverse | Powys, Wales | 5718.1 | 469 | 40 |
| `wales_rally_02_route_4` | Dyffryn Afon | Powys, Wales | 5718.1 | 469 | 40 |
| `wales_rally_02_route_5` | Fferm Wynt Reverse | Powys, Wales | 5717.4 | 488 | 36 |
| `usa_rally_01_route_0` | North Fork Pass | New England, USA | 12228.8 | 927 | 60 |
| `usa_rally_01_route_1` | North Fork Pass Reverse | New England, USA | 12276.1 | 924 | 60 |
| `usa_rally_01_route_2` | Hancock Creek Burst | New England, USA | 6701.6 | 519 | 35 |
| `usa_rally_01_route_3` | Fuller Mountain Descent | New England, USA | 6488.3 | 466 | 32 |
| `usa_rally_01_route_4` | Fuller Mountain Ascent | New England, USA | 6468.3 | 460 | 30 |
| `usa_rally_01_route_5` | Fury Lake Depart | New England, USA | 6681.6 | 516 | 33 |
| `usa_rally_02_route_0` | Beaver Creek Trail Forward | New England, USA | 12856.7 | 930 | 63 |
| `usa_rally_02_route_1` | Beaver Creek Trail Reverse | New England, USA | 12765.9 | 1494 | 64 |
| `usa_rally_02_route_2` | Hancock Hill Sprint Forward | New England, USA | 6229.1 | 466 | 31 |
| `usa_rally_02_route_3` | Tolt Valley Sprint Reverse | New England, USA | 6604.0 | 530 | 31 |
| `usa_rally_02_route_4` | Tolt Valley Sprint Forward | New England, USA | 6575.9 | 508 | 33 |
| `usa_rally_02_route_5` | Hancock Hill Sprint Reverse | New England, USA | 6109.5 | 460 | 34 |

### Physics surface codes used near the routes

69 codes appear within 250 m of the 156 routes. All are mapped in `config/surface-map.toml`, where each mapping
is commented with its DR2 mechanics class. By mechanics class:
- gravel: `GLD+`, `GD*`, `GMD+`, `GM*`, `GHD+`, `GH*`, `GLE+`, `GL*`, `GV0+`, `GBK+`, `GB*`, `GWS+`
- dirt and mud: `DRT+`, `DR*`, `DLE+`, `DL0+`, `DBK+`, `DB*`, `MD0+`, `MB0+`
- grass: `GRS+`, `GR*`, `GSL+`, `GGL+`, `GSO+`, `GS0+`, `GWT+`
- rock: `ROK+`, `RBK+`
- snow and ice: `SNO+`, `SWC+`, `SSS+`, `SWS+`, `SWD+`, `SNB+`, `ICS+`
- sealed: `TSD+`, `TRD+`, `TR2+`, `CON+`, `CNR+`, `CPR+`, `CBW+`
- wood and metal: `WDB+`, `WDS+`, `MRD+`, `CATG`
- default: `DEFA`

## 9. Open questions

- **Handedness:** right-handed is assumed (§6). The open check is whether the stages are mirrored relative to the
  real places. Overlay a stage on a map, e.g. NZ coastline or Ocean Beach, or decode a stage map texture (tpk
  format 38).
- **Object placements and object collision:** `objects.ens`, `ornaments.bin`, `trees.bin` and
  `drivable_entities.jpk` are not decoded, so the IR has no trees, fences, signs or buildings, and no wall
  collision.
- **Pacenote semantics:** subcall `type`/`angle`/`modifier*`, and the origin of `distanceAlongTrack`.
- **Terrain materials:** how `terrain_array.fx` blends its texture arrays. Vertex colour and the packed ST stream
  are exported or ignored but not interpreted.
- **Smaller unknowns:**
  - `resetlines.cqtc` (reset positions) and AI line files are not decoded.
  - The `flags` field in the ground triangles.
  - `landscape.heightfield`.
- **Patch-layer order:** `game_2_1.dat` and `game_2.dat` are placed above the location archives, but the engine's
  real precedence was not observed (the two never overlap each other).
