//! Walks GObjects and turns every UStruct (classes, script structs, Blueprint classes, user
//! structs; not functions) and every UEnum into a [`Schema`], following Dumper-7's usmap
//! conventions: own properties in `ChildProperties` order, enum entry names without the
//! `EType::` prefix, byte properties with an enum written as `EnumProperty<ByteProperty>`.

use crate::schema::{Enum, Kind, PropType, Property, Schema, Struct};
use acr_ue::mem::Memory;
use acr_ue::reflection::{ObjHeader, Ue, RF_CLASS_DEFAULT_OBJECT};
use anyhow::{Context, Result};
use std::collections::{BTreeMap, HashMap, HashSet};

/// Member offsets beyond `[raw_offsets.uobject]` (UE 5.6, as in Dumper-7's CppSDK `Basic.hpp`).
#[derive(Clone, Copy, Debug)]
pub struct PropLayout {
    /// `FProperty::ArrayDim`.
    pub array_dim: usize,
    /// `FFieldClass::Name` (an FName at the start of the field class).
    pub field_class_name: usize,
    /// First member after `FProperty` (`sizeof(FProperty)`): `FStructProperty::Struct`,
    /// `FByteProperty::Enum`, `FSetProperty::ElementProp`, `FMapProperty::KeyProp`,
    /// `FEnumProperty::UnderlyingProp`, `FOptionalProperty::ValueProperty`.
    pub first: usize,
    /// `FMapProperty::ValueProp`, `FEnumProperty::Enum`.
    pub second: usize,
    /// `FArrayProperty::Inner` (after `EArrayPropertyFlags`).
    pub array_inner: usize,
    /// `UEnum::Names` (`TArray<TPair<FName, int64>>`).
    pub enum_names: usize,
}

impl Default for PropLayout {
    fn default() -> Self {
        Self { array_dim: 0x30, field_class_name: 0x00, first: 0x70, second: 0x78, array_inner: 0x78, enum_names: 0x40 }
    }
}

#[derive(Debug, Default)]
pub struct WalkStats {
    pub objects: usize,
    pub structs: usize,
    pub enums: usize,
    pub duplicate_names: usize,
    /// Field classes we could not map, with occurrence counts (written as `Unknown`).
    pub unknown_field_classes: BTreeMap<String, usize>,
    /// Structs whose property chain broke off (unreadable field).
    pub truncated_structs: Vec<String>,
}

const MAX_PROPS: usize = 8192;
const MAX_DEPTH: usize = 8;
const MAX_ENUM_ENTRIES: usize = 0xFFFF;

enum Meta {
    Struct,
    Enum,
    Other,
}

pub struct Walker<'u, M: Memory> {
    ue: &'u Ue<M>,
    pl: PropLayout,
    field_class_names: HashMap<usize, Option<String>>,
    unknown: BTreeMap<String, usize>,
}

impl<'u, M: Memory> Walker<'u, M> {
    pub fn new(ue: &'u Ue<M>, pl: PropLayout) -> Self {
        Self { ue, pl, field_class_names: HashMap::new(), unknown: BTreeMap::new() }
    }

    fn mem(&self) -> &M {
        &self.ue.mem
    }

    fn obj_name(&self, obj: usize) -> Option<String> {
        self.ue.name(obj)
    }

    fn field_class(&mut self, field: usize) -> Option<String> {
        let fc = self.mem().read_valid_ptr(field + self.ue.layout.ffield_class)?;
        if let Some(n) = self.field_class_names.get(&fc) {
            return n.clone();
        }
        let n = self.ue.fname(fc + self.pl.field_class_name);
        self.field_class_names.insert(fc, n.clone());
        n
    }

    fn sub_type(&mut self, field: usize, off: usize, depth: usize) -> PropType {
        match self.mem().read_valid_ptr(field + off) {
            Some(p) => self.prop_type(p, depth + 1),
            None => PropType::Simple(Kind::Unknown),
        }
    }

    fn ref_name(&self, field: usize, off: usize) -> Option<String> {
        self.mem().read_valid_ptr(field + off).and_then(|o| self.obj_name(o))
    }

    /// Type tree of the FProperty at `field`.
    pub fn prop_type(&mut self, field: usize, depth: usize) -> PropType {
        if depth > MAX_DEPTH {
            return PropType::Simple(Kind::Unknown);
        }
        let Some(class) = self.field_class(field) else {
            return PropType::Simple(Kind::Unknown);
        };
        let pl = self.pl;
        let simple = |k| PropType::Simple(k);
        match class.as_str() {
            "ByteProperty" => match self.ref_name(field, pl.first) {
                Some(e) => PropType::Enum { inner: Box::new(simple(Kind::Byte)), name: Some(e) },
                None => simple(Kind::Byte),
            },
            "BoolProperty" => simple(Kind::Bool),
            "IntProperty" => simple(Kind::Int),
            "Int8Property" => simple(Kind::Int8),
            "Int16Property" => simple(Kind::Int16),
            "Int64Property" => simple(Kind::Int64),
            "UInt16Property" => simple(Kind::UInt16),
            "UInt32Property" => simple(Kind::UInt32),
            "UInt64Property" => simple(Kind::UInt64),
            "FloatProperty" => simple(Kind::Float),
            "DoubleProperty" | "LargeWorldCoordinatesRealProperty" => simple(Kind::Double),
            "NameProperty" => simple(Kind::Name),
            "StrProperty" => simple(Kind::Str),
            "Utf8StrProperty" => simple(Kind::Utf8Str),
            "AnsiStrProperty" => simple(Kind::AnsiStr),
            "TextProperty" => simple(Kind::Text),
            "ObjectProperty" | "ObjectPtrProperty" | "ClassProperty" | "ClassPtrProperty" => simple(Kind::Object),
            "WeakObjectProperty" => simple(Kind::WeakObject),
            "LazyObjectProperty" => simple(Kind::LazyObject),
            "SoftObjectProperty" | "SoftClassProperty" => simple(Kind::SoftObject),
            "InterfaceProperty" => simple(Kind::Interface),
            "DelegateProperty" => simple(Kind::Delegate),
            "MulticastDelegateProperty" | "MulticastInlineDelegateProperty" | "MulticastSparseDelegateProperty" => {
                simple(Kind::MulticastDelegate)
            }
            "FieldPathProperty" => simple(Kind::FieldPath),
            "StructProperty" => PropType::Struct(self.ref_name(field, pl.first)),
            "ArrayProperty" => PropType::Array(Box::new(self.sub_type(field, pl.array_inner, depth))),
            "SetProperty" => PropType::Set(Box::new(self.sub_type(field, pl.first, depth))),
            "OptionalProperty" => PropType::Optional(Box::new(self.sub_type(field, pl.first, depth))),
            "MapProperty" => {
                let k = self.sub_type(field, pl.first, depth);
                PropType::Map(Box::new(k), Box::new(self.sub_type(field, pl.second, depth)))
            }
            "EnumProperty" => {
                let inner = self.sub_type(field, pl.first, depth);
                PropType::Enum { inner: Box::new(inner), name: self.ref_name(field, pl.second) }
            }
            other => {
                *self.unknown.entry(other.to_string()).or_default() += 1;
                simple(Kind::Unknown)
            }
        }
    }

    /// Own properties of a UStruct. The bool is false if the chain broke off early.
    pub fn properties(&mut self, ustruct: usize) -> (Vec<Property>, bool) {
        let l = self.ue.layout;
        let mut out = Vec::new();
        let mut field = self.mem().read_valid_ptr(ustruct + l.ustruct_child_properties);
        while let Some(f) = field {
            if out.len() >= MAX_PROPS {
                return (out, false);
            }
            let (Some(name), Some(dim)) =
                (self.ue.fname(f + l.ffield_name), self.mem().read_i32(f + self.pl.array_dim))
            else {
                return (out, false);
            };
            let array_dim = u8::try_from(dim).ok().filter(|&d| d > 0);
            let Some(array_dim) = array_dim else {
                return (out, false);
            };
            if self.field_class(f).is_some_and(|c| c.ends_with("Property")) {
                let ty = self.prop_type(f, 0);
                out.push(Property { name, array_dim, ty });
            }
            field = self.mem().read_valid_ptr(f + l.ffield_next);
        }
        (out, true)
    }

    pub fn enum_entries(&self, uenum: usize) -> Option<Vec<(i64, String)>> {
        let base = uenum + self.pl.enum_names;
        let data = self.mem().read_ptr(base)?;
        let num = usize::try_from(self.mem().read_i32(base + 8)?).ok()?;
        if num == 0 {
            return Some(Vec::new());
        }
        if num > MAX_ENUM_ENTRIES {
            return None;
        }
        let raw = self.mem().read_vec(data, num * 16)?;
        let mut out = Vec::with_capacity(num);
        for i in 0..num {
            let e = data + i * 16;
            let full = self.ue.fname(e)?;
            let value = i64::from_le_bytes(raw[i * 16 + 8..i * 16 + 16].try_into().ok()?);
            let short = full.rsplit_once("::").map_or(full.as_str(), |(_, s)| s).to_string();
            out.push((value, short));
        }
        Some(out)
    }
}

/// UClass objects (meta classes) by name, found through the `Class` class.
fn class_class(ue: &Ue<impl Memory>, headers: &[ObjHeader]) -> Option<usize> {
    headers.iter().find(|h| h.class == h.obj && ue.name(h.obj).as_deref() == Some("Class")).map(|h| h.obj)
}

/// Builds the schema from every loaded UStruct and UEnum.
pub fn walk<M: Memory>(ue: &Ue<M>, pl: PropLayout) -> Result<(Schema, WalkStats)> {
    let headers = ue.object_headers().context("GObjects unreadable")?;
    let cc = class_class(ue, &headers).context("no 'Class' UClass in GObjects (FNamePool or UObject layout wrong)")?;
    let named = |n: &str| headers.iter().find(|h| h.class == cc && ue.name(h.obj).as_deref() == Some(n)).map(|h| h.obj);
    let struct_cls = named("Struct").context("UClass 'Struct' not found")?;
    let function_cls = named("Function").context("UClass 'Function' not found")?;
    let enum_cls = named("Enum").context("UClass 'Enum' not found")?;

    let mut meta: HashMap<usize, Meta> = HashMap::new();
    let mut walker = Walker::new(ue, pl);
    let mut stats = WalkStats { objects: headers.len(), ..Default::default() };
    let mut schema = Schema::default();
    let mut seen_structs = HashSet::new();
    let mut seen_enums = HashSet::new();
    for h in &headers {
        if h.flags & RF_CLASS_DEFAULT_OBJECT != 0 {
            continue;
        }
        let kind = meta.entry(h.class).or_insert_with(|| {
            if ue.class_is_a(h.class, function_cls) {
                Meta::Other
            } else if ue.class_is_a(h.class, struct_cls) {
                Meta::Struct
            } else if ue.class_is_a(h.class, enum_cls) {
                Meta::Enum
            } else {
                Meta::Other
            }
        });
        match kind {
            Meta::Other => {}
            Meta::Struct => {
                let Some(name) = ue.name(h.obj) else { continue };
                if !seen_structs.insert(name.clone()) {
                    stats.duplicate_names += 1;
                    continue;
                }
                let super_name = ue.super_of(h.obj).and_then(|s| ue.name(s));
                let (properties, complete) = walker.properties(h.obj);
                if !complete {
                    stats.truncated_structs.push(name.clone());
                }
                schema.structs.push(Struct { name, super_name, properties });
            }
            Meta::Enum => {
                let Some(name) = ue.name(h.obj) else { continue };
                if !seen_enums.insert(name.clone()) {
                    stats.duplicate_names += 1;
                    continue;
                }
                let entries = walker.enum_entries(h.obj).unwrap_or_default();
                schema.enums.push(Enum { name, entries });
            }
        }
    }
    stats.structs = schema.structs.len();
    stats.enums = schema.enums.len();
    stats.unknown_field_classes = std::mem::take(&mut walker.unknown);
    Ok((schema, stats))
}

/// Loading state of the game's reflection data.
#[derive(Debug)]
pub struct Readiness {
    pub objects: usize,
    /// UClass + UScriptStruct objects.
    pub structs: usize,
    pub enums: usize,
    /// Types (from the caller's list) that are absent or whose property chain is not linked yet.
    pub pending: Vec<String>,
}

/// Counts loaded types and checks that `with_properties` exist with a linked property chain.
/// UE registers UClass objects early in startup and links their properties later, so a type
/// count alone is not enough.
pub fn readiness<M: Memory>(ue: &Ue<M>, with_properties: &[&str]) -> Option<Readiness> {
    let headers = ue.object_headers()?;
    let cc = class_class(ue, &headers)?;
    let meta = |n: &str| headers.iter().find(|h| h.class == cc && ue.name(h.obj).as_deref() == Some(n)).map(|h| h.obj);
    let script_struct = meta("ScriptStruct");
    let enum_cls = meta("Enum");
    let mut found: HashMap<&str, usize> = HashMap::new();
    let (mut structs, mut enums) = (0, 0);
    for h in &headers {
        if h.flags & RF_CLASS_DEFAULT_OBJECT != 0 {
            continue;
        }
        if h.class == cc || Some(h.class) == script_struct {
            structs += 1;
            if let Some(n) = ue.name(h.obj) {
                if let Some(&want) = with_properties.iter().find(|w| **w == n) {
                    found.entry(want).or_insert(h.obj);
                }
            }
        } else if Some(h.class) == enum_cls {
            enums += 1;
        }
    }
    let linked = |s: usize| ue.mem.read_valid_ptr(s + ue.layout.ustruct_child_properties).is_some();
    let pending =
        with_properties.iter().filter(|w| !found.get(*w).is_some_and(|&s| linked(s))).map(|w| w.to_string()).collect();
    Some(Readiness { objects: headers.len(), structs, enums, pending })
}

#[cfg(test)]
mod tests {
    use super::*;
    use acr_ue::mem::SliceMemory;
    use acr_ue::reflection::UeGlobals;
    use acr_ue::sigs::{FNamePoolLayout, GObjectsLayout, Signatures, UObjectLayout};

    const HEAP: usize = 0x10_0000_0000;

    /// Synthetic UE heap: GObjects (one chunk), an FNamePool block, UObjects, FFields.
    struct Fake {
        mem: SliceMemory,
        l: UObjectLayout,
        pl: PropLayout,
        cursor: usize,
        gobjects: usize,
        pool: usize,
        names: usize,
        name_cursor: usize,
        chunk: usize,
        count: usize,
        field_classes: HashMap<String, usize>,
    }

    impl Fake {
        fn new() -> Self {
            let sigs = Signatures::from_toml_str(include_str!("../signatures.toml")).unwrap();
            let mut f = Self {
                mem: SliceMemory::new(HEAP, vec![0u8; 0x40_0000]),
                l: sigs.raw_offsets.uobject,
                pl: PropLayout::default(),
                cursor: HEAP + 0x100,
                gobjects: 0,
                pool: 0,
                names: 0,
                name_cursor: 0,
                chunk: 0,
                count: 0,
                field_classes: HashMap::new(),
            };
            f.gobjects = f.alloc(0x40);
            f.pool = f.alloc(0x100);
            f.names = f.alloc(0x2_0000);
            f.chunk = f.alloc(0x18 * 256);
            let table = f.alloc(8);
            f.w64(table, f.chunk);
            f.w64(f.gobjects, table);
            f.w32(f.gobjects + 0x10, 65536);
            f.w32(f.gobjects + 0x18, 1);
            f.w32(f.gobjects + 0x1C, 1);
            f.w64(f.pool + 0x10, f.names);
            f.name("None");
            f
        }

        fn alloc(&mut self, size: usize) -> usize {
            let a = (self.cursor + 15) & !15;
            self.cursor = a + size;
            a
        }
        fn w32(&self, a: usize, v: u32) {
            assert!(self.mem.write(a, &v.to_le_bytes()));
        }
        fn w64(&self, a: usize, v: usize) {
            assert!(self.mem.write(a, &(v as u64).to_le_bytes()));
        }

        fn name(&mut self, s: &str) -> u32 {
            let idx = (self.name_cursor / 2) as u32;
            let at = self.names + self.name_cursor;
            assert!(self.mem.write(at, &((s.len() as u16) << 6).to_le_bytes()));
            assert!(self.mem.write(at + 2, s.as_bytes()));
            self.name_cursor += (2 + s.len() + 1) & !1;
            idx
        }

        fn object(&mut self, name: &str, class: usize, sup: usize, flags: u32) -> usize {
            let o = self.alloc(0x100);
            let idx = self.name(name);
            self.w32(o + self.l.object_flags, flags);
            self.w32(o + self.l.internal_index, self.count as u32);
            self.w64(o + self.l.class, class);
            self.w32(o + self.l.name, idx);
            self.w64(o + self.l.ustruct_super, sup);
            self.w64(self.chunk + self.count * 0x18, o);
            self.count += 1;
            self.w32(self.gobjects + 0x14, self.count as u32);
            o
        }

        fn field_class(&mut self, class: &str) -> usize {
            if let Some(&c) = self.field_classes.get(class) {
                return c;
            }
            let c = self.alloc(0x28);
            let idx = self.name(class);
            self.w32(c + self.pl.field_class_name, idx);
            self.field_classes.insert(class.to_string(), c);
            c
        }

        /// A free-standing FProperty (inner / key / value properties).
        fn prop(&mut self, class: &str, name: &str, dim: u32, refs: &[(usize, usize)]) -> usize {
            let p = self.alloc(0x80);
            let fc = self.field_class(class);
            let idx = self.name(name);
            self.w64(p + self.l.ffield_class, fc);
            self.w32(p + self.l.ffield_name, idx);
            self.w32(p + self.pl.array_dim, dim);
            for &(off, v) in refs {
                self.w64(p + off, v);
            }
            p
        }

        /// Appends `p` to `ustruct`'s ChildProperties chain.
        fn add(&mut self, ustruct: usize, p: usize) {
            let mut slot = ustruct + self.l.ustruct_child_properties;
            while let Some(next) = self.mem.read_valid_ptr(slot) {
                slot = next + self.l.ffield_next;
            }
            self.w64(slot, p);
        }

        fn enum_names(&mut self, uenum: usize, entries: &[(&str, i64)]) {
            let data = self.alloc(entries.len() * 16);
            for (i, (n, v)) in entries.iter().enumerate() {
                let idx = self.name(n);
                self.w32(data + i * 16, idx);
                assert!(self.mem.write(data + i * 16 + 8, &v.to_le_bytes()));
            }
            self.w64(uenum + self.pl.enum_names, data);
            self.w32(uenum + self.pl.enum_names + 8, entries.len() as u32);
        }

        fn ue(&self) -> Ue<&SliceMemory> {
            Ue::new(
                &self.mem,
                UeGlobals { gobjects: self.gobjects, fnamepool: self.pool, gworld: None, process_event: None },
                GObjectsLayout::default(),
                FNamePoolLayout::default(),
                self.l,
            )
        }
    }

    fn build() -> Fake {
        let mut f = Fake::new();
        let class = f.object("Class", 0, 0, 0);
        f.w64(class + f.l.class, class);
        let object = f.object("Object", class, 0, 0);
        let strukt = f.object("Struct", class, object, 0);
        f.w64(class + f.l.ustruct_super, strukt);
        let script_struct = f.object("ScriptStruct", class, strukt, 0);
        let function = f.object("Function", class, strukt, 0);
        let enum_cls = f.object("Enum", class, object, 0);
        let bpgc = f.object("BlueprintGeneratedClass", class, class, 0);
        f.object("Default__ScriptStruct", script_struct, 0, RF_CLASS_DEFAULT_OBJECT);

        let color = f.object("EColor", enum_cls, 0, 0);
        f.enum_names(color, &[("EColor::Red", 0), ("EColor::Blue", 4), ("EColor::EColor_MAX", 5)]);
        let role = f.object("ENetRole", enum_cls, 0, 0);
        f.enum_names(role, &[("ROLE_None", 0), ("ROLE_MAX", 1)]);

        let vector = f.object("Vector", script_struct, 0, 0);
        let x = f.prop("DoubleProperty", "X", 1, &[]);
        f.add(vector, x);
        f.object("Vector", script_struct, 0, 0);

        let actor = f.object("MyActor", class, object, 0);
        let pl = f.pl;
        let pos = f.prop("StructProperty", "Pos", 1, &[(pl.first, vector)]);
        let inner = f.prop("IntProperty", "Arr", 1, &[]);
        let arr = f.prop("ArrayProperty", "Arr", 1, &[(pl.array_inner, inner)]);
        let col = f.prop("ByteProperty", "Color", 1, &[(pl.first, color)]);
        let raw = f.prop("ByteProperty", "Raw", 1, &[]);
        let fixed = f.prop("FloatProperty", "Fixed", 3, &[]);
        let key = f.prop("NameProperty", "Lookup_Key", 1, &[]);
        let under = f.prop("ByteProperty", "UnderlyingType", 1, &[]);
        let val = f.prop("EnumProperty", "Lookup", 1, &[(pl.first, under), (pl.second, color)]);
        let map = f.prop("MapProperty", "Lookup", 1, &[(pl.first, key), (pl.second, val)]);
        let cls = f.prop("ClassProperty", "Kind", 1, &[]);
        let soft = f.prop("SoftClassProperty", "SoftKind", 1, &[]);
        let opt_inner = f.prop("FloatProperty", "Maybe", 1, &[]);
        let opt = f.prop("OptionalProperty", "Maybe", 1, &[(pl.first, opt_inner)]);
        let weird = f.prop("VerseStringProperty", "Weird", 1, &[]);
        for p in [pos, arr, col, raw, fixed, map, cls, soft, opt, weird] {
            f.add(actor, p);
        }
        let func = f.object("DoThing", function, 0, 0);
        let parm = f.prop("IntProperty", "ReturnValue", 1, &[]);
        f.add(func, parm);
        f.object("Default__MyActor", actor, 0, RF_CLASS_DEFAULT_OBJECT);
        let bp = f.object("BP_Thing_C", bpgc, actor, 0);
        let b = f.prop("BoolProperty", "bOn", 1, &[]);
        f.add(bp, b);
        f
    }

    #[test]
    fn walks_types_like_dumper7() {
        let f = build();
        let ue = f.ue();
        let (schema, stats) = walk(&ue, f.pl).unwrap();
        let names: Vec<&str> = schema.structs.iter().map(|s| s.name.as_str()).collect();
        assert!(names.contains(&"Vector") && names.contains(&"MyActor") && names.contains(&"BP_Thing_C"), "{names:?}");
        assert!(names.contains(&"Class") && names.contains(&"ScriptStruct"), "meta classes are UClasses too");
        assert!(!names.contains(&"DoThing"), "functions are not written");
        assert!(!names.iter().any(|n| n.starts_with("Default__")), "CDOs are skipped");
        assert_eq!(stats.duplicate_names, 1, "second Vector skipped");

        let sm = schema.struct_map();
        let a = sm["MyActor"];
        assert_eq!(a.super_name.as_deref(), Some("Object"));
        let ty: Vec<String> =
            a.properties.iter().map(|p| format!("{} {} {}", p.name, p.array_dim, p.ty.describe())).collect();
        assert_eq!(
            ty,
            [
                "Pos 1 Struct<Vector>",
                "Arr 1 Array<Int>",
                "Color 1 Enum<EColor:Byte>",
                "Raw 1 Byte",
                "Fixed 3 Float",
                "Lookup 1 Map<Name, Enum<EColor:Byte>>",
                "Kind 1 Object",
                "SoftKind 1 SoftObject",
                "Maybe 1 Optional<Float>",
                "Weird 1 Unknown",
            ]
        );
        assert_eq!(a.slot_count(), 12);
        assert_eq!(sm["BP_Thing_C"].super_name.as_deref(), Some("MyActor"));
        assert_eq!(stats.unknown_field_classes.get("VerseStringProperty"), Some(&1));

        let em = schema.enum_map();
        assert_eq!(em["EColor"].entries, [(0, "Red".to_string()), (4, "Blue".into()), (5, "EColor_MAX".into())]);
        assert_eq!(em["ENetRole"].entries, [(0, "ROLE_None".to_string()), (1, "ROLE_MAX".into())]);

        let bytes = crate::usmap::write(&schema).unwrap();
        assert_eq!(crate::usmap::read(&bytes).unwrap().1, schema);
    }

    #[test]
    fn readiness_waits_for_linked_properties() {
        let f = build();
        let ue = f.ue();
        let r = readiness(&ue, &["MyActor", "Vector", "Missing"]).unwrap();
        assert_eq!(r.pending, ["Missing"]);
        assert_eq!(r.enums, 2);
        let r = readiness(&ue, &["Object"]).unwrap();
        assert_eq!(r.pending, ["Object"], "Object has no properties in the fake");
    }
}
