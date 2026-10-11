//! Synthetic runs that follow the transformed centreline, for tests and for trying `check` without the game.

use crate::route::Route;
use crate::run::Sample;

#[derive(Clone, Debug)]
pub struct SynthOpts {
    pub hz: f64,
    /// Cruise speed (km/h); the synthetic car slows to 60% in the tighter half of the bins.
    pub speed_kmh: f64,
    /// Lateral weave amplitude (m) and wavelength (m along the route).
    pub weave_m: f64,
    pub weave_len_m: f64,
    /// Height of the car position above the road surface (m).
    pub ride_m: f64,
    /// Stop driving at this distance (None = run-out end).
    pub stop_at_s: Option<f64>,
    /// Fall through the ground at this distance: drop under gravity to 60 m below the DR2 floor.
    pub fall_at_s: Option<f64>,
    /// Publish shm timing fields like a working stage would.
    pub timing: bool,
}

impl Default for SynthOpts {
    fn default() -> Self {
        Self {
            hz: 50.0,
            speed_kmh: 90.0,
            weave_m: 1.5,
            weave_len_m: 120.0,
            ride_m: 0.4,
            stop_at_s: None,
            fall_at_s: None,
            timing: true,
        }
    }
}

pub fn synthesize(route: &Route, o: &SynthOpts) -> Vec<Sample> {
    let dt = 1.0 / o.hz;
    let end = o
        .stop_at_s
        .unwrap_or(route.total() - 1.0)
        .min(route.total() - 1.0);
    let (start_s, finish_s) = (route.start_s(), route.finish_s());
    let mut out = Vec::new();
    let mut s = 0.0;
    let mut t = 0.0;
    let mut packet = 1;
    let mut clock_start: Option<f64> = None;
    let mut finished: Option<(f64, i32)> = None;
    let mut prev = route.at(0.0, 0.0);
    let mut fall: Option<(f64, [f64; 3])> = None;
    let floor = route
        .floor_y
        .unwrap_or(route.centre.iter().map(|p| p[1]).fold(f64::MAX, f64::min))
        - 60.0;
    loop {
        let v_kmh = if ((s / 500.0) as i64) % 2 == 1 {
            o.speed_kmh * 0.6
        } else {
            o.speed_kmh
        };
        let lat = o.weave_m * (s / o.weave_len_m * std::f64::consts::TAU).sin();
        let mut p = route.at(s, lat);
        p[1] += o.ride_m;
        if let Some(fs) = o.fall_at_s {
            if s >= fs && fall.is_none() {
                fall = Some((t, p));
            }
        }
        let mut vy = (p[1] - prev[1]) / dt;
        let mut speed = v_kmh;
        if let Some((t0, p0)) = fall {
            let ft = t - t0;
            p = [p0[0], (p0[1] - 0.5 * 9.81 * ft * ft).max(floor), p0[2]];
            vy = if p[1] > floor { -9.81 * ft } else { 0.0 };
            speed = (vy.abs() * 3.6).min(300.0);
        }
        if s >= start_s && clock_start.is_none() {
            clock_start = Some(t);
        }
        if s >= finish_s && finished.is_none() && fall.is_none() {
            finished = Some((t, ((t - clock_start.unwrap_or(0.0)) * 1000.0) as i32));
        }
        let clock_ms = match (clock_start, finished) {
            (_, Some((_, ms))) => ms,
            (Some(c), None) => ((t - c) * 1000.0) as i32,
            _ => 0,
        };
        let h = (p[0] - prev[0], p[2] - prev[2]);
        let hlen = (h.0 * h.0 + h.1 * h.1).sqrt().max(1e-9);
        let vh = if fall.is_some() { 0.0 } else { v_kmh / 3.6 };
        out.push(Sample {
            t_s: t,
            phys_packet: packet,
            gfx_packet: packet,
            pos_src: 2,
            pos_x: p[0] as f32,
            pos_y: p[1] as f32,
            pos_z: p[2] as f32,
            vel_x: (vh * h.0 / hlen) as f32,
            vel_y: vy as f32,
            vel_z: (vh * h.1 / hlen) as f32,
            speed_kmh: speed as f32,
            gear: 3,
            rpm: 5000,
            gas: 0.7,
            load_fl: 3000.0,
            load_fr: 3000.0,
            load_rl: 3000.0,
            load_rr: 3000.0,
            i_current_ms: if o.timing { clock_ms } else { 0 },
            completed_laps: if o.timing && finished.is_some() { 1 } else { 0 },
            i_last_ms: if o.timing {
                finished.map_or(0, |f| f.1)
            } else {
                0
            },
            normalized_pos: if o.timing {
                (s / route.total()) as f32
            } else {
                0.0
            },
            ..Default::default()
        });
        prev = p;
        packet += 1;
        t += dt;
        if fall.is_some_and(|(t0, _)| t - t0 > 6.0) {
            break;
        }
        if fall.is_none() {
            s += v_kmh / 3.6 * dt;
            if s >= end {
                break;
            }
        }
    }
    // parked after the end for a few seconds (clock frozen)
    if fall.is_none() {
        let last = out.last().cloned().unwrap_or_default();
        for k in 1..=(4.0 * o.hz) as i32 {
            out.push(Sample {
                t_s: last.t_s + k as f64 * dt,
                phys_packet: last.phys_packet + k,
                gfx_packet: last.gfx_packet + k,
                speed_kmh: 0.0,
                vel_x: 0.0,
                vel_y: 0.0,
                vel_z: 0.0,
                ..last.clone()
            });
        }
    }
    out
}
