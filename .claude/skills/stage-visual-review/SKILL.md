---
name: stage-visual-review
description: Check and improve how a converted ACR Extra Stages stage looks in Assetto Corsa Rally, and grade whether it looks like a polished modern rally game. Use after any change that affects a stage's visuals (scenery.toml, visuals.toml, NativeFoliage/NativeProps/DropIn/StageBuild code, launcher visual flags), when asked to check how a stage looks in game, or to "make the track look good". Builds and installs the stage, takes in-game screenshots with tools/acr-autoshot, inspects them for artifacts, low-resolution textures, missing, wrongly used and implausibly placed assets (fences, posts, signs), grades the result, and drives fixes until the stage looks like a real ACR stage.
---

# Stage visual review

Goal: a converted DR2 stage should look like it shipped with Assetto Corsa Rally: polished, not weird. No visible
holes, stretching or placeholder colours, textures as sharp as ACR's own, props and branding that fit the country,
every object placed where it makes sense, natural ground and banks. Judge it as a player of a modern rally game
would, and grade it (see "Quality grade"). Every claim about how a stage looks must come from a new in-game screenshot, never from the build report alone.

Read AGENTS.md first (asset rules, what works, open issues). The in-game tool is described in docs/autoshot.md.

## The loop

1. **One change at a time.** Make a single visual change, so a new artifact can be traced to its cause.
2. **Build and install one stage** (default New Zealand, the tuned one):
   `AcrExtraStages build --only AcrXsNz01r5 --force --keep-temp --yes`, then install it with the same flags the user
   uses (docs/native-assets.md, e.g. `install --only AcrXsNz01r5 --native-props --acr-ground --acr-road`). Check
   `work/acr/additive/stage-<id>.report.json` and `work/acr/batch/logs/<id>.log` for failed checks first.
3. **Screenshot it in game:** tell the user it is starting and that they must not touch the PC for ~2 minutes, then
   `tools/acr-autoshot/acr-autoshot.py run --stage AcrXsNz01r5 --shots 2` (run in the background, watch its output).
   It needs the game closed. On failure it saves `work/autoshot/shots/<id>-failed.jpg`: look at it before retrying.
   "the game is not the active window (active window: ...Claude...)" means the user typed in the chat during the run
   (keys are only sent to the game): ask them to leave the PC, including the chat, alone and retry.
4. **Make the review views:** `python3 .claude/skills/stage-visual-review/scripts/crops.py <shot>.png`. Look at
   `overview.jpg` first, then **every** tile at native resolution and the two road views. Small defects (holes,
   seams, stretched texels) are invisible in the overview.
5. **Write the findings** (format below), compare with the previous run's shots of the same stage (same camera, so
   a side-by-side of the same tile shows the effect of the change), decide the next change, repeat.
6. Stop and report when the remaining issues need a decision from the user (a new approach, a long rebuild of all
   stages) or when the grade is 4 or better in every area.

Baseline: a vanilla ACR stage shot shows what "good" is. Take one with
`acr-autoshot.py run --stage WelesS4HafrenSouthFullForward` (Hafren South, the host level; the game's row id is
spelled "Weles") and keep it in `work/autoshot/reference/`; compare texture sharpness, ground cover density, bank and
road look against it. One is there: `WelesS4HafrenSouthFullForward-manual-20261101.jpg` (the user's own screenshot).
What it shows about gravel roads: no edge line at all; light grey-brown gravel with single stones and only slightly
darker wheel tracks; over the last 0.5-1 m the gravel thins into sparse grass and moss, and 3D grass tufts grow over
the outer gravel; tall grass right up to the road, no bare band.

## What to ignore (game and overlay UI on the start line, chase camera)

Timer box and country/FP/time strip (top left), stage progress bar (top centre), "Start Procedure / Accelerate"
box (centre, on the car), rev counter (bottom right), icon column (left), MangoHud (top left, normally hidden).
The player's car itself. Fine detail exactly behind these is not reviewable: say so instead of guessing.

## What to look for

Name each issue by what is seen, where, how bad, the likely cause, and where to fix it. Known causes:

### Artifacts (geometry / projection)
- **Stretched streaks / "barcode"** (sharp texture patches broken by bands of long parallel stripes): first suspect
  **UV precision**, not projection. The engine renders ProceduralMesh UVs as half floats, so any world-space UV
  larger than ~16 repeats snaps in visible steps (at 256-512 repeats: 1/4 repeat). Seen 2026-11-01 on the NZ01r5
  start, right bank (~1/6 of the frame); fixed the same day by shifting each triangle's box UVs by whole repeats
  into 0..1 (Visuals.cs `BoxUv`; verify check "terrain box UVs within +-N repeats"). Any section with world-scale or
  distance-along UVs (road UV0/UV1 along the stage) can show the same, worse the further from the start. Only if
  UVs are small: the box projection axis runs along the face (pick the axis per face normal, or triplanar).
- **Triangles, sharp corners and boxy shapes (a failure, high severity)**: nature has no straight edges. Any visible
  triangle, wedge, sharp corner, straight crease, square ledge or box-like block in the ground, road, verge or bank is
  a failure, not a detail, even when small: the eye reads it as "game mesh" at once. Look for: triangular facets with
  hard shadow edges on banks ("origami"), light or white wedges and slivers at the verge, flat strips with straight
  parallel sides across a slope (a terrace or ledge with a hard lip), rectangular patches of another texture, a
  texture or colour change along a straight line, saw teeth (see below), square-cut ends of a road, verge or bank, and
  props whose box-shaped base or cut-off end shows. Seen 2026-11-01 on NZ01r5: white wedges at the left verge by the
  cones, a brown flat strip with a straight lip across the right rock bank, faceted rock planes on that bank. Likely
  causes: the coarse DR2 ground mesh (big triangles), steep-face texture on single small faces (`terrain:steep`), a
  road- or bake-coloured patch on a ledge, cut lines that follow triangles. Fixes: hide with rocks, boulders, ground
  cover or grass placed on it; smooth or subdivide the mesh there; soften material changes (blend, no straight
  seams). Any such shape within about 30 m of the road caps geometry at 2.
- **Holes in the ground** (small sky-blue triangles or slivers along the road edge and on banks; the sky shows
  through). Seen on NZ01r5 at both road edges right in front of the car. Cracks between the road strip and the
  ground mesh, or missing triangles. `hole_check_m` in config/visuals.toml is the offline check; a hole the check
  missed means it samples too coarsely or only near the centreline.
- **Saw-tooth road edges (a failure, high severity)**: the road strip ends in a row of triangular teeth against the
  grass instead of a line. Real gravel roads have an organic edge: a smooth, gently wandering line where gravel thins
  out into the verge, never straight-sided triangles. Seen 2026-11-01 on NZ01r5 at the start
  (`work/autoshot/reference/fail-sawtooth-road-edge.jpg`): left of the car, teeth 0.5-1.5 m deep along the whole
  edge from the cones to the bottom of the frame; right of the car the same, with sky-blue holes in the notches.
  Light tan or white slivers between road and grass and blue holes in the notches go with it. Cause: the road
  section is whole DR2 collision triangles with road surface codes (Visuals.cs, `roadCodes`), so its outline is
  their outline. Fix in Visuals.cs (RenderGround / road section): cut the road to a smooth edge line along the
  centreline (inside the teeth, slight low-frequency wander) and let the DR2 ground bake's verge show beyond it.
  Never accept this as "a bit stepped": any visible tooth along the road edge caps geometry at 2.
- Z-fighting (flickering stripes where two surfaces overlap), floating or sunken props and plants, plants or props
  on the driving line, dark "black" materials (RVT materials: see AGENTS.md).

### Low-resolution or wrong-looking textures
- **Flat, uniform grass fields** (one lime colour, no detail, no ground cover): the DR2 ground bake (~14 cm/px)
  with nothing on it. Seen left of the NZ01r5 start. Raise ground cover (`grass_per_100m2`, `grass_max_offset_m`
  in `[native_props]`), add ACR detail (`--acr-ground`), vary colour.
- **Blotchy verges** (watercolour patches of green and tan with hard seams): the ground bake's colour resolution.
- **Road surface**: check tiling (the same crack/tread pattern repeating), longitudinal stripes, a hard change in
  colour or brightness along the road (ACR road vs DR2 road sections), wet/dark tarmac look on a gravel stage.
  NZ01r5 near the start: very dark, high-contrast tyre-track pattern that reads as mud next to the lighter gravel
  further on.
- Bare vista: distant hills as smooth green blobs without trees (DR2 vista mesh, no foliage there).
- Any texture blurrier than the ACR props next to it at the same distance.

### Missing assets
- Placeholder or flat class colours (magenta, violet, plain grey boxes: `hide_flat_colours` should prevent these).
- Gaps where DR2 had objects: no fences/tape along the road, no spectator area, no signs at the start; bare edges
  where the DR2 shots (`acr-ref preview`, docs/visual-preview.md) show vegetation.
- Missing trees: compare density with the DR2 preview at the same station.

### Wrongly used assets
- **Branding for another country**: the start gantry says "RALLY WALES / CYMRU" on a New Zealand stage
  (`MI_RallyGantryWales` in the `core_game_startfinish_i` rule). Use a material matching the stage's country or a
  neutral one; check `acr-ref mic-scan` for `MI_RallyGantry*` variants.
- Vegetation that does not fit the country or biome (e.g. Welsh spruce in a New Zealand coastal scene); a single
  mesh repeated so often it looks cloned.
- Props facing the wrong way (signs showing their back to the road), wrong scale (scale stays 1: pick another
  variant), props of another era or region (French signage in New Zealand: `nz_placesign_a` uses Alsace signage).
- Fences or tape crossing the road, gantries not spanning the road.

## Does it make sense? (plausibility)

A shot can be free of artifacts and still look weird. Look at every object and ask: would a rally organiser, a
farmer or nature have put this here, like this? Judge each against the country of the stage (NZ: coastal farmland,
pohutukawa, flax, wire stock fences, timber posts; Scotland: forestry, spruce, stone walls, wire fences).

**Fences**
- Type fits the country and the land use: stock wire fences on timber posts along NZ / Scottish farmland, log or
  rail fences in alpine areas. Check the rules in `[native_props]`: e.g. `core_barr_fence_log_mid_a` becomes
  `ItaLivModualarFencingC2` (Livigno, an Italian alpine fence). Ask whether that looks right in New Zealand; if
  not, map it to a fence from `/Game/Props/Fences/` that does (`acr-ref mesh-thumbs --filter Fence`).
- Fences follow the ground: posts stand on it (not floating, not buried to the top wire), rails follow the slope
  instead of cutting into the hill or hanging in the air.
- They run continuously, like a real fence line: no lone 2 m segments in a field, no random gaps, no overlapping
  doubles, ends finished with an end post, roughly parallel to the road or a field edge. Never across the road or
  the driving line, never through trees or rocks.
- One fence type per run of fence; a change of type mid-line looks like an error.

**Posts, poles, markers, tape**
- Edge marker posts: evenly spaced, on the verge (not on the road, not 10 m into a field), upright, all the same
  type along a stretch, at a sensible height (about 1 m).
- Tape poles (`SM_PlasticTapePoleA`) belong at spectator areas, junctions and the start/finish, carrying tape
  between them; scattered single poles in the forest look wrong.
- No posts standing alone in the middle of nowhere, in the road, on top of each other or leaning at random.

**Signs and branding**
- Text, language and road-sign design fit the country (NZ uses English, NZ-style signs: French
  `TrafficSignFraSheepShort` for a cattle grid and Alsace `FraSavSignageAB` place signs are suspects).
- Rally signs in the order a real stage has them (start, flying finish, stop control) and facing the driver.
- Event branding (gantries, banners) for this rally's country, not another one.

**Start / finish areas**
- A believable start: gantry spanning the road at the start line, timing board, marshal or service vehicles parked
  off the road, cones and tape keeping spectators back, TV camera positions. Nothing blocking the car's path.

**Nature and scale**
- Plants grow where they could: not out of rock faces at odd angles, not on the road, not floating; tree line and
  species fit the region; density natural (no clones in rows, no single tree on a bare hillside unless DR2 had it).
- Scale is right relative to the car (about 1.5 m high, 4 m long): doors, posts, signs, people-sized props.
- Parked cars and vans on level ground, off the road, facing a sensible direction.

## Quality grade: does this look like a polished modern game?

Before the detail work, do the **3-second test**: look at the overview as a player would at the start line. Does
anything draw the eye that should not be there? A polished modern rally game (ACR's own stages, EA WRC, DiRT
Rally 2.0) never shows stretched textures, holes, floating objects, placeholder colours or out-of-place assets.

Saw-tooth road edges (triangular teeth where road meets grass) fail the 3-second test on their own, as does any other
triangle, sharp corner or boxy shape in the ground near the road (natural ground has no straight edges).

Then score each area from 1 to 5, using these anchors:

| Area | 1 broken | 3 acceptable | 5 polished |
|---|---|---|---|
| Geometry | holes, sky through the ground, spikes | closed; no visible triangles, wedges or boxy ledges near the road; road edges smooth lines | smooth natural terrain, organic road edges, no straight edges anywhere |
| Textures | stretched, smeared, placeholder colours | correct but blurry or visibly tiling | sharp up close, varied, no visible repeats |
| Road | breaks, stripes, wrong surface | right surface, some tiling or colour jumps | consistent and believable for the rally |
| Vegetation | missing, floating, on the road | present, a bit sparse or uniform | dense, natural, fits the region |
| Trackside props | flat colours, wrong country, nonsense placement | right kinds, a few odd placements | everything where a real event would put it |
| Overall polish | looks like a mod in progress | believable at a glance, flaws on a closer look | could be a screenshot from the game's store page |

**Grade** = the lowest area score, not the average: one broken area is enough to make the stage look unfinished.
5 = polished, 4 = good, 3 = acceptable, 2 = rough, 1 = broken. The aim is 4 or better in every area.

Calibration (NZ01r5 start, 2026-11-01): geometry 2 (stretched rock bank, holes, saw-tooth road edges; the user
confirmed the teeth are a bug, not acceptable: roads must have organic edges), textures 2,
road 3, vegetation 4, trackside props 3 (good ACR props, but a Wales gantry), overall 2: **grade 2, rough**. The
rock bank alone fails the 3-second test.

## Report format

Write each review to `work/autoshot/reviews/<id>-<time>.md`, so progress can be followed across runs:

```
Stage AcrXsNz01r5, shots work/autoshot/shots/<files>, change: <what changed since the last run>
Grade: 2 (rough) | geometry 2, textures 2, road 3, vegetation 4, props 3, overall 2 | previous run: <grade>
3-second test: <what draws the eye first>
| # | Severity | Category | Where (tile, image area) | What is seen | Likely cause | Proposed fix |
```
Categories: artifact, low-res texture, missing asset, wrong asset, implausible (makes no sense). Severity:
**high** = draws the eye at once or breaks immersion (stretched banks, holes, saw-tooth road edges, triangles,
wedges, boxy ledges or straight seams in natural ground, wrong country
branding, placeholder colours, a fence across a field for no reason); **medium** = noticed when looking (flat grass,
road tiling, a foreign-looking fence type); **low** = only up close or arguable. Then: what got better or worse than the
previous run (same tile side by side), and the next change you propose: the one that raises the lowest area score.

## Rules

- Tuned on New Zealand (AcrXsNz01r5) only; changes for one country must not break the others (Scotland builds too).
- Never commit game data or screenshots of it unless the user asks (docs/media is curated by the user).
- Keep Windows behaviour unchanged (AGENTS.md ground rules). Do not commit unless the user asks.
- The start line is one camera position. When a fix concerns things not visible there, say that the screenshot
  cannot confirm it.
