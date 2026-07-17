//! Binary parser for .eff files.

use std::io::{Read, Cursor};
use byteorder::{LittleEndian, ReadBytesExt};
use encoding_rs::SHIFT_JIS;

use super::types::*;

/// Errors that can occur during parsing.
#[derive(Debug)]
pub enum EffParseError {
    IoError(std::io::Error),
    UnsupportedVersion(u32),
    InvalidUtf8,
}

impl From<std::io::Error> for EffParseError {
    fn from(e: std::io::Error) -> Self {
        EffParseError::IoError(e)
    }
}

impl std::fmt::Display for EffParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EffParseError::IoError(e) => write!(f, "IO error: {}", e),
            EffParseError::UnsupportedVersion(v) => write!(f, "Unsupported version: 0x{:08X}", v),
            EffParseError::InvalidUtf8 => write!(f, "Invalid UTF-8 in string"),
        }
    }
}

/// Read a fixed-size null-terminated string (ASCII).
fn read_fixed_ascii<R: Read>(r: &mut R, size: usize) -> std::io::Result<String> {
    Ok(read_fixed_ascii_raw(r, size)?.0)
}

/// Like read_fixed_ascii but also returns the raw field bytes (for byte-perfect
/// write-back of leftover authoring bytes after the null terminator).
fn read_fixed_ascii_raw<R: Read>(r: &mut R, size: usize) -> std::io::Result<(String, Vec<u8>)> {
    let mut buf = vec![0u8; size];
    r.read_exact(&mut buf)?;
    let end = buf.iter().position(|&b| b == 0).unwrap_or(size);
    Ok((String::from_utf8_lossy(&buf[..end]).to_string(), buf))
}

/// Read a fixed-size null-terminated string (cp932/Shift-JIS).
fn read_fixed_cp932<R: Read>(r: &mut R, size: usize) -> std::io::Result<String> {
    Ok(read_fixed_cp932_raw(r, size)?.0)
}

/// Like read_fixed_cp932 but also returns the raw field bytes (for byte-perfect
/// write-back of names that don't survive a cp932 round-trip).
fn read_fixed_cp932_raw<R: Read>(r: &mut R, size: usize) -> std::io::Result<(String, Vec<u8>)> {
    let mut buf = vec![0u8; size];
    r.read_exact(&mut buf)?;
    let end = buf.iter().position(|&b| b == 0).unwrap_or(size);
    let (cow, _, _) = SHIFT_JIS.decode(&buf[..end]);
    Ok((cow.into_owned(), buf))
}

/// Read 9 f32 + 2 u32 + 1 f32 (48 bytes total).
fn read_array_record_48<R: Read>(r: &mut R) -> std::io::Result<ArrayRecord48> {
    let mut floats = [0.0f32; 9];
    for f in floats.iter_mut() {
        *f = r.read_f32::<LittleEndian>()?;
    }
    let ints = [r.read_u32::<LittleEndian>()?, r.read_u32::<LittleEndian>()?];
    let trailing = r.read_f32::<LittleEndian>()?;
    Ok(ArrayRecord48 { floats, ints, trailing })
}

/// Read a vector of 48-byte records.
fn read_array_48<R: Read>(r: &mut R) -> std::io::Result<Vec<ArrayRecord48>> {
    let count = r.read_u32::<LittleEndian>()? as usize;
    let mut vec = Vec::with_capacity(count);
    for _ in 0..count {
        vec.push(read_array_record_48(r)?);
    }
    Ok(vec)
}

/// Read a 72-byte record (data_17).
fn read_array_record_72<R: Read>(r: &mut R) -> std::io::Result<ArrayRecord72> {
    let ints0 = [
        r.read_u32::<LittleEndian>()?,
        r.read_u32::<LittleEndian>()?,
        r.read_u32::<LittleEndian>()?,
    ];
    let f0 = r.read_f32::<LittleEndian>()?;
    let int1 = r.read_u32::<LittleEndian>()?;
    let mut floats = [0.0f32; 11];
    for f in floats.iter_mut() {
        *f = r.read_f32::<LittleEndian>()?;
    }
    let ints1 = [r.read_u32::<LittleEndian>()?, r.read_u32::<LittleEndian>()?];
    Ok(ArrayRecord72 { ints0, f0, int1, floats, ints1 })
}

/// Parse a complete .eff file from a reader.
pub fn parse_eff<R: Read>(mut reader: R) -> Result<EffFile, EffParseError> {
    let ver_raw = reader.read_u32::<LittleEndian>()?;
    let version = GameVersion::from_raw(ver_raw);

    // Validate version
    match version {
        GameVersion::V0x04 | GameVersion::V0x6A | GameVersion::V0x6B
        | GameVersion::V0x6C | GameVersion::V0x6D => {}
        GameVersion::Unknown(v) => return Err(EffParseError::UnsupportedVersion(v)),
    }

    let unk1 = reader.read_u32::<LittleEndian>()?;

    // Effect name
    let name_len = if ver_raw >= 0x6D {
        reader.read_u32::<LittleEndian>()? as usize
    } else {
        16
    };
    let (effect_name, effect_name_raw) = read_fixed_cp932_raw(&mut reader, name_len)?;

    // Textures (v26)
    let tex_count = reader.read_u32::<LittleEndian>()? as usize;
    let mut textures = Vec::with_capacity(tex_count);
    for _ in 0..tex_count {
        textures.push(read_fixed_ascii(&mut reader, 20)?);
    }

    // v40 list
    let v40_count = reader.read_u32::<LittleEndian>()? as usize;
    let mut v40_list = Vec::with_capacity(v40_count);
    for _ in 0..v40_count {
        v40_list.push(read_fixed_ascii(&mut reader, 36)?);
    }

    // Segments
    let seg_count = reader.read_u32::<LittleEndian>()? as usize;
    let mut segments = Vec::with_capacity(seg_count);

    for _ in 0..seg_count {
        let seg = parse_segment(&mut reader, ver_raw)?;
        segments.push(seg);
    }

    Ok(EffFile {
        version,
        unk1,
        effect_name,
        effect_name_raw,
        textures,
        v40_list,
        segments,
        trailing: Vec::new(),
    })
}

/// Parse a single segment.
fn parse_segment<R: Read>(r: &mut R, ver_raw: u32) -> std::io::Result<Segment> {
    let (name, name_raw) = read_fixed_cp932_raw(r, 16)?;
    let (fn_name_1, fn1_raw) = read_fixed_ascii_raw(r, 16)?;
    let (fn_name_2, fn2_raw) = read_fixed_ascii_raw(r, 16)?;

    let struct_flags = if ver_raw >= 0x6A {
        let mut tmp = [0u8; 16];
        r.read_exact(&mut tmp)?;
        u32::from_le_bytes([tmp[4], tmp[5], tmp[6], tmp[7]])
    } else {
        0
    };

    // data_02: 8 uint32s
    let mut data_02 = [0u32; 8];
    for v in data_02.iter_mut() {
        *v = r.read_u32::<LittleEndian>()?;
    }

    // data_03 (CS2+ only)
    let data_03 = if ver_raw >= 0x6B {
        Some([r.read_f32::<LittleEndian>()?, r.read_f32::<LittleEndian>()?])
    } else {
        None
    };

    // data_04: 12 floats
    let mut data_04 = [0.0f32; 12];
    for v in data_04.iter_mut() {
        *v = r.read_f32::<LittleEndian>()?;
    }

    // data_05 (CS1 only, i.e. ver < 0x6B)
    let data_05 = if ver_raw < 0x6B {
        Some([
            r.read_f32::<LittleEndian>()?,
            r.read_f32::<LittleEndian>()?,
            r.read_f32::<LittleEndian>()?,
        ])
    } else {
        None
    };

    // data_06: 9 floats
    let mut data_06 = [0.0f32; 9];
    for v in data_06.iter_mut() {
        *v = r.read_f32::<LittleEndian>()?;
    }

    // data_07 (CS3+ only)
    let data_07 = if ver_raw >= 0x6C {
        Some([
            r.read_f32::<LittleEndian>()?,
            r.read_f32::<LittleEndian>()?,
            r.read_f32::<LittleEndian>()?,
            r.read_f32::<LittleEndian>()?,
        ])
    } else {
        None
    };

    // data_08: 8 floats
    let mut data_08 = [0.0f32; 8];
    for v in data_08.iter_mut() {
        *v = r.read_f32::<LittleEndian>()?;
    }

    // Array blocks 09-0E
    let data_09 = read_array_48(r)?;
    let data_0a = read_array_48(r)?;
    let data_0b = read_array_48(r)?;
    let data_0c = read_array_48(r)?;
    let data_0d = read_array_48(r)?;
    let data_0e = read_array_48(r)?;

    // Conditional array blocks 0F-12
    let data_0f = if struct_flags & 0x0100_0000 != 0 { read_array_48(r)? } else { Vec::new() };
    let data_10 = if struct_flags & 0x0400_0000 != 0 { read_array_48(r)? } else { Vec::new() };
    let data_11 = if struct_flags & 0x0800_0000 != 0 { read_array_48(r)? } else { Vec::new() };
    let data_12 = if struct_flags & 0x2000_0000 != 0 { read_array_48(r)? } else { Vec::new() };

    // data_13 nested arrays
    let data_13 = if struct_flags & 0x0200_0000 != 0 {
        let outer = r.read_u32::<LittleEndian>()? as usize;
        let mut nested = Vec::with_capacity(outer);
        for _ in 0..outer {
            let inner_cnt = r.read_u32::<LittleEndian>()? as usize;
            let mut inner = Vec::with_capacity(inner_cnt);
            for _ in 0..inner_cnt {
                inner.push(read_array_record_48(r)?);
            }
            nested.push(inner);
        }
        nested
    } else {
        Vec::new()
    };

    // data_14
    let data_14 = read_array_48(r)?;

    // Conditional blocks 15-21
    let mut struct_flags = struct_flags;
    let data_15 = if ver_raw <= 4 {
        let d15 = Some([r.read_f32::<LittleEndian>()?, r.read_f32::<LittleEndian>()?]);
        struct_flags = 3;
        d15
    } else {
        None
    };

    // data_16 (flag 0x002)
    let data_16 = if struct_flags & 0x002 != 0 {
        let mut arr = [0.0f32; 16];
        for v in arr.iter_mut() { *v = r.read_f32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_17 (flag 0x001)
    let mut data_17_cs1_raw = Vec::new();
    let data_17 = if struct_flags & 0x001 != 0 {
        if ver_raw >= 0x6B {
            let cnt = r.read_u32::<LittleEndian>()? as usize;
            let mut vec = Vec::with_capacity(cnt);
            for _ in 0..cnt {
                vec.push(read_array_record_72(r)?);
            }
            vec
        } else {
            // CS1: unparsed 16-byte block — keep it raw for a byte-perfect round-trip.
            let mut buf = [0u8; 16];
            r.read_exact(&mut buf)?;
            data_17_cs1_raw = buf.to_vec();
            Vec::new()
        }
    } else { Vec::new() };

    // data_18 (flag 0x010): 4 u32s
    let data_18 = if struct_flags & 0x010 != 0 {
        let mut arr = [0u32; 4];
        for v in arr.iter_mut() { *v = r.read_u32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_19 (flag 0x004): 8 u32s
    let data_19 = if struct_flags & 0x004 != 0 {
        let mut arr = [0u32; 8];
        for v in arr.iter_mut() { *v = r.read_u32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_1a (flag 0x008): 24 f32s
    let data_1a = if struct_flags & 0x008 != 0 {
        let mut arr = [0.0f32; 24];
        for v in arr.iter_mut() { *v = r.read_f32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_1b (ver >= 0x6A): Vec of [u32; 3]
    let data_1b = if ver_raw >= 0x6A {
        let cnt = r.read_u32::<LittleEndian>()? as usize;
        let mut vec = Vec::with_capacity(cnt);
        for _ in 0..cnt {
            let mut arr = [0u32; 3];
            for v in arr.iter_mut() { *v = r.read_u32::<LittleEndian>()?; }
            vec.push(arr);
        }
        vec
    } else { Vec::new() };

    // data_1c (flag 0x020): 6 f32s
    let data_1c = if struct_flags & 0x020 != 0 {
        let mut arr = [0.0f32; 6];
        for v in arr.iter_mut() { *v = r.read_f32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_1d (flag 0x040): 4 f32s
    let data_1d = if struct_flags & 0x040 != 0 {
        let mut arr = [0.0f32; 4];
        for v in arr.iter_mut() { *v = r.read_f32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_1e (flag 0x080): 8 u32s
    let data_1e = if struct_flags & 0x080 != 0 {
        let mut arr = [0u32; 8];
        for v in arr.iter_mut() { *v = r.read_u32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_1f (flag 0x100): 2 u32s
    let data_1f = if struct_flags & 0x100 != 0 {
        let mut arr = [0u32; 2];
        for v in arr.iter_mut() { *v = r.read_u32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    // data_20 (flag 0x200): 13 f32s
    let data_20 = if struct_flags & 0x200 != 0 {
        let mut arr = [0.0f32; 13];
        for v in arr.iter_mut() { *v = r.read_f32::<LittleEndian>()?; }
        Some(arr)
    } else { None };

    Ok(Segment {
        name,
        name_raw,
        fn_name_1,
        fn1_raw,
        fn_name_2,
        fn2_raw,
        struct_flags,
        data_02,
        data_03,
        data_04,
        data_05,
        data_06,
        data_07,
        data_08,
        data_09,
        data_0a,
        data_0b,
        data_0c,
        data_0d,
        data_0e,
        data_0f,
        data_10,
        data_11,
        data_12,
        data_13,
        data_14,
        data_15,
        data_16,
        data_17,
        data_17_cs1_raw,
        data_18,
        data_19,
        data_1a,
        data_1b,
        data_1c,
        data_1d,
        data_1e,
        data_1f,
        data_20,
    })
}

/// Parse a .eff file from a byte slice.
pub fn parse_eff_bytes(data: &[u8]) -> Result<EffFile, EffParseError> {
    let mut cursor = Cursor::new(data);
    let mut eff = parse_eff(&mut cursor)?;
    // Preserve any bytes after the structured content (trailing padding/footer)
    // so the round-trip is byte-perfect.
    let pos = (cursor.position() as usize).min(data.len());
    eff.trailing = data[pos..].to_vec();
    Ok(eff)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn test_parse_break03() {
        let path = r"..\docs\break03.eff";
        if let Ok(data) = fs::read(path) {
            let eff = parse_eff_bytes(&data).expect("Failed to parse break03.eff");
            assert!(eff.segment_count() > 0);
            println!("Parsed: {} ({} segments, {} textures)",
                eff.effect_name, eff.segment_count(), eff.textures.len());
        }
    }
}
