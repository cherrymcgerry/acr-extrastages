# Visual preview (offline)

`acr-ref preview` renders a built stage next to DR2 from the same cameras, so a person or an agent can see what a
visuals change did without starting the game. It takes a few seconds per stage.

## Run it

Build the stage and keep the scratch folder (it holds the built cell), then preview it:

```bash
AcrExtraStages build --only AcrXsNz01r5 --force --keep-temp --yes
acr-ref preview --ir work/ir/new_zealand_rally_01_route_5 \
    --report work/acr/additive/stage-acrxsnz01r5.report.json \
    --cell work/acr/additive/mod-build-acrxsnz01r5 \
    [--every 250] [--size 960x540] [--far 2500] [--out work/preview/<stage>]
```

`--cell` takes the mod-build folder or the cell `.umap` itself. Use `acr-ref` from `work/bin/acr-ref` (or
`tools/acr-ref/bin/Release/net10.0`).

## What it draws

Cameras sit on the DR2 centreline every `--every` metres: a **chase** view (9 m behind, 3.2 m up) and an **aerial**
view (35 m behind, 30 m up). Each camera renders two scenes:

| Scene | Contents |
|---|---|
| `dr2` | The port's ground sections (terrain, road, vista) plus **every** DR2 object instance with its DR2 render mesh and texture, vegetation included: the target. |
| `port` | Exactly the visuals mesh of the built cell (`ProceduralMeshComponent`), textured the way the build mapped it: DR2 textures where it made DR2 materials, a flat class colour for the ACR `MI_Wall*` materials. |

The renderer (`tools/acr-ref/Raster.cs`) is a small CPU rasterizer: textures with alpha test, Lambert sun and
distance fog. It has no ACR materials, shadows or LODs, so it shows **what is there and where**, not the in-game
look. DR2's billboard LODs and shadow cards are skipped and full meshes are drawn at every distance. Both scenes share
the port's ground, so their differences come from objects and vegetation only.

## Output (`work/preview/<stage>/`)

| File | Contents |
|---|---|
| `station-<m>.png` | 2 x 2: DR2 and port, chase view on top, aerial below |
| `contact.png` | All chase views, DR2 and port per row: the overview to look at first |
| `metrics.json` | Per view: share of pixels per category (sky, ground, road, object, vegetation, trunk) in each scene, and the pixel agreement between them; a summary |
| `index.md` | The table of stations with the main numbers and links |

The metrics in the summary are the numbers to move:

- `agreement_mean`: share of pixels with the same category in DR2 and the port (1.0 = same layout);
- `vegetation_cover_dr2` / `vegetation_cover_port`: share of the view that is vegetation or trunks;
- `object_cover_dr2` / `object_cover_port`: the same for other objects.

Baseline for `AcrXsNz01r5` (stage-build v1.8, 2026-10-31): agreement 0.79, vegetation 20 % (DR2) against 1 % (port).

## Loop for an agent

1. Change the build (code or `config/visuals.toml`).
2. Rebuild the one stage with `--force --keep-temp`, then run `preview`.
3. Read `metrics.json` and look at `contact.png` and a few `station-*.png`.
4. Keep the change if the numbers moved the right way **and** the images look closer to DR2. Watch the triangle
   count in the build report (`cell.visuals.triangles_total`, checked against `[objects] max_total_triangles`).
5. Confirm the in-game look by hand before calling a change done: the preview cannot show ACR's materials.
