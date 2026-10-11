//! DR2 -> ACR physics placement, exactly as `acr-ref stage-build` applies it
//! (`StageBuild.Placement.ToPhysics` in tools/acr-ref/StageBuild.cs):
//!
//! ```text
//! dx = x - cx, dz = z - cz
//! physics = (cos θ·dx + sin θ·dz + tx,  y + dy,  -sin θ·dx + cos θ·dz + tz)
//! UE cm   = (px, pz, py) · 100
//! ```
//!
//! Both frames are metres, Y up, right-handed, so this is a rotation about Y plus a translation.

use anyhow::{Context, Result};
use serde::Deserialize;
use std::path::Path;

pub type V3 = [f64; 3];

#[derive(Clone, Copy, Debug, PartialEq, Deserialize)]
pub struct Placement {
    pub theta_deg: f64,
    pub cx: f64,
    pub cz: f64,
    pub tx: f64,
    pub dy: f64,
    pub tz: f64,
}

#[derive(Deserialize)]
struct Report {
    placement: Placement,
}

impl Placement {
    /// Reads `placement` from a `stage-build` report (e.g. `work\acr\stage-nz01r5.report.json`).
    pub fn from_report(path: &Path) -> Result<Self> {
        let text =
            std::fs::read_to_string(path).with_context(|| format!("reading {}", path.display()))?;
        let r: Report = serde_json::from_str(&text)
            .with_context(|| format!("{}: no usable `placement`", path.display()))?;
        Ok(r.placement)
    }

    pub fn to_physics(self, p: V3) -> V3 {
        let r = self.theta_deg.to_radians();
        let (s, c) = r.sin_cos();
        let (dx, dz) = (p[0] - self.cx, p[2] - self.cz);
        [
            c * dx + s * dz + self.tx,
            p[1] + self.dy,
            -s * dx + c * dz + self.tz,
        ]
    }

    /// Inverse of [`Self::to_physics`].
    #[cfg(test)]
    pub fn to_dr2(self, q: V3) -> V3 {
        let r = self.theta_deg.to_radians();
        let (s, c) = r.sin_cos();
        let (px, pz) = (q[0] - self.tx, q[2] - self.tz);
        [
            c * px - s * pz + self.cx,
            q[1] - self.dy,
            s * px + c * pz + self.cz,
        ]
    }

    #[cfg(test)]
    pub fn to_ue(self, p: V3) -> V3 {
        let q = self.to_physics(p);
        [q[0] * 100.0, q[2] * 100.0, q[1] * 100.0]
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// `work\acr\stage-nz01r5.report.json` placement.
    pub(crate) const NZ01R5: Placement = Placement {
        theta_deg: 78.0,
        cx: -3918.19,
        cz: -3256.5,
        tx: -560.452474226315,
        dy: -118.144,
        tz: 525.7704134147657,
    };

    fn close(a: V3, b: V3, tol: f64) -> bool {
        a.iter().zip(b).all(|(x, y)| (x - y).abs() <= tol)
    }

    #[test]
    fn rotation_convention() {
        let p = Placement {
            theta_deg: 90.0,
            cx: 0.0,
            cz: 0.0,
            tx: 0.0,
            dy: 0.0,
            tz: 0.0,
        };
        // R_y: x' = cos·x + sin·z, z' = -sin·x + cos·z
        assert!(close(
            p.to_physics([1.0, 0.0, 0.0]),
            [0.0, 0.0, -1.0],
            1e-12
        ));
        assert!(close(p.to_physics([0.0, 2.0, 1.0]), [1.0, 2.0, 0.0], 1e-12));
    }

    #[test]
    fn roundtrip_and_isometry() {
        let a = [-4871.62, 582.36, -2006.44];
        let b = [-3025.72, 511.65, -4435.84];
        assert!(close(NZ01R5.to_dr2(NZ01R5.to_physics(a)), a, 1e-9));
        let d = |p: V3, q: V3| {
            ((p[0] - q[0]).powi(2) + (p[1] - q[1]).powi(2) + (p[2] - q[2]).powi(2)).sqrt()
        };
        let (pa, pb) = (NZ01R5.to_physics(a), NZ01R5.to_physics(b));
        assert!((d(a, b) - d(pa, pb)).abs() < 1e-9);
    }

    /// The converter placed RaceSector0 / RaceSector3 on the start / finish gate crossings (DR2 gates 3 and 433)
    /// with the small lateral / up offsets it logged in the report (start: lat -0.2, up 0.22 m; finish: lat 0.28,
    /// up 0.16 m). Our transform of the IR crossings must land within those offsets of the logged UE positions.
    #[test]
    fn matches_converter_output() {
        let start = NZ01R5.to_ue([-4871.62, 582.36, -2006.44]);
        assert!(
            close(start, [46388.0, 171835.0, 46444.0], 30.0),
            "{start:?}"
        );
        let finish = NZ01R5.to_ue([-3025.72, 511.65, -4435.84]);
        assert!(
            close(finish, [-152843.0, -59212.0, 39366.0], 30.0),
            "{finish:?}"
        );
    }

    #[test]
    fn reads_report() {
        let dir =
            std::env::temp_dir().join(format!("stage-validate-report-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let f = dir.join("r.json");
        std::fs::write(
            &f,
            r#"{"stage":"x","placement":{"formula":"...","theta_deg":78,"cx":-3918.19,"cz":-3256.5,
               "tx":-560.452474226315,"dy":-118.144,"tz":525.7704134147657,"slack_m":297.3}}"#,
        )
        .unwrap();
        assert_eq!(Placement::from_report(&f).unwrap(), NZ01R5);
        std::fs::remove_dir_all(dir).ok();
    }
}
