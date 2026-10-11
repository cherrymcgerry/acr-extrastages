//! `check`: compares a recorded run with the stage IR placed in ACR physics space.

use crate::ground::Ground;
use crate::route::{Projection, Route, Tracker};
use crate::run::{field_stats, Sample};
use serde::Serialize;
use std::fmt::Write as _;

/// A car position at most this far above a DR2 surface below it counts as resting on that ground (m).
const SUPPORT_M: f64 = 2.0;

#[derive(Clone, Debug)]
pub struct CheckOpts {
    /// Off-route when |lateral| > half-width + this (m).
    pub off_margin_m: f64,
    /// Off-route segments shorter than this are ignored (s).
    pub off_min_s: f64,
    /// Fall-through when this far below the ground / road / DR2 floor (m).
    pub fall_m: f64,
    /// Vertical velocity below this is a sudden drop (m/s, negative).
    pub drop_vy: f64,
    /// Samples further than this beyond the road edge don't count as progress (m).
    pub lost_m: f64,
    /// Speed profile bin size along the route (m).
    pub bin_m: f64,
}

impl Default for CheckOpts {
    fn default() -> Self {
        Self {
            off_margin_m: 2.0,
            off_min_s: 1.0,
            fall_m: 1.0,
            drop_vy: -15.0,
            lost_m: 30.0,
            bin_m: 250.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub enum Verdict {
    Pass,
    Warn,
    Fail,
    Unknown,
}

impl std::fmt::Display for Verdict {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Verdict::Pass => "PASS",
            Verdict::Warn => "WARN",
            Verdict::Fail => "FAIL",
            Verdict::Unknown => "UNKNOWN",
        })
    }
}

/// Per-sample route-relative state (also used for plotting).
#[derive(Clone, Debug, Serialize)]
pub struct Point {
    pub t: f64,
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub s: f64,
    pub lateral: f64,
    pub half_width: f64,
    pub edge_ratio: f64,
    pub road_y: f64,
    pub ground_y: Option<f64>,
    pub speed_kmh: f64,
    pub vy: f64,
    /// First sample after a position jump (reset / rewind / respawn).
    pub teleport: bool,
    pub fall: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct Event {
    pub kind: String,
    pub t_start: f64,
    pub t_end: f64,
    pub s: f64,
    pub lateral: f64,
    pub detail: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct Segment {
    pub t_start: f64,
    pub t_end: f64,
    pub s_start: f64,
    pub s_end: f64,
    pub max_lateral: f64,
    pub side: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct SpeedBin {
    pub s_from: f64,
    pub s_to: f64,
    pub samples: usize,
    pub mean_kmh: f64,
    pub min_kmh: f64,
    pub max_kmh: f64,
}

#[derive(Clone, Debug, Serialize)]
pub struct Timing {
    pub fields_published: bool,
    pub started: bool,
    pub started_t: Option<f64>,
    pub started_s: Option<f64>,
    pub finished: bool,
    pub finished_t: Option<f64>,
    pub finished_s: Option<f64>,
    pub shm_stage_time_s: Option<f64>,
    pub evidence: Vec<String>,
    pub sector_sequence: Vec<i32>,
    pub max_completed_laps: i32,
    pub max_normalized_pos: f64,
    pub max_distance_traveled: f64,
    /// From the car position crossing the IR start / finish lines.
    pub pos_start_t: Option<f64>,
    pub pos_finish_t: Option<f64>,
    pub pos_stage_time_s: Option<f64>,
    pub pos_splits: Vec<(String, f64)>,
}

#[derive(Clone, Debug, Serialize)]
pub struct Check {
    pub name: String,
    pub verdict: Verdict,
    pub detail: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct Report {
    pub stage: String,
    pub stage_name: String,
    pub samples: usize,
    pub samples_with_pos: usize,
    pub samples_phys_zero: usize,
    pub duration_s: f64,
    pub route_total_m: f64,
    pub ir_length_m: f64,
    pub start_s: f64,
    pub finish_s: f64,
    pub max_s: f64,
    pub progress_pct: f64,
    pub reached_finish: bool,
    pub lateral_p05: f64,
    pub lateral_p50: f64,
    pub lateral_p95: f64,
    pub max_abs_lateral: f64,
    pub on_road_pct: f64,
    /// (label, share of on-route samples) of |lateral| / half-width.
    pub edge_ratio_histogram: Vec<(String, f64)>,
    pub height_above_ground_p50: Option<f64>,
    pub contact_on_dr2_ground_pct: Option<f64>,
    pub off_route: Vec<Segment>,
    pub falls: Vec<Event>,
    pub teleports: Vec<Event>,
    pub speed_bins: Vec<SpeedBin>,
    pub max_speed_kmh: f64,
    pub mean_moving_speed_kmh: f64,
    pub timing: Timing,
    pub always_zero_columns: Vec<String>,
    pub checks: Vec<Check>,
    pub verdict: Verdict,
    #[serde(skip)]
    pub points: Vec<Point>,
}

fn percentile(sorted: &[f64], q: f64) -> f64 {
    if sorted.is_empty() {
        return f64::NAN;
    }
    let k = ((sorted.len() - 1) as f64 * q).round() as usize;
    sorted[k.min(sorted.len() - 1)]
}

fn sorted(mut v: Vec<f64>) -> Vec<f64> {
    v.sort_by(f64::total_cmp);
    v
}

pub fn analyze(
    route: &Route,
    ground: Option<&Ground>,
    samples: &[Sample],
    o: &CheckOpts,
) -> Report {
    let mut tracker = Tracker::new(route);
    let mut points: Vec<Point> = Vec::new();
    let mut prev: Option<&Sample> = None;
    let mut phys_zero = 0usize;
    let mut contact_total = 0usize;
    let mut contact_hit = 0usize;
    let mut teleports = Vec::new();
    // recent positions since the last teleport, for speed / vertical velocity when the physics page is zero
    let mut history: std::collections::VecDeque<(f64, [f64; 3])> = Default::default();

    for s in samples {
        if !s.phys_ok() {
            phys_zero += 1;
        }
        if !s.has_pos() {
            continue;
        }
        let q = s.pos();
        let mut teleport = false;
        if let Some(p) = prev {
            let dt = (s.t_s - p.t_s).max(1e-3);
            let pq = p.pos();
            let d =
                ((q[0] - pq[0]).powi(2) + (q[1] - pq[1]).powi(2) + (q[2] - pq[2]).powi(2)).sqrt();
            let v = if s.phys_ok() {
                (s.speed_kmh as f64 / 3.6).max(p.speed_kmh as f64 / 3.6)
            } else {
                0.0
            };
            // generous: ~2x the speed-based step, 70 m/s when speed is unknown, + 5 m slack
            let expect = if s.phys_ok() { v * dt * 2.0 } else { 70.0 * dt };
            if d > expect + 5.0 {
                teleport = true;
                tracker.reset();
                history.clear();
            }
        }
        history.push_back((s.t_s, q));
        while history.len() > 2 && s.t_s - history[1].0 >= 0.25 {
            history.pop_front();
        }
        let derived_v = history
            .front()
            .filter(|(t0, _)| s.t_s - t0 > 1e-3)
            .map(|&(t0, p0)| {
                let dt = s.t_s - t0;
                let d = ((q[0] - p0[0]).powi(2) + (q[1] - p0[1]).powi(2) + (q[2] - p0[2]).powi(2))
                    .sqrt();
                ((q[1] - p0[1]) / dt, d / dt)
            });
        let pr: Projection = tracker.project(q);
        if teleport {
            teleports.push(Event {
                kind: "teleport".into(),
                t_start: s.t_s,
                t_end: s.t_s,
                s: pr.s,
                lateral: pr.lateral,
                detail: format!(
                    "position jumped from ({:.1}, {:.1}, {:.1}) to ({:.1}, {:.1}, {:.1}) (reset, rewind or respawn)",
                    prev.map_or(0.0, |p| p.pos_x),
                    prev.map_or(0.0, |p| p.pos_y),
                    prev.map_or(0.0, |p| p.pos_z),
                    s.pos_x,
                    s.pos_y,
                    s.pos_z
                ),
            });
        }
        let (vy, speed) = if s.phys_ok() {
            (s.vel_y as f64, s.speed_kmh as f64)
        } else {
            derived_v.map_or((0.0, 0.0), |(vy, v)| (vy, v * 3.6))
        };
        let ground_y = ground.and_then(|g| {
            let gq = g.query(q);
            gq.below.or(gq.lowest)
        });
        if let Some(g) = ground {
            let loads = s.wheel_loads();
            for (w, cp) in s.contact_points().iter().enumerate() {
                if loads[w] > 50.0 && cp.iter().any(|v| *v != 0.0) {
                    contact_total += 1;
                    if g.query(*cp)
                        .nearest
                        .is_some_and(|h| (h - cp[1]).abs() < 0.3)
                    {
                        contact_hit += 1;
                    }
                }
            }
        }
        points.push(Point {
            t: s.t_s,
            x: q[0],
            y: q[1],
            z: q[2],
            s: pr.s,
            lateral: pr.lateral,
            half_width: pr.half_width,
            edge_ratio: pr.edge_ratio(),
            road_y: pr.road_y,
            ground_y,
            speed_kmh: speed,
            vy,
            teleport,
            fall: false,
        });
        prev = Some(s);
    }

    // ---- falls ----
    let mut falls: Vec<Event> = Vec::new();
    for p in points.iter_mut() {
        let mut kinds = Vec::new();
        let mut detail = Vec::new();
        let near_road = p.lateral.abs() <= p.half_width + 5.0;
        let gq = ground.map(|g| g.query([p.x, p.y, p.z]));
        // off the road edge the terrain can lie lower than the road; a car resting on DR2 ground there is not falling
        let supported = gq
            .as_ref()
            .and_then(|q| q.below)
            .is_some_and(|h| p.y - h < SUPPORT_M);
        if near_road && p.y < p.road_y - o.fall_m && !supported {
            kinds.push("below-road");
            detail.push(format!("{:.1} m below the IR road surface", p.road_y - p.y));
        }
        if let Some(gq) = gq {
            if let Some(low) = gq.lowest {
                if p.y < low - o.fall_m {
                    kinds.push("below-ground");
                    detail.push(format!(
                        "{:.1} m below the lowest DR2 ground surface",
                        low - p.y
                    ));
                }
            }
        }
        if let Some(floor) = route.floor_y {
            if p.y < floor - o.fall_m {
                kinds.push("below-floor");
                detail.push(format!(
                    "{:.1} m below the DR2 collision floor (y {:.1})",
                    floor - p.y,
                    floor
                ));
            }
        }
        if p.vy < o.drop_vy {
            kinds.push("drop");
            detail.push(format!("vertical velocity {:.1} m/s", p.vy));
        }
        if kinds.is_empty() {
            continue;
        }
        p.fall = kinds.iter().any(|k| *k != "drop");
        let kind = kinds.join("+");
        match falls.last_mut() {
            Some(e) if p.t - e.t_end < 0.5 && !p.teleport => {
                e.t_end = p.t;
                for k in kind.split('+') {
                    if !e.kind.split('+').any(|x| x == k) {
                        e.kind.push('+');
                        e.kind.push_str(k);
                    }
                }
                e.detail = format!("at the end of the event: {}", detail.join("; "));
            }
            _ => falls.push(Event {
                kind,
                t_start: p.t,
                t_end: p.t,
                s: p.s,
                lateral: p.lateral,
                detail: detail.join("; "),
            }),
        }
    }

    // ---- progress ----
    let start_s = route.start_s();
    let finish_s = route.finish_s();
    let total = route.total();
    let progress_pts: Vec<&Point> = points
        .iter()
        .filter(|p| p.lateral.abs() <= p.half_width + o.lost_m && !p.fall)
        .collect();
    let max_s = progress_pts.iter().map(|p| p.s).fold(0.0, f64::max);
    let reached_finish = max_s >= finish_s - 1.0;

    // ---- position-based line crossings ----
    let crossing = |line: f64| -> Option<f64> {
        progress_pts
            .windows(2)
            .find(|w| !w[1].teleport && w[0].s < line && w[1].s >= line)
            .map(|w| {
                let k = (line - w[0].s) / (w[1].s - w[0].s).max(1e-9);
                w[0].t + k * (w[1].t - w[0].t)
            })
    };
    let pos_start_t = crossing(start_s);
    let pos_finish_t = pos_start_t.and(crossing(finish_s));
    let pos_splits: Vec<(String, f64)> = route
        .timing
        .iter()
        .filter(|t| t.kind == "time")
        .filter_map(|t| {
            Some((
                format!("split at {:.0} m (DR2 gate {})", t.s, t.gate),
                crossing(t.s)? - pos_start_t?,
            ))
        })
        .collect();

    // ---- lateral distribution ----
    let on_route: Vec<&Point> = progress_pts
        .iter()
        .copied()
        .filter(|p| p.speed_kmh > 2.0)
        .collect();
    let lat = sorted(on_route.iter().map(|p| p.lateral).collect());
    let ratios: Vec<f64> = on_route.iter().map(|p| p.edge_ratio).collect();
    let n_on = ratios.len().max(1) as f64;
    let buckets = [
        (0.0, 0.25),
        (0.25, 0.5),
        (0.5, 0.75),
        (0.75, 1.0),
        (1.0, 1.5),
        (1.5, 2.0),
        (2.0, f64::INFINITY),
    ];
    let edge_ratio_histogram = buckets
        .iter()
        .map(|&(a, b)| {
            let label = if b.is_infinite() {
                format!(">{a}")
            } else {
                format!("{a}-{b}")
            };
            (
                label,
                ratios.iter().filter(|&&r| r >= a && r < b).count() as f64 / n_on,
            )
        })
        .collect();
    let on_road_pct = 100.0 * ratios.iter().filter(|&&r| r <= 1.0).count() as f64 / n_on;
    let heights = sorted(
        points
            .iter()
            .filter(|p| !p.fall)
            .filter_map(|p| Some(p.y - p.ground_y?))
            .collect(),
    );

    // ---- off-route segments ----
    let mut off_route: Vec<Segment> = Vec::new();
    let mut cur: Option<Segment> = None;
    for p in &points {
        let off = p.lateral.abs() > p.half_width + o.off_margin_m;
        if off && !p.teleport {
            let side = if p.lateral >= 0.0 { "right" } else { "left" };
            match &mut cur {
                Some(c) => {
                    c.t_end = p.t;
                    c.s_end = p.s;
                    if p.lateral.abs() > c.max_lateral.abs() {
                        c.max_lateral = p.lateral;
                        c.side = side.into();
                    }
                }
                None => {
                    cur = Some(Segment {
                        t_start: p.t,
                        t_end: p.t,
                        s_start: p.s,
                        s_end: p.s,
                        max_lateral: p.lateral,
                        side: side.into(),
                    })
                }
            }
        } else if let Some(c) = cur.take() {
            if c.t_end - c.t_start >= o.off_min_s {
                off_route.push(c);
            }
        }
    }
    if let Some(c) = cur.take() {
        if c.t_end - c.t_start >= o.off_min_s {
            off_route.push(c);
        }
    }

    // ---- speed profile ----
    let nbins = ((total / o.bin_m).ceil() as usize).max(1);
    let mut speed_bins: Vec<SpeedBin> = (0..nbins)
        .map(|b| SpeedBin {
            s_from: b as f64 * o.bin_m,
            s_to: ((b + 1) as f64 * o.bin_m).min(total),
            samples: 0,
            mean_kmh: 0.0,
            min_kmh: f64::INFINITY,
            max_kmh: 0.0,
        })
        .collect();
    for p in &progress_pts {
        let b = &mut speed_bins[((p.s / o.bin_m) as usize).min(nbins - 1)];
        b.samples += 1;
        b.mean_kmh += p.speed_kmh;
        b.min_kmh = b.min_kmh.min(p.speed_kmh);
        b.max_kmh = b.max_kmh.max(p.speed_kmh);
    }
    for b in &mut speed_bins {
        if b.samples > 0 {
            b.mean_kmh /= b.samples as f64;
        } else {
            b.min_kmh = 0.0;
        }
    }
    let driving = points.iter().filter(|p| !p.fall);
    let max_speed_kmh = driving.clone().map(|p| p.speed_kmh).fold(0.0, f64::max);
    let moving: Vec<f64> = driving.map(|p| p.speed_kmh).filter(|v| *v > 5.0).collect();
    let mean_moving_speed_kmh = if moving.is_empty() {
        0.0
    } else {
        moving.iter().sum::<f64>() / moving.len() as f64
    };

    // ---- shm timing ----
    let timing = analyze_timing(
        samples,
        &points,
        route,
        pos_start_t,
        pos_finish_t,
        pos_splits,
        reached_finish,
    );

    let fs = field_stats(samples);
    let duration_s = match (samples.first(), samples.last()) {
        (Some(a), Some(b)) => b.t_s - a.t_s,
        _ => 0.0,
    };

    // ---- verdicts ----
    let mut checks = Vec::new();
    let mut add = |name: &str, verdict: Verdict, detail: String| {
        checks.push(Check {
            name: name.into(),
            verdict,
            detail,
        })
    };
    let with_pos = points.len();
    add(
        "telemetry",
        if with_pos == 0 {
            Verdict::Fail
        } else if phys_zero * 2 > samples.len() {
            Verdict::Warn
        } else {
            Verdict::Pass
        },
        format!(
            "{} samples, {} with a car position, {} with an all-zero physics page",
            samples.len(),
            with_pos,
            phys_zero
        ),
    );
    add(
        "progress",
        if reached_finish {
            Verdict::Pass
        } else {
            Verdict::Fail
        },
        format!(
            "max {:.0} of {:.0} m ({:.1}%); finish line at {:.0} m {}",
            max_s,
            total,
            100.0 * max_s / total,
            finish_s,
            if reached_finish {
                "reached"
            } else {
                "NOT reached"
            }
        ),
    );
    let real_falls: Vec<&Event> = falls.iter().filter(|e| e.kind != "drop").collect();
    let drops = falls.len() - real_falls.len();
    add(
        "fall-through",
        if !real_falls.is_empty() {
            Verdict::Fail
        } else if drops > 0 {
            Verdict::Warn
        } else {
            Verdict::Pass
        },
        if falls.is_empty() {
            "no samples below the road/ground and no sudden drops".into()
        } else {
            format!(
                "{} fall-through event(s), {} sudden-drop-only event(s)",
                real_falls.len(),
                drops
            )
        },
    );
    let off_len = off_route
        .iter()
        .fold(0.0, |a, s| a + (s.s_end - s.s_start).abs());
    add(
        "off-route",
        if off_route.is_empty() { Verdict::Pass } else { Verdict::Warn },
        format!(
            "{} segment(s) beyond road edge + {} m for >= {} s ({:.0} m of route); {:.1}% of moving samples within the road edges",
            off_route.len(),
            o.off_margin_m,
            o.off_min_s,
            off_len,
            on_road_pct
        ),
    );
    let tv = if !timing.fields_published {
        Verdict::Unknown
    } else if timing.started && timing.finished {
        Verdict::Pass
    } else if reached_finish {
        Verdict::Fail
    } else {
        Verdict::Warn
    };
    add(
        "timing",
        tv,
        format!(
            "shm: started {}, finished {}{}; position: start {} finish {}",
            timing.started,
            timing.finished,
            timing
                .shm_stage_time_s
                .map(|t| format!(", time {}", fmt_time(t)))
                .unwrap_or_default(),
            timing
                .pos_start_t
                .map_or("not crossed".into(), |t| format!("crossed at t={t:.1}s")),
            timing
                .pos_finish_t
                .map_or("not crossed".into(), |t| format!("crossed at t={t:.1}s")),
        ),
    );
    if let Some(pct) =
        (contact_total > 0).then(|| 100.0 * contact_hit as f64 / contact_total as f64)
    {
        add(
            "wheel contact on DR2 ground",
            if pct >= 90.0 {
                Verdict::Pass
            } else {
                Verdict::Warn
            },
            format!(
                "{pct:.1}% of loaded wheel contact points within 0.3 m of a DR2 ground surface"
            ),
        );
    }
    let verdict = if checks.iter().any(|c| c.verdict == Verdict::Fail) {
        Verdict::Fail
    } else if checks
        .iter()
        .any(|c| c.verdict == Verdict::Warn || c.verdict == Verdict::Unknown)
    {
        Verdict::Warn
    } else {
        Verdict::Pass
    };

    Report {
        stage: route.id.clone(),
        stage_name: route.name.clone(),
        samples: samples.len(),
        samples_with_pos: with_pos,
        samples_phys_zero: phys_zero,
        duration_s,
        route_total_m: total,
        ir_length_m: route.length_m,
        start_s,
        finish_s,
        max_s,
        progress_pct: 100.0 * max_s / total,
        reached_finish,
        lateral_p05: percentile(&lat, 0.05),
        lateral_p50: percentile(&lat, 0.5),
        lateral_p95: percentile(&lat, 0.95),
        max_abs_lateral: lat.iter().map(|v| v.abs()).fold(0.0, f64::max),
        on_road_pct,
        edge_ratio_histogram,
        height_above_ground_p50: (!heights.is_empty()).then(|| percentile(&heights, 0.5)),
        contact_on_dr2_ground_pct: (contact_total > 0)
            .then(|| 100.0 * contact_hit as f64 / contact_total as f64),
        off_route,
        falls,
        teleports,
        speed_bins,
        max_speed_kmh,
        mean_moving_speed_kmh,
        timing,
        always_zero_columns: fs.always_zero.iter().map(|s| s.to_string()).collect(),
        checks,
        verdict,
        points,
    }
}

fn analyze_timing(
    samples: &[Sample],
    points: &[Point],
    route: &Route,
    pos_start_t: Option<f64>,
    pos_finish_t: Option<f64>,
    pos_splits: Vec<(String, f64)>,
    reached_finish: bool,
) -> Timing {
    let s_at = |t: f64| -> Option<f64> {
        let i = points.partition_point(|p| p.t < t);
        points
            .get(i.min(points.len().saturating_sub(1)))
            .map(|p| p.s)
    };
    let published = samples.iter().any(|s| {
        s.i_current_ms != 0
            || s.i_last_ms != 0
            || s.completed_laps != 0
            || s.sector_index != 0
            || s.normalized_pos != 0.0
    });
    let mut evidence = Vec::new();
    let mut started_t = None;
    let mut finished_t = None;
    let mut shm_time = None;

    // clock running: i_current_ms increasing between consecutive samples
    for w in samples.windows(2) {
        if w[1].i_current_ms > w[0].i_current_ms && w[1].i_current_ms > 0 {
            let t0 = w[1].t_s - w[1].i_current_ms as f64 / 1000.0;
            started_t = Some(t0.max(samples[0].t_s));
            evidence.push(format!(
                "iCurrentTime starts counting at t={:.1}s (clock {} ms, so it started ~t={:.1}s)",
                w[1].t_s, w[1].i_current_ms, t0
            ));
            break;
        }
    }
    for w in samples.windows(2) {
        if w[1].completed_laps > w[0].completed_laps {
            finished_t.get_or_insert(w[1].t_s);
            evidence.push(format!(
                "completedLaps {} -> {} at t={:.1}s",
                w[0].completed_laps, w[1].completed_laps, w[1].t_s
            ));
        }
        if w[1].i_last_ms != w[0].i_last_ms && w[1].i_last_ms > 0 {
            finished_t.get_or_insert(w[1].t_s);
            shm_time = Some(w[1].i_last_ms as f64 / 1000.0);
            evidence.push(format!(
                "iLastTime set to {} ms at t={:.1}s",
                w[1].i_last_ms, w[1].t_s
            ));
        }
    }
    // clock froze after passing the finish line
    let finish_s = route.finish_s();
    if finished_t.is_none() && started_t.is_some() {
        let mut last_change: Option<&Sample> = None;
        for w in samples.windows(2) {
            if w[1].i_current_ms != w[0].i_current_ms {
                last_change = Some(&w[1]);
            }
        }
        if let Some(lc) = last_change {
            let frozen_for = samples.last().map_or(0.0, |l| l.t_s - lc.t_s);
            if frozen_for > 3.0 && s_at(lc.t_s).is_some_and(|s| s >= finish_s - 30.0) {
                finished_t = Some(lc.t_s);
                shm_time.get_or_insert(lc.i_current_ms as f64 / 1000.0);
                evidence.push(format!(
                    "iCurrentTime stopped at {} ms at t={:.1}s near the finish line and stayed for {:.1}s",
                    lc.i_current_ms, lc.t_s, frozen_for
                ));
            }
        }
    }
    if !published {
        evidence.push("no timing field (iCurrentTime, iLastTime, completedLaps, currentSectorIndex, normalizedCarPosition) ever left 0".into());
    } else if reached_finish && finished_t.is_none() {
        evidence
            .push("the car passed the finish line but no shm timing field showed a finish".into());
    }
    let mut sector_sequence: Vec<i32> = Vec::new();
    for s in samples {
        if sector_sequence.last() != Some(&s.sector_index) {
            sector_sequence.push(s.sector_index);
        }
    }
    sector_sequence.truncate(50);
    Timing {
        fields_published: published,
        started: started_t.is_some(),
        started_t,
        started_s: started_t.and_then(s_at),
        finished: finished_t.is_some(),
        finished_t,
        finished_s: finished_t.and_then(s_at),
        shm_stage_time_s: shm_time,
        evidence,
        sector_sequence,
        max_completed_laps: samples.iter().map(|s| s.completed_laps).max().unwrap_or(0),
        max_normalized_pos: samples
            .iter()
            .map(|s| s.normalized_pos as f64)
            .fold(0.0, f64::max),
        max_distance_traveled: samples
            .iter()
            .map(|s| s.distance_traveled as f64)
            .fold(0.0, f64::max),
        pos_start_t,
        pos_finish_t,
        pos_stage_time_s: pos_start_t.zip(pos_finish_t).map(|(a, b)| b - a),
        pos_splits,
    }
}

pub fn fmt_time(t: f64) -> String {
    let m = (t / 60.0).floor();
    format!("{}:{:06.3}", m as i64, t - m * 60.0)
}

pub fn render_text(r: &Report) -> String {
    let mut o = String::new();
    let _ = writeln!(o, "stage-validate check: {} ({})", r.stage_name, r.stage);
    let _ = writeln!(
        o,
        "run: {} samples over {:.1} s ({} with position, {} with all-zero physics page)",
        r.samples, r.duration_s, r.samples_with_pos, r.samples_phys_zero
    );
    let _ = writeln!(
        o,
        "route: {:.0} m centreline, start line {:.0} m, finish line {:.0} m (competitive {:.0} m; DR2 stage length {:.0} m)",
        r.route_total_m,
        r.start_s,
        r.finish_s,
        r.finish_s - r.start_s,
        r.ir_length_m
    );
    let _ = writeln!(o);
    let _ = writeln!(
        o,
        "Progress: max {:.0} / {:.0} m ({:.1}%), finish {}",
        r.max_s,
        r.route_total_m,
        r.progress_pct,
        if r.reached_finish {
            "reached"
        } else {
            "NOT reached"
        }
    );
    let _ = writeln!(
        o,
        "Lateral offset (m, + = right): p5 {:.2}, median {:.2}, p95 {:.2}, max |{:.2}|; {:.1}% within the road edges",
        r.lateral_p05, r.lateral_p50, r.lateral_p95, r.max_abs_lateral, r.on_road_pct
    );
    let hist: Vec<String> = r
        .edge_ratio_histogram
        .iter()
        .map(|(l, f)| format!("{l}: {:.1}%", f * 100.0))
        .collect();
    let _ = writeln!(o, "  |lateral| / half-width: {}", hist.join(", "));
    if let Some(h) = r.height_above_ground_p50 {
        let _ = writeln!(o, "  car position median height above DR2 ground: {h:.2} m");
    }
    if let Some(c) = r.contact_on_dr2_ground_pct {
        let _ = writeln!(o, "  wheel contact points on DR2 ground: {c:.1}%");
    }
    let _ = writeln!(
        o,
        "Off-route segments (beyond edge + margin): {}",
        r.off_route.len()
    );
    for s in r.off_route.iter().take(20) {
        let _ = writeln!(
            o,
            "  t {:.1}-{:.1}s, s {:.0}-{:.0} m, max {:.1} m to the {}",
            s.t_start,
            s.t_end,
            s.s_start,
            s.s_end,
            s.max_lateral.abs(),
            s.side
        );
    }
    let _ = writeln!(o, "Fall-through / drop events: {}", r.falls.len());
    for e in r.falls.iter().take(20) {
        let _ = writeln!(
            o,
            "  [{}] t {:.1}-{:.1}s at s {:.0} m, lateral {:.1} m: {}",
            e.kind, e.t_start, e.t_end, e.s, e.lateral, e.detail
        );
    }
    let _ = writeln!(
        o,
        "Teleports (resets / rewinds / respawns): {}",
        r.teleports.len()
    );
    for e in r.teleports.iter().take(20) {
        let _ = writeln!(
            o,
            "  t {:.1}s -> s {:.0} m, lateral {:.1} m: {}",
            e.t_start, e.s, e.lateral, e.detail
        );
    }
    let t = &r.timing;
    let _ = writeln!(o, "Timing (shm fields published: {}):", t.fields_published);
    let _ = writeln!(
        o,
        "  started: {}{}; finished: {}{}{}",
        t.started,
        t.started_s
            .map(|s| format!(" (car at s {s:.0} m; start line {:.0} m)", r.start_s))
            .unwrap_or_default(),
        t.finished,
        t.finished_s
            .map(|s| format!(" (car at s {s:.0} m; finish line {:.0} m)", r.finish_s))
            .unwrap_or_default(),
        t.shm_stage_time_s
            .map(|v| format!(", shm time {}", fmt_time(v)))
            .unwrap_or_default()
    );
    for e in &t.evidence {
        let _ = writeln!(o, "  - {e}");
    }
    let _ = writeln!(
        o,
        "  completedLaps max {}, normalizedCarPosition max {:.3}, distanceTraveled max {:.0} m, sector index sequence {:?}",
        t.max_completed_laps, t.max_normalized_pos, t.max_distance_traveled, t.sector_sequence
    );
    let _ = writeln!(
        o,
        "  by position: start {} finish {}{}",
        t.pos_start_t.map_or("-".into(), |v| format!("t={v:.1}s")),
        t.pos_finish_t.map_or("-".into(), |v| format!("t={v:.1}s")),
        t.pos_stage_time_s
            .map(|v| format!(", stage time {}", fmt_time(v)))
            .unwrap_or_default()
    );
    for (name, v) in &t.pos_splits {
        let _ = writeln!(o, "    {name}: {}", fmt_time(*v));
    }
    let _ = writeln!(
        o,
        "Speed: max {:.0} km/h, mean while moving {:.0} km/h",
        r.max_speed_kmh, r.mean_moving_speed_kmh
    );
    for b in &r.speed_bins {
        if b.samples == 0 {
            let _ = writeln!(o, "  {:5.0}-{:5.0} m: -", b.s_from, b.s_to);
        } else {
            let bar = "#".repeat((b.mean_kmh / 5.0).round().clamp(0.0, 40.0) as usize);
            let _ = writeln!(
                o,
                "  {:5.0}-{:5.0} m: mean {:5.1} min {:5.1} max {:5.1} km/h {bar}",
                b.s_from, b.s_to, b.mean_kmh, b.min_kmh, b.max_kmh
            );
        }
    }
    if !r.always_zero_columns.is_empty() {
        let _ = writeln!(
            o,
            "Columns always 0 in this run (not published by ACR, or unused): {}",
            r.always_zero_columns.join(", ")
        );
    }
    let _ = writeln!(o);
    for c in &r.checks {
        let _ = writeln!(o, "{:7} {:28} {}", c.verdict.to_string(), c.name, c.detail);
    }
    let _ = writeln!(o, "OVERALL: {}", r.verdict);
    o
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::route::tests::synthetic_ir;
    use crate::run::{read_run_from, RunWriter};
    use crate::synth::{synthesize, SynthOpts};
    use crate::transform::tests::NZ01R5;

    fn route() -> Route {
        Route::new(&synthetic_ir(), &NZ01R5)
    }

    /// Ground strip 20 m beyond each road edge, at the edge heights.
    fn ground(r: &Route) -> Ground {
        let mut verts = Vec::new();
        for i in 0..r.centre.len() {
            let (l, rr, c) = (r.left[i], r.right[i], r.centre[i]);
            let out = |e: [f64; 3]| {
                let (dx, dz) = (e[0] - c[0], e[2] - c[2]);
                let k = 20.0 / (dx * dx + dz * dz).sqrt();
                [(e[0] + dx * k) as f32, e[1] as f32, (e[2] + dz * k) as f32]
            };
            verts.push(out(l));
            verts.push([l[0] as f32, l[1] as f32, l[2] as f32]);
            verts.push([c[0] as f32, c[1] as f32, c[2] as f32]);
            verts.push([rr[0] as f32, rr[1] as f32, rr[2] as f32]);
            verts.push(out(rr));
        }
        let mut tris = Vec::new();
        for i in 0..r.centre.len() as u32 - 1 {
            for k in 0..4 {
                let (a, b, c, d) = (
                    i * 5 + k,
                    i * 5 + k + 1,
                    (i + 1) * 5 + k,
                    (i + 1) * 5 + k + 1,
                );
                tris.push([a, b, d]);
                tris.push([a, d, c]);
            }
        }
        Ground::from_mesh(verts, tris)
    }

    fn verdict(r: &Report, name: &str) -> Verdict {
        r.checks
            .iter()
            .find(|c| c.name == name)
            .map(|c| c.verdict)
            .unwrap()
    }

    #[test]
    fn clean_run_passes() {
        let rt = route();
        let g = ground(&rt);
        let samples = synthesize(&rt, &SynthOpts::default());
        // through the CSV writer/reader, as a real run would be
        let mut buf = Vec::new();
        let mut w = RunWriter::new(&mut buf, &[]).unwrap();
        for s in &samples {
            w.write(s).unwrap();
        }
        w.flush().unwrap();
        let samples = read_run_from(std::io::Cursor::new(buf)).unwrap().samples;

        let r = analyze(&rt, Some(&g), &samples, &CheckOpts::default());
        let text = render_text(&r);
        assert_eq!(r.verdict, Verdict::Pass, "{text}");
        assert!(r.reached_finish);
        assert!(r.progress_pct > 99.0, "{}", r.progress_pct);
        assert!(
            r.falls.is_empty() && r.teleports.is_empty() && r.off_route.is_empty(),
            "{text}"
        );
        assert!(
            (r.lateral_p95 - 1.5).abs() < 0.2 && (r.lateral_p05 + 1.5).abs() < 0.2,
            "{text}"
        );
        assert!(r.on_road_pct > 99.0);
        let h = r.height_above_ground_p50.unwrap();
        assert!((h - 0.4).abs() < 0.1, "height above ground {h}");
        assert!(r.timing.started && r.timing.finished);
        let (shm, pos) = (
            r.timing.shm_stage_time_s.unwrap(),
            r.timing.pos_stage_time_s.unwrap(),
        );
        assert!((shm - pos).abs() < 0.1, "shm {shm} vs position {pos}");
        assert!((r.timing.started_s.unwrap() - rt.start_s()).abs() < 5.0);
        assert!((r.timing.finished_s.unwrap() - rt.finish_s()).abs() < 5.0);
        assert_eq!(r.timing.pos_splits.len(), 1);
        // the last bin includes the car parked after the run-out
        let bins = &r.speed_bins[..r.speed_bins.len() - 1];
        assert!(
            bins.iter().all(|b| b.samples > 0 && b.mean_kmh > 50.0),
            "{:?}",
            r.speed_bins
        );

        let svg = crate::svg::render(&rt, &r);
        assert!(
            svg.starts_with("<svg")
                && svg.contains("<polyline")
                && svg.trim_end().ends_with("</svg>")
        );
        // every '<' must start a tag (text content is escaped)
        for (i, _) in svg.match_indices('<') {
            let next = svg[i + 1..].chars().next().unwrap();
            assert!(
                next.is_ascii_alphabetic() || next == '/',
                "unescaped '<' at {i}: {}",
                &svg[i..(i + 40).min(svg.len())]
            );
        }
    }

    #[test]
    fn fall_through_fails() {
        let rt = route();
        let g = ground(&rt);
        let samples = synthesize(
            &rt,
            &SynthOpts {
                fall_at_s: Some(250.0),
                ..Default::default()
            },
        );
        let r = analyze(&rt, Some(&g), &samples, &CheckOpts::default());
        assert_eq!(r.verdict, Verdict::Fail);
        assert_eq!(verdict(&r, "fall-through"), Verdict::Fail);
        assert_eq!(verdict(&r, "progress"), Verdict::Fail);
        let e = &r.falls[0];
        assert!((e.s - 250.0).abs() < 10.0, "{e:?}");
        for k in ["below-road", "below-ground", "below-floor", "drop"] {
            assert!(
                r.falls.iter().any(|e| e.kind.contains(k)),
                "missing {k}: {:?}",
                r.falls
            );
        }
        assert!(
            r.max_s < 260.0,
            "falling samples must not count as progress: {}",
            r.max_s
        );
    }

    #[test]
    fn off_route_warns() {
        let rt = route();
        let samples = synthesize(
            &rt,
            &SynthOpts {
                weave_m: 10.0,
                weave_len_m: 400.0,
                ..Default::default()
            },
        );
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert_eq!(
            verdict(&r, "off-route"),
            Verdict::Warn,
            "{}",
            render_text(&r)
        );
        assert!(
            r.off_route.iter().any(|s| s.side == "right")
                && r.off_route.iter().any(|s| s.side == "left")
        );
        assert!(r.reached_finish);
        assert_eq!(r.verdict, Verdict::Warn);
    }

    #[test]
    fn rewind_is_a_teleport_not_a_fall() {
        let rt = route();
        let base = synthesize(&rt, &SynthOpts::default());
        let k = base.len() / 2;
        let back = k - 300; // 6 s earlier
        let mut samples: Vec<Sample> = base[..k].to_vec();
        let dt = base[1].t_s - base[0].t_s;
        let t_k = base[k].t_s;
        for (n, s) in base[back..].iter().enumerate() {
            samples.push(Sample {
                t_s: t_k + n as f64 * dt,
                ..s.clone()
            });
        }
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert_eq!(r.teleports.len(), 1, "{}", render_text(&r));
        assert!(r.falls.is_empty());
        assert!(r.reached_finish);
        assert_eq!(r.verdict, Verdict::Pass);
    }

    #[test]
    fn missing_timing_is_unknown() {
        let rt = route();
        let samples = synthesize(
            &rt,
            &SynthOpts {
                timing: false,
                ..Default::default()
            },
        );
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert_eq!(verdict(&r, "timing"), Verdict::Unknown);
        assert!(!r.timing.fields_published);
        assert!(r.timing.pos_stage_time_s.is_some());
        assert_eq!(r.verdict, Verdict::Warn);
    }

    #[test]
    fn timing_without_finish_after_crossing_fails() {
        let rt = route();
        let mut samples = synthesize(&rt, &SynthOpts::default());
        for s in &mut samples {
            s.completed_laps = 0;
            s.i_last_ms = 0;
            s.i_current_ms = (s.t_s * 1000.0) as i32; // clock never stops
        }
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert!(r.timing.started && !r.timing.finished);
        assert_eq!(verdict(&r, "timing"), Verdict::Fail);
    }

    #[test]
    fn short_run_reports_progress() {
        let rt = route();
        let samples = synthesize(
            &rt,
            &SynthOpts {
                stop_at_s: Some(300.0),
                ..Default::default()
            },
        );
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert!(!r.reached_finish);
        assert!((r.max_s - 300.0).abs() < 3.0, "{}", r.max_s);
        assert_eq!(verdict(&r, "progress"), Verdict::Fail);
        assert!(r.timing.started && !r.timing.finished);
    }

    #[test]
    fn physics_page_all_zero_uses_derived_speed() {
        let rt = route();
        let mut samples = synthesize(&rt, &SynthOpts::default());
        for s in &mut samples {
            let keep = (
                s.t_s,
                s.pos_src,
                s.pos_x,
                s.pos_y,
                s.pos_z,
                s.i_current_ms,
                s.completed_laps,
                s.i_last_ms,
            );
            *s = Sample::default();
            (
                s.t_s,
                s.pos_src,
                s.pos_x,
                s.pos_y,
                s.pos_z,
                s.i_current_ms,
                s.completed_laps,
                s.i_last_ms,
            ) = keep;
        }
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert_eq!(r.samples_phys_zero, samples.len());
        assert!(
            r.max_speed_kmh > 80.0 && r.max_speed_kmh < 100.0,
            "{}",
            r.max_speed_kmh
        );
        assert!(r.teleports.is_empty() && r.falls.is_empty());
        assert!(r.reached_finish);
    }

    /// The real first port, when the IR and report exist locally (work/ is not committed).
    #[test]
    fn real_stage_synthetic_run() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let (stage, report) = (
            root.join("work/ir/new_zealand_rally_01_route_5/stage.json"),
            root.join("work/acr/stage-nz01r5.report.json"),
        );
        if !stage.is_file() || !report.is_file() {
            eprintln!("skipped: {} not found", stage.display());
            return;
        }
        let pl = crate::transform::Placement::from_report(&report).unwrap();
        assert_eq!(pl, NZ01R5);
        let rt = Route::new(&crate::route::Ir::load(&stage).unwrap(), &pl);
        // the converter logged the centre polyline as 4842.776 m (`dr2_centre_length_m`) and the sectors at s 35.6 / 4715.6 m
        assert!((rt.total() - 4842.776).abs() < 0.01, "{}", rt.total());
        assert!((rt.start_s() - 35.6).abs() < 0.2 && (rt.finish_s() - 4715.6).abs() < 0.2);
        let samples = synthesize(
            &rt,
            &SynthOpts {
                speed_kmh: 110.0,
                ..Default::default()
            },
        );
        let r = analyze(&rt, None, &samples, &CheckOpts::default());
        assert_eq!(r.verdict, Verdict::Pass, "{}", render_text(&r));
    }
}
