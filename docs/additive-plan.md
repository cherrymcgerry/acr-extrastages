# Additive stages: every ported stage next to the vanilla ones

**Goal:** ported DR2 stages show up in the stage menu **in addition to** the ACR stages. No vanilla track or variant
is replaced, and many ported stages can be installed at once.

**Status (2026-10-11):**
- The prototype is built and passes every offline check. It adds "ACR-DIRT TEST", a Hafren South forward clone with
  a menu length of 1.23 km, under Wales.
- The in-game test is pending ([additive-test.md](additive-test.md)).
- Tooling: `tools/acr-additive` (new tool) and `mods/acrdirt-additive/build.ps1`.
- Outputs go to `work\acr\additive\`.

Evidence tags: **[V]** = verified offline (CUE4Parse / byte level); **[G]** = verified in game earlier (load proof,
nz01r5); **[I]** = inferred, still to be confirmed in game.

## 1. How a stage reaches the menu

The menu reads the dmdb database plugin.

- `DefaultGame.ini [/Script/dmdb.DBProjectSettings] Profile` points at `DA_DBMain` (a `DBProfile`).
- `DBProfile.Tables` maps a table key (`Levels`, `Tracks`, `TracksVariants`, `TracksLocations`, `TracksTextures`, …)
  to a DataTable soft path. A `DBProfile` also has a `Parent` profile (soft pointer).
- Rows point at other rows with `DBReference {Table = DA_DBMain key, Row}`.

| table (key) | row struct | what matters for a new stage |
|---|---|---|
| `DT_TracksLocations` (`TracksLocations`) | DBTrackLocation | `Tracks` (**Set** of refs). A new track must be appended to its location's set. |
| `DT_Tracks` (`Tracks`) | DBTrack | `Name` (Text), `Asset` → `Levels` row, `Location`, `Variants` (Set), plus images (`TracksTextures` refs) |
| `DT_TracksVariants` (`TracksVariants`) | DBTrackVariant | `Name`, `StageName` (Text), `Track`, `Length` (float, km, shown in the menu), `DataLayers` (data-layer asset names), elevation, images, sectors, weather |
| `DT_Levels` (`Levels`) | DBLevel | `Value` = soft path of the level (`/Game/…/X.X`) |
| `DT_DataLayers`, string tables, textures | – | Reused as they are. New Text uses culture-invariant strings, so no string-table rows are needed; images are the template's `TracksTextures` refs. |

Facts behind the design:
- No package hard-imports the DB tables [V]. A container-header scan of 38,430 packages in 106 containers found
  none; they are reached only through DA_DBMain's soft paths.
- Only `/Script` packages and `ST_Track` are imported by the tables themselves [V].
- An IoStore container in `Paks\~mods\*_P` overrides a vanilla package with the same path [G].
- The level is reached only through `DT_Levels` → soft path. Its cells are reached through the level's
  `WorldPartitionLevelStreamingDynamic` objects:
  - `PackageNameToLoad` (an FName) and `WorldAsset` (a soft path) point at each cell;
  - each cell points back through `Level.WorldPartitionRuntimeCell` (a soft path);
  - cells never hard-import the persistent level [V].
- The track's gameplay data lives in `DA_*BakedData`:
  - the level's `BC_TrackAvatar` points at it with a soft `BakedData` pointer;
  - the colliders inside it carry `Owner` soft paths that name the level [V].

## 2. Level / gameplay options

### (a) New data layer and cell inside the host level: rejected

The variant row's `DataLayers` selects which data layers load. A new variant could in principle activate a new layer
that holds our cell.

- It still has to **override the host persistent level** (to add the layer instance, the streaming object and the
  cell) and the host's BakedData (which holds the track spline, colliders and timing). Both are shared with the vanilla
  variants, which is exactly the overriding we must avoid.
- The host scenery cells and always-loaded actors stay on in the new variant.
- One mod per host level could own the persistent package, so stages on the same host would conflict.

### (b) Relocated clone of the host level: chosen

Copy the host's persistent level, its 163 generated cells and its BakedData under new package paths. Rename every
reference, and register the copy with a new `DT_Levels` row.

- Every package in a stage container is new: **0 vanilla packages overridden** [V]. The vanilla variants load the
  untouched vanilla level.
- What gets renamed (all legacy, by `acr-additive relocate`):
  - the summary `PackageName`;
  - name-map entries equal to an old package path or starting with `old/`;
  - exact object renames, so the World keeps the package's short name.

  In cooked UE 5.6 these name-map entries cover every package reference: soft paths, `PackageNameToLoad`, the
  `WorldAsset`/`WorldPartitionRuntimeCell` soft paths and import package names. Actor labels, debug cell names and the
  level-script class (`WalesS4HafrenSouth_C`) are raw FStrings or export names local to the package and stay as they
  are. The rename counts were 167 names in the persistent level, 3 per cell and 4 in BakedData.
- Shared on purpose, untouched:
  - `WalesS4HafrenSouth_BuiltData` (58 MB). Only the level imports it, and it does not import the level back [V].
  - the `DL_HafrenSouth*` data-layer assets, so the template variant row's `DataLayers` still resolve;
  - environment meshes and materials, the baked occlusion textures and the pacenote tables.
- BakedData / TrackAvatar: the clone's `BC_TrackAvatar.BakedData` points at the clone's own
  `DA_<Id>BakedData` [V]. That BakedData's collider `Owner` paths name the clone level (renamed with the package map).
  For a DR2 port, the stage-build output BakedData (today written under the Hafren path) gets relocated the same way.
- The zen package id is `CityHash64(lowercase package path)`; retoc computes it from the summary `PackageName`. New
  paths therefore give new ids, checked against all 38k vanilla ids [V].

### (c) acr-rewind runtime hook: fallback only

A UE4SS-style hook could register rows in `DBSubsystem.Tables` at runtime, or redirect a level load. This is not
needed: (b) works with plain containers, needs no injected DLL, and survives game updates by rebuilding. Keep it as
the fallback if the in-game test shows the menu ignores the merged tables, for example because some cache or a
profile parent is read elsewhere.

## 3. Architecture

```text
Paks\~mods\
  zz_acrdirt_registry_P.{utoc,ucas,pak}        the 4 DB tables = vanilla rows + every installed stage's rows
  zz_acrdirt_s_<id>_P.{utoc,ucas,pak}          one per stage: /Game/AcrDirt/Stages/<Id>/... only (new paths)
```

- **Stage spec** (`mods/acrdirt-additive/stages/<id>.json`): `Id`, `Level`, `Location` (an existing row, or a new one
  cloned from a template), `Track` (template plus name), `Variants` (row, template, name, length, optional data
  layers), and `Clone` (package map and object renames).
- **Registry** (`acr-additive registry`): loads the vanilla tables, appends the spec rows, and rejects key collisions
  with vanilla rows. It writes four legacy packages, which are packed into one container (50 KB).
- **Stage container** (`acr-additive relocate`, then `retoc to-zen`): only new package paths.
- **Build**: `pwsh -NoProfile -File mods\acrdirt-additive\build.ps1 -Verify` (about 35 s once extracted).
  - Every input comes from the vanilla hard-link view (`mods/common/vanilla.ps1`), never `~mods`.
  - The legacy cache is re-extracted when the stamp's container list differs from the game's (a patch).

**Naming**
- Package paths: `/Game/AcrDirt/Stages/<Id>/<Id>` (level), `/Game/AcrDirt/Stages/<Id>/<Id>/_Generated_/<cell>`, and
  `/Game/AcrDirt/Stages/<Id>/DA_<Id>BakedData`.
- Row keys: `<Id>` for the Levels and Tracks rows, `<Id><Variant>` for variants, e.g. `AcrDirtTestFullForward`.
  Ids are PascalCase with an `AcrDirt` prefix for real stages, e.g. `AcrDirtNz01r5`.
- Containers: `zz_acrdirt_registry_P` and `zz_acrdirt_s_<id lowercase>_P`.

**Package ids**
- Unique by construction (the path namespace `/Game/AcrDirt/`).
- `verify` checks for each container:
  - every id equals the hash of its path;
  - ids are unique within the container;
  - no id is shared with a vanilla package or another mounted container;
  - every hard import resolves.

**Why one registry and not per-stage tables**
- A DataTable package is overridden as a whole, so two containers that each carry `DT_TracksVariants` would hide each
  other's rows. Exactly one container may own the DB tables.
- Install the registry built **in the same run** as the stage containers. A stage container without its registry is
  inert (nothing references it). A registry row without its stage container would fail to load when picked, so
  build and install them as a set.
- Alternatives worth one in-game experiment each, because they would remove the single-owner constraint:
  1. `DBProfile.Parent`: a mod profile whose parent is DA_DBMain and which lists only the new tables. It is selected
     by a user `Game.ini` override of `[/Script/dmdb.DBProjectSettings] Profile`. Whether dmdb merges parent and
     child tables row by row is not verified; `DBSubsystem.Tables[*].Sources` suggests multi-source tables [I].
  2. `CompositeDataTable` (an engine class present in the usmap): a composite `DT_TracksVariants` with parents
     `[vanilla copy, stage A, stage B]`. This would still override the four vanilla table paths, but only once, with
     a generic composite, and each stage would carry its own rows. It is unknown whether the dmdb row lookups accept
     a composite [I].

## 4. Limits and budget

- **Stage count:** no hard limit found.
  - Each stage adds one container (three files, mounted at startup) and three to four rows.
  - Menu layout is the likely practical limit: Wales goes from one track to two in the prototype. A location with
    many tracks may page or overflow, which is still to be seen in game.
  - New locations can be added through `Location.Template`. Their logo and background are the template's.
- **Size:** retoc writes uncompressed `.ucas`.
  - Prototype: a full Hafren clone of 364 MB, made up of the persistent level (53 MB), 163 cells (150 MB) and
    BakedData (172 MB).
  - A DR2 port should not carry Hafren's scenery cells. Ship only the persistent level, the gameplay cell(s), the
    stage BakedData and its pacenote table, which is expected to be about the size of today's nz01r5 override
    (~230 MB). Aim for under 250 MB per stage, i.e. about 5 GB for 20 stages.
  - Dropping cells means removing their streaming objects, runtime cells and HLOD entries from the persistent level.
    That step is not implemented yet.
- **Shared vanilla assets:** anything the clone shares (BuiltData, data layers, pacenote tables, meshes) is also
  changed by any other mod that overrides it. Example: the nz01r5 override's Hafren BakedData and persistent level do
  not touch the clone, but its `DT_PacenoteHafrenSouthForward` override would reach the clone too.

## 5. After an ACR patch

1. Run the build again. The vanilla stamp differs, so the legacy cache is re-extracted, the registry is rebuilt from
   the patched tables (new vanilla rows included), and the clones are rebuilt from the patched host level.
2. Check that `verify` passes, in particular that the row schemas still decode. `dt-selftest` round-trips the tables
   byte for byte, and a schema change shows up there first; refresh the usmap with Dumper-7 if it fails.
3. Until rebuilt, **uninstall the registry container**. A stale registry hides vanilla rows added or changed by the
   patch. Stale stage containers alone are harmless because nothing references them.

## 6. Risks and open questions

- [I] The menu might cache or enumerate tracks some other way, e.g. through the AssetRegistry, which our new map is
  not in. If so, the stage either won't list or won't load. Fallbacks: AssetRegistry entries, a profile parent, or (c).
- [I] Online, leaderboard and career features reference tracks by row. Use the new stages in offline modes only.
- [I] Raw FStrings keep the host names, e.g. cell debug names and actor labels. These are cosmetic; level-script
  `WalesS4HafrenSouth_C` lives in the clone's own package, so it does not collide.
- [I] Only one registry can be installed. It conflicts with the nz01r5 override (dist v1.x), which also ships
  `DT_TracksVariants`, so do not install them together. Porting nz01r5 to a stage spec removes the conflict.
- The variant `DataLayers` override in the spec is unused. Its struct encoding is a guess, and the template's value is
  kept.
