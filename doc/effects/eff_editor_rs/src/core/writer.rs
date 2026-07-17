//! Binary writer for .eff files (round-trip support).
//! WIP — will be used by the editor to save modifications.

use std::io::{Cursor, Write};
use byteorder::{LittleEndian, WriteBytesExt};
use encoding_rs::SHIFT_JIS;

use super::types::*;

/// Write padding (zeros) to fill to a fixed byte length.
fn write_padded<W: Write>(w: &mut W, data: &[u8], size: usize) -> std::io::Result<()> {
    w.write_all(data)?;
    if data.len() < size {
        let padding = vec![0u8; size - data.len()];
        w.write_all(&padding)?;
    }
    Ok(())
}

/// Decode a fixed cp932 field (up to the first null) — matches the parser, so
/// we can tell whether a name still corresponds to its stored raw bytes.
fn cp932_decode(buf: &[u8]) -> String {
    let end = buf.iter().position(|&b| b == 0).unwrap_or(buf.len());
    SHIFT_JIS.decode(&buf[..end]).0.into_owned()
}

/// Decode a fixed ASCII field (up to the first null), matching the parser.
fn ascii_decode(buf: &[u8]) -> String {
    let end = buf.iter().position(|&b| b == 0).unwrap_or(buf.len());
    String::from_utf8_lossy(&buf[..end]).to_string()
}

/// Write a string as cp932 (Shift-JIS), padded/truncated to fixed length.
fn write_fixed_cp932<W: Write>(w: &mut W, s: &str, size: usize) -> std::io::Result<()> {
    let (encoded, _, _) = SHIFT_JIS.encode(s);
    let mut bytes = encoded.into_owned();
    bytes.truncate(size);
    write_padded(w, &bytes, size)
}

/// Write an ASCII string, padded/truncated.
fn write_fixed_ascii<W: Write>(w: &mut W, s: &str, size: usize) -> std::io::Result<()> {
    let bytes = s.as_bytes();
    let len = bytes.len().min(size);
    w.write_all(&bytes[..len])?;
    if len < size {
        let padding = vec![0u8; size - len];
        w.write_all(&padding)?;
    }
    Ok(())
}

/// Write an ArrayRecord48.
fn write_arr48<W: Write>(w: &mut W, rec: &ArrayRecord48) -> std::io::Result<()> {
    for &f in &rec.floats { w.write_f32::<LittleEndian>(f)?; }
    for &i in &rec.ints { w.write_u32::<LittleEndian>(i)?; }
    w.write_f32::<LittleEndian>(rec.trailing)
}

/// Write a Vec of ArrayRecord48 with count prefix.
fn write_arr48_vec<W: Write>(w: &mut W, vec: &[ArrayRecord48]) -> std::io::Result<()> {
    w.write_u32::<LittleEndian>(vec.len() as u32)?;
    for rec in vec {
        write_arr48(w, rec)?;
    }
    Ok(())
}

/// Write an ArrayRecord72 (data_17 records).
#[allow(dead_code)]
fn write_arr72<W: Write>(w: &mut W, rec: &ArrayRecord72) -> std::io::Result<()> {
    for &i in &rec.ints0 { w.write_u32::<LittleEndian>(i)?; }
    w.write_f32::<LittleEndian>(rec.f0)?;
    w.write_u32::<LittleEndian>(rec.int1)?;
    for &f in &rec.floats { w.write_f32::<LittleEndian>(f)?; }
    for &i in &rec.ints1 { w.write_u32::<LittleEndian>(i)?; }
    Ok(())
}

/// Write a complete .eff file.
pub fn write_eff<W: Write>(eff: &EffFile, mut w: W) -> std::io::Result<()> {
    let ver_raw = eff.version.as_raw();

    w.write_u32::<LittleEndian>(ver_raw)?;
    w.write_u32::<LittleEndian>(eff.unk1)?;

    if ver_raw >= 0x6D {
        let (encoded, _, _) = SHIFT_JIS.encode(&eff.effect_name);
        let bytes = encoded.into_owned();
        w.write_u32::<LittleEndian>(bytes.len() as u32)?;
        w.write_all(&bytes)?;
    } else if eff.effect_name_raw.len() == 16 && cp932_decode(&eff.effect_name_raw) == eff.effect_name {
        w.write_all(&eff.effect_name_raw)?;
    } else {
        write_fixed_cp932(&mut w, &eff.effect_name, 16)?;
    }

    // Textures
    w.write_u32::<LittleEndian>(eff.textures.len() as u32)?;
    for tex in &eff.textures {
        write_fixed_ascii(&mut w, tex, 20)?;
    }

    // v40
    w.write_u32::<LittleEndian>(eff.v40_list.len() as u32)?;
    for v in &eff.v40_list {
        write_fixed_ascii(&mut w, v, 36)?;
    }

    // Segments
    w.write_u32::<LittleEndian>(eff.segments.len() as u32)?;
    for seg in &eff.segments {
        write_segment(&mut w, seg, ver_raw)?;
    }

    // Trailing padding/footer preserved from the original for a byte-perfect round-trip.
    w.write_all(&eff.trailing)?;

    Ok(())
}

fn write_segment<W: Write>(w: &mut W, seg: &Segment, ver_raw: u32) -> std::io::Result<()> {
    // Prefer the original raw bytes when the name is unchanged (some names don't
    // survive a cp932 decode/re-encode round-trip); otherwise re-encode.
    if seg.name_raw.len() == 16 && cp932_decode(&seg.name_raw) == seg.name {
        w.write_all(&seg.name_raw)?;
    } else {
        write_fixed_cp932(w, &seg.name, 16)?;
    }
    if seg.fn1_raw.len() == 16 && ascii_decode(&seg.fn1_raw) == seg.fn_name_1 {
        w.write_all(&seg.fn1_raw)?;
    } else {
        write_fixed_ascii(w, &seg.fn_name_1, 16)?;
    }
    if seg.fn2_raw.len() == 16 && ascii_decode(&seg.fn2_raw) == seg.fn_name_2 {
        w.write_all(&seg.fn2_raw)?;
    } else {
        write_fixed_ascii(w, &seg.fn_name_2, 16)?;
    }

    if ver_raw >= 0x6A {
        let mut buf = [0u8; 16];
        buf[4..8].copy_from_slice(&seg.struct_flags.to_le_bytes());
        w.write_all(&buf)?;
    }

    // data_02
    for &v in &seg.data_02 { w.write_u32::<LittleEndian>(v)?; }

    // data_03
    if let Some(ref d) = seg.data_03 {
        w.write_f32::<LittleEndian>(d[0])?;
        w.write_f32::<LittleEndian>(d[1])?;
    }

    // data_04
    for &v in &seg.data_04 { w.write_f32::<LittleEndian>(v)?; }

    // data_05 (CS1)
    if let Some(ref d) = seg.data_05 {
        w.write_f32::<LittleEndian>(d[0])?;
        w.write_f32::<LittleEndian>(d[1])?;
        w.write_f32::<LittleEndian>(d[2])?;
    }

    // data_06
    for &v in &seg.data_06 { w.write_f32::<LittleEndian>(v)?; }

    // data_07
    if let Some(ref d) = seg.data_07 {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    // data_08
    for &v in &seg.data_08 { w.write_f32::<LittleEndian>(v)?; }

    // Arrays 09-0E
    write_arr48_vec(w, &seg.data_09)?;
    write_arr48_vec(w, &seg.data_0a)?;
    write_arr48_vec(w, &seg.data_0b)?;
    write_arr48_vec(w, &seg.data_0c)?;
    write_arr48_vec(w, &seg.data_0d)?;
    write_arr48_vec(w, &seg.data_0e)?;

    // Conditional 0F-12
    if seg.struct_flags & 0x0100_0000 != 0 { write_arr48_vec(w, &seg.data_0f)?; }
    if seg.struct_flags & 0x0400_0000 != 0 { write_arr48_vec(w, &seg.data_10)?; }
    if seg.struct_flags & 0x0800_0000 != 0 { write_arr48_vec(w, &seg.data_11)?; }
    if seg.struct_flags & 0x2000_0000 != 0 { write_arr48_vec(w, &seg.data_12)?; }

    // data_13 nested
    if seg.struct_flags & 0x0200_0000 != 0 {
        w.write_u32::<LittleEndian>(seg.data_13.len() as u32)?;
        for inner in &seg.data_13 {
            w.write_u32::<LittleEndian>(inner.len() as u32)?;
            for rec in inner {
                write_arr48(w, rec)?;
            }
        }
    }

    // data_14
    write_arr48_vec(w, &seg.data_14)?;

    // data_15 (ver <= 4 / Reverie/CS1 PC)
    if let Some(ref d) = seg.data_15 {
        w.write_f32::<LittleEndian>(d[0])?;
        w.write_f32::<LittleEndian>(d[1])?;
    }

    // Note: for ver <= 4, struct_flags is overridden to 3 in the parser.
    // We use the stored struct_flags as-is.

    // data_16 (flag 0x002): 16 f32s
    if let Some(ref d) = seg.data_16 {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    // data_17 (flag 0x001): Vec<ArrayRecord72> or 16 raw bytes (CS1)
    if !seg.data_17.is_empty() {
        w.write_u32::<LittleEndian>(seg.data_17.len() as u32)?;
        for rec in &seg.data_17 {
            write_arr72(w, rec)?;
        }
    } else if ver_raw < 0x6B && seg.struct_flags & 0x001 != 0 {
        // CS1: unparsed 16-byte block, written back verbatim (else 16 zeros).
        if seg.data_17_cs1_raw.len() == 16 {
            w.write_all(&seg.data_17_cs1_raw)?;
        } else {
            w.write_all(&[0u8; 16])?;
        }
    }

    // data_18 (flag 0x010): 4 u32s
    if let Some(ref d) = seg.data_18 {
        for &v in d { w.write_u32::<LittleEndian>(v)?; }
    }

    // data_19 (flag 0x004): 8 u32s
    if let Some(ref d) = seg.data_19 {
        for &v in d { w.write_u32::<LittleEndian>(v)?; }
    }

    // data_1a (flag 0x008): 24 f32s
    if let Some(ref d) = seg.data_1a {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    // data_1b (ver >= 0x6A): Vec<[u32; 3]>
    if ver_raw >= 0x6A {
        w.write_u32::<LittleEndian>(seg.data_1b.len() as u32)?;
        for arr in &seg.data_1b {
            for &v in arr { w.write_u32::<LittleEndian>(v)?; }
        }
    }

    // data_1c (flag 0x020): 6 f32s
    if let Some(ref d) = seg.data_1c {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    // data_1d (flag 0x040): 4 f32s
    if let Some(ref d) = seg.data_1d {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    // data_1e (flag 0x080): 8 u32s
    if let Some(ref d) = seg.data_1e {
        for &v in d { w.write_u32::<LittleEndian>(v)?; }
    }

    // data_1f (flag 0x100): 2 u32s
    if let Some(ref d) = seg.data_1f {
        for &v in d { w.write_u32::<LittleEndian>(v)?; }
    }

    // data_20 (flag 0x200): 13 f32s
    if let Some(ref d) = seg.data_20 {
        for &v in d { w.write_f32::<LittleEndian>(v)?; }
    }

    Ok(())
}

/// Serialize to bytes (for in-memory use).
pub fn write_eff_to_bytes(eff: &EffFile) -> std::io::Result<Vec<u8>> {
    let mut buf = Vec::new();
    write_eff(eff, &mut Cursor::new(&mut buf))?;
    Ok(buf)
}

/// Write a single segment to bytes (for copy-paste into Cheat Engine).
pub fn write_segment_to_bytes(seg: &Segment, ver_raw: u32) -> std::io::Result<Vec<u8>> {
    let mut buf = Vec::new();
    write_segment(&mut Cursor::new(&mut buf), seg, ver_raw)?;
    Ok(buf)
}
