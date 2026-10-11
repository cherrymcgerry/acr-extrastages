//! DR2 physics ground (`collision.glb` of the stage IR) in ACR physics space, for height queries.
//!
//! This is the same mesh `acr-ref stage-build` appended to BakedData (node transforms are not applied; the IR
//! bakes them into the vertices), transformed with the same placement.

use crate::transform::{Placement, V3};
use anyhow::{bail, Context, Result};
use serde_json::Value;
use std::path::Path;

const CELL_M: f64 = 8.0;

pub struct Ground {
    verts: Vec<[f32; 3]>,
    tris: Vec<[u32; 3]>,
    min_x: f64,
    min_z: f64,
    nx: usize,
    nz: usize,
    cells: Vec<Vec<u32>>,
}

fn u32_at(d: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(d[at..at + 4].try_into().unwrap())
}

struct Glb {
    json: Value,
    bin: Vec<u8>,
}

impl Glb {
    fn parse(d: &[u8]) -> Result<Self> {
        if d.len() < 12 || u32_at(d, 0) != 0x4654_6C67 {
            bail!("not a binary glTF file");
        }
        let (mut p, mut json, mut bin) = (12usize, None, Vec::new());
        while p + 8 <= d.len() {
            let len = u32_at(d, p) as usize;
            let kind = u32_at(d, p + 4);
            let chunk = d.get(p + 8..p + 8 + len).context("truncated glb chunk")?;
            match kind {
                0x4E4F_534A => json = Some(serde_json::from_slice(chunk)?),
                0x004E_4942 => bin = chunk.to_vec(),
                _ => {}
            }
            p += 8 + len;
        }
        Ok(Self {
            json: json.context("glb has no JSON chunk")?,
            bin,
        })
    }

    fn view(&self, accessor: usize, elem: usize) -> Result<(usize, usize, usize, u64)> {
        let a = &self.json["accessors"][accessor];
        let v = &self.json["bufferViews"][a["bufferView"]
            .as_u64()
            .context("accessor without bufferView")?
            as usize];
        let off = v["byteOffset"].as_u64().unwrap_or(0) + a["byteOffset"].as_u64().unwrap_or(0);
        let stride = v["byteStride"].as_u64().unwrap_or(elem as u64);
        let count = a["count"].as_u64().context("accessor without count")?;
        Ok((
            off as usize,
            stride as usize,
            count as usize,
            a["componentType"].as_u64().unwrap_or(0),
        ))
    }

    fn positions(&self, accessor: usize) -> Result<Vec<[f32; 3]>> {
        let (off, stride, count, ty) = self.view(accessor, 12)?;
        if ty != 5126 {
            bail!("non-float positions");
        }
        let f = |at: usize| f32::from_le_bytes(self.bin[at..at + 4].try_into().unwrap());
        Ok((0..count)
            .map(|i| off + i * stride)
            .map(|at| [f(at), f(at + 4), f(at + 8)])
            .collect())
    }

    fn indices(&self, accessor: usize) -> Result<Vec<u32>> {
        let ty = self.json["accessors"][accessor]["componentType"]
            .as_u64()
            .unwrap_or(0);
        let size = match ty {
            5121 => 1,
            5123 => 2,
            5125 => 4,
            t => bail!("index type {t}"),
        };
        let (off, stride, count, _) = self.view(accessor, size)?;
        Ok((0..count)
            .map(|i| {
                let at = off + i * stride;
                match size {
                    1 => self.bin[at] as u32,
                    2 => u16::from_le_bytes([self.bin[at], self.bin[at + 1]]) as u32,
                    _ => u32_at(&self.bin, at),
                }
            })
            .collect())
    }
}

impl Ground {
    pub fn load(path: &Path, pl: &Placement) -> Result<Self> {
        let data = std::fs::read(path).with_context(|| format!("reading {}", path.display()))?;
        let glb = Glb::parse(&data).with_context(|| path.display().to_string())?;
        let mut verts = Vec::new();
        let mut tris = Vec::new();
        for mesh in glb.json["meshes"].as_array().context("glb has no meshes")? {
            for prim in mesh["primitives"].as_array().into_iter().flatten() {
                if prim["mode"].as_u64().unwrap_or(4) != 4 {
                    continue;
                }
                let pos = glb.positions(
                    prim["attributes"]["POSITION"]
                        .as_u64()
                        .context("primitive without POSITION")? as usize,
                )?;
                let base = verts.len() as u32;
                let idx = match prim["indices"].as_u64() {
                    Some(a) => glb.indices(a as usize)?,
                    None => (0..pos.len() as u32).collect(),
                };
                verts.extend(pos.iter().map(|p| {
                    let q = pl.to_physics([p[0] as f64, p[1] as f64, p[2] as f64]);
                    [q[0] as f32, q[1] as f32, q[2] as f32]
                }));
                tris.extend(idx.as_chunks::<3>().0.iter().map(|t| t.map(|i| base + i)));
            }
        }
        Ok(Self::from_mesh(verts, tris))
    }

    pub fn from_mesh(verts: Vec<[f32; 3]>, tris: Vec<[u32; 3]>) -> Self {
        let (mut min_x, mut max_x, mut min_z, mut max_z) = (f64::MAX, f64::MIN, f64::MAX, f64::MIN);
        for v in &verts {
            min_x = min_x.min(v[0] as f64);
            max_x = max_x.max(v[0] as f64);
            min_z = min_z.min(v[2] as f64);
            max_z = max_z.max(v[2] as f64);
        }
        if verts.is_empty() {
            (min_x, max_x, min_z, max_z) = (0.0, 0.0, 0.0, 0.0);
        }
        let nx = ((max_x - min_x) / CELL_M).floor() as usize + 1;
        let nz = ((max_z - min_z) / CELL_M).floor() as usize + 1;
        let mut cells = vec![Vec::new(); nx * nz];
        for (ti, t) in tris.iter().enumerate() {
            let vs = t.map(|i| verts[i as usize]);
            let lo_x = vs.iter().map(|v| v[0]).fold(f32::MAX, f32::min) as f64;
            let hi_x = vs.iter().map(|v| v[0]).fold(f32::MIN, f32::max) as f64;
            let lo_z = vs.iter().map(|v| v[2]).fold(f32::MAX, f32::min) as f64;
            let hi_z = vs.iter().map(|v| v[2]).fold(f32::MIN, f32::max) as f64;
            let cx = |x: f64| (((x - min_x) / CELL_M).floor().max(0.0) as usize).min(nx - 1);
            let cz = |z: f64| (((z - min_z) / CELL_M).floor().max(0.0) as usize).min(nz - 1);
            for gz in cz(lo_z)..=cz(hi_z) {
                for gx in cx(lo_x)..=cx(hi_x) {
                    cells[gz * nx + gx].push(ti as u32);
                }
            }
        }
        Self {
            verts,
            tris,
            min_x,
            min_z,
            nx,
            nz,
            cells,
        }
    }

    pub fn triangle_count(&self) -> usize {
        self.tris.len()
    }

    /// Heights of every ground surface at (x, z), unsorted (empty off the DR2 terrain).
    pub fn heights(&self, x: f64, z: f64) -> Vec<f64> {
        let (fx, fz) = ((x - self.min_x) / CELL_M, (z - self.min_z) / CELL_M);
        if fx < 0.0 || fz < 0.0 || fx >= self.nx as f64 || fz >= self.nz as f64 {
            return Vec::new();
        }
        let mut out: Vec<f64> = Vec::new();
        for &ti in &self.cells[fz as usize * self.nx + fx as usize] {
            let [a, b, c] = self.tris[ti as usize].map(|i| self.verts[i as usize]);
            let (ax, az, bx, bz, cx, cz) = (
                a[0] as f64,
                a[2] as f64,
                b[0] as f64,
                b[2] as f64,
                c[0] as f64,
                c[2] as f64,
            );
            let det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if det.abs() < 1e-12 {
                continue;
            }
            let l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / det;
            let l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / det;
            let l3 = 1.0 - l1 - l2;
            const E: f64 = -1e-6;
            if l1 >= E && l2 >= E && l3 >= E {
                let h = l1 * a[1] as f64 + l2 * b[1] as f64 + l3 * c[1] as f64;
                if !out.iter().any(|o| (o - h).abs() < 0.01) {
                    out.push(h);
                }
            }
        }
        out
    }

    /// Classifies a point against the ground below/above it.
    pub fn query(&self, p: V3) -> GroundQuery {
        let hs = self.heights(p[0], p[2]);
        if hs.is_empty() {
            return GroundQuery::default();
        }
        let lowest = hs.iter().copied().fold(f64::MAX, f64::min);
        let below = hs
            .iter()
            .copied()
            .filter(|&h| h <= p[1] + 0.5)
            .fold(f64::MIN, f64::max);
        let nearest = hs
            .iter()
            .copied()
            .min_by(|a, b| (a - p[1]).abs().total_cmp(&(b - p[1]).abs()));
        GroundQuery {
            lowest: Some(lowest),
            below: (below > f64::MIN).then_some(below),
            nearest,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GroundQuery {
    /// Lowest DR2 surface at this XZ (None = off the DR2 terrain).
    pub lowest: Option<f64>,
    /// Highest surface at or below the point (+0.5 m tolerance).
    pub below: Option<f64>,
    /// Surface closest in height to the point.
    pub nearest: Option<f64>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn heights_on_two_layers() {
        // A 20x20 m slab at y 10 sloping to 12, and a 4x4 m bridge deck at y 20 above it.
        let verts = vec![
            [0.0, 10.0, 0.0],
            [20.0, 12.0, 0.0],
            [20.0, 12.0, 20.0],
            [0.0, 10.0, 20.0],
            [8.0, 20.0, 8.0],
            [12.0, 20.0, 8.0],
            [12.0, 20.0, 12.0],
            [8.0, 20.0, 12.0],
        ];
        let tris = vec![[0, 1, 2], [0, 2, 3], [4, 5, 6], [4, 6, 7]];
        let g = Ground::from_mesh(verts, tris);
        let h = g.heights(10.0, 10.0);
        assert_eq!(h.len(), 2);
        assert!(h.iter().any(|&v| (v - 11.0).abs() < 1e-6));
        let q = g.query([10.0, 11.5, 10.0]);
        assert_eq!(q.below, Some(11.0));
        assert_eq!(q.lowest, Some(11.0));
        let q = g.query([10.0, 21.0, 10.0]);
        assert_eq!(q.below, Some(20.0));
        assert!(g.query([30.0, 0.0, 5.0]).lowest.is_none());
        assert_eq!(g.heights(2.0, 2.0).len(), 1);
    }

    #[test]
    fn parses_minimal_glb() {
        let pos: [f32; 9] = [0.0, 1.0, 0.0, 10.0, 1.0, 0.0, 0.0, 1.0, 10.0];
        let idx: [u16; 3] = [0, 1, 2];
        let mut bin: Vec<u8> = pos.iter().flat_map(|f| f.to_le_bytes()).collect();
        bin.extend(idx.iter().flat_map(|i| i.to_le_bytes()));
        bin.extend([0, 0]);
        let json = br#"{"asset":{"version":"2.0"},"buffers":[{"byteLength":44}],
            "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],
            "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                         {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
            "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}"#;
        let mut js = json.to_vec();
        while !js.len().is_multiple_of(4) {
            js.push(b' ');
        }
        let mut d = Vec::new();
        d.extend(0x4654_6C67u32.to_le_bytes());
        d.extend(2u32.to_le_bytes());
        d.extend(((12 + 8 + js.len() + 8 + bin.len()) as u32).to_le_bytes());
        d.extend((js.len() as u32).to_le_bytes());
        d.extend(0x4E4F_534Au32.to_le_bytes());
        d.extend(&js);
        d.extend((bin.len() as u32).to_le_bytes());
        d.extend(0x004E_4942u32.to_le_bytes());
        d.extend(&bin);
        let f = std::env::temp_dir().join(format!("stage-validate-{}.glb", std::process::id()));
        std::fs::write(&f, d).unwrap();
        let id = Placement {
            theta_deg: 0.0,
            cx: 0.0,
            cz: 0.0,
            tx: 0.0,
            dy: 5.0,
            tz: 0.0,
        };
        let g = Ground::load(&f, &id).unwrap();
        std::fs::remove_file(&f).ok();
        assert_eq!(g.triangle_count(), 1);
        assert_eq!(g.heights(2.0, 2.0), vec![6.0]);
    }
}
