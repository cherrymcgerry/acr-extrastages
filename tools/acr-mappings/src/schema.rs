//! The reflection schema a `.usmap` describes: enums and structs (classes + script structs) with
//! their own serialized properties in declaration order.

use std::collections::BTreeMap;

/// `EPropertyType` as CUE4Parse reads it from a usmap (`CUE4Parse.MappingsProvider.Usmap`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[repr(u8)]
pub enum Kind {
    Byte = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    Object = 4,
    Name = 5,
    Delegate = 6,
    Double = 7,
    Array = 8,
    Struct = 9,
    Str = 10,
    Text = 11,
    Interface = 12,
    MulticastDelegate = 13,
    WeakObject = 14,
    LazyObject = 15,
    AssetObject = 16,
    SoftObject = 17,
    UInt64 = 18,
    UInt32 = 19,
    UInt16 = 20,
    Int64 = 21,
    Int16 = 22,
    Int8 = 23,
    Map = 24,
    Set = 25,
    Enum = 26,
    FieldPath = 27,
    Optional = 28,
    Utf8Str = 29,
    AnsiStr = 30,
    Unknown = 0xFF,
}

impl Kind {
    pub fn from_u8(v: u8) -> Option<Self> {
        use Kind::*;
        const ALL: [Kind; 31] = [
            Byte,
            Bool,
            Int,
            Float,
            Object,
            Name,
            Delegate,
            Double,
            Array,
            Struct,
            Str,
            Text,
            Interface,
            MulticastDelegate,
            WeakObject,
            LazyObject,
            AssetObject,
            SoftObject,
            UInt64,
            UInt32,
            UInt16,
            Int64,
            Int16,
            Int8,
            Map,
            Set,
            Enum,
            FieldPath,
            Optional,
            Utf8Str,
            AnsiStr,
        ];
        match v {
            0xFF => Some(Unknown),
            _ => ALL.get(v as usize).copied(),
        }
    }
}

/// A property type tree.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum PropType {
    Simple(Kind),
    Enum { inner: Box<PropType>, name: Option<String> },
    Struct(Option<String>),
    Array(Box<PropType>),
    Set(Box<PropType>),
    Optional(Box<PropType>),
    Map(Box<PropType>, Box<PropType>),
}

impl PropType {
    pub fn kind(&self) -> Kind {
        match self {
            Self::Simple(k) => *k,
            Self::Enum { .. } => Kind::Enum,
            Self::Struct(_) => Kind::Struct,
            Self::Array(_) => Kind::Array,
            Self::Set(_) => Kind::Set,
            Self::Optional(_) => Kind::Optional,
            Self::Map(..) => Kind::Map,
        }
    }

    pub fn describe(&self) -> String {
        match self {
            Self::Simple(k) => format!("{k:?}"),
            Self::Enum { inner, name } => format!("Enum<{}:{}>", name.as_deref().unwrap_or("?"), inner.describe()),
            Self::Struct(n) => format!("Struct<{}>", n.as_deref().unwrap_or("?")),
            Self::Array(i) => format!("Array<{}>", i.describe()),
            Self::Set(i) => format!("Set<{}>", i.describe()),
            Self::Optional(i) => format!("Optional<{}>", i.describe()),
            Self::Map(k, v) => format!("Map<{}, {}>", k.describe(), v.describe()),
        }
    }

    /// True if any node of the tree is [`Kind::Unknown`].
    pub fn has_unknown(&self) -> bool {
        match self {
            Self::Simple(k) => *k == Kind::Unknown,
            Self::Enum { inner, .. } | Self::Array(inner) | Self::Set(inner) | Self::Optional(inner) => {
                inner.has_unknown()
            }
            Self::Struct(_) => false,
            Self::Map(k, v) => k.has_unknown() || v.has_unknown(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Property {
    pub name: String,
    pub array_dim: u8,
    pub ty: PropType,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Struct {
    pub name: String,
    pub super_name: Option<String>,
    pub properties: Vec<Property>,
}

impl Struct {
    /// Schema slots (one per array element), the usmap `propertyCount`.
    pub fn slot_count(&self) -> usize {
        self.properties.iter().map(|p| p.array_dim as usize).sum()
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Enum {
    pub name: String,
    /// `(value, entry name)` in declaration order.
    pub entries: Vec<(i64, String)>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Schema {
    pub enums: Vec<Enum>,
    pub structs: Vec<Struct>,
}

impl Schema {
    pub fn struct_map(&self) -> BTreeMap<&str, &Struct> {
        self.structs.iter().map(|s| (s.name.as_str(), s)).collect()
    }

    pub fn enum_map(&self) -> BTreeMap<&str, &Enum> {
        self.enums.iter().map(|e| (e.name.as_str(), e)).collect()
    }

    pub fn property_count(&self) -> usize {
        self.structs.iter().map(|s| s.properties.len()).sum()
    }
}
