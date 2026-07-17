//! Types representing parsed .eff file data.

use serde::{Deserialize, Serialize};

/// Eff file format version. Named by hex version number.
///
/// Known versions:
/// - `V0x04`: CS1/CS2 PC ports + Hajimari no Kiseki
/// - `V0x6A`: CS1 (Vita/PS3)
/// - `V0x6B`: CS2 (Vita/PS3)
/// - `V0x6C`: CS3
/// - `V0x6D`: CS4
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum GameVersion {
    V0x04,
    V0x6A,
    V0x6B,
    V0x6C,
    V0x6D,
    Unknown(u32),
}

impl GameVersion {
    pub fn from_raw(v: u32) -> Self {
        match v {
            0x04 => Self::V0x04,
            0x6A => Self::V0x6A,
            0x6B => Self::V0x6B,
            0x6C => Self::V0x6C,
            0x6D => Self::V0x6D,
            _ => Self::Unknown(v),
        }
    }

    pub fn as_raw(&self) -> u32 {
        match self {
            Self::V0x04 => 0x04,
            Self::V0x6A => 0x6A,
            Self::V0x6B => 0x6B,
            Self::V0x6C => 0x6C,
            Self::V0x6D => 0x6D,
            Self::Unknown(v) => *v,
        }
    }

    /// Human-readable label with game context.
    pub fn label(&self) -> &'static str {
        match self {
            Self::V0x04 => "v0x04 (CS1/CS2 PC, Hajimari)",
            Self::V0x6A => "v0x6A (CS1 Vita/PS3)",
            Self::V0x6B => "v0x6B (CS2 Vita/PS3)",
            Self::V0x6C => "v0x6C (CS3)",
            Self::V0x6D => "v0x6D (CS4)",
            Self::Unknown(_) => "???",
        }
    }
}

/// A single segment within an effect file.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Segment {
    /// Segment name (cp932-encoded, e.g. "エミッタ", "パーティクル")
    pub name: String,
    /// Original raw bytes of the 16-byte name field. Written back verbatim when
    /// `name` is unchanged, so names that don't survive a cp932 decode/re-encode
    /// round-trip (invalid trailing bytes) stay byte-perfect.
    #[serde(default)]
    pub name_raw: Vec<u8>,
    /// First function name
    pub fn_name_1: String,
    #[serde(default)]
    pub fn1_raw: Vec<u8>,
    /// Second function name
    pub fn_name_2: String,
    #[serde(default)]
    pub fn2_raw: Vec<u8>,
    /// Structure flags controlling which optional blocks are present
    pub struct_flags: u32,

    // Fixed data blocks
    /// data_02: 8 uint32 values (some reinterpreted as float depending on context)
    pub data_02: [u32; 8],
    /// data_03: 2 floats (only if ver >= 0x6B/CS2+)
    pub data_03: Option<[f32; 2]>,
    /// data_04: 12 floats
    pub data_04: [f32; 12],
    /// data_05: 3 floats (only if ver < 0x6B, i.e. CS1/old format)
    pub data_05: Option<[f32; 3]>,
    /// data_06: 9 floats
    pub data_06: [f32; 9],
    /// data_07: 4 floats (only if ver >= 0x6C/CS3+)
    pub data_07: Option<[f32; 4]>,
    /// data_08: 8 floats
    pub data_08: [f32; 8],

    // Array blocks (09-0E): each is Vec<[f32; 9] + [u32; 2] + f32> = 48 bytes per record
    pub data_09: Vec<ArrayRecord48>,
    pub data_0a: Vec<ArrayRecord48>,
    pub data_0b: Vec<ArrayRecord48>,
    pub data_0c: Vec<ArrayRecord48>,
    pub data_0d: Vec<ArrayRecord48>,
    pub data_0e: Vec<ArrayRecord48>,

    // Conditional array blocks (0F-12)
    pub data_0f: Vec<ArrayRecord48>,
    pub data_10: Vec<ArrayRecord48>,
    pub data_11: Vec<ArrayRecord48>,
    pub data_12: Vec<ArrayRecord48>,

    // data_13: nested arrays
    pub data_13: Vec<Vec<ArrayRecord48>>,

    // data_14: [u32;3] + f32 + [u32;4] + [f32;4] per record
    pub data_14: Vec<ArrayRecord48>,

    // Conditional terminal blocks
    pub data_15: Option<[f32; 2]>,
    pub data_16: Option<[f32; 16]>,
    pub data_17: Vec<ArrayRecord72>,
    /// CS1 (ver < 0x6B) data_17 is an unparsed 16-byte block; kept raw so it
    /// round-trips byte-perfect instead of being zeroed.
    #[serde(default)]
    pub data_17_cs1_raw: Vec<u8>,
    pub data_18: Option<[u32; 4]>,
    pub data_19: Option<[u32; 8]>,
    pub data_1a: Option<[f32; 24]>,
    pub data_1b: Vec<[u32; 3]>,
    pub data_1c: Option<[f32; 6]>,
    pub data_1d: Option<[f32; 4]>,
    pub data_1e: Option<[u32; 8]>,
    pub data_1f: Option<[u32; 2]>,
    pub data_20: Option<[f32; 13]>,
}

/// Standard 48-byte record used in array blocks 09-0E, 0F-12, 14.
/// Schema: 9 floats + 2 uint32s + 1 float
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct ArrayRecord48 {
    pub floats: [f32; 9],
    pub ints: [u32; 2],
    pub trailing: f32,
}

/// 72-byte record used in data_17.
/// Schema: 3 uint32s + 1 float + 1 uint32 + rest floats (11) + 2 uint32s
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct ArrayRecord72 {
    pub ints0: [u32; 3],
    pub f0: f32,
    pub int1: u32,
    pub floats: [f32; 11],
    pub ints1: [u32; 2],
}

impl ArrayRecord48 {
    /// A zeroed-out default record.
    pub fn default_record() -> Self {
        Self { floats: [0.0; 9], ints: [0, 0], trailing: 0.0 }
    }
}

/// Fully parsed .eff file.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EffFile {
    pub version: GameVersion,
    pub unk1: u32,
    pub effect_name: String,
    #[serde(default)]
    pub effect_name_raw: Vec<u8>,
    /// Texture references (max 20 chars ASCII each)
    pub textures: Vec<String>,
    /// Unknown string list (max 36 chars ASCII each)
    pub v40_list: Vec<String>,
    pub segments: Vec<Segment>,
    /// Bytes after the last parsed segment (trailing padding/footer, often 8
    /// zero bytes). Preserved verbatim for a byte-perfect round-trip.
    #[serde(default)]
    pub trailing: Vec<u8>,
}

impl EffFile {
    /// Number of segments
    pub fn segment_count(&self) -> usize {
        self.segments.len()
    }

    /// Create a new empty .eff file with sensible defaults for the given version.
    pub fn new_default(version: GameVersion) -> Self {
        Self {
            version,
            unk1: 0,
            effect_name: "new_effect".to_string(),
            effect_name_raw: Vec::new(),
            textures: Vec::new(),
            v40_list: Vec::new(),
            segments: vec![Segment::default_segment(version)],
            trailing: vec![0u8; 8],
        }
    }
}

impl Segment {
    /// Create a default segment with sensible values for a new effect.
    pub fn default_segment(version: GameVersion) -> Self {
        let ver_raw = version.as_raw();
        Self {
            name: "root".to_string(),
            name_raw: Vec::new(),
            fn_name_1: String::new(),
            fn1_raw: Vec::new(),
            fn_name_2: String::new(),
            fn2_raw: Vec::new(),
            struct_flags: 0,
            // d02: [flagsA, orient&enable, parent-inherit, draw-order+mesh, shape/blend, sound-id, trail-framerate, unknown-float]
            data_02: [0, 0x11, 0, 0, 0, 0, 0, 0],
            data_03: if ver_raw >= 0x6B { Some([0.0; 2]) } else { None },
            // d04: [crop L,T,R,B, lifetime, unk5, unk6, unk7, v0-min, v0-max, gravity, bounce]
            data_04: [0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
            data_05: if ver_raw < 0x6B { Some([0.0; 3]) } else { None },
            data_06: [0.0; 9],
            data_07: if ver_raw >= 0x6C { Some([0.0; 4]) } else { None },
            // Default unit quad corners
            data_08: [-0.5, 0.5, 0.0, 0.0, 0.5, -0.5, 0.0, 0.0],
            // One keyframe at t=0 for essential tracks
            data_09: vec![ArrayRecord48 { floats: [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], ints: [0, 0], trailing: 0.0 }],
            data_0a: vec![ArrayRecord48 { floats: [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], ints: [0, 0], trailing: 0.0 }],
            data_0b: vec![ArrayRecord48 { floats: [1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0], ints: [0, 0], trailing: 0.0 }],
            data_0c: Vec::new(),
            data_0d: vec![ArrayRecord48 { floats: [1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0], ints: [0, 0], trailing: 0.0 }],
            data_0e: vec![ArrayRecord48 { floats: [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], ints: [0, 0], trailing: 0.0 }],
            data_0f: Vec::new(),
            data_10: Vec::new(),
            data_11: Vec::new(),
            data_12: Vec::new(),
            data_13: Vec::new(),
            data_14: Vec::new(),
            data_15: None,
            data_16: None,
            data_17: Vec::new(),
            data_17_cs1_raw: Vec::new(),
            data_18: None,
            data_19: None,
            data_1a: None,
            data_1b: Vec::new(),
            data_1c: None,
            data_1d: None,
            data_1e: None,
            data_1f: None,
            data_20: None,
        }
    }
}
