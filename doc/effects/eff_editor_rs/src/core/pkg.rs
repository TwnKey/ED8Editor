//! .pkg archive parser + NISLZSS decompression for Trails of Cold Steel.
//!
//! PKG format:
//!   magic: u32 (4 bytes)
//!   count: u32 (4 bytes) — number of entries
//!   entries: count × 80 bytes (name[64] + uncompressed[4] + compressed[4] + offset[4] + flags[4])
//!   data: raw file data (may be NISLZSS compressed if flags & 1)

use std::io::{Read, Cursor};

/// A single entry in a .pkg archive.
#[derive(Debug, Clone)]
pub struct PkgEntry {
    pub name: String,
    pub uncompressed_size: u32,
    pub compressed_size: u32,
    pub offset: u32,
    pub flags: u32,
}

/// Parsed .pkg archive.
#[derive(Debug, Clone)]
pub struct PkgArchive {
    pub magic: u32,
    pub entries: Vec<PkgEntry>,
    pub raw_data: Vec<u8>,
}

impl PkgArchive {
    /// Parse a .pkg from raw bytes.
    pub fn parse(data: &[u8]) -> Option<Self> {
        if data.len() < 8 { return None; }
        let magic = u32::from_le_bytes([data[0], data[1], data[2], data[3]]);
        let count = u32::from_le_bytes([data[4], data[5], data[6], data[7]]) as usize;
        if count > 1000 { return None; } // sanity check

        let mut entries = Vec::with_capacity(count);
        for i in 0..count {
            let off = 8 + i * 80;
            if off + 80 > data.len() { return None; }

            let name_bytes = &data[off..off + 64];
            let name_end = name_bytes.iter().position(|&b| b == 0).unwrap_or(64);
            let name = String::from_utf8_lossy(&name_bytes[..name_end]).to_string();

            let get_u32 = |pos: usize| -> u32 {
                u32::from_le_bytes([data[pos], data[pos+1], data[pos+2], data[pos+3]])
            };

            entries.push(PkgEntry {
                name,
                uncompressed_size: get_u32(off + 64),
                compressed_size: get_u32(off + 68),
                offset: get_u32(off + 72),
                flags: get_u32(off + 76),
            });
        }

        Some(PkgArchive {
            magic,
            entries,
            raw_data: data.to_vec(),
        })
    }

    /// Extract and decompress an entry by name.
    pub fn extract(&self, name: &str) -> Option<Vec<u8>> {
        let entry = self.entries.iter().find(|e| e.name == name)?;
        let start = entry.offset as usize;
        let compressed_len = entry.compressed_size as usize;

        if start + compressed_len > self.raw_data.len() {
            return None;
        }

        let raw = &self.raw_data[start..start + compressed_len];

        if entry.flags & 1 != 0 && entry.compressed_size < entry.uncompressed_size {
            // NISLZSS compressed
            nislzss_decompress(raw, entry.uncompressed_size as usize)
        } else {
            // Uncompressed
            Some(raw.to_vec())
        }
    }

    /// Find the first texture entry (.dds.phyre or .dds).
    pub fn find_texture(&self) -> Option<&PkgEntry> {
        self.entries.iter().find(|e|
            e.name.ends_with(".dds.phyre") || e.name.ends_with(".dds")
        )
    }
}

/// Build a .pkg archive from a list of (entry_name, uncompressed_data) pairs.
///
/// Each entry is NISLZSS-compressed (flags = 1) to match the game's own format (all
/// real texture pkgs are compressed). If compression doesn't shrink an entry, it is
/// stored raw (flags = 0) — the loader supports both via the per-entry flags.
///
/// `magic` should match the game's value (copy it from an existing pkg you parsed).
/// `compress`: NISLZSS-compress entries (flags=1) when it shrinks them. Pass `false`
/// to store everything raw (flags=0) — safest, since it removes our compressor from
/// the trust chain (the game's LZSS decoder must match our stream dialect exactly).
pub fn build_pkg(magic: u32, entries: &[(String, Vec<u8>)], compress: bool) -> Vec<u8> {
    let count = entries.len();
    let header_size = 8 + count * 80;

    struct Blob { name: String, uncompressed: u32, blob: Vec<u8>, flags: u32 }
    let blobs: Vec<Blob> = entries.iter().map(|(name, data)| {
        let compressed = if compress { nislzss_compress(data) } else { Vec::new() };
        if compress && compressed.len() < data.len() {
            Blob { name: name.clone(), uncompressed: data.len() as u32, blob: compressed, flags: 1 }
        } else {
            Blob { name: name.clone(), uncompressed: data.len() as u32, blob: data.clone(), flags: 0 }
        }
    }).collect();

    // Lay blobs out contiguously after the header table.
    let mut offset = header_size as u32;
    let mut offsets = Vec::with_capacity(count);
    for b in &blobs {
        offsets.push(offset);
        offset += b.blob.len() as u32;
    }
    let total = offset as usize;

    let mut out = vec![0u8; total];
    out[0..4].copy_from_slice(&magic.to_le_bytes());
    out[4..8].copy_from_slice(&(count as u32).to_le_bytes());

    for (i, b) in blobs.iter().enumerate() {
        let eoff = 8 + i * 80;
        // name[64], null-padded/truncated
        let nb = b.name.as_bytes();
        let nlen = nb.len().min(63);
        out[eoff..eoff + nlen].copy_from_slice(&nb[..nlen]);
        out[eoff + 64..eoff + 68].copy_from_slice(&b.uncompressed.to_le_bytes());
        out[eoff + 68..eoff + 72].copy_from_slice(&(b.blob.len() as u32).to_le_bytes());
        out[eoff + 72..eoff + 76].copy_from_slice(&offsets[i].to_le_bytes());
        out[eoff + 76..eoff + 80].copy_from_slice(&b.flags.to_le_bytes());
        let start = offsets[i] as usize;
        out[start..start + b.blob.len()].copy_from_slice(&b.blob);
    }

    out
}

/// Compress with NISLZSS (PKG compression method 1) — a faithful port of the game
/// modding tool's authoritative `compress.d` (Sen-no-Kiseki-PKG-Sharp). The crucial
/// property is that matches are **non-overlapping** (length ≤ distance): the official
/// compressor never emits an overlapping copy, so the game's decompressor is only ever
/// asked to copy a source region that lies fully before the current output position.
/// (Our earlier hand-rolled compressor emitted overlapping runs — valid for our own
/// decoder but decoded to garbage in-game.)
///
/// Tokens: literal byte (≠ escape); `escape escape` = a literal escape byte;
/// `escape offset length` = copy `length` bytes from `distance = offset` back
/// (offset is `distance`, +1 when `distance ≥ escape`). distance/length ∈ [1,254],
/// length ≥ 4 to be worth a block. Escape = least-frequent byte.
/// Header: [uncompressed_size u32][total_compressed_size u32][escape u32].
pub fn nislzss_compress(data: &[u8]) -> Vec<u8> {
    const MAX_LEN: usize = 254;
    const SEARCH: usize = 254; // search-buffer size = max distance

    let n = data.len();
    let mut freq = [0u32; 256];
    for &b in data { freq[b as usize] += 1; }
    let escape = (0..256).min_by_key(|&i| freq[i]).unwrap() as u8;

    let mut stream: Vec<u8> = Vec::with_capacity(n);
    let mut i = 0usize;
    while i < n {
        // Longest NON-OVERLAPPING match (mirrors lz77GetLongestMatchNoOverlap):
        // extend only while the source read position stays strictly before `i`.
        let mut best_len = 0usize;
        let mut best_off = 0usize;
        let min_p = i.saturating_sub(SEARCH);
        let mut p = i;
        while p > min_p {
            p -= 1;
            if data[p] != data[i] { continue; }
            let mut l = 1usize;
            while p + l < i                      // no overlap with current position
                && i + l < n
                && l < MAX_LEN
                && data[p + l] == data[i + l]
            {
                l += 1;
            }
            if l > best_len {
                best_len = l;
                best_off = i - p; // distance
            }
        }

        if best_len < 4 {
            let v = data[i];
            if v == escape { stream.push(escape); stream.push(escape); } else { stream.push(v); }
            i += 1;
        } else {
            let off = if best_off >= escape as usize { best_off + 1 } else { best_off };
            stream.push(escape);
            stream.push(off as u8);
            stream.push(best_len as u8);
            i += best_len;
        }
    }

    let mut out = Vec::with_capacity(12 + stream.len());
    out.extend_from_slice(&(n as u32).to_le_bytes());
    out.extend_from_slice(&((12 + stream.len()) as u32).to_le_bytes());
    out.extend_from_slice(&(escape as u32).to_le_bytes());
    out.extend_from_slice(&stream);
    out
}

/// Decompress NISLZSS (used in CS1/CS2 PC).
///
/// Header: 12 bytes (3 × u32: decompressed_size, compressed_size, escape_byte)
/// Followed by LZSS variant compressed data.
pub fn nislzss_decompress(data: &[u8], expected_size: usize) -> Option<Vec<u8>> {
    if data.len() < 12 { return None; } // 12-byte header, possibly-empty stream

    let mut cursor = Cursor::new(data);

    let mut hdr = [0u8; 4];
    cursor.read_exact(&mut hdr).ok()?;
    let _decompressed_size = u32::from_le_bytes(hdr) as usize;

    cursor.read_exact(&mut hdr).ok()?;
    let compressed_hdr_size = u32::from_le_bytes(hdr) as usize;

    cursor.read_exact(&mut hdr).ok()?;
    let escape_byte = hdr[0]; // only first byte matters

    // The compressed data length is compressed_hdr_size - 12 (header)
    let compressed_len = if compressed_hdr_size >= 12 {
        compressed_hdr_size - 12
    } else {
        return None;
    };

    let target_size = expected_size.max(_decompressed_size);
    let mut dst = vec![0u8; target_size];
    let mut dst_pos = 0usize;

    let fin = cursor.position() as usize + compressed_len;

    while (cursor.position() as usize) < fin && dst_pos < target_size {
        let mut b = [0u8; 1];
        if cursor.read_exact(&mut b).is_err() { break; }
        let byte = b[0];

        if byte == escape_byte {
            let mut b2 = [0u8; 1];
            if cursor.read_exact(&mut b2).is_err() { break; }
            let byte2 = b2[0];

            if byte2 != escape_byte {
                let mut adjusted = byte2;
                if adjusted >= escape_byte { adjusted = adjusted.wrapping_sub(1); }

                let mut b3 = [0u8; 1];
                if cursor.read_exact(&mut b3).is_err() { break; }
                let byte3 = b3[0] as usize;
                let backref = adjusted as usize;

                if backref < byte3 {
                    // Single byte repeat
                    for _ in 0..byte3 {
                        if dst_pos > 0 && dst_pos > backref {
                            dst[dst_pos] = dst[dst_pos - backref];
                        }
                        dst_pos += 1;
                        if dst_pos >= target_size { break; }
                    }
                } else if dst_pos >= backref {
                    // Multi-byte copy
                    let src_start = dst_pos - backref;
                    let copy_len = byte3;
                    for j in 0..copy_len {
                        if src_start + j < dst.len() && dst_pos < dst.len() {
                            dst[dst_pos] = dst[src_start + j];
                        }
                        dst_pos += 1;
                        if dst_pos >= target_size { break; }
                    }
                }
            } else {
                // Literal escape byte
                if dst_pos < dst.len() {
                    dst[dst_pos] = escape_byte;
                }
                dst_pos += 1;
            }
        } else {
            // Literal byte
            if dst_pos < dst.len() {
                dst[dst_pos] = byte;
            }
            dst_pos += 1;
        }
    }

    dst.truncate(target_size);
    Some(dst)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn test_parse_tex_pkg() {
        let path = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11\I_EFTEX000.pkg";
        if let Ok(data) = fs::read(path) {
            let pkg = PkgArchive::parse(&data).expect("parse pkg");
            assert!(pkg.entries.len() >= 1);
            println!("PKG: {} entries", pkg.entries.len());
            for e in &pkg.entries {
                println!("  {} (unc={}, cmp={})", e.name, e.uncompressed_size, e.compressed_size);
            }
            // Try extracting the texture
            if let Some(tex_entry) = pkg.find_texture() {
                println!("Extracting: {}", tex_entry.name);
                if let Some(dec) = pkg.extract(&tex_entry.name) {
                    println!("Decompressed: {} bytes", dec.len());
                    // Phyre header: the first u32 after "PHYRT" is a size/value
                    // Let's search for DDS from offset 256 onwards
                    if let Some(pos) = dec[256..].windows(4).position(|w| w == b"DDS ") {
                        let actual = 256 + pos;
                        println!("Found DDS at offset {}", actual);
                        let w = u32::from_le_bytes([dec[actual+16], dec[actual+17], dec[actual+18], dec[actual+19]]);
                        let h = u32::from_le_bytes([dec[actual+12], dec[actual+13], dec[actual+14], dec[actual+15]]);
                        println!("  DDS dimensions: {}x{}", w, h);
                    } else {
                        // Try brute force: look for DDS in ALL of the data
                        let found = dec.windows(4).position(|w| w == b"DDS ");
                        println!("DDS search result: {:?}", found);
                    }
                    // Also try: maybe the Phyre header is small and DDS is right after
                    // The value at offset 8 (0x08D7 = 2263) might be header size
                    // Check at offset 2263
                    if dec.len() > 2263 + 4 {
                        let test = &dec[2263..2263+4];
                        println!("Bytes at 2263: {:02X} {:02X} {:02X} {:02X} ({:?})", 
                            test[0], test[1], test[2], test[3],
                            std::str::from_utf8(test).unwrap_or("?"));
                    }
                    // Print bytes at various potential DDS offsets
                    for &off in &[0x80, 0xB4, 0x100, 0x200, 0x400, 0x800, 2263usize] {
                        if dec.len() > off + 4 {
                            let test = &dec[off..off+4];
                            if test == b"DDS " || test == b"DXT1" || test == b"DXT5" {
                                println!("Match at offset 0x{:X}: {:?}", off, std::str::from_utf8(test));
                            }
                        }
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod compress_tests {
    use super::*;

    /// The compressor must be the exact inverse of the decompressor.
    fn roundtrip(data: &[u8]) {
        let c = nislzss_compress(data);
        let d = nislzss_decompress(&c, data.len()).expect("decompress");
        assert_eq!(d, data, "roundtrip mismatch (len {})", data.len());
    }

    #[test]
    fn synthetic_roundtrips() {
        roundtrip(b"");
        roundtrip(b"a");
        roundtrip(b"abababababababab");
        roundtrip(&[0u8; 1000]);            // long run (RLE-like via overlap)
        roundtrip(&[0xFFu8; 300]);
        // pseudo-random-ish (poorly compressible) — exercises literals + escape doubling
        let mut v = Vec::new();
        let mut x = 0x12345678u32;
        for _ in 0..5000 { x = x.wrapping_mul(1664525).wrapping_add(1013904223); v.push((x >> 24) as u8); }
        roundtrip(&v);
        // data containing every byte value (escape choice must still work)
        let all: Vec<u8> = (0..=255u8).cycle().take(4096).collect();
        roundtrip(&all);
    }

    #[test]
    fn roundtrips_real_textures() {
        // Compress→decompress real game texture data through our own codec.
        let dir = std::path::Path::new(r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11");
        if !dir.exists() { eprintln!("skip: no game dir"); return; }
        for name in ["I_EFTEX000.pkg", "I_EFTEX091.pkg", "I_EFTEX992.pkg"] {
            let Ok(data) = std::fs::read(dir.join(name)) else { continue; };
            let pkg = PkgArchive::parse(&data).unwrap();
            for e in pkg.entries.clone() {
                let raw = pkg.extract(&e.name).unwrap();
                let c = nislzss_compress(&raw);
                let d = nislzss_decompress(&c, raw.len()).unwrap();
                assert_eq!(d, raw, "{}/{}", name, e.name);
                println!("{}/{}: {} -> {} ({:.1}%)", name, e.name, raw.len(), c.len(),
                    100.0 * c.len() as f64 / raw.len().max(1) as f64);
            }
        }
    }
}
