//! `.usmap` reader and writer, matching CUE4Parse's `UsmapParser` / `UsmapProperties`.
//!
//! File: `u16 magic 0x30C4, u8 version, [versioning], u8 compression, u32 compSize, u32 decompSize, data`.
//! Data: name table, enums, structs. Names are referenced by `i32` index (`-1` = none). We write
//! version 4 (`ExplicitEnumValues`, the version Dumper-7 writes) without package versioning,
//! Zstandard-compressed.

use crate::schema::{Enum, Kind, PropType, Property, Schema, Struct};
use anyhow::{bail, ensure, Context, Result};
use std::collections::HashMap;

pub const MAGIC: u16 = 0x30C4;

/// `EUsmapVersion`.
pub mod version {
    pub const PACKAGE_VERSIONING: u8 = 1;
    pub const LONG_FNAME: u8 = 2;
    pub const LARGE_ENUMS: u8 = 3;
    pub const EXPLICIT_ENUM_VALUES: u8 = 4;
    pub const ENGINE_VERSIONING: u8 = 5;
}

/// The version this crate writes.
pub const WRITE_VERSION: u8 = version::EXPLICIT_ENUM_VALUES;

/// `EUsmapCompressionMethod`: 0 none, 1 Oodle, 2 Brotli, 3 Zstandard.
const COMPRESSION_NONE: u8 = 0;
const COMPRESSION_ZSTD: u8 = 3;

#[derive(Clone, Debug)]
pub struct Header {
    pub version: u8,
    /// Package/engine versioning block present (we never write one; CUE4Parse ignores it for mappings).
    #[allow(dead_code)]
    pub has_versioning: bool,
    pub compression: u8,
    pub compressed_size: u32,
    pub decompressed_size: u32,
}

// ---- writing ---------------------------------------------------------------------------

#[derive(Default)]
struct Names {
    index: HashMap<String, i32>,
    list: Vec<String>,
}

impl Names {
    fn id(&mut self, s: &str) -> i32 {
        if let Some(&i) = self.index.get(s) {
            return i;
        }
        let i = self.list.len() as i32;
        self.index.insert(s.to_string(), i);
        self.list.push(s.to_string());
        i
    }

    fn opt(&mut self, s: Option<&str>) -> i32 {
        s.map_or(-1, |s| self.id(s))
    }
}

fn write_type(out: &mut Vec<u8>, names: &mut Names, t: &PropType) {
    out.push(t.kind() as u8);
    match t {
        PropType::Simple(_) => {}
        PropType::Enum { inner, name } => {
            write_type(out, names, inner);
            out.extend(names.opt(name.as_deref()).to_le_bytes());
        }
        PropType::Struct(name) => out.extend(names.opt(name.as_deref()).to_le_bytes()),
        PropType::Array(i) | PropType::Set(i) | PropType::Optional(i) => write_type(out, names, i),
        PropType::Map(k, v) => {
            write_type(out, names, k);
            write_type(out, names, v);
        }
    }
}

/// Uncompressed data block (names, enums, structs).
pub fn encode_data(schema: &Schema) -> Result<Vec<u8>> {
    let mut names = Names::default();
    let mut body = Vec::new();
    body.extend((schema.enums.len() as u32).to_le_bytes());
    for e in &schema.enums {
        ensure!(e.entries.len() <= u16::MAX as usize, "enum {} has {} entries", e.name, e.entries.len());
        body.extend(names.id(&e.name).to_le_bytes());
        body.extend((e.entries.len() as u16).to_le_bytes());
        for (value, name) in &e.entries {
            body.extend(value.to_le_bytes());
            body.extend(names.id(name).to_le_bytes());
        }
    }
    body.extend((schema.structs.len() as u32).to_le_bytes());
    for s in &schema.structs {
        let slots = s.slot_count();
        ensure!(slots <= u16::MAX as usize, "struct {} has {slots} property slots", s.name);
        body.extend(names.id(&s.name).to_le_bytes());
        body.extend(names.opt(s.super_name.as_deref()).to_le_bytes());
        body.extend((slots as u16).to_le_bytes());
        body.extend((s.properties.len() as u16).to_le_bytes());
        let mut index = 0u16;
        for p in &s.properties {
            body.extend(index.to_le_bytes());
            body.push(p.array_dim);
            body.extend(names.id(&p.name).to_le_bytes());
            write_type(&mut body, &mut names, &p.ty);
            index += p.array_dim as u16;
        }
    }
    let mut out = Vec::with_capacity(body.len() + names.list.len() * 24);
    out.extend((names.list.len() as u32).to_le_bytes());
    for n in &names.list {
        let b = n.as_bytes();
        ensure!(b.len() <= u16::MAX as usize, "name too long: {n}");
        out.extend((b.len() as u16).to_le_bytes());
        out.extend(b);
    }
    out.extend(body);
    Ok(out)
}

/// A complete `.usmap` file.
pub fn write(schema: &Schema) -> Result<Vec<u8>> {
    let data = encode_data(schema)?;
    let packed = zstd::bulk::compress(&data, 19).context("zstd compression")?;
    let mut out = Vec::with_capacity(packed.len() + 16);
    out.extend(MAGIC.to_le_bytes());
    out.push(WRITE_VERSION);
    out.extend(0i32.to_le_bytes()); // bHasVersioning = false
    out.push(COMPRESSION_ZSTD);
    out.extend((packed.len() as u32).to_le_bytes());
    out.extend((data.len() as u32).to_le_bytes());
    out.extend(packed);
    Ok(out)
}

// ---- reading ---------------------------------------------------------------------------

struct Reader<'a> {
    b: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let end = self.pos.checked_add(n).filter(|&e| e <= self.b.len()).context("usmap truncated")?;
        let s = &self.b[self.pos..end];
        self.pos = end;
        Ok(s)
    }
    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into()?))
    }
    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into()?))
    }
    fn i32(&mut self) -> Result<i32> {
        Ok(i32::from_le_bytes(self.take(4)?.try_into()?))
    }
    fn i64(&mut self) -> Result<i64> {
        Ok(i64::from_le_bytes(self.take(8)?.try_into()?))
    }
    fn name(&mut self, lut: &[String]) -> Result<Option<String>> {
        match self.i32()? {
            -1 => Ok(None),
            i => {
                Ok(Some(lut.get(usize::try_from(i)?).with_context(|| format!("name index {i} out of range"))?.clone()))
            }
        }
    }
    fn fstring(&mut self) -> Result<()> {
        let n = self.i32()?;
        let len = if n < 0 { (-n) as usize * 2 } else { n as usize };
        self.take(len)?;
        Ok(())
    }
}

fn read_type(r: &mut Reader<'_>, lut: &[String]) -> Result<PropType> {
    let raw = r.u8()?;
    let kind = Kind::from_u8(raw).with_context(|| format!("unknown property type {raw:#x}"))?;
    Ok(match kind {
        Kind::Enum => {
            let inner = Box::new(read_type(r, lut)?);
            PropType::Enum { inner, name: r.name(lut)? }
        }
        Kind::Struct => PropType::Struct(r.name(lut)?),
        Kind::Array => PropType::Array(Box::new(read_type(r, lut)?)),
        Kind::Set => PropType::Set(Box::new(read_type(r, lut)?)),
        Kind::Optional => PropType::Optional(Box::new(read_type(r, lut)?)),
        Kind::Map => {
            let k = read_type(r, lut)?;
            PropType::Map(Box::new(k), Box::new(read_type(r, lut)?))
        }
        k => PropType::Simple(k),
    })
}

pub fn read_header(bytes: &[u8]) -> Result<(Header, usize)> {
    let mut r = Reader { b: bytes, pos: 0 };
    ensure!(r.u16()? == MAGIC, "not a usmap (bad magic)");
    let version = r.u8()?;
    ensure!(version <= version::ENGINE_VERSIONING, "usmap version {version} is newer than this reader");
    let has_versioning = version >= version::PACKAGE_VERSIONING && r.i32()? != 0;
    if has_versioning {
        if version >= version::ENGINE_VERSIONING {
            r.take(2 + 2 + 2 + 4)?;
            r.fstring()?;
        }
        r.take(8)?; // FPackageFileVersion
        let n = r.i32()?;
        r.take(usize::try_from(n)? * 20)?; // FCustomVersion: FGuid + i32
        r.take(4)?; // NetCL
    }
    let h = Header {
        version,
        has_versioning,
        compression: r.u8()?,
        compressed_size: r.u32()?,
        decompressed_size: r.u32()?,
    };
    Ok((h, r.pos))
}

pub fn read(bytes: &[u8]) -> Result<(Header, Schema)> {
    let (h, at) = read_header(bytes)?;
    let payload = bytes.get(at..at + h.compressed_size as usize).context("usmap payload truncated")?;
    let data = match h.compression {
        COMPRESSION_NONE => payload.to_vec(),
        COMPRESSION_ZSTD => zstd::bulk::decompress(payload, h.decompressed_size as usize).context("zstd")?,
        c => bail!("usmap compression method {c} not supported by this reader (only none / zstd)"),
    };
    ensure!(data.len() == h.decompressed_size as usize, "decompressed size mismatch");
    let v = h.version;
    let mut r = Reader { b: &data, pos: 0 };
    let n = r.u32()? as usize;
    let mut lut = Vec::with_capacity(n);
    for _ in 0..n {
        let len = if v >= version::LONG_FNAME { r.u16()? as usize } else { r.u8()? as usize };
        lut.push(String::from_utf8_lossy(r.take(len)?).into_owned());
    }
    let mut schema = Schema::default();
    for _ in 0..r.u32()? {
        let name = r.name(&lut)?.context("enum without name")?;
        let count = if v >= version::LARGE_ENUMS { r.u16()? as usize } else { r.u8()? as usize };
        let mut entries = Vec::with_capacity(count);
        for j in 0..count {
            if v >= version::EXPLICIT_ENUM_VALUES {
                let value = r.i64()?;
                entries.push((value, r.name(&lut)?.unwrap_or_default()));
            } else {
                entries.push((j as i64, r.name(&lut)?.unwrap_or_default()));
            }
        }
        schema.enums.push(Enum { name, entries });
    }
    for _ in 0..r.u32()? {
        let name = r.name(&lut)?.context("struct without name")?;
        let super_name = r.name(&lut)?;
        let _slots = r.u16()?;
        let count = r.u16()?;
        let mut properties = Vec::with_capacity(count as usize);
        for _ in 0..count {
            let _index = r.u16()?;
            let array_dim = r.u8()?;
            let pname = r.name(&lut)?.unwrap_or_default();
            properties.push(Property { name: pname, array_dim, ty: read_type(&mut r, &lut)? });
        }
        schema.structs.push(Struct { name, super_name, properties });
    }
    ensure!(r.pos == data.len(), "{} trailing bytes after the struct table", data.len() - r.pos);
    Ok((h, schema))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> Schema {
        Schema {
            enums: vec![Enum {
                name: "ESurface".into(),
                entries: vec![(0, "ESurface::Gravel".into()), (7, "ESurface::Tarmac".into())],
            }],
            structs: vec![
                Struct { name: "TableRowBase".into(), super_name: None, properties: vec![] },
                Struct {
                    name: "DBTrack".into(),
                    super_name: Some("TableRowBase".into()),
                    properties: vec![
                        Property { name: "Name".into(), array_dim: 1, ty: PropType::Simple(Kind::Text) },
                        Property { name: "Lengths".into(), array_dim: 3, ty: PropType::Simple(Kind::Float) },
                        Property {
                            name: "Surface".into(),
                            array_dim: 1,
                            ty: PropType::Enum {
                                inner: Box::new(PropType::Simple(Kind::Byte)),
                                name: Some("ESurface".into()),
                            },
                        },
                        Property {
                            name: "Variants".into(),
                            array_dim: 1,
                            ty: PropType::Array(Box::new(PropType::Struct(Some("DataTableRowHandle".into())))),
                        },
                        Property {
                            name: "Tags".into(),
                            array_dim: 1,
                            ty: PropType::Map(
                                Box::new(PropType::Simple(Kind::Name)),
                                Box::new(PropType::Set(Box::new(PropType::Simple(Kind::Int)))),
                            ),
                        },
                    ],
                },
            ],
        }
    }

    #[test]
    fn round_trip() {
        let s = sample();
        let bytes = write(&s).unwrap();
        assert_eq!(&bytes[..3], &[0xC4, 0x30, WRITE_VERSION]);
        let (h, back) = read(&bytes).unwrap();
        assert_eq!(h.compression, 3);
        assert!(!h.has_versioning);
        assert_eq!(back, s);
        assert_eq!(back.structs[1].slot_count(), 7);
    }

    #[test]
    fn slot_indices_follow_array_dims() {
        let data = encode_data(&sample()).unwrap();
        // DBTrack's property indices are 0, 1, 4, 5, 6 (Lengths takes three slots).
        let mut r = Reader { b: &data, pos: 0 };
        let n = r.u32().unwrap();
        for _ in 0..n {
            let l = r.u16().unwrap() as usize;
            r.take(l).unwrap();
        }
        let enums = r.u32().unwrap();
        for _ in 0..enums {
            r.i32().unwrap();
            let c = r.u16().unwrap();
            r.take(c as usize * 12).unwrap();
        }
        assert_eq!(r.u32().unwrap(), 2);
        r.take(4 + 4 + 2 + 2).unwrap(); // TableRowBase
        r.take(8).unwrap();
        assert_eq!((r.u16().unwrap(), r.u16().unwrap()), (7, 5));
        let mut idx = Vec::new();
        for _ in 0..5 {
            idx.push(r.u16().unwrap());
            r.u8().unwrap();
            r.i32().unwrap();
            skip_type(&mut r);
        }
        assert_eq!(idx, [0, 1, 4, 5, 6]);
    }

    fn skip_type(r: &mut Reader<'_>) {
        match Kind::from_u8(r.u8().unwrap()).unwrap() {
            Kind::Enum => {
                skip_type(r);
                r.i32().unwrap();
            }
            Kind::Struct => {
                r.i32().unwrap();
            }
            Kind::Array | Kind::Set | Kind::Optional => skip_type(r),
            Kind::Map => {
                skip_type(r);
                skip_type(r);
            }
            _ => {}
        }
    }

    #[test]
    fn rejects_garbage() {
        assert!(read(b"\x00\x00\x04").is_err());
        let mut b = write(&sample()).unwrap();
        b.truncate(b.len() - 3);
        assert!(read(&b).is_err());
    }
}
