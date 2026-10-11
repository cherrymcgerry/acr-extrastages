# ACR stage anatomy (ground truth: Wales S4 "Hafren South" / Afon Bidno)

Source: ACR Steam build, UE **5.6.1**, IoStore (unencrypted, Oodle), unversioned properties, read with
`tools/acr-ref` (CUE4Parse + Dumper-7 usmap). Raw exports live in `work/acr/` (gitignored).

Legend: **[V]** verified from cooked data or the binary · **[I]** inferred, not proven · **[?]** open.

Hafren South was picked because it is the smallest real stage (163 World Partition cells, 4.8 km,
forward + reverse variants).

## 1. Registration chain (DataTables) — what makes a stage appear in menus

All under `/Game/Data/Database/Main/...`. Row names are the join keys. The game itself has a typo
(`Weles…`) in some keys; new rows can use any consistent names. [V]

```
DT_TracksLocations["Wales"] ── Tracks[] ──► DT_Tracks["WalesS4HafrenSouth"]
                                              ├─ Asset    ──► DT_Levels["WelesS4HafrenSouth"] ─► /Game/Levels/WalesS4HafrenSouth (umap)
                                              ├─ Variants ──► DT_TracksVariants["WelesS4HafrenSouthFullForward" | "...FullReverse"]
                                              └─ Location ──► DT_TracksLocations["Wales"]
DT_TracksVariants[...] ── DataLayers ──► DT_DataLayers[...] ─► DL_HafrenSouthFullForward{RallyProps,GameplayActors} (DataLayerAsset)
```

| Table / row | Fields that matter for a new stage | Status |
|---|---|---|
| `DT_TracksLocations` `Wales` | `Tracks[]`, `DefaultVariant`, `Country`, `Discipline=Rally`, `Difficulty`, `Surface` (Gravel 1.0) | [V] |
| `DT_Tracks` `WalesS4HafrenSouth` | `Name` (string table `ST_Track` key `TRACK_STAGE_WALES_HAFRENSOUTH`), `Map`/`TrackImage`/`LobbyTrackImage` (rows in `TracksTextures`), `Asset` (→ DT_Levels row), `Location`, `Variants[]`, `WeatherTypesDistribution`, `OptimalAirTemperature` | [V] |
| `DT_Levels` `WelesS4HafrenSouth` | soft path to the umap | [V] |
| `DT_TracksVariants` `WelesS4HafrenSouthFullForward` | `Name`/`StageName` (ST_Track keys; "Afon Bidno", "Afon Bidno - Severn"), `Track`, `Surface` (Gravel 100), `TireCompounds`, `Length` 4.8, `Elevation` (345, 410), `ElevationAtStart` 478.28683, `AverageRoadWidness` 4.5, `StartLocation` (lat/long ints), 4 texture refs (location, lobby, variant map, elevation profile), `DifficultyLevel`, `DegradationLevel`, `DataLayers.Common[]`, `Sectors[]` (3 × length/surface/difficulty), `CarsData` (→ `AITrackVariantsCarsData` row), `WeatherConfig` (→ `WeatherTrackConfigs` row), `bIsCircuit=false` | [V] |
| `DT_DataLayers` | row → `DataLayerAsset` (runtime type) | [V] |
| `DT_PacenoteHafrenSouthForward/Reverse` | pacenote rows, referenced by `PacenoteSetupActor` in the gameplay cell | [V] (row schema not decoded in detail) |

The menu reads `Length`/`Elevation` from the DataTable rather than computing them. Patching `Length` is
load-proof (a) and has been verified offline; whether the game picks it up is still untested. [V/?]

## 2. The level (`/Game/Levels/WalesS4HafrenSouth.umap`)

World Partition level. `MainGrid` CellSize 35000 uu, LoadingRange 64000 uu. Cooked as 1 persistent
package (2982 exports) plus 163 streaming cell packages and 87 HLOD actors. [V]

### Persistent level actors [V]

| Actor | Role / overridden properties |
|---|---|
| `BC_TrackAvatar_C` | Physics bridge (`/Script/dmphysics.TrackAvatar`). Instance sets only `BakedData` → `DA_WalesS4HafrenSouthBakedData`, `MaterialsForWorldAlignedPhysicalMatMaskUVs` [M_RoadSurface_Layered, M_TerrainMaster_light], `TileSizeParameterName`. The CDO provides `PhysicalMaterialsSettings` = `DA_PhysMaterialsMappingCommon`, `CollisionProviderOwnerClasses` [StaticMeshActor, InstancedFoliageActor, PCGPartitionActor, PCGGrassContainer, BC_SplineMeshRepeatCollisions_C, TrackTile] and `IgnoredComponentClasses` [ReplaceableFoliageISMC]. `bForceUseBakedData` is not set (false). |
| `BC_PhysicsAvatar_C` | dmphysics world; references a `PhysicsWorldBounds` brush volume |
| `GameConfigurator` | `Configurations`: FullForward / FullReverse → `DataLayersToLoad` |
| `WorldDataLayers` | 4 × `DataLayerInstanceWithAsset`: `DataLayer_BE01…` Fwd Gameplay, `E8A3…` Fwd RallyProps, `B42F…` Rev Gameplay, `C2CD…` Rev RallyProps |
| `BC_PlayerStartsManager_C`, `AcrTargetPoint`, `ServiceParkTargetPoint`, `BC_RefuelZoneTargetPoint_C` | spawn and service points |
| `WorldSettings` | `DefaultGameMode` = `BC_GameMode_C` |
| `BC_DMSky_Candidate_C`, SkyLight, SkyAtmosphere, `BC_DynamicSurfaceManager_C`, weather/replay/crowd/audio/tape/skid managers | environment and presentation |
| 5 `DMSplineComponent` + crowd/mechanic splines | crowd and mechanic paths only. The **racing splines are not here**; they live in the gameplay cells. |

### Gameplay data-layer cells [V]

Forward set in cell `22VG0QIP9ATUQI633K86KWPFP`; reverse set in `7FF9A0XR2HIRJVFZU6AEAFHMU`.

- `SplinesActor` holds the DMSplineComponents `IdealSpline` (with `DMSplineMetadata`: LeftWidth, …),
  `CenterSpline`, `LeftBorder`, `RightBorder`, `Contour`, and an empty `Pitlane`. The forward variant's
  actor has `bIsReverse=true`. Forward driving runs toward decreasing CenterSpline point indices.
- `BC_StartSequenceTrigger_C` (box; UE (-96992, 93576, 47829), yaw 84.3) and `BC_EndSequenceTrigger_C`
  (UE (29534, -79891, 39912)); `BC_CompleStartTargetPoint_C` is the car start.
- `PacenoteSetupActor` → `DT_PacenoteHafrenSouthForward`.
- 8 `RaceSector`, 19 `SplineEventTrigger`, RespawnPlanes, start/stop marshals
  (`BC_MarshalStartEndSequence*`, `BC_StopMarshal*`), and 8 `BC_GantryWales_C`.

### Visual / static cells [V]

- 87 `TrackTile` actors (StaticMeshComponents `Terrain_0` + `Road_0`, tag `PCG_Ground`).
- 160 `StaticMeshActor`s, PCG partition actors, foliage, crowd, tapes and HLODs.
- Render meshes are Nanite static meshes cooked into the cell packages.

## 3. Physics: `UBakedDataContainer` (`DA_<Stage>BakedData`) [V]

Physics is **Kunos dmphysics**, not Chaos. The baked asset is a pre-cooked collision soup:

| Field | Hafren South |
|---|---|
| `BakedMeshCollisionData[]` | 734 colliders, 9.29 M vertices, 4.77 M triangles. Each: `Transform` (always identity), `Positions` (f32 xyz), `Colors` (always (0,0,0,1)), `Indices` (u32), `PhysMaterial`, `Owner` (soft path of the source actor). By material: GravelFine 269 colliders / 3.84 M triangles, GravelHard 139, WallBasic 288, WallWood 33, Special 3, HayBale 1, MetalGate 1. |
| `BakedSimpleCollisionData[]` | 15,326 entries (81 boxes, 17,100 capsules); mostly tree trunks (`PM_WallWoodTreeTrunks`), in UE units |
| `DataLayerToMeshCollisionDataIndices` / `...SimpleCollisionDataIndices` | map DataLayer name → list of {Index, Num} strides. Key `None` = always active; `DataLayer_<guid>` = colliders that exist only with that layer (rally props such as gates and bales) |
| `BakedSplinesData` | empty |

- **Coordinates [V]:** physics (x, y, z) = UE (X, Z, Y) / 100, i.e. metres with Y up. Evidence: the start
  trigger's Z/100 = 478.29 equals `ElevationAtStart`, and both triggers fall inside the road colliders'
  bounds; the opposite sign hits nothing.
- **Owners [V]:** colliders keep the source actor path (731 from this level, 3 from WalesServicePark).
  The same geometry can appear under several TrackTile owners.
- **Binary layout [V]:** fully parsed and re-written byte-exactly by `acr-ref baked-add-ramp`, with the
  trailer and stride maps round-tripping.

### Runtime vs baked: what the shipping binary says

- **[V]** Shipping strings exist for both paths:
  - "Baked Data asset not set" / "Assign an existing one or create … 'Create Baked Colliders Asset'"
  - "Mesh %s is used as a runtime collider but it does not have CPU Access"
  - "Mesh %s added as collider does not contain valid collider data"
  - "PhysicalMaterialsSettings is not valid, colliders will be ignored"
- **[V]** TrackAvatar init (`0x14505C5D0`) caches the PhysicsAvatar. If the `BakedData` soft path is
  empty it only logs the warning and does **not** return early. No `bForceUseBakedData` test exists there.
- **[V]** Scanning all dmphysics code (0x144E00000–0x145200000) found no read of the `bForceUseBakedData`
  offset (0x349). The flag is effectively unused in shipping (likely editor-only).
- **[V]** A world-level "actor registered" handler (`0x14505BDF0`) checks the new actor's class:
  - `IsA(TrackAvatar)` → `0x14504B660`, which calls the runtime mesh-collider builder `0x1450541F0`
    (CPU-access, LOD/section and physical-material-mask checks).
  - `IsA(PhysicsAvatar)` → a separate handler.
  - A deferred task also calls `0x14504B660` for each entry of an array on the avatar.
- **[I]** So runtime collider gathering from `CollisionProviderOwnerClasses` exists in shipping, alongside
  BakedData. Cooked Nanite render meshes normally lack CPU access, which is presumably why BakedData
  exists.
- **[V] In-game (load-proof b):** an appended BakedData collider is driven on, so BakedData supplies the
  road physics in shipping.

## 4. What a new stage must provide

Minimum, by layer. **[I]** unless marked; this is derived from what Hafren South contains.

1. **DataTable rows** (append; the game has no per-mod DT merging, so the whole table is overridden):
   - `DT_TracksLocations` (or reuse a location and append to `Tracks[]`), `DT_Tracks`, `DT_Levels`,
     `DT_TracksVariants` (one per direction), `DT_DataLayers`, `DT_Pacenote<Stage><Dir>`.
   - Rows in `AITrackVariantsCarsData`, `WeatherTrackConfigs`, `TracksTextures`.
   - `ST_Track` string-table keys.
   - 4 textures per variant (location, lobby, variant map, elevation) plus track images.
2. **A level package** whose persistent level contains at least `BC_TrackAvatar_C` (with `BakedData`),
   `BC_PhysicsAvatar_C` + `PhysicsWorldBounds`, `GameConfigurator`, `WorldDataLayers` with one
   gameplay data layer per direction, `BC_PlayerStartsManager_C`, `WorldSettings` (BC_GameMode_C) and
   a sky / weather / dynamic-surface setup.
3. **Per-direction gameplay actors** (in that direction's data layer):
   - `SplinesActor` (Ideal/Center/Left/Right/Contour DMSplines + metadata)
   - Start/End sequence triggers, the start target point, `PacenoteSetupActor`, `RaceSector`s and
     respawn planes
   - Marshals/gantries are optional dressing **[?]**
4. **Physics**: a `UBakedDataContainer` with road/terrain mesh colliders (gravel/tarmac physical materials
   from `DA_PhysMaterialsMappingCommon`), walls/props, optional simple colliders, and the `None` stride
   map covering everything always present.
5. **Render**: static meshes (terrain, road, objects) with materials. Optional: foliage, crowd, audio
   splines, HLODs.

## 5. Open questions

- [V] The game reads both the patched DT row (menu length) and the edited BakedData (physics); see
  `docs/load-proof.md`. The HUD route length comes from the route spline, not the DT.
- [?] What does the gathering array on the TrackAvatar contain? (It's drained by the deferred task.)
  Would runtime colliders double up with baked ones?
- [?] Is World Partition mandatory? Could a non-WP level with the same actors work (GameConfigurator
  data layers suggest WP / data layers are expected)?
- [?] Schemas of the pacenote, `AITrackVariantsCarsData` and `WeatherTrackConfigs` rows are not yet
  documented field by field.
