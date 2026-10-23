# ACR load proof — install & test checklist (~5 min)

Two independent mod containers, built and re-read offline with CUE4Parse.

## Result (2026-10-11, user in-game test): both PASSED

- **(a)** The stage-select card for **Afon Bidno - Severn** (variant **1/2**, FullForward) showed **42 km**.
  IoStore containers in `Paks\~mods\*_P` mount and override base-game packages. [V]
- **(b)** On that variant the car jumped at the invisible ramp shortly after the start. The baked collision
  asset (`UBakedDataContainer`) **is used by the shipping game for driving physics**, and an appended
  collider in the `None` stride set is live. [V]
- The in-stage HUD route length still showed ~5.1 km. The HUD distance comes from the route
  spline, not from `DT_TracksVariants.Length`, which only feeds the menus. [V]
- Pitfall from the first attempt: variant **2/2** "Severn - Afon Bidno" is **FullReverse**. It isn't
  patched, and its route reaches the ramp only at the finish, from the high side. Both variant names
  contain "Afon Bidno".

| Container | Overrides | What you should see |
|---|---|---|
| `zz_acrxs_test_P` (.utoc/.ucas/.pak, ~40 KB) | `/Game/Data/Database/Main/TrackSelection/DT_TracksVariants` — FullForward "Afon Bidno" `Length` 4.8 → **42.0** | Stage length shows 42 km |
| `zz_acrxs_ramp_P` (~172 MB) | `/Game/Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData` — one extra mesh collider | Invisible ramp launches the car ~47 m after the start line |

## 0. Build (skip if `work\acr\dist` already has the six files)

```powershell
cd C:\Users\robbe\Projects\acr-dirt
pwsh -NoProfile -File mods\loadproof\build.ps1            # add -Verify to re-read with CUE4Parse
```

Output: `work\acr\dist-loadproof\zz_acrxs_test_P.{utoc,ucas,pak}` and `zz_acrxs_ramp_P.{utoc,ucas,pak}`.

## 1. Install (manual, fully reversible)

```powershell
$paks = 'C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally\acr\Content\Paks'
New-Item -ItemType Directory -Force "$paks\~mods" | Out-Null
Copy-Item C:\Users\robbe\Projects\acr-dirt\work\acr\dist\zz_acrxs_* "$paks\~mods\"
```

`~mods` did not exist before (checked on 2026-10-11), so nothing of the game is overwritten. You can
install only the `test` container first if you want to isolate test (a).

## 2. Test (a) — container mounts and overrides a cooked asset

1. Start ACR, go to stage selection, choose **Wales → Hafren South**, variant **1/2 "Afon Bidno - Severn"**
   (not 2/2 "Severn - Afon Bidno", which is the reverse).
2. Read the stage length on the info panel.

- **42 km (or 42.0)** → IoStore mods in `~mods` load and override base-game assets (`_P` priority works). ✅
- **4.8 km** → container not mounted. Check the file names end in `_P`, all three files are present, then
  try moving them directly into `Paks\` (next to `pakchunk0-Windows.utoc`). Note what worked.
- Reverse variant should still say 4.8 km (only the forward row was patched).

## 3. Test (b) — BakedData drives physics

1. Start a stage (free practice, any car) on **Afon Bidno - Severn** (variant 1/2).
2. After the countdown, drive straight down the road at 40–60 km/h. About **47 m after the start line**
   (the first stretch, before any notable corner) there is an invisible 6 m wide × 8 m long wedge that
   rises 1 m in the driving direction, spanning the road centre.
3. Results:
   - **Car is launched / pitches up hard at an invisible bump** → ACR uses the BakedData asset for road
     collision at runtime; editing it is a viable route for new stages. ✅
   - **Nothing happens** (and test (a) passed) → BakedData mesh colliders are *not* what physics uses here
     (or are rebuilt at runtime). Report this — it changes the authoring recommendation.
   - **Game crashes / hangs on stage load** → the edited asset is malformed for the game's reader.
     Remove `zz_acrxs_ramp_P.*` and report; include the newest UE log if you can find one (usually
     `%LOCALAPPDATA%\acr\Saved\Logs\` for UE games — path not verified for ACR).
   - **Car falls through the world** → the asset loaded but colliders were dropped; report with log.

The ramp is in the always-active collider set, so it also exists on the **reverse** variant (~47 m before
the finish). Coming from that side you would hit its 1 m high edge like a kerb/wall, so don't test it there.

## 4. Rollback

```powershell
Remove-Item "$paks\~mods\zz_acrxs_*"
Remove-Item "$paks\~mods" -ErrorAction SilentlyContinue   # only removes the folder if it is empty
```

If anything still looks off, Steam → ACR → Properties → Installed Files → *Verify integrity*.

## What exactly was changed (for interpreting results)

- **test**: one float in the `DT_TracksVariants` row `WelesS4HafrenSouthFullForward` (offset 5902 of the
  legacy `.uexp`, matched uniquely by `Length` followed by `Elevation (345, 410)` and
  `int32 1, ElevationAtStart 478.28683`).
- **ramp**: collider #734 appended to `BakedMeshCollisionData` (4 vertices, 4 double-sided triangles,
  identity transform, physical material `PM_SurfaceGravelFine` and owner copied from collider #10),
  plus stride `{Index 734, Num 1}` appended to the `None` (always-active) entry of
  `DataLayerToMeshCollisionDataIndices`. Physics-space corners (metres, Y up):
  `(-1014.79, 477.58, 936.71) (-1013.89, 477.91, 942.65) (-1021.80, 478.87, 943.84) (-1022.70, 478.55, 937.91)`.
  The leading edge sits 5 cm under the sampled baked road surface; the far edge is 1 m above it.
  Position: point 274 of the **reverse** cell's (`7FF9…`) CenterSpline, ≈ 40 m past the forward start
  line. The start line is ≈ point 275 there, and ≈ point 17 in the forward cell `22VG…`. (This was
  first written as "forward CenterSpline", which was wrong; the location is correct, as the in-game jump
  confirmed.)
- Both containers are produced with `retoc to-legacy` → edit → `retoc to-zen --version UE5_6`.
  The round trip leaves the `.uexp` byte-identical; the `.uasset` gains one harmless name-map entry.

## Known caveats

- The game might validate content or require a matching pak signature; there were no `.sig` files in
  `Paks\` and the TOC is unencrypted, so this is not expected (inferred, not tested).
- The ramp container replaces a 172 MB asset; load time for this stage may rise slightly.
- If a future game patch changes either asset, rebuild (`build.ps1` re-extracts only when
  `work\acr\legacy` is missing — delete that folder after a game update).
