# Track-selection menu images (2026-10-23)

What ACR's track-selection screens show for a stage, where each image comes from, and what the additive port replaces.
Tools: `acr-additive menu-dump`, `menu-assets`, `menu-options`, `tpk-png` (`tools/acr-additive/Menu.cs`, `MenuArt.cs`, `MenuOptions.cs`).
Dumps and previews: `work/acr/menu-preview/` (`dump/` = every DB table as JSON, `vanilla-*/` = a vanilla stage's images, `<Id>/` = a stage's
generated images, `loading-contact.*.png` = all DR2 loading art, `spot-*.png` = one stage per location).

## How the menu finds an image

Every image field of a track / variant / location row is a `DBReference {Table, Row}` into a texture table; a texture-table row
(`DBTexture`) is a single soft path. So a new image needs a new **texture-table row** - the four tables the registry overrode before
(DT_Levels, DT_Tracks, DT_TracksVariants, DT_TracksLocations) can only point at rows that already exist.

| Screen element | Row field | Texture table | Wales / Hafren South (vanilla) | Format |
|---|---|---|---|---|
| Main-menu location tile (portrait card) | DT_TracksLocations.BackgroundImage | DT_TracksLocationsBackgrounds | T_WalesRally 406x737 photo | B8G8R8A8, 1 mip |
| Location logo plate | DT_TracksLocations.Logo | DT_TracksLocationsLogos | T_RallyWales 485x218 | B8G8R8A8 |
| Track map (all variants, white) | DT_Tracks.Map | DT_TracksTextures | T_HafrenSouthS4Base 800x800 | DXT5 |
| Track backdrop photo / lobby | DT_Tracks.TrackImage, LobbyTrackImage | DT_TracksTextures | T_HafrenSouthBackground 1325x922 | B8G8R8A8 |
| Variant banner | DT_TracksVariants.VariantImage | DT_TracksTextures | T_Severne 1720x448 | BC7 |
| Variant lobby thumbnail | DT_TracksVariants.LobbyVariantImage | DT_TracksTextures | T_Afon_Lobby 170x100 | B8G8R8A8 |
| Variant route map (red, start/finish icons) | DT_TracksVariants.MapSmall | DT_TracksTextures | T_HafrenSouthS4 800x800 | DXT5 |
| Height profile | DT_TracksVariants.ElevationMap | DT_TracksTextures | T_HafrenSouthAltimetry 306x142 | B8G8R8A8 |
| Loading screen | DT_LoadingScreen (Data/UI/Datatable) | - | T_*TrackBkgWide 7680x2160 | DXT1 (not touched) |

All are UI textures: TEXTUREGROUP_UI, sRGB, NeverStream, inline mips (one, except the T_Rally* logos, which have a full chain).
Numbers on the info panel come from the variant row: `Length` (km), `Elevation` (Vector2D min / max m), `ElevationAtStart`,
`Sectors[] {Length km, SurfaceType, DegradationLevel}`, `DifficultyLevel`, `SurfaceTypes`, `StartLocation` (lat / long x 360000; left as the template's).

## What the build does (`menu_images = true`, set for every stage in `config/stages.toml`)

Spec block `"Menu": { "Images": true, "Art": "<DR2 loading tpk>", "ElevationOffset": <m, optional> }` (written by `scripts/gen-stage-specs.ps1`).

- `menu-assets` (run by `mods/additive/build.ps1` per stage) writes into the **stage container** under `/Game/AcrXs/Stages/<Id>/Menu/T_<Id>_<Kind>`,
  each a copy of a same-size, same-format vanilla UI texture with its mip overwritten (`UiTexture.Write`); previews go to `work/acr/menu-preview/<Id>/`.
  - drawn from the IR centreline (`work/ir/<stage>/stage.json`), vanilla style: **Map** 800x800 white route; **MapSmall** 800x800 red route
    (222,24,22) with start ring + direction arrow, finish dot and chequered flag; **Elevation** 306x142 red profile over white dashed guides,
    scaled to the stage's own climb (at least 20 m). Plan view: screen right = +X, screen up = -Z (DR2's own route maps, up to a rotation).
  - cropped from DR2's loading art (`Art`, inside a 6% inset because the art's edges are blurred): **Backdrop** 1325x922 (TrackImage and
    LobbyTrackImage), **Banner** 1720x448 BC7 (VariantImage, encoded with BCnEncoder), **Lobby** 170x100 (LobbyVariantImage).
- DR2 art: `scripts/prepare-menu-art.ps1` extracts `frontend/streamed_textures/loading/<loc>_rally_NN_route_{0,2,3}.tpk` (78 painted aerials,
  2304x1296 BC1, no logos or text; all checked on `loading-contact.*.png`) from the local DR2 install into `work/dr2/menu-art/`; gen-stage-specs
  and build.ps1 run it when missing. Choice: the stage's own route when DR2 has art for it, else route 0 of the same rally, else any art of the
  location; `menu_art = "<name>"` overrides, `menu_art = "none"` keeps the template's photos.
- `registry` appends `<Id>_{Map,MapSmall,Elevation,Backdrop,Banner,Lobby}` to **DT_TracksTextures** (the fifth overridden table, add-only,
  vanilla rows byte-identical) and points the track / variant fields at them; sets the 3 sector lengths from the DR2 splits the level's
  RaceSector1/2 use. Elevation / ElevationAtStart only change with `ElevationOffset` (DR2 Y is not altitude: Poland is about -1644 m).
- Locations (`[locations.*]` in `config/stages.toml`; Wales and Greece stages join the vanilla rows, the others are new rows cloned from Wales):
  - `country` = a vanilla DT_Countries row (flag via its Icon -> DT_CountryFlags, and the "Nation" text). There is no Scotland row, so
    Scotland uses UnitedKingdom; Monte Carlo uses Monaco like ACR's own. Note the row spellings `FinlAnd` / `PolAnd`.
  - `surface` = Gravel / Tarmac / Snow: the location's SurfaceTypesData (info-box surface %) comes from Wales / Alsace / Livigno, and every
    variant's SurfaceTypes, CarSetupDefaultSurfaceType and TireCompounds from WelesS4HafrenSouth / AlsaceS2Munster / LivignoTestTrack01
    (GravelOnly / TarmacSnow / FullSnow). Matches `config/surface-map.toml` (tarmac: Germany, Spain, Monte Carlo; snow: Sweden).
  - `tile` / `logo` reuse a vanilla DT_TracksLocationsBackgrounds / Logos row (only Monte Carlo: Montecarlo / MonteCarlo). Otherwise the
    registry generates them (`MenuLocations.cs`, `menu-locations`) into the **registry container** as `/Game/AcrXs/Locations/<Loc>/T_<Loc>_{Tile,Logo}`
    and appends `AcrXs<Loc>` rows to DT_TracksLocationsBackgrounds / Logos (overridden only then; add-only):
    **Tile** 406x737 (template T_WalesRally) cropped from `tile_art` or the location's first `*_route_0` loading art; **Logo** 485x218
    (template T_RallyWales, full mip chain, box-filtered) from `scripts/menu-location-logos.ps1`: a neutral plate in the vanilla badge shape,
    "RALLY" + the location name, `logo_colors` for the plate / stripes, no DR2 or WRC marks. Previews: `work/acr/menu-preview/locations/`
    (`<Loc>.Tile.png`, `<Loc>.Logo.png`, `selection-strip.png` = rough mock of the rally strip with the vanilla flags).
  - `description` -> HistoryAndTrivia (info-box text). Difficulty (DifficultyLevelData) is still the Wales clone (6, Medium).
- `verify`: DT_TracksTextures / Backgrounds / Logos keep every vanilla row identical; each stage texture row resolves to a Texture2D of the
  expected format / size in exactly that stage's container and not in vanilla; the track / variant fields point at them (or at the template's
  when there is no art); sectors add up to the stage length; variant surface fields equal the vanilla surface variant's. Per new location:
  Country is the configured vanilla row; tile / logo are the configured vanilla row or a B8G8R8A8 texture of the template size only in the
  registry container; SurfaceTypesData equals the vanilla surface location's. The registry container holds exactly the overridden tables
  plus the generated location textures, and all of them survive the retoc round trip byte-identical.
- Alternative tile source (not used yet, needs a decision): DR2's frontend bundles `b_locations_{non,}persistent_{base,patch}.pssg` hold
  ground-level location photos like ACR's own tiles (`s_<loc>_l` 608x400, `panel_<loc>` 352x240; USA = `s_michigan`, Scotland =
  `s_scotland_gravel`). Dump with `dr2-extract pssg-textures <pssg> <dir>`; comparison: `work/acr/menu-preview/locations/photo-tiles.png`.

## Open

- `ElevationOffset` per location (absolute heights in the panel are still Hafren's).
- The profile is scaled per stage, so very flat stages (Poland 01 r2, 27 m) look as hilly as steep ones; raise the 20 m minimum span if wanted.
- Not tested in game yet.
