//! `live`: read-only look at objects in the running game (attach only; never starts or closes it). Finds objects whose
//! name contains one of the given texts and prints their path, class, flags, outer chain and a set of reflected
//! properties, decoded by size (bytes, numbers, pointers to named objects, TArray data/count, FVector doubles). Used to
//! check that edited level content (e.g. moved foliage instances) arrives in the game as built.

use crate::engine::open_ue;
use crate::process::{PageCache, RemoteProcess};
use acr_ue::mem::Memory;
use acr_ue::sigs::Signatures;
use anyhow::{bail, Context, Result};

const DEFAULT_PROPS: &[&str] = &[
    "bVisible",
    "bHiddenInGame",
    "bHidden",
    "RelativeLocation",
    "RelativeRotation",
    "Mobility",
    "AttachParent",
    "RootComponent",
    "StaticMesh",
    "PerInstanceSMData",
    "InstanceCountToRender",
    "NumBuiltInstances",
    "BuiltInstanceBounds",
    "InstanceStartCullDistance",
    "InstanceEndCullDistance",
    "CachedMaxDrawDistance",
    "LDMaxDrawDistance",
    "bIsEditorOnly",
];

pub fn run(sigs: &Signatures, pid: Option<u32>, find: &[String], extra: &[String], near: &[f64], limit: usize) -> Result<()> {
    let Some(pid) = pid else { bail!("{} is not running: start ACR and load the stage first", sigs.module.name) };
    let proc_ = RemoteProcess::open(pid)?;
    let mem = PageCache::new(&proc_, 1 << 16);
    let modules = proc_.modules()?;
    let (ue, _) = open_ue(&mem, &modules, sigs).context("resolving the engine globals")?;
    let objs = ue.all_objects().context("reading GObjects")?;
    let needles: Vec<String> = find.iter().map(|f| f.to_ascii_lowercase()).collect();
    let props: Vec<&str> = DEFAULT_PROPS.iter().copied().chain(extra.iter().map(String::as_str)).collect();
    let mut shown = 0;
    for &obj in &objs {
        let Some(name) = ue.name(obj) else { continue };
        // match on the full path (outer chain included) when a needle contains '.', else on the name
        let lower = if needles.iter().any(|n| n.contains('.')) {
            ue.path_name(obj).unwrap_or_default().to_ascii_lowercase()
        } else {
            name.to_ascii_lowercase()
        };
        if !needles.iter().any(|n| lower.contains(n)) {
            continue;
        }
        shown += 1;
        if shown > limit {
            println!("... more matches (raise --limit)");
            break;
        }
        let class = ue.class_name(obj).unwrap_or_default();
        println!(
            "{} [{}] flags {:#x} at {:#x}",
            ue.path_name(obj).unwrap_or(name.clone()),
            class,
            ue.flags(obj).unwrap_or(0),
            obj
        );
        let Some(cls) = ue.class_of(obj) else { continue };
        for p in &props {
            let Some(info) = ue.find_property(cls, p) else { continue };
            let at = obj + info.offset;
            let mut b = vec![0u8; info.size.min(256)];
            if !mem.read(at, &mut b) {
                println!("    {p} (+{:#x}, {} B): unreadable", info.offset, info.size);
                continue;
            }
            let value = match info.size {
                1 if bool_mask(&ue, cls, p).is_some() => {
                    let (byte_off, mask) = bool_mask(&ue, cls, p).unwrap();
                    let v = mem.read_u8(at + byte_off as usize).unwrap_or(0);
                    format!("bool {} (byte {v:#04x}, mask {mask:#04x})", v & mask != 0)
                }
                1 => format!("byte {:#04x} ({:08b})", b[0], b[0]),
                4 => format!(
                    "i32 {} / f32 {}",
                    i32::from_le_bytes(b[..4].try_into()?),
                    f32::from_le_bytes(b[..4].try_into()?)
                ),
                8 => {
                    let v = u64::from_le_bytes(b[..8].try_into()?) as usize;
                    match ue.name(v) {
                        Some(n) if v > 0x10000 => format!("-> {n} [{}]", ue.class_name(v).unwrap_or_default()),
                        _ => format!("{v:#x} / f64 {}", f64::from_le_bytes(b[..8].try_into()?)),
                    }
                }
                16 => {
                    let data = u64::from_le_bytes(b[..8].try_into()?) as usize;
                    let num = i32::from_le_bytes(b[8..12].try_into()?);
                    let mut s =
                        format!("TArray data {data:#x} num {num} max {}", i32::from_le_bytes(b[12..16].try_into()?));
                    if *p == "Tags" && num > 0 {
                        let names: Vec<String> = (0..num.min(8) as usize).filter_map(|k| ue.fname(data + k * 8)).collect();
                        s += &format!(" {names:?}");
                    }
                    if *p == "PerInstanceSMData" && num > 0 {
                        for k in 0..num.min(3) as usize {
                            let mut m = [0u8; 128];
                            if mem.read(data + k * 128, &mut m) {
                                let d: Vec<f64> =
                                    m.chunks(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
                                s += &format!("\n        [{k}] axes ({:.2} {:.2} {:.2}) ({:.2} {:.2} {:.2}) ({:.2} {:.2} {:.2}) at ({:.0}, {:.0}, {:.0})",
                                              d[0], d[1], d[2], d[4], d[5], d[6], d[8], d[9], d[10], d[12], d[13], d[14]);
                            }
                        }
                    }
                    s
                }
                24 => {
                    let d: Vec<f64> = b.chunks(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
                    format!("({:.1}, {:.1}, {:.1})", d[0], d[1], d[2])
                }
                _ => b.iter().take(64).map(|x| format!("{x:02x}")).collect::<String>(),
            };
            println!("    {p} (+{:#x}, {} B): {value}", info.offset, info.size);
        }
        // --near: offsets (first 4 KiB of the object) holding a double within 1 of a value, e.g. a world X coordinate,
        // to locate unreflected members such as the component-to-world transform
        for v in near {
            let mut raw = vec![0u8; 4096];
            if mem.read(obj, &mut raw) {
                let hits: Vec<String> = (0..raw.len() - 8)
                    .step_by(8)
                    .filter_map(|o| {
                        let d = f64::from_le_bytes(raw[o..o + 8].try_into().unwrap());
                        ((d - v).abs() < 1.0).then(|| {
                            let more: Vec<f64> = (1..3)
                                .filter_map(|k| raw.get(o + k * 8..o + k * 8 + 8).map(|c| f64::from_le_bytes(c.try_into().unwrap())))
                                .collect();
                            format!("+{o:#x} ({d:.1}, {:.1}, {:.1})", more.first().unwrap_or(&0.0), more.get(1).unwrap_or(&0.0))
                        })
                    })
                    .collect();
                println!("    near {v}: {}", if hits.is_empty() { "none".to_string() } else { hits.join("  ") });
            }
        }
        let mut chain = Vec::new();
        let mut cur = ue.outer(obj);
        while let Some(o) = cur {
            if o == 0 || chain.len() > 8 {
                break;
            }
            chain.push(format!("{} [{}]", ue.name(o).unwrap_or_default(), ue.class_name(o).unwrap_or_default()));
            cur = ue.outer(o);
        }
        println!("    outer: {}", chain.join(" < "));
    }
    println!("{shown} object(s) matched {:?} among {} objects", find, objs.len());
    Ok(())
}

/// FBoolProperty's ByteOffset and FieldMask (after sizeof(FProperty) = 0x70: FieldSize, ByteOffset, ByteMask, FieldMask),
/// for a bool property of `class` or one of its supers.
fn bool_mask<M: Memory>(ue: &acr_ue::reflection::Ue<M>, class: usize, name: &str) -> Option<(u8, u8)> {
    let l = ue.layout;
    let mut cur = Some(class);
    let mut depth = 0;
    while let Some(st) = cur {
        let mut field = ue.mem.read_valid_ptr(st + l.ustruct_child_properties);
        let mut n = 0;
        while let Some(f) = field {
            if ue.fname(f + l.ffield_name).as_deref() == Some(name) {
                let cls_name = ue.mem.read_valid_ptr(f + 0x08).and_then(|fc| ue.fname(fc));
                if cls_name.as_deref() != Some("BoolProperty") {
                    return None;
                }
                return Some((ue.mem.read_u8(f + 0x71)?, ue.mem.read_u8(f + 0x73)?));
            }
            field = ue.mem.read_valid_ptr(f + l.ffield_next);
            n += 1;
            if n > 4096 {
                break;
            }
        }
        cur = ue.super_of(st);
        depth += 1;
        if depth > 64 {
            break;
        }
    }
    None
}
