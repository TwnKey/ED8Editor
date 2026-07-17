//! PhyreEngine D3D11 .dds.phyre texture parser.
//!
//! Handles the D3D11 texture format used in CS1/CS2 PC.
//! The .dds.phyre format wraps raw DDS pixel data (without DDS header)
//! in a PhyreEngine serialization bytecode.
//!
//! For D3D11 textures, the data is stored linearly (no swizzle) and
//! the pixel format is ARGB8 (BGRA byte order in memory).


/// Parsed PhyreEngine D3D11 texture.
#[derive(Debug, Clone)]
pub struct PhyreTexture {
    pub width: u32,
    pub height: u32,
    pub format: PhyreTexFormat,
    pub mip_levels: u32,
    /// RGBA8 pixel data (only top mip level for preview)
    pub rgba_data: Vec<u8>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PhyreTexFormat {
    ARGB8,   // BGRA in D3D11 memory (B,G,R,A) → swap R↔B
    RGBA8,   // RGBA in D3D11 memory (R,G,B,A) → no swap
    DXT1,    // BC1
    DXT5,    // BC3
}

/// Decode a D3D11 `.dds.phyre` texture — fully deterministic: dimensions are read
/// straight from the serialized object, mip count from `floor(log2(max))+1`, and the
/// pixel region is the file tail. No factoring/guessing of any kind.
pub fn parse_phyre_texture(data: &[u8]) -> Option<PhyreTexture> {
    if data.len() < 128 { return None; }
    if &data[0..4] != b"RYHP" || data[4] != b'T' { return None; } // "PHYRT"
    if &data[12..16] != b"11XD" { return None; }                  // "DX11"

    let format = detect_format(data)?;
    let (width, height) = texture_dimensions(data)?;
    if width == 0 || height == 0 || width > 8192 || height > 8192 { return None; }

    let bpp = format_bpp(format);
    let mip_count = mip_levels_for(width, height);
    let top_mip_size = (width as usize * height as usize * bpp) / 8;

    // Pixel region = the last (Σ mip sizes) bytes of the file.
    let mut total_pixel_size = 0usize;
    let (mut mw, mut mh) = (width as usize, height as usize);
    for _ in 0..mip_count {
        total_pixel_size += mip_byte_size(format, mw, mh);
        mw = (mw / 2).max(1);
        mh = (mh / 2).max(1);
    }
    if total_pixel_size >= data.len() { return None; }
    let pixel_data = &data[data.len() - total_pixel_size..];
    if pixel_data.len() < top_mip_size { return None; }

    let rgba = decode_pixels(format, &pixel_data[..top_mip_size], width, height)?;
    let rgba_data = flip_vertical(&rgba, width, height); // D3D11 stores Y-inverted
    Some(PhyreTexture { width, height, format, mip_levels: mip_count, rgba_data })
}

/// The pixel format, read from the format-name marker the serializer wrote (there is
/// exactly one — this is a lookup, not a guess).
fn detect_format(data: &[u8]) -> Option<PhyreTexFormat> {
    if data.windows(5).any(|w| w == b"ARGB8") { Some(PhyreTexFormat::ARGB8) }
    else if data.windows(5).any(|w| w == b"RGBA8") { Some(PhyreTexFormat::RGBA8) }
    else if data.windows(4).any(|w| w == b"DXT5") { Some(PhyreTexFormat::DXT5) }
    else if data.windows(4).any(|w| w == b"DXT1") { Some(PhyreTexFormat::DXT1) }
    else { None }
}

/// Width/height read directly from the PTexture2D object at deterministic offsets
/// (`obj_start = 84 + packedNamespaceSize + instanceListCount*36`; W @+96, H @+100).
fn texture_dimensions(data: &[u8]) -> Option<(u32, u32)> {
    let rd = |o: usize| -> Option<u32> {
        data.get(o..o + 4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    };
    let ns_size = rd(8)? as usize;
    let ilist_count = rd(16)? as usize;
    let obj = 84 + ns_size + ilist_count * 36;
    Some((rd(obj + 96)?, rd(obj + 100)?))
}

/// Flip RGBA8 image vertically (D3D11 V axis goes up, screen goes down).
fn flip_vertical(data: &[u8], width: u32, height: u32) -> Vec<u8> {
    let w = width as usize;
    let h = height as usize;
    let row_bytes = w * 4;
    let mut out = data.to_vec();
    for y in 0..h / 2 {
        let top = y * row_bytes;
        let bottom = (h - 1 - y) * row_bytes;
        out.copy_within(top..top + row_bytes, bottom);
        // Can't use copy_within for both simultaneously, do swap manually
    }
    // Simple approach: create new vec
    let mut out = vec![0u8; data.len()];
    for y in 0..h {
        let src_row = y * row_bytes;
        let dst_row = (h - 1 - y) * row_bytes;
        out[dst_row..dst_row + row_bytes].copy_from_slice(&data[src_row..src_row + row_bytes]);
    }
    out
}

fn format_bpp(fmt: PhyreTexFormat) -> usize {
    match fmt {
        PhyreTexFormat::ARGB8 => 32,
        PhyreTexFormat::RGBA8 => 32,
        PhyreTexFormat::DXT1 => 4,
        PhyreTexFormat::DXT5 => 8,
    }
}

/// Convert ARGB8 (BGRA little-endian in D3D11) to RGBA8.
fn decode_argb8_to_rgba(data: &[u8], width: u32, height: u32) -> Option<Vec<u8>> {
    let expected = (width as usize) * (height as usize) * 4;
    if data.len() < expected { return None; }
    let mut rgba = vec![0u8; expected];
    for i in (0..expected).step_by(4) {
        rgba[i] = data[i + 2];     // R
        rgba[i + 1] = data[i + 1]; // G
        rgba[i + 2] = data[i];     // B
        rgba[i + 3] = data[i + 3]; // A
    }
    Some(rgba)
}

fn decode_pixels(format: PhyreTexFormat, data: &[u8], width: u32, height: u32) -> Option<Vec<u8>> {
    match format {
        PhyreTexFormat::ARGB8 => decode_argb8_to_rgba(data, width, height),
        PhyreTexFormat::RGBA8 => {
            let expected = (width as usize) * (height as usize) * 4;
            if data.len() >= expected { Some(data[..expected].to_vec()) } else { None }
        }
        PhyreTexFormat::DXT1 | PhyreTexFormat::DXT5 => {
            decode_dds_via_image(data, width, height, format)
        }
    }
}

fn decode_dds_via_image(data: &[u8], width: u32, height: u32, format: PhyreTexFormat) -> Option<Vec<u8>> {
    let fourcc = match format {
        PhyreTexFormat::DXT1 => b"DXT1",
        PhyreTexFormat::DXT5 => b"DXT5",
        _ => return None,
    };
    let header = build_dds_header(width, height, fourcc);
    let mut dds_data = Vec::with_capacity(128 + data.len());
    dds_data.extend_from_slice(&header);
    dds_data.extend_from_slice(data);
    let img = image::load_from_memory_with_format(&dds_data, image::ImageFormat::Dds).ok()?;
    let rgba = img.to_rgba8();
    Some(rgba.into_raw())
}

fn build_dds_header(width: u32, height: u32, fourcc: &[u8; 4]) -> Vec<u8> {
    let mut h = vec![0u8; 128];
    h[0..4].copy_from_slice(b"DDS ");
    h[4..8].copy_from_slice(&124u32.to_le_bytes());
    h[8..12].copy_from_slice(&0x00081007u32.to_le_bytes()); // CAPS|HEIGHT|WIDTH|PIXELFORMAT
    h[12..16].copy_from_slice(&height.to_le_bytes());
    h[16..20].copy_from_slice(&width.to_le_bytes());
    h[76..80].copy_from_slice(&32u32.to_le_bytes()); // pfSize
    h[80..84].copy_from_slice(&0x00000004u32.to_le_bytes()); // pfFlags: FOURCC
    h[84..88].copy_from_slice(fourcc);
    h[108..112].copy_from_slice(&0x00001000u32.to_le_bytes()); // caps: TEXTURE
    h
}

// ───────────────────────── texture pixel encoding ─────────────────────────

fn mip_byte_size(format: PhyreTexFormat, w: usize, h: usize) -> usize {
    match format {
        PhyreTexFormat::ARGB8 | PhyreTexFormat::RGBA8 => w * h * 4,
        PhyreTexFormat::DXT1 => ((w + 3) / 4) * ((h + 3) / 4) * 8,
        PhyreTexFormat::DXT5 => ((w + 3) / 4) * ((h + 3) / 4) * 16,
    }
}

/// Encode one RGBA8 mip level into `format`, appending to `out`.
fn encode_mip(format: PhyreTexFormat, rgba: &[u8], w: usize, h: usize, out: &mut Vec<u8>) {
    match format {
        PhyreTexFormat::ARGB8 => {
            // BGRA byte order in D3D11 memory.
            for px in rgba.chunks_exact(4) {
                out.push(px[2]); out.push(px[1]); out.push(px[0]); out.push(px[3]);
            }
        }
        PhyreTexFormat::RGBA8 => out.extend_from_slice(rgba),
        PhyreTexFormat::DXT1 => encode_dxt(rgba, w, h, false, out),
        PhyreTexFormat::DXT5 => encode_dxt(rgba, w, h, true, out),
    }
}

/// Basic BC1 (DXT1) / BC3 (DXT5) block encoder. Per 4×4 block: bounding-box RGB
/// endpoints with 4 interpolated colors; for DXT5, a BC4 alpha block with 8 levels.
/// Quality is adequate for effect textures.
fn encode_dxt(rgba: &[u8], w: usize, h: usize, dxt5: bool, out: &mut Vec<u8>) {
    let bw = (w + 3) / 4;
    let bh = (h + 3) / 4;
    for by in 0..bh {
        for bx in 0..bw {
            // Gather the (up to) 16 texels of this block, clamping at edges.
            let mut block = [[0u8; 4]; 16];
            for py in 0..4 {
                for px in 0..4 {
                    let sx = (bx * 4 + px).min(w.saturating_sub(1));
                    let sy = (by * 4 + py).min(h.saturating_sub(1));
                    let i = (sy * w + sx) * 4;
                    block[py * 4 + px] = [rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]];
                }
            }
            if dxt5 {
                encode_alpha_block(&block, out);
            }
            encode_color_block(&block, dxt5, out);
        }
    }
}

fn encode_alpha_block(block: &[[u8; 4]; 16], out: &mut Vec<u8>) {
    let mut amin = 255u8;
    let mut amax = 0u8;
    for t in block { amin = amin.min(t[3]); amax = amax.max(t[3]); }
    out.push(amax);
    out.push(amin);
    // 8-level interpolation (alpha0 > alpha1 mode).
    let mut bits: u64 = 0;
    let range = (amax as i32 - amin as i32).max(1);
    for (i, t) in block.iter().enumerate() {
        // idx: 0=amax,1=amin,2..8 interpolate amax→amin
        let f = ((amax as i32 - t[3] as i32) * 7 + range / 2) / range; // 0..7
        let idx = match f {
            0 => 0u64,
            7 => 1u64,
            n => (n + 1) as u64,
        };
        bits |= idx << (3 * i);
    }
    for k in 0..6 { out.push(((bits >> (8 * k)) & 0xFF) as u8); }
}

fn to_565(c: [u8; 4]) -> u16 {
    (((c[0] as u16) >> 3) << 11) | (((c[1] as u16) >> 2) << 5) | ((c[2] as u16) >> 3)
}

fn from_565(v: u16) -> [u8; 3] {
    let r = ((v >> 11) & 0x1F) as u32;
    let g = ((v >> 5) & 0x3F) as u32;
    let b = (v & 0x1F) as u32;
    [((r * 255 + 15) / 31) as u8, ((g * 255 + 31) / 63) as u8, ((b * 255 + 15) / 31) as u8]
}

fn encode_color_block(block: &[[u8; 4]; 16], dxt5: bool, out: &mut Vec<u8>) {
    // Bounding-box endpoints in RGB.
    let mut lo = [255u8; 3];
    let mut hi = [0u8; 3];
    for t in block {
        for c in 0..3 {
            lo[c] = lo[c].min(t[c]);
            hi[c] = hi[c].max(t[c]);
        }
    }
    let mut c0 = to_565([hi[0], hi[1], hi[2], 255]);
    let mut c1 = to_565([lo[0], lo[1], lo[2], 255]);
    // For DXT1 the 4-color mode requires c0 > c1; ensure it (DXT5 always 4-color).
    if c0 == c1 {
        // Flat block: still emit; indices all 0.
        out.extend_from_slice(&c0.to_le_bytes());
        out.extend_from_slice(&c1.to_le_bytes());
        out.extend_from_slice(&[0, 0, 0, 0]);
        return;
    }
    let swap = c0 < c1;
    if swap && !dxt5 {
        std::mem::swap(&mut c0, &mut c1);
    }
    let e0 = from_565(c0);
    let e1 = from_565(c1);
    // 4 palette entries: e0, e1, 2/3·e0+1/3·e1, 1/3·e0+2/3·e1
    let mut pal = [[0u8; 3]; 4];
    pal[0] = e0;
    pal[1] = e1;
    for c in 0..3 {
        pal[2][c] = ((2 * e0[c] as u16 + e1[c] as u16) / 3) as u8;
        pal[3][c] = ((e0[c] as u16 + 2 * e1[c] as u16) / 3) as u8;
    }
    let mut idx_bits: u32 = 0;
    for (i, t) in block.iter().enumerate() {
        let mut best = 0usize;
        let mut best_d = i32::MAX;
        for (p, pc) in pal.iter().enumerate() {
            let dr = t[0] as i32 - pc[0] as i32;
            let dg = t[1] as i32 - pc[1] as i32;
            let db = t[2] as i32 - pc[2] as i32;
            let d = dr * dr + dg * dg + db * db;
            if d < best_d { best_d = d; best = p; }
        }
        idx_bits |= (best as u32) << (2 * i);
    }
    out.extend_from_slice(&c0.to_le_bytes());
    out.extend_from_slice(&c1.to_le_bytes());
    out.extend_from_slice(&idx_bits.to_le_bytes());
}

// ───────────────────── STANDALONE PhyreEngine texture writer ─────────────────────
//
// A `.dds.phyre` texture cluster is a fixed PhyreEngine serialization: a constant
// type-schema (packed namespace + class descriptors + fixup tables) that is IDENTICAL
// for every texture of a given pixel format, followed by a small object whose only
// per-texture fields are the width, height, asset name and the pixel buffer. We ship
// one constant skeleton per format (bytes 0..pixel_start of a real texture, schema
// only — no pixels) and generate the variable fields for ANY dimensions. This is a
// true from-scratch encoder: no runtime template file, arbitrary sizes.
//
// Field map (reverse-engineered against the PhyreEngine SDK + real files):
//   @80                = m_maxTextureBufferSize = top-mip byte size
//   obj_start+96       = width, obj_start+100 = height
//   obj_start+40       = inline asset path "effects/images/<name>.dds" (56-byte buffer)
//   obj_start          = 84 + packedNamespaceSize(@8) + instanceListCount(@16)*36

const SKEL_ARGB8: &[u8] = include_bytes!("phyre_skel/argb8.bin");
const SKEL_RGBA8: &[u8] = include_bytes!("phyre_skel/rgba8.bin");
const SKEL_DXT5:  &[u8] = include_bytes!("phyre_skel/dxt5.bin");

fn skeleton_for(format: PhyreTexFormat) -> &'static [u8] {
    match format {
        PhyreTexFormat::ARGB8 => SKEL_ARGB8,
        PhyreTexFormat::RGBA8 => SKEL_RGBA8,
        PhyreTexFormat::DXT5  => SKEL_DXT5,
        PhyreTexFormat::DXT1  => SKEL_DXT5, // no DXT1 skeleton shipped; caller avoids DXT1
    }
}

/// Number of mip levels for a texture: full chain down to 1×1 (`floor(log2(max))+1`).
fn mip_levels_for(w: u32, h: u32) -> u32 {
    (32 - w.max(h).max(1).leading_zeros()).max(1)
}

fn wr_u32(buf: &mut [u8], off: usize, v: u32) {
    buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
}

/// Build a complete `.dds.phyre` texture from scratch (schema skeleton + generated
/// object fields + full mip chain). `internal_name` is the asset base (e.g. `eftex068`).
/// `full_mip_pixels` must already be the encoded mip chain in `format` (top mip first).
pub fn build_phyre_texture(
    internal_name: &str,
    width: u32,
    height: u32,
    format: PhyreTexFormat,
    full_mip_pixels: &[u8],
) -> Option<Vec<u8>> {
    let skel = skeleton_for(format);
    if skel.len() < 96 { return None; }
    let mut out = skel.to_vec();

    // Object start from the skeleton's own header (schema-size independent).
    let ns_size = u32::from_le_bytes([out[8], out[9], out[10], out[11]]) as usize;
    let ilist_count = u32::from_le_bytes([out[16], out[17], out[18], out[19]]) as usize;
    let obj_start = 84 + ns_size + ilist_count * 36;
    if obj_start + 104 > out.len() { return None; }

    // Top-mip byte size → m_maxTextureBufferSize @80.
    let top_mip = mip_byte_size(format, width as usize, height as usize) as u32;
    wr_u32(&mut out, 80, top_mip);

    // Dimensions.
    wr_u32(&mut out, obj_start + 96, width);
    wr_u32(&mut out, obj_start + 100, height);

    // Mip-chain depth = floor(log2(max(w,h))) (= mip_levels-1), stored twice in the
    // object (obj+80 & obj+84). The game uses these to interpret the mip/pixel layout —
    // leaving the skeleton's value for a different size gives interlaced garbage in-game.
    let mip_depth = mip_levels_for(width, height).saturating_sub(1);
    wr_u32(&mut out, obj_start + 80, mip_depth);
    wr_u32(&mut out, obj_start + 84, mip_depth);

    // Inline asset path in its fixed 40-byte buffer (obj+40 .. obj+80), zero-padded.
    let name_off = obj_start + 40;
    for b in &mut out[name_off..obj_start + 80] { *b = 0; }
    let path = format!("effects/images/{internal_name}.dds");
    let pb = path.as_bytes();
    let n = pb.len().min(39);
    out[name_off..name_off + n].copy_from_slice(&pb[..n]);

    // Append the pixel buffer (full mip chain).
    out.extend_from_slice(full_mip_pixels);
    Some(out)
}

/// Encode an RGBA8 image (top-left origin) into a standalone `.dds.phyre` of `format`.
/// Generates the full mip chain (D3D11 Y-flip applied, matching the parser).
pub fn encode_phyre_texture(
    internal_name: &str,
    rgba: &[u8],
    width: u32,
    height: u32,
    format: PhyreTexFormat,
) -> Option<Vec<u8>> {
    let src = image::RgbaImage::from_raw(width, height, rgba.to_vec())?;
    let top = image::imageops::flip_vertical(&src); // D3D11 stores Y-inverted

    let mut pixels = Vec::new();
    let levels = mip_levels_for(width, height);
    let (mut mw, mut mh) = (width, height);
    let mut cur = top;
    for level in 0..levels {
        if level > 0 {
            cur = image::imageops::resize(&cur, mw, mh, image::imageops::FilterType::Triangle);
        }
        encode_mip(format, cur.as_raw(), mw as usize, mh as usize, &mut pixels);
        mw = (mw / 2).max(1);
        mh = (mh / 2).max(1);
    }
    build_phyre_texture(internal_name, width, height, format, &pixels)
}


#[cfg(test)]
mod standalone_tests {
    use super::*;
    use crate::core::pkg::PkgArchive;

    fn real(name: &str) -> Option<Vec<u8>> {
        let p = std::path::Path::new(r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11").join(name);
        let d = std::fs::read(p).ok()?;
        let pk = PkgArchive::parse(&d)?;
        let t = pk.find_texture()?.name.clone();
        pk.extract(&t)
    }

    // The skeleton + patched fields + the file's ORIGINAL pixels must reproduce the
    // real texture byte-for-byte. This proves the skeleton boundary and every patched
    // field (buffer size, dims, name) are exactly right.
    #[test]
    fn reproduces_real_texture_byte_exact() {
        for (name, internal, w, h, fmt) in [
            ("I_EFTEX000.pkg", "eftex000", 512u32, 512u32, PhyreTexFormat::ARGB8),
            ("I_EFTEX091.pkg", "eftex091", 256, 256, PhyreTexFormat::RGBA8),
            ("I_EFTEX992.pkg", "eftex992", 512, 128, PhyreTexFormat::DXT5),
        ] {
            let Some(orig) = real(name) else { eprintln!("skip {name}"); continue; };
            let skel = skeleton_for(fmt);
            let pixels = &orig[skel.len()..];
            let rebuilt = build_phyre_texture(internal, w, h, fmt, pixels).expect("build");
            assert_eq!(rebuilt.len(), orig.len(), "{name}: length");
            assert!(rebuilt == orig, "{name}: not byte-exact");
            println!("{name}: byte-exact reproduction OK ({} bytes)", orig.len());
        }
    }

    // Generating at an ARBITRARY dimension must produce a phyre our decoder (which
    // models the game) reads back at those exact dimensions.
    #[test]
    fn arbitrary_dims_roundtrip() {
        for (w, h, fmt) in [(64u32, 64u32, PhyreTexFormat::RGBA8), (100, 40, PhyreTexFormat::ARGB8), (256, 64, PhyreTexFormat::DXT5)] {
            let mut rgba = vec![0u8; (w * h * 4) as usize];
            for y in 0..h { for x in 0..w {
                let i = ((y * w + x) * 4) as usize;
                rgba[i] = (x * 255 / w) as u8; rgba[i+1] = (y * 255 / h) as u8; rgba[i+2] = 200; rgba[i+3] = 255;
            }}
            let phyre = encode_phyre_texture("modtex", &rgba, w, h, fmt).expect("encode");
            let dec = parse_phyre_texture(&phyre).expect("decode");
            assert_eq!((dec.width, dec.height), (w, h), "dims for {:?}", fmt);
            println!("{}x{} {:?}: standalone encode->decode OK ({} phyre bytes)", w, h, fmt, phyre.len());
        }
    }
}
