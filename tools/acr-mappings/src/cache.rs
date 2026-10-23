//! `work/mappings/<buildId>.usmap` plus a `<buildId>.json` sidecar.

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

pub const DEFAULT_DIR: &str = "work/mappings";

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Sidecar {
    pub build_id: String,
    /// Unix seconds.
    pub created: u64,
    pub created_utc: String,
    pub tool: String,
    pub usmap_version: u8,
    pub signatures_verified_build: String,
    pub gobjects: String,
    pub fnamepool: String,
    pub objects: usize,
    pub structs: usize,
    pub enums: usize,
    pub properties: usize,
    #[serde(default)]
    pub unknown_field_classes: Vec<String>,
    /// Set by `validate` when CUE4Parse loaded the reference packages with this file.
    #[serde(default)]
    pub validated: Option<Validation>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Validation {
    pub ok: bool,
    pub at_utc: String,
    pub game: String,
    pub summary: String,
}

pub fn usmap_path(dir: &Path, build_id: &str) -> PathBuf {
    dir.join(format!("{build_id}.usmap"))
}

pub fn sidecar_path(usmap: &Path) -> PathBuf {
    usmap.with_extension("json")
}

pub fn read_sidecar(usmap: &Path) -> Option<Sidecar> {
    serde_json::from_slice(&std::fs::read(sidecar_path(usmap)).ok()?).ok()
}

pub fn write_sidecar(usmap: &Path, s: &Sidecar) -> Result<()> {
    let p = sidecar_path(usmap);
    std::fs::write(&p, serde_json::to_vec_pretty(s)?).with_context(|| format!("writing {}", p.display()))
}

/// Writes through a temporary file so a crash never leaves a half-written usmap in the cache.
pub fn write_atomic(path: &Path, bytes: &[u8]) -> Result<()> {
    if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
        std::fs::create_dir_all(dir).with_context(|| format!("creating {}", dir.display()))?;
    }
    let tmp = path.with_extension("usmap.tmp");
    std::fs::write(&tmp, bytes).with_context(|| format!("writing {}", tmp.display()))?;
    std::fs::rename(&tmp, path).with_context(|| format!("renaming {} -> {}", tmp.display(), path.display()))
}

pub fn now_unix() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

/// `YYYY-MM-DDTHH:MM:SSZ` for Unix seconds.
pub fn utc(secs: u64) -> String {
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    // Civil-from-days (Howard Hinnant), proleptic Gregorian.
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, rem % 3600 / 60, rem % 60)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn utc_format() {
        assert_eq!(utc(0), "1970-01-01T00:00:00Z");
        assert_eq!(utc(951_782_400), "2000-02-29T00:00:00Z");
        assert_eq!(utc(1_792_756_800), "2026-10-23T12:00:00Z");
    }

    #[test]
    fn paths_and_sidecar_round_trip() {
        let dir = std::env::temp_dir().join(format!("acr-mappings-cache-{}", std::process::id()));
        let u = usmap_path(&dir, "25170642");
        assert!(u.ends_with("25170642.usmap"));
        assert!(sidecar_path(&u).ends_with("25170642.json"));
        write_atomic(&u, b"x").unwrap();
        let s = Sidecar { build_id: "25170642".into(), structs: 3, ..Default::default() };
        write_sidecar(&u, &s).unwrap();
        assert_eq!(read_sidecar(&u).unwrap().structs, 3);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
