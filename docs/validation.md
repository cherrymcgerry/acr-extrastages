# In-game validation recorder (`tools/stage-validate`)

Records ACR telemetry while you drive a ported stage. Then checks the run against the stage IR, placed in ACR
physics space with the same transform `acr-ref stage-build` used. You get a text report, a pass/fail verdict and
an SVG plot.

- **Read-only:** only `OpenFileMapping(FILE_MAP_READ)` on the `Local\acpmf_*` pages. Nothing is written to, or
  injected into, the game.
- **Telemetry reader:** the page layouts come from `acr-rewind/crates/acr_shm` via a path dependency. That repo
  must be checked out next to this one (`..\acr-rewind`); it is not modified.

## Build

```powershell
cd C:\Users\robbe\Projects\acr-dirt
cargo build --release --manifest-path tools\stage-validate\Cargo.toml
$sv = 'tools\stage-validate\target\release\stage-validate.exe'
```

`cargo test --manifest-path tools\stage-validate\Cargo.toml` runs the unit and synthetic-run tests (20). One
of them also uses the real NZ route 5 IR and report when `work\` has them.

## 1. Record

Start the recorder before or after launching the game. It waits for the shared memory to appear.

```powershell
& $sv record --out work\validate\nz01r5-run1.csv
```

Then drive. Recording stops on any of these:

- **Ctrl+C**.
- **Leaving the stage:** the car position disappears (back to the menus) for 5 s (`--lost-timeout`, 0 = off).
- **Idle:** shared memory doesn't change for 300 s (`--idle-timeout`, 0 = off). This catches a closed game.

Other options:

- `--hz` sets the poll rate (default 50).
- `--no-wait` fails instead of waiting when the game isn't running.

A status line shows speed, gear, position, ACR's clock and the lap count. A row is written only when a page
updated or the car moved, so pauses don't bloat the file.

Outputs:

- **`<out>.csv`:** one row per sample. `#` lines at the top hold metadata (static page: sm/ac version, car,
  track). Columns are listed below.
- **`<out>.csv.meta.json`:** written on stop. Row count, stop reason, static page, and the columns that stayed
  0 or never changed during the run.

### Columns

All positions and velocities are ACR physics/sim space: metres, Y up, `sim = (UE.x, UE.z, UE.y) / 100`.
Per-wheel columns are in FL, FR, RL, RR order.

| Columns | Source | Notes |
|---|---|---|
| `t_s` | recorder clock | seconds since start |
| `phys_packet`, `gfx_packet`, `status`, `session` | packetId, graphics status/session | `status` reads 0 (OFF) while live on build 25170642, so it is logged but not used |
| `pos_src`, `pos_x/y/z` | graphics `carCoordinates` | **The physics page has no world position.** `pos_src`: 1 = player entry, 2 = slot 0 (what offline solo fills), 0 = no car. Trails physics by ~1 update (~0.5 m at 60 km/h) |
| `vel_x/y/z`, `speed_kmh`, `gear`, `rpm`, `gas`, `brake`, `clutch`, `steer` | physics | `gear` -1 = R, 0 = N, 1.. = forward |
| `heading`, `pitch`, `roll`, `cg_height`, `acc_g_x/y/z` | physics | |
| `tyres_out` | physics `numberOfTyresOut` | |
| `load_*`, `susp_*`, `slip_*`, `slip_ratio_*`, `slip_angle_*`, `wheel_rads_*` | physics per wheel | |
| `dirty_*` | physics `tyreDirtyLevel` | |
| `cp_{x,y,z}_*`, `cn_y_*` | physics `tyreContactPoint`, `tyreContactNormal` (y only) | world contact points, if ACR publishes them |
| `damage_0..4`, `susp_damage_*` | physics `carDamage`, `suspensionDamage` | |
| `surface_grip` | graphics `surfaceGrip` | the only surface-related field: **no per-wheel surface/material ID exists in these pages** |
| `i_current_ms`, `i_last_ms`, `i_best_ms`, `completed_laps`, `number_of_laps`, `sector_index`, `last_sector_ms`, `i_split_ms`, `distance_traveled`, `normalized_pos`, `session_time_left`, `penalty_time`, `is_valid_lap`, `flag`, `is_in_pit` | graphics | timing/session |

Known ACR quirks (acr-rewind `docs/technical-reference.md` section 7) and how they're handled:

- **Physics page all zero:** `check` counts those samples. It derives speed and vertical velocity from the
  position over a 0.25 s window instead.
- **Fields ACR doesn't publish** (`camberRAD`, `rideHeight`, `tyreTempI/M/O`, `tyreWear`): not recorded. Which
  of the recorded columns ACR actually fills is only known after a first real run. Check the
  `always_zero_columns` list in the meta file and in the `check` report.

## 2. Check

```powershell
& $sv check --run work\validate\nz01r5-run1.csv `
    --stage work\ir\new_zealand_rally_01_route_5\stage.json `
    --transform work\acr\stage-nz01r5.report.json `
    --json work\validate\nz01r5-run1.report.json
```

This prints the report and writes `<run>.svg` (`--svg` to change the path). The exit code is 1 when the overall
verdict is FAIL.

By default, the IR's `collision.glb` (1M triangles, ~2 s to load) is used for exact ground heights; `--no-ground`
skips it. Tuning:

- `--off-margin 2` (m beyond the road edge)
- `--fall 1` (m below the road or ground)
- `--drop-vy -15` (m/s)
- `--bin 250` (m, speed profile bins)

### Transform

`physics = R_y(θ)·(dr2 − (cx, 0, cz)) + (tx, dy, tz)`, with `x' = cos θ·x + sin θ·z` and
`z' = −sin θ·x + cos θ·z`. The values are read from the report's `placement`.

This is the same formula as `StageBuild.Placement.ToPhysics` (tools/acr-ref/StageBuild.cs). The unit tests pin
it in two ways:

- the converter's own output: our transform of DR2 gates 3 and 433 lands on the RaceSector0 / RaceSector3 UE
  positions logged in the report, within their logged lateral/up offsets;
- the route length (`dr2_centre_length_m` 4842.776 m) and the sector distances (35.6 / 4715.6 m).

### What it checks

Route distance `s` is measured along the transformed IR centreline, the same polyline the race sectors were
placed on. The car is projected onto it sequentially, searching near the previous match, so hairpins and
parallel road sections don't make it jump. Lateral offset is positive to the right of the driving direction.

| Check | Verdict |
|---|---|
| **telemetry** | FAIL if no sample has a car position; WARN if most physics pages read zero |
| **progress** | max `s` reached (only samples within 30 m of the road and not falling count), vs the total and the finish line. FAIL if the finish line wasn't reached |
| **fall-through** | FAIL on any of: **below-road** (within 5 m of the road and > 1 m under the IR road surface, which is interpolated across the gate's left/right edges); **below-ground** (> 1 m under the lowest DR2 collision surface at that XZ; needs the glb); **below-floor** (> 1 m under the DR2 collision minimum, y = 380 m; the flattened Hafren layer is at 306–318 m). A **drop** (vertical velocity < −15 m/s) on its own is a WARN |
| **off-route** | WARN if any stretch of ≥ 1 s is beyond the road edge + 2 m (edges from the gates' left/right points). Lists `s` range, side and max offset |
| **timing** | From the shm fields. **Started:** `iCurrentTime` starts counting. **Finished:** `completedLaps` increments, `iLastTime` is set, or `iCurrentTime` freezes within 30 m of the finish line for > 3 s. PASS if both; FAIL if the car crossed the finish line but shm never showed a finish; UNKNOWN if no timing field ever left 0. The report also gives start/split/finish times derived from the car crossing the IR lines, as a cross-check |
| **wheel contact on DR2 ground** | only if the contact points are published and the glb is loaded: share of loaded wheels whose contact point is within 0.3 m of a DR2 surface. WARN under 90% (wrong ground, or the points aren't world space) |

The report also gives:

- the lateral offset percentiles and a histogram of |lateral| / half-width;
- the car position's median height above the DR2 ground (expect a few tenths of a metre);
- teleports (resets, ACR Rewind rewinds, respawns): listed, not counted as falls or progress jumps;
- a speed profile per 250 m;
- `normalizedCarPosition`, `distanceTraveled` and the sector index sequence (they show whether ACR's own
  progress tracking follows the new splines);
- the columns that stayed 0.

**Overall:** FAIL if any check fails, WARN if any warns or is unknown, else PASS.

### Plot

- **Top-down map:** SVG x = physics x, y = physics z. This is not mirrored, so right-hand corners turn right.
  - grey: the IR road corridor (gate edges); dashed: the centreline; coloured bars: start, split and finish lines;
  - driven path coloured by |lateral| / half-width: dark green ≤ 0.5, light green ≤ edge, orange ≤ 1.5×, red beyond;
  - magenta circles: fall-throughs; thin blue dashes: teleports.
- **Charts below the map:** speed, and signed lateral / half-width, against route distance.

## Trying it without the game

`synth` writes a fake run that follows the centreline: a ±1.5 m weave, 100/60 km/h stretches, and timing
fields like a working stage.

```powershell
& $sv synth --stage work\ir\new_zealand_rally_01_route_5\stage.json --transform work\acr\stage-nz01r5.report.json --out work\validate\synth.csv
& $sv check --run work\validate\synth.csv --stage work\ir\new_zealand_rally_01_route_5\stage.json --transform work\acr\stage-nz01r5.report.json
```

Options:

- `--fall-at 2500` injects a fall-through at 2.5 km (FAIL);
- `--stop-at <m>` ends the run early;
- `--no-timing` leaves the shm timing fields at 0 (timing UNKNOWN).

The clean synthetic run on NZ route 5 passes: progress 100%, all samples on the road, a median 0.42 m above the
real DR2 collision mesh for a 0.4 m synthetic ride height, start/finish detected, time 3:41.6.
