//! Run CSV: one row per telemetry sample. Lines starting with `#` are comments (metadata). Columns are matched by
//! header name, so readers tolerate added/removed columns (missing ones read as 0).

use anyhow::{bail, Context, Result};
use std::io::{BufRead, Write};
use std::path::Path;

macro_rules! sample {
    ($($(#[doc = $doc:literal])* $name:ident : $ty:ty),* $(,)?) => {
        /// One telemetry sample. Positions/velocities: ACR physics space (metres, Y up). Per-wheel order FL FR RL RR.
        #[derive(Clone, Debug, Default, PartialEq)]
        pub struct Sample { $($(#[doc = $doc])* pub $name: $ty),* }

        pub const COLUMNS: &[&str] = &[$(stringify!($name)),*];

        impl Sample {
            pub fn to_fields(&self) -> Vec<String> {
                vec![$(self.$name.to_string()),*]
            }

            /// `cols[i]` is the field index of header column i (None = unknown column).
            fn from_fields(fields: &[&str], cols: &[Option<usize>]) -> Result<Self> {
                let mut s = Sample::default();
                for (raw, col) in fields.iter().zip(cols) {
                    let Some(col) = col else { continue };
                    let raw = raw.trim();
                    if raw.is_empty() { continue; }
                    let mut k = 0usize;
                    $(
                        if *col == k {
                            s.$name = raw.parse().with_context(|| format!("column {}: {raw:?}", stringify!($name)))?;
                        }
                        k += 1;
                    )*
                    let _ = k;
                }
                Ok(s)
            }

            /// All fields as f64, in [`COLUMNS`] order.
            pub fn values(&self) -> Vec<f64> {
                vec![$(self.$name as f64),*]
            }
        }
    };
}

sample! {
    /// Seconds since the recorder started.
    t_s: f64,
    phys_packet: i32,
    gfx_packet: i32,
    /// graphics.status (reads 0/OFF while live on build 25170642).
    status: i32,
    session: i32,
    /// 0 = no car position, 1 = player entry in carCoordinates, 2 = slot 0 (offline solo).
    pos_src: i32,
    pos_x: f32, pos_y: f32, pos_z: f32,
    vel_x: f32, vel_y: f32, vel_z: f32,
    speed_kmh: f32,
    /// -1 R, 0 N, 1.. forward.
    gear: i32,
    rpm: i32,
    gas: f32, brake: f32, clutch: f32, steer: f32,
    heading: f32, pitch: f32, roll: f32, cg_height: f32,
    acc_g_x: f32, acc_g_y: f32, acc_g_z: f32,
    tyres_out: i32,
    load_fl: f32, load_fr: f32, load_rl: f32, load_rr: f32,
    susp_fl: f32, susp_fr: f32, susp_rl: f32, susp_rr: f32,
    slip_fl: f32, slip_fr: f32, slip_rl: f32, slip_rr: f32,
    slip_ratio_fl: f32, slip_ratio_fr: f32, slip_ratio_rl: f32, slip_ratio_rr: f32,
    slip_angle_fl: f32, slip_angle_fr: f32, slip_angle_rl: f32, slip_angle_rr: f32,
    wheel_rads_fl: f32, wheel_rads_fr: f32, wheel_rads_rl: f32, wheel_rads_rr: f32,
    dirty_fl: f32, dirty_fr: f32, dirty_rl: f32, dirty_rr: f32,
    cp_x_fl: f32, cp_y_fl: f32, cp_z_fl: f32,
    cp_x_fr: f32, cp_y_fr: f32, cp_z_fr: f32,
    cp_x_rl: f32, cp_y_rl: f32, cp_z_rl: f32,
    cp_x_rr: f32, cp_y_rr: f32, cp_z_rr: f32,
    cn_y_fl: f32, cn_y_fr: f32, cn_y_rl: f32, cn_y_rr: f32,
    damage_0: f32, damage_1: f32, damage_2: f32, damage_3: f32, damage_4: f32,
    susp_damage_fl: f32, susp_damage_fr: f32, susp_damage_rl: f32, susp_damage_rr: f32,
    surface_grip: f32,
    i_current_ms: i32,
    i_last_ms: i32,
    i_best_ms: i32,
    completed_laps: i32,
    number_of_laps: i32,
    sector_index: i32,
    last_sector_ms: i32,
    i_split_ms: i32,
    distance_traveled: f32,
    normalized_pos: f32,
    session_time_left: f32,
    penalty_time: f32,
    is_valid_lap: i32,
    flag: i32,
    is_in_pit: i32,
}

impl Sample {
    pub fn pos(&self) -> [f64; 3] {
        [self.pos_x as f64, self.pos_y as f64, self.pos_z as f64]
    }

    pub fn has_pos(&self) -> bool {
        self.pos_src != 0
    }

    /// False when the physics page read all zero (a known ACR quirk).
    pub fn phys_ok(&self) -> bool {
        self.phys_packet != 0 || self.speed_kmh != 0.0 || self.rpm != 0 || self.vel_y != 0.0
    }

    pub fn contact_points(&self) -> [[f64; 3]; 4] {
        let p = |x: f32, y: f32, z: f32| [x as f64, y as f64, z as f64];
        [
            p(self.cp_x_fl, self.cp_y_fl, self.cp_z_fl),
            p(self.cp_x_fr, self.cp_y_fr, self.cp_z_fr),
            p(self.cp_x_rl, self.cp_y_rl, self.cp_z_rl),
            p(self.cp_x_rr, self.cp_y_rr, self.cp_z_rr),
        ]
    }

    pub fn wheel_loads(&self) -> [f32; 4] {
        [self.load_fl, self.load_fr, self.load_rl, self.load_rr]
    }
}

pub struct RunWriter<W: Write> {
    out: W,
}

impl<W: Write> RunWriter<W> {
    pub fn new(mut out: W, comments: &[String]) -> Result<Self> {
        for c in comments {
            writeln!(out, "# {c}")?;
        }
        writeln!(out, "{}", COLUMNS.join(","))?;
        Ok(Self { out })
    }

    pub fn write(&mut self, s: &Sample) -> Result<()> {
        writeln!(self.out, "{}", s.to_fields().join(","))?;
        Ok(())
    }

    pub fn flush(&mut self) -> Result<()> {
        self.out.flush()?;
        Ok(())
    }
}

pub struct Run {
    pub comments: Vec<String>,
    pub samples: Vec<Sample>,
    /// Header columns this tool doesn't know (ignored).
    pub unknown_columns: Vec<String>,
    /// Known columns absent from the file (read as 0).
    pub missing_columns: Vec<String>,
}

pub fn read_run(path: &Path) -> Result<Run> {
    let f = std::fs::File::open(path).with_context(|| format!("opening {}", path.display()))?;
    read_run_from(std::io::BufReader::new(f)).with_context(|| path.display().to_string())
}

pub fn read_run_from(r: impl BufRead) -> Result<Run> {
    let mut comments = Vec::new();
    let mut cols: Option<Vec<Option<usize>>> = None;
    let mut run = Run {
        comments: Vec::new(),
        samples: Vec::new(),
        unknown_columns: Vec::new(),
        missing_columns: Vec::new(),
    };
    for (n, line) in r.lines().enumerate() {
        let line = line?;
        let t = line.trim();
        if t.is_empty() {
            continue;
        }
        if let Some(c) = t.strip_prefix('#') {
            comments.push(c.trim().to_owned());
            continue;
        }
        let fields: Vec<&str> = t.split(',').collect();
        match &cols {
            None => {
                let map: Vec<Option<usize>> = fields
                    .iter()
                    .map(|h| COLUMNS.iter().position(|c| c == &h.trim()))
                    .collect();
                run.unknown_columns = fields
                    .iter()
                    .zip(&map)
                    .filter(|(_, m)| m.is_none())
                    .map(|(h, _)| h.trim().to_owned())
                    .collect();
                run.missing_columns = COLUMNS
                    .iter()
                    .enumerate()
                    .filter(|(i, _)| !map.contains(&Some(*i)))
                    .map(|(_, c)| c.to_string())
                    .collect();
                if !map.contains(&Some(0)) {
                    bail!("header has no t_s column");
                }
                cols = Some(map);
            }
            Some(map) => run.samples.push(
                Sample::from_fields(&fields, map).with_context(|| format!("line {}", n + 1))?,
            ),
        }
    }
    run.comments = comments;
    Ok(run)
}

/// Columns that were zero (or never changed) in every sample: a hint which shm fields ACR doesn't publish.
pub struct FieldStats {
    pub always_zero: Vec<&'static str>,
    pub constant_nonzero: Vec<(&'static str, f64)>,
}

pub fn field_stats(samples: &[Sample]) -> FieldStats {
    let mut first: Option<Vec<f64>> = None;
    let mut nonzero = vec![false; COLUMNS.len()];
    let mut varies = vec![false; COLUMNS.len()];
    for s in samples {
        let v = s.values();
        for (i, x) in v.iter().enumerate() {
            nonzero[i] |= *x != 0.0;
        }
        match &first {
            None => first = Some(v),
            Some(f) => {
                for (i, x) in v.iter().enumerate() {
                    varies[i] |= *x != f[i];
                }
            }
        }
    }
    let first = first.unwrap_or_default();
    let skip = |c: &str| c == "t_s";
    FieldStats {
        always_zero: COLUMNS
            .iter()
            .enumerate()
            .filter(|(i, c)| !skip(c) && !nonzero[*i])
            .map(|(_, c)| *c)
            .collect(),
        constant_nonzero: COLUMNS
            .iter()
            .enumerate()
            .filter(|(i, c)| !skip(c) && nonzero[*i] && !varies[*i])
            .map(|(i, c)| (*c, first[i]))
            .collect(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn csv_roundtrip_and_tolerant_header() {
        let s = Sample {
            t_s: 1.25,
            pos_src: 2,
            pos_x: -12.5,
            gear: 3,
            cp_y_rr: 380.125,
            i_current_ms: 5000,
            ..Default::default()
        };
        let mut buf = Vec::new();
        let mut w = RunWriter::new(&mut buf, &["meta: test".into()]).unwrap();
        w.write(&s).unwrap();
        w.flush().unwrap();
        let run = read_run_from(std::io::Cursor::new(&buf)).unwrap();
        assert_eq!(run.comments, vec!["meta: test"]);
        assert_eq!(run.samples, vec![s]);
        assert!(run.missing_columns.is_empty());

        let csv = "t_s,extra,pos_x,gear\n0.5,abc,1.5,2\n";
        let run = read_run_from(std::io::Cursor::new(csv)).unwrap();
        assert_eq!(run.unknown_columns, vec!["extra"]);
        assert!(run.missing_columns.contains(&"pos_y".to_string()));
        assert_eq!(run.samples[0].pos_x, 1.5);
        assert_eq!(run.samples[0].gear, 2);
    }

    #[test]
    fn stats() {
        let a = Sample {
            t_s: 0.0,
            rpm: 1000,
            gear: 2,
            ..Default::default()
        };
        let b = Sample {
            t_s: 0.02,
            rpm: 1100,
            gear: 2,
            ..Default::default()
        };
        let st = field_stats(&[a, b]);
        assert!(st.always_zero.contains(&"surface_grip"));
        assert!(!st.always_zero.contains(&"rpm"));
        assert_eq!(st.constant_nonzero, vec![("gear", 2.0)]);
    }
}
