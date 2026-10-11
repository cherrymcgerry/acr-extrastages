//! Stage IR (`stage.json`, docs/stage-ir.md) transformed into ACR physics space, plus route-relative projection.
//!
//! Distances along the route (`s`) are the cumulative 3D length of the transformed centreline polyline, the same
//! measure `acr-ref stage-build` used to place the race sectors (`Polyline.S`). Projection is horizontal (XZ).
//! Lateral offsets are positive to the right of the driving direction (`right = cross(f, +Y)`, as in the IR).

use crate::transform::{Placement, V3};
use anyhow::{bail, Context, Result};
use serde::Deserialize;
use std::path::{Path, PathBuf};

#[derive(Deserialize)]
pub struct IrGate {
    pub crossing: V3,
    pub left: V3,
    pub right: V3,
}

#[derive(Deserialize)]
pub struct IrTiming {
    #[serde(rename = "type")]
    pub kind: String,
    pub gate: usize,
}

#[derive(Deserialize)]
pub struct IrBounds {
    pub min: V3,
}

#[derive(Deserialize)]
pub struct IrCollision {
    pub file: Option<String>,
    pub bounds: IrBounds,
}

/// The subset of `stage.json` this tool needs (unknown keys are ignored).
#[derive(Deserialize)]
pub struct Ir {
    pub id: String,
    pub name: String,
    pub length_m: f64,
    pub centreline: Vec<V3>,
    pub gates: Vec<IrGate>,
    pub timing: Vec<IrTiming>,
    pub collision: Option<IrCollision>,
    #[serde(skip)]
    pub dir: PathBuf,
}

impl Ir {
    pub fn load(path: &Path) -> Result<Self> {
        let text =
            std::fs::read_to_string(path).with_context(|| format!("reading {}", path.display()))?;
        let mut ir: Ir =
            serde_json::from_str(&text).with_context(|| format!("parsing {}", path.display()))?;
        if ir.gates.len() != ir.centreline.len() {
            bail!("{}: expected one gate per centreline point", path.display());
        }
        if ir.centreline.len() < 2 {
            bail!("{}: centreline has fewer than 2 points", path.display());
        }
        ir.dir = path.parent().map(Path::to_path_buf).unwrap_or_default();
        Ok(ir)
    }

    /// The IR's physics-ground glb, if the file exists next to stage.json.
    pub fn collision_glb(&self) -> Option<PathBuf> {
        let f = self.dir.join(self.collision.as_ref()?.file.as_deref()?);
        f.is_file().then_some(f)
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct TimingLine {
    pub kind: String,
    pub gate: usize,
    pub s: f64,
}

pub struct Route {
    pub id: String,
    pub name: String,
    /// Competitive length from the IR (finish gate distance - start gate distance).
    pub length_m: f64,
    pub centre: Vec<V3>,
    pub left: Vec<V3>,
    pub right: Vec<V3>,
    /// Cumulative 3D length at each centreline point.
    pub s: Vec<f64>,
    /// Horizontal distance from the crossing to the left / right road edge at each gate.
    pub half_left: Vec<f64>,
    pub half_right: Vec<f64>,
    pub timing: Vec<TimingLine>,
    /// Physics y of the lowest DR2 collision vertex (everything below is not DR2 ground).
    pub floor_y: Option<f64>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Projection {
    /// Distance along the route of the nearest centreline point.
    pub s: f64,
    /// Signed horizontal offset, positive to the right of the driving direction.
    pub lateral: f64,
    /// Road half-width on the side of the offset.
    pub half_width: f64,
    /// Road surface height at the projected point, interpolated across the gate edges (clamped at the edge).
    pub road_y: f64,
    pub segment: usize,
    pub u: f64,
}

impl Projection {
    /// |lateral| / half-width (0 = centreline, 1 = road edge).
    pub fn edge_ratio(&self) -> f64 {
        self.lateral.abs() / self.half_width.max(0.5)
    }
}

fn hdist(a: V3, b: V3) -> f64 {
    ((a[0] - b[0]).powi(2) + (a[2] - b[2]).powi(2)).sqrt()
}

fn dist(a: V3, b: V3) -> f64 {
    ((a[0] - b[0]).powi(2) + (a[1] - b[1]).powi(2) + (a[2] - b[2]).powi(2)).sqrt()
}

fn lerp(a: f64, b: f64, u: f64) -> f64 {
    a + (b - a) * u
}

impl Route {
    pub fn new(ir: &Ir, pl: &Placement) -> Self {
        let centre: Vec<V3> = ir.centreline.iter().map(|&p| pl.to_physics(p)).collect();
        let left: Vec<V3> = ir.gates.iter().map(|g| pl.to_physics(g.left)).collect();
        let right: Vec<V3> = ir.gates.iter().map(|g| pl.to_physics(g.right)).collect();
        let mut s = vec![0.0; centre.len()];
        for i in 1..centre.len() {
            s[i] = s[i - 1] + dist(centre[i - 1], centre[i]);
        }
        let half_left = ir.gates.iter().map(|g| hdist(g.left, g.crossing)).collect();
        let half_right = ir
            .gates
            .iter()
            .map(|g| hdist(g.right, g.crossing))
            .collect();
        let timing = ir
            .timing
            .iter()
            .filter(|t| t.gate < centre.len())
            .map(|t| TimingLine {
                kind: t.kind.clone(),
                gate: t.gate,
                s: s[t.gate],
            })
            .collect();
        let floor_y = ir.collision.as_ref().map(|c| c.bounds.min[1] + pl.dy);
        Route {
            id: ir.id.clone(),
            name: ir.name.clone(),
            length_m: ir.length_m,
            centre,
            left,
            right,
            s,
            half_left,
            half_right,
            timing,
            floor_y,
        }
    }

    pub fn total(&self) -> f64 {
        *self.s.last().unwrap()
    }

    pub fn timing_s(&self, kind: &str) -> Option<f64> {
        self.timing.iter().find(|t| t.kind == kind).map(|t| t.s)
    }

    /// Start, `time` splits and finish (the lines ACR's race sectors were built from).
    pub fn start_s(&self) -> f64 {
        self.timing_s("start").unwrap_or(0.0)
    }

    pub fn finish_s(&self) -> f64 {
        self.timing_s("finish").unwrap_or_else(|| self.total())
    }

    fn project_segment(&self, i: usize, q: V3) -> Option<(f64, Projection)> {
        let (a, b) = (self.centre[i], self.centre[i + 1]);
        let (ex, ez) = (b[0] - a[0], b[2] - a[2]);
        let len2 = ex * ex + ez * ez;
        if len2 < 1e-9 {
            return None;
        }
        let u = (((q[0] - a[0]) * ex + (q[2] - a[2]) * ez) / len2).clamp(0.0, 1.0);
        let (px, pz) = (a[0] + u * ex, a[2] + u * ez);
        let (dx, dz) = (q[0] - px, q[2] - pz);
        let d2 = dx * dx + dz * dz;
        let len = len2.sqrt();
        let lateral = (dx * -ez + dz * ex) / len;
        let (hw, edge_y) = if lateral >= 0.0 {
            (
                lerp(self.half_right[i], self.half_right[i + 1], u),
                lerp(self.right[i][1], self.right[i + 1][1], u),
            )
        } else {
            (
                lerp(self.half_left[i], self.half_left[i + 1], u),
                lerp(self.left[i][1], self.left[i + 1][1], u),
            )
        };
        let centre_y = lerp(a[1], b[1], u);
        let k = if hw > 1e-6 {
            (lateral.abs() / hw).min(1.0)
        } else {
            0.0
        };
        let road_y = lerp(centre_y, edge_y, k);
        let s = lerp(self.s[i], self.s[i + 1], u);
        Some((
            d2,
            Projection {
                s,
                lateral,
                half_width: hw,
                road_y,
                segment: i,
                u,
            },
        ))
    }

    fn project_range(&self, q: V3, lo: usize, hi: usize) -> Option<Projection> {
        let mut best: Option<(f64, Projection)> = None;
        for i in lo..hi.min(self.centre.len() - 1) {
            if let Some((d2, p)) = self.project_segment(i, q) {
                if best.as_ref().is_none_or(|(b, _)| d2 < *b) {
                    best = Some((d2, p));
                }
            }
        }
        best.map(|(_, p)| p)
    }

    /// Nearest point on the whole route (horizontal).
    pub fn project(&self, q: V3) -> Projection {
        self.project_range(q, 0, self.centre.len())
            .expect("route has at least one non-degenerate segment")
    }

    /// Point on the route at distance `s` with a lateral offset (right positive), on the road surface.
    pub fn at(&self, s: f64, lateral: f64) -> V3 {
        let s = s.clamp(0.0, self.total());
        let i = match self.s.binary_search_by(|v| v.total_cmp(&s)) {
            Ok(i) => i,
            Err(i) => i.saturating_sub(1),
        }
        .min(self.centre.len() - 2);
        let (a, b) = (self.centre[i], self.centre[i + 1]);
        let seg = self.s[i + 1] - self.s[i];
        let u = if seg > 0.0 {
            (s - self.s[i]) / seg
        } else {
            0.0
        };
        let (ex, ez) = (b[0] - a[0], b[2] - a[2]);
        let len = (ex * ex + ez * ez).sqrt().max(1e-9);
        let (rx, rz) = (-ez / len, ex / len);
        let p = [
            lerp(a[0], b[0], u) + rx * lateral,
            0.0,
            lerp(a[2], b[2], u) + rz * lateral,
        ];
        let y = self
            .project_segment(i, p)
            .map(|(_, pr)| pr.road_y)
            .unwrap_or(lerp(a[1], b[1], u));
        [p[0], y, p[2]]
    }
}

/// Sequential projector: searches near the previous match so hairpins and parallel road sections don't make the
/// projection jump; falls back to a global search when the car is far from the windowed match or after a reset.
pub struct Tracker<'a> {
    route: &'a Route,
    last: Option<usize>,
}

const WINDOW_BACK_M: f64 = 150.0;
const WINDOW_AHEAD_M: f64 = 300.0;
const WINDOW_MAX_OFFSET_M: f64 = 40.0;

impl<'a> Tracker<'a> {
    pub fn new(route: &'a Route) -> Self {
        Self { route, last: None }
    }

    pub fn reset(&mut self) {
        self.last = None;
    }

    pub fn project(&mut self, q: V3) -> Projection {
        let r = self.route;
        let windowed = self.last.and_then(|i| {
            let s0 = r.s[i];
            let lo =
                r.s.partition_point(|&v| v < s0 - WINDOW_BACK_M)
                    .saturating_sub(1);
            let hi = r.s.partition_point(|&v| v <= s0 + WINDOW_AHEAD_M) + 1;
            r.project_range(q, lo, hi)
                .filter(|p| p.lateral.abs() <= WINDOW_MAX_OFFSET_M)
        });
        let p = windowed.unwrap_or_else(|| r.project(q));
        self.last = Some(p.segment);
        p
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::transform::tests::NZ01R5;

    /// A small synthetic IR in DR2 coordinates: a 600 m S-bend with a hairpin-like return, 10 m wide
    /// (4 m to the left, 6 m to the right), descending, with start / split / finish lines.
    pub(crate) fn synthetic_ir() -> Ir {
        let mut centre = Vec::new();
        let mut gates = Vec::new();
        let n = 61;
        for k in 0..n {
            let t = k as f64 / (n - 1) as f64;
            let ang = t * std::f64::consts::PI * 1.5;
            let (x, z) = (
                -3900.0 + 120.0 * ang.cos(),
                -3250.0 + 120.0 * ang.sin() + 200.0 * t,
            );
            centre.push([x, 560.0 - 20.0 * t, z]);
        }
        for k in 0..n {
            let c = centre[k];
            let (a, b) = (centre[k.saturating_sub(1)], centre[(k + 1).min(n - 1)]);
            let (fx, fz) = (b[0] - a[0], b[2] - a[2]);
            let l = (fx * fx + fz * fz).sqrt();
            let (rx, rz) = (-fz / l, fx / l);
            gates.push(IrGate {
                crossing: c,
                left: [c[0] - rx * 4.0, c[1] + 0.1, c[2] - rz * 4.0],
                right: [c[0] + rx * 6.0, c[1] - 0.2, c[2] + rz * 6.0],
            });
        }
        Ir {
            id: "synthetic".into(),
            name: "Synthetic".into(),
            length_m: 0.0,
            centreline: centre,
            gates,
            timing: vec![
                IrTiming {
                    kind: "start".into(),
                    gate: 3,
                },
                IrTiming {
                    kind: "time".into(),
                    gate: 30,
                },
                IrTiming {
                    kind: "finish".into(),
                    gate: 56,
                },
            ],
            collision: Some(IrCollision {
                file: None,
                bounds: IrBounds {
                    min: [0.0, 530.0, 0.0],
                },
            }),
            dir: PathBuf::new(),
        }
    }

    #[test]
    fn edges_project_to_their_side() {
        let ir = synthetic_ir();
        let r = Route::new(&ir, &NZ01R5);
        for i in 1..r.centre.len() - 1 {
            let pl = r.project(r.left[i]);
            let pr = r.project(r.right[i]);
            assert!((pl.lateral + 4.0).abs() < 0.3, "left {i}: {}", pl.lateral);
            assert!((pr.lateral - 6.0).abs() < 0.3, "right {i}: {}", pr.lateral);
            assert!((pl.road_y - r.left[i][1]).abs() < 0.05);
            assert!((pr.road_y - r.right[i][1]).abs() < 0.05);
        }
    }

    #[test]
    fn at_and_project_roundtrip() {
        let r = Route::new(&synthetic_ir(), &NZ01R5);
        for &(s, lat) in &[(10.0, 0.0), (200.0, 3.0), (333.3, -2.5), (500.0, 12.0)] {
            let p = r.project(r.at(s, lat));
            assert!((p.s - s).abs() < 1.0, "s {s}: {}", p.s);
            assert!((p.lateral - lat).abs() < 0.2, "lat {lat}: {}", p.lateral);
        }
        assert!(r.floor_y.unwrap() - (530.0 - 118.144) < 1e-9);
        assert_eq!(r.start_s(), r.s[3]);
        assert_eq!(r.finish_s(), r.s[56]);
    }

    #[test]
    fn tracker_follows_sequentially() {
        let r = Route::new(&synthetic_ir(), &NZ01R5);
        let mut t = Tracker::new(&r);
        let mut last = -1.0;
        let mut s = 0.0;
        while s < r.total() {
            let p = t.project(r.at(s, 1.0));
            assert!(p.s > last - 1e-6);
            last = p.s;
            s += 5.0;
        }
    }
}
