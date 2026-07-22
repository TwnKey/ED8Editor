use ecow::{eco_format, EcoString};
use indexmap::IndexMap;
use smallvec::SmallVec;

pub type SmallBuf = SmallVec<[u8; 16]>; // TODO: replace with ecow::EcoBytes or something

// Schema definition types

/// Types as they appear in the schema definition
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize))]
#[cfg_attr(feature = "serde", serde(deny_unknown_fields, rename_all = "snake_case"))]
pub enum Type {
    I8,
    I16,
    I32,
    //I64,
    U8,
    U16,
    U32,
    //U64,
    F32,
    //F64,
    CUtf8, // null terminated UTF-8 string
    //C_SJIS, // null terminated Shift-JIS string
    #[cfg_attr(feature = "serde", serde(rename = "bytes", serialize_with = "serde_serialize_type_x", deserialize_with = "serde_deserialize_type_x"))]
    X(usize),
    #[cfg_attr(feature = "serde", serde(serialize_with = "serde_serialize_type_repeat", deserialize_with = "serde_deserialize_type_repeat"))]
    Repeat(usize, Box<Type>),
    Ref(Header),
}

/// Values corresponding to the types available in the schema
#[derive(Clone, Debug)]
pub enum Value {
    I8(i8),
    I16(i16),
    I32(i32),
    U8(u8),
    U16(u16),
    U32(u32),
    CUtf8(String),
    //C_SJIS(SJISString),
    X(std::vec::Vec<u8>),
    Repeat(std::vec::Vec<Value>),
    Ref(Box<Value>),
}

/// Atomic types; after unrolling repetitions and dereferencing references -- for converting to/from CSV/SQL
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum AtomicType {
    I8,
    I16,
    I32,
    U8,
    U16,
    U32,
    F32,
    CUtf8,
    X(usize),
}

/// Atomic values - values corresponding to the atomic types
#[derive(Clone, Debug)]
pub enum AtomicValue {
    I8(i8),
    I16(i16),
    I32(i32),
    U8(u8),
    U16(u16),
    U32(u32),
    F32(f32),
    CUtf8(EcoString),
    X(SmallBuf),
}

#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize))]
#[cfg_attr(feature = "serde", serde(deny_unknown_fields))]
pub struct Schemas {
    pub entries: IndexMap<Header, Schema>,
    pub common: IndexMap<Header, Schema>,
}

// useful type definitions
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Header {
    name: crate::io::PaddedAsciiString<1>,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct HeaderRef<'a> {
    name: crate::io::PaddedAsciiStr<'a, 1>,
}
pub type Attribute = EcoString;

pub type Schema = IndexMap<Attribute, Type>;

pub type AtomicSchema = IndexMap<Attribute, AtomicType>;
pub type AtomicSchemas = IndexMap<Header, AtomicSchema>;
pub type AtomicAttributeValues = IndexMap<Attribute, AtomicValue>;

// header

impl Header {
    pub fn new(s: &str) -> Result<Self, crate::io::PaddedStringError> {
        crate::io::PaddedAsciiString::<1>::new(s).map(|name| Self { name })
    }

    pub fn write_with_terminator<F: std::io::Write>(&self, f: &mut F) -> std::io::Result<usize> {
        self.name.write_to(f)
    }

    pub fn as_str(&self) -> &str {
        self.name.as_str()
    }
}

impl<'a> HeaderRef<'a> {
    pub fn to_owned(self) -> Header {
        Header::from(self.name.to_owned())
    }
}

impl<'a> From<crate::io::PaddedAsciiStr<'a, 1>> for HeaderRef<'a> {
    fn from(name: crate::io::PaddedAsciiStr<'a, 1>) -> Self {
        Self { name }
    }
}

impl From<crate::io::PaddedAsciiString<1>> for Header {
    fn from(name: crate::io::PaddedAsciiString<1>) -> Self {
        Self { name }
    }
}

impl TryFrom<crate::io::PaddedByteString<1>> for Header {
    type Error = crate::io::PaddedStringError;
    fn try_from(s: crate::io::PaddedByteString<1>) -> Result<Self, Self::Error> {
        crate::io::PaddedAsciiString::<1>::try_from(s).map(|name| Self { name })
    }
}

impl<'a> PartialEq<Header> for HeaderRef<'a> {
    fn eq(&self, other: &Header) -> bool {
        self.name == other.name
    }
}

impl<'a> PartialEq<HeaderRef<'a>> for Header {
    fn eq(&self, other: &HeaderRef<'a>) -> bool {
        self.name == other.name
    }
}

impl core::fmt::Display for Header {
    fn fmt(&self, f: &mut core::fmt::Formatter) -> core::fmt::Result {
        self.name.fmt(f)
        //write!(f, "{}", self.name)
    }
}

impl indexmap::Equivalent<HeaderRef<'_>> for Header {
    fn equivalent(&self, key: &HeaderRef<'_>) -> bool {
        self == key
    }
}

impl<'a> indexmap::Equivalent<Header> for HeaderRef<'a> {
    fn equivalent(&self, key: &Header) -> bool {
        self == key
    }
}

// serialize schemas with serde

#[cfg(feature = "serde")]
impl serde::Serialize for Header {
    fn serialize<S: serde::Serializer>(&self, ser: S) -> Result<S::Ok, S::Error> {
        ser.serialize_str(self.name.as_str())
    }
}

#[cfg(feature = "serde")]
impl<'a> serde::Deserialize<'a> for Header {
    fn deserialize<D: serde::Deserializer<'a>>(des: D) -> Result<Self, D::Error> {
        use serde::de::Error as _;
        let s = <&str>::deserialize(des)?;
        crate::io::PaddedAsciiString::<1>::new(s)
            .map(Self::from)
            .map_err(|err| D::Error::custom(format!("invalid header name: {err}")))
    }
}

#[cfg(feature = "serde")]
fn serde_serialize_type_x<S: serde::Serializer>(len: &usize, ser: S) -> Result<S::Ok, S::Error> {
    use serde::ser::SerializeStruct as _;
    let mut s = ser.serialize_struct("bytes", 1)?;
    s.serialize_field("count", len)?;
    s.end()
}

#[cfg(feature = "serde")]
fn serde_serialize_type_repeat<S: serde::Serializer>(count: &usize, ty: &Type, ser: S) -> Result<S::Ok, S::Error> {
    use serde::ser::SerializeStruct as _;
    let mut s = ser.serialize_struct("repeat", 2)?;
    s.serialize_field("count", count)?;
    s.serialize_field("type", ty)?;
    s.end()
}

#[cfg(feature = "serde")]
fn serde_deserialize_type_x<'a, D: serde::Deserializer<'a>>(des: D) -> Result<usize, D::Error> {
    use serde::Deserialize;
    #[derive(Deserialize)]
    struct TypeX {
        count: usize,
    }
    TypeX::deserialize(des).map(|x| x.count)
}

#[cfg(feature = "serde")]
fn serde_deserialize_type_repeat<'a, D: serde::Deserializer<'a>>(des: D) -> Result<(usize, Box<Type>), D::Error> {
    use serde::Deserialize;
    #[derive(Deserialize)]
    struct TypeRepeat {
        count: usize,
        r#type: Box<Type>,
    }
    TypeRepeat::deserialize(des).map(|x| (x.count, x.r#type))
}

// convert schema type to atomic type (by unrolling sequences and dereferencing common structures such as effects)

#[derive(Clone, Debug)]
pub enum NF1ErrorDetail {
    NoSuchSchema(Header),
    Duplicate(Header),
}

#[derive(Clone, Debug)]
pub enum NF1ErrorPosition {
    SchemaLookup,
    Deref(Header),
}

#[derive(Clone, Debug)]
pub struct NF1Error {
    detail: NF1ErrorDetail,
    context: std::vec::Vec<NF1ErrorPosition>, // first element is the position of the original cause, the rest is how we got there; may be empty
}

impl NF1Error {
    pub fn add_context(mut self, position: NF1ErrorPosition) -> Self {
        self.context.push(position);
        self
    }
}

impl std::fmt::Display for NF1ErrorDetail {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> Result<(), std::fmt::Error> {
        match self {
            NF1ErrorDetail::NoSuchSchema(s) => write!(f, "no schema '{}' found", s),
            NF1ErrorDetail::Duplicate(s) => write!(f, "duplicate definition of schema '{}'", s),
        }
    }
}

impl std::fmt::Display for NF1ErrorPosition {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> Result<(), std::fmt::Error> {
        match self {
            NF1ErrorPosition::SchemaLookup => write!(f, "during schema lookup"),
            NF1ErrorPosition::Deref(s) => write!(f, "while dereferencing a subschema of '{}'", s),
        }
    }
}

impl std::fmt::Display for NF1Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> Result<(), std::fmt::Error> {
        let Self { detail, context } = self;
        write!(f, "schema normalization error: {}", detail)?;
        if !context.is_empty() {
            write!(f, ". error occured {}", context[0])?;
            for pos in &context[1..] {
                write!(f, "\n - {}", pos)?;
            }
        }
        Ok(())
    }
}

impl std::error::Error for NF1Error {}

impl Type {
    pub fn try_to_atomic_type(&self) -> Option<AtomicType> {
        match self {
            Type::I8 => Some(AtomicType::I8),
            Type::I16 => Some(AtomicType::I16),
            Type::I32 => Some(AtomicType::I32),
            Type::U8 => Some(AtomicType::U8),
            Type::U16 => Some(AtomicType::U16),
            Type::U32 => Some(AtomicType::U32),
            Type::F32 => Some(AtomicType::F32),
            Type::CUtf8 => Some(AtomicType::CUtf8),
            Type::X(n) => Some(AtomicType::X(*n)),
            Type::Repeat(_, _) => None,
            Type::Ref(_) => None,
        }
    }
}

impl AtomicType {
    pub fn describe(&self) -> &'static str {
        match self {
            AtomicType::I8 => "i8 (-128 to 127 inclusive)",
            AtomicType::I16 => "i16 (-32768 to 32767 inclusive)",
            AtomicType::I32 => "i32 (-2147483648 to 2147483647 inclusive)",
            AtomicType::U8 => "u8 (0 to 255 inclusive)",
            AtomicType::U16 => "u16 (0 to 65535 inclusive)",
            AtomicType::U32 => "u32 (0 to 4294967295 inclusive)",
            AtomicType::F32 => "f32 (single-precision floating point)",
            AtomicType::CUtf8 => "UTF-8 string",
            AtomicType::X(_) => "hex-encoded data",
        }
    }

    // for creating sqlite tbl schemas
    #[cfg(feature = "rusqlite")]
    pub fn sqlite_type(&self) -> &'static str {
        match self {
            AtomicType::I8 | AtomicType::I16 | AtomicType::I32 | AtomicType::U8 | AtomicType::U16 | AtomicType::U32 => "int",
            AtomicType::F32 => "real",
            AtomicType::CUtf8 => "text",
            AtomicType::X(_) => "blob",
        }
    }
}

impl AtomicValue {
    pub fn r#type(&self) -> AtomicType {
        match self {
            AtomicValue::I8(_) => AtomicType::I8,
            AtomicValue::I16(_) => AtomicType::I16,
            AtomicValue::I32(_) => AtomicType::I32,
            AtomicValue::U8(_) => AtomicType::U8,
            AtomicValue::U16(_) => AtomicType::U16,
            AtomicValue::U32(_) => AtomicType::U32,
            AtomicValue::F32(_) => AtomicType::F32,
            AtomicValue::CUtf8(_) => AtomicType::CUtf8,
            AtomicValue::X(data) => AtomicType::X(data.len()),
        }
    }

    /// Get value as `i8`. Panics if the type doesn't match.
    pub fn expect_i8(&self) -> i8 {
        match self {
            AtomicValue::I8(x) => *x,
            _ => panic!("expected i8, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `i16`. Panics if the type doesn't match.
    pub fn expect_i16(&self) -> i16 {
        match self {
            AtomicValue::I16(x) => *x,
            _ => panic!("expected i16, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `i32`. Panics if the type doesn't match.
    pub fn expect_i32(&self) -> i32 {
        match self {
            AtomicValue::I32(x) => *x,
            _ => panic!("expected i32, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `u8`. Panics if the type doesn't match.
    pub fn expect_u8(&self) -> u8 {
        match self {
            AtomicValue::U8(x) => *x,
            _ => panic!("expected u8, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `u16`. Panucs if the type doesn't match.
    pub fn expect_u16(&self) -> u16 {
        match self {
            AtomicValue::U16(x) => *x,
            _ => panic!("expected u16, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `u32`. Panics if the type doesn't match.
    pub fn expect_u32(&self) -> u32 {
        match self {
            AtomicValue::U32(x) => *x,
            _ => panic!("expected u32, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as `f32`. Panics if the type doesn't match.
    pub fn expect_f32(&self) -> f32 {
        match self {
            AtomicValue::F32(x) => *x,
            _ => panic!("expected f32, but got a {:?}", self.r#type()),
        }
    }

    /// Get value as a string. Panics if the type doesn't match.
    pub fn expect_c_utf8(self) -> ecow::EcoString {
        match self {
            AtomicValue::CUtf8(s) => s,
            _ => panic!("expected utf8 string, but got a {:?}", self.r#type()),
        }
    }

    /// Get value reference as a string. Panics if the type doesn't match.
    pub fn expect_c_utf8_as_ref(&self) -> &str {
        match self {
            AtomicValue::CUtf8(s) => s.as_str(),
            _ => panic!("expected utf8 string, but got a {:?}", self.r#type()),
        }
    }

    /// Get value reference as hex data. Panics if the type doesn't match.
    pub fn expect_data_as_ref(&self) -> &[u8] {
        match self {
            AtomicValue::X(data) => data.as_slice(),
            _ => panic!("expected data slice, but got a {:?}", self.r#type()),
        }
    }
}

// "Flatten" the schema by unrolling sequences and dereferencing references
pub fn get_nf1(schema_name: &Header, schemas: &indexmap::IndexMap<Header, Schema>) -> Result<AtomicSchema, NF1Error> {
    // TODO: check duplicates
    let schema = schemas.get(schema_name).ok_or_else(|| NF1Error {
        detail: NF1ErrorDetail::NoSuchSchema(schema_name.clone()),
        context: vec![NF1ErrorPosition::SchemaLookup],
    })?;
    let mut nf1 = IndexMap::new();
    for (name, ty) in schema.iter() {
        if let Some(t) = ty.try_to_atomic_type() {
            nf1.insert(name.clone(), t);
            continue;
        }
        match ty {
            Type::Repeat(n, ty) => {
                if let Some(t) = ty.try_to_atomic_type() {
                    for i in 1..=*n {
                        nf1.insert(eco_format!("{}[{}]", name, i), t);
                    }
                } else if let Type::Ref(s) = ty.as_ref() {
                    let sub_nf1 = get_nf1(s, schemas).map_err(|e| e.add_context(NF1ErrorPosition::Deref(schema_name.clone())))?;
                    for i in 1..=*n {
                        for (n, t) in sub_nf1.iter() {
                            nf1.insert(eco_format!("{}[{}] {}", name, i, n), *t);
                        }
                    }
                } else {
                    panic!("unsupported type for repeat in normal form: {:?}", ty);
                }
            }
            Type::Ref(s) => {
                let sub_nf1 = get_nf1(s, schemas).map_err(|e| e.add_context(NF1ErrorPosition::Deref(schema_name.clone())))?;
                for (n, t) in sub_nf1.into_iter() {
                    nf1.insert(eco_format!("{} {}", name, n), t);
                }
            }
            _ => unreachable!("all other types should be primitive and caught by the 'if let' above"),
        }
    }
    Ok(nf1)
}

pub fn get_nf1_for_all(schemas: &Schemas) -> Result<AtomicSchemas, NF1Error> {
    let mut joint = schemas.entries.clone();
    for (header, schema) in schemas.common.iter() {
        match joint.entry(header.clone()) {
            indexmap::map::Entry::Vacant(entry) => {
                entry.insert(schema.clone());
            }
            indexmap::map::Entry::Occupied(entry) => {
                return Err(NF1Error {
                    detail: NF1ErrorDetail::Duplicate(entry.key().clone()),
                    context: vec![],
                });
            }
        }
    }
    let joint = joint;
    let mut res = IndexMap::new();
    for name in schemas.entries.keys() {
        res.insert(name.clone(), get_nf1(name, &joint)?);
    }
    Ok(res)
}