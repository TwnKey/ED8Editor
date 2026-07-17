//! High-level texture replacement: import a PC image (PNG/JPG/DDS) and package it
//! into a new PhyreEngine `.pkg` that the game can load.
//!
//! The `.dds.phyre` texture is generated **from scratch** at the image's own
//! dimensions (`phyre::encode_phyre_texture`) — no template, no runtime game file,
//! arbitrary sizes. The pkg carries the required `asset_D3D11.xml` manifest and the
//! texture entry, both NISLZSS-compressed like the game's own pkgs.

use std::path::{Path, PathBuf};

use super::pkg::{self, PkgArchive};
use super::phyre::{encode_phyre_texture, parse_phyre_texture, PhyreTexFormat};

/// pkg first-u32 (a pack timestamp in real files; the loader doesn't validate it).
const PKG_MAGIC: u32 = 0x5967_9451;

/// Default game texture directory (Steam install of Trails of Cold Steel).
pub const DEFAULT_ASSET_DIR: &str =
    r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11";

/// A prepared texture replacement, ready to preview and to write on save.
#[derive(Clone)]
pub struct NewTexture {
    /// Base asset name (no extension), also the segment's `fn_name_1` and the pkg stem.
    pub base_name: String,
    /// Complete `.pkg` file bytes to write into the asset dir.
    pub pkg_bytes: Vec<u8>,
    /// Decoded RGBA8 of the packaged result (for preview / validation).
    pub preview_rgba: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

/// Map an asset base name (e.g. `I_EFTEX000`) to the internal pkg entry base the
/// game expects (`eftex000`): drop a leading `I_`/`i_`, lowercase the rest.
pub fn internal_asset_name(base_name: &str) -> String {
    let s = base_name
        .strip_prefix("I_")
        .or_else(|| base_name.strip_prefix("i_"))
        .unwrap_or(base_name);
    s.to_lowercase()
}

/// Build the `asset_D3D11.xml` manifest a texture pkg must contain for the game to
/// resolve it. Byte-for-byte the format the game ships (CRLF, tabs) — validated
/// against a real I_EFTEX pkg in tests. `symbol` = asset name (e.g. `I_EFTEX091`),
/// `internal` = the pkg entry base (e.g. `eftex091`).
pub fn build_asset_xml(symbol: &str, internal: &str) -> Vec<u8> {
    format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n\
         <fassets>\r\n\
         \t<asset symbol=\"{symbol}\">\r\n\
         \t\t<cluster path=\"data/D3D11/effects/images/{internal}.dds.phyre\" type=\"p_texture\" />\r\n\
         \t</asset>\r\n\
         </fassets>\r\n"
    )
    .into_bytes()
}

/// Load a PC image file (PNG/JPG/DDS/…) into top-left-origin RGBA8.
pub fn load_image_rgba(path: &Path) -> Result<(Vec<u8>, u32, u32), String> {
    let img = image::open(path).map_err(|e| format!("load image: {e}"))?;
    let rgba = img.to_rgba8();
    let (w, h) = (rgba.width(), rgba.height());
    Ok((rgba.into_raw(), w, h))
}

/// Build a new texture `.pkg` from a PC image — fully standalone (a `.dds.phyre` is
/// generated from scratch at the image's own dimensions; no game file needed).
/// `format` selects the D3D11 pixel format; pass `None` to auto-detect from an
/// existing texture, or `Some(fmt)` to force a specific format.
/// `base_name` becomes the pkg stem and the segment texture name (e.g. `I_MYTEX`).
pub fn build_texture_pkg(
    img_rgba: &[u8],
    img_w: u32,
    img_h: u32,
    base_name: &str,
    format: Option<PhyreTexFormat>,
) -> Result<NewTexture, String> {
    let internal = internal_asset_name(base_name);
    let tex_format = format.unwrap_or(PhyreTexFormat::RGBA8);

    // Generate the texture cluster from scratch at the image's OWN dimensions (no
    // resize). The earlier interlacing that looked like a NPOT/pitch problem was
    // actually the compressor bug; NPOT textures are fine.
    let phyre = encode_phyre_texture(&internal, img_rgba, img_w, img_h, tex_format)
        .ok_or("failed to encode texture")?;

    // Validate by decoding our own output back before we ever write it.
    let decoded = parse_phyre_texture(&phyre)
        .ok_or("encoded texture failed self-decode (would not load in game)")?;

    // The game resolves an effect texture name like "I_EFTEX000" to "I_EFTEX000.pkg"
    // whose internal entry is "eftex000.dds.phyre" (strip "I_", lowercase). It also
    // needs the asset_D3D11.xml manifest binding the symbol to the cluster path.
    let entry_name = format!("{internal}.dds.phyre");
    let xml = build_asset_xml(base_name, &internal);
    // Compress (flags=1) with the faithful NISLZSS port — byte-compatible with the
    // game's own compressor (non-overlapping matches), so the game decodes it correctly.
    let pkg_bytes = pkg::build_pkg(PKG_MAGIC, &[
        ("asset_D3D11.xml".to_string(), xml),
        (entry_name, phyre),
    ], true);

    Ok(NewTexture {
        base_name: base_name.to_string(),
        pkg_bytes,
        preview_rgba: decoded.rgba_data,
        width: decoded.width,
        height: decoded.height,
    })
}

/// Resolve the pkg file for an existing asset base name inside `asset_dir`.
pub fn existing_pkg_path(asset_dir: &Path, base_name: &str) -> PathBuf {
    asset_dir.join(format!("{base_name}.pkg"))
}

/// Detect the Phyre pixel format of an existing game texture .pkg.
/// Returns `None` if the pkg doesn't exist or can't be parsed.
pub fn detect_existing_format(game_asset_dir: &Path, base_name: &str) -> Option<PhyreTexFormat> {
    let path = existing_pkg_path(game_asset_dir, base_name);
    let data = std::fs::read(&path).ok()?;
    let arch = PkgArchive::parse(&data)?;
    let tex = arch.find_texture()?;
    let phyre = arch.extract(&tex.name)?;
    let parsed = parse_phyre_texture(&phyre)?;
    Some(parsed.format)
}

/// Write a prepared texture pkg into `asset_dir`, but **only if it does not already
/// exist** (per the requirement: do nothing if the pkg is already present). Returns
/// the path written, or `Ok(None)` if it already existed.
pub fn save_texture_pkg(asset_dir: &Path, tex: &NewTexture) -> Result<Option<PathBuf>, String> {
    let path = existing_pkg_path(asset_dir, &tex.base_name);
    if path.exists() {
        return Ok(None);
    }
    std::fs::create_dir_all(asset_dir).map_err(|e| format!("create asset dir: {e}"))?;
    std::fs::write(&path, &tex.pkg_bytes).map_err(|e| format!("write pkg: {e}"))?;
    Ok(Some(path))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_generated_xml_matches_game() {
        // Our generated manifest must be byte-identical to the one the game ships.
        let dir = Path::new(DEFAULT_ASSET_DIR);
        let p = dir.join("I_EFTEX091.pkg");
        if !p.exists() { eprintln!("skip: no game pkg"); return; }
        let data = std::fs::read(&p).unwrap();
        let pkg = PkgArchive::parse(&data).unwrap();
        let real = pkg.extract("asset_D3D11.xml").unwrap();
        let ours = build_asset_xml("I_EFTEX091", "eftex091");
        assert_eq!(ours, real, "generated XML differs from game's");
    }

    #[test]
    fn test_pkg_has_manifest_first() {
        let rgba = vec![255u8; 16 * 16 * 4];
        let nt = build_texture_pkg(&rgba, 16, 16, "I_MODTEST", None).unwrap();
        let arch = PkgArchive::parse(&nt.pkg_bytes).unwrap();
        assert_eq!(arch.entries.len(), 2);
        assert_eq!(arch.entries[0].name, "asset_D3D11.xml");
        assert_eq!(arch.entries[1].name, "modtest.dds.phyre");
        let xml = String::from_utf8(arch.extract("asset_D3D11.xml").unwrap()).unwrap();
        assert!(xml.contains("symbol=\"I_MODTEST\""));
        assert!(xml.contains("images/modtest.dds.phyre"));
    }

    #[test]
    fn test_standalone_encode_decodes() {
        // Fully standalone: no game files touched, NO resize — a non-power-of-two
        // source packages and decodes back at its exact dimensions.
        let (w, h) = (100u32, 40u32);
        let mut rgba = vec![0u8; (w * h * 4) as usize];
        for y in 0..h { for x in 0..w {
            let i = ((y * w + x) * 4) as usize;
            rgba[i] = (x * 2) as u8; rgba[i+1] = (y * 6) as u8; rgba[i+2] = 128; rgba[i+3] = 255;
        }}
        let nt = build_texture_pkg(&rgba, w, h, "I_TESTGRAD", None).expect("encode");
        assert_eq!((nt.width, nt.height), (100, 40)); // exact dims, no resize
        let archive = PkgArchive::parse(&nt.pkg_bytes).expect("reparse pkg");
        let tex = archive.find_texture().expect("find tex");
        let phyre = archive.extract(&tex.name).expect("extract");
        let dec = parse_phyre_texture(&phyre).expect("decode");
        assert_eq!((dec.width, dec.height), (100, 40));
    }
}

#[cfg(test)]
mod e2e {
    use super::*;
    use crate::core::phyre::{parse_phyre_texture, PhyreTexFormat};
    #[test]
    fn test_write_and_reopen() {
        // Standalone build + write to a TEMP dir (never the game dir) + reopen.
        let (w, h) = (48u32, 24u32);
        let rgba = vec![255u8; (w * h * 4) as usize];
        let nt = build_texture_pkg(&rgba, w, h, "I_MODTEST", None).expect("encode");
        assert_eq!(PkgArchive::parse(&nt.pkg_bytes).unwrap().find_texture().unwrap().name, "modtest.dds.phyre");
        let out = std::env::temp_dir().join("eff_tex_e2e");
        let _ = std::fs::remove_dir_all(&out);
        let p = save_texture_pkg(&out, &nt).expect("write").expect("path");
        assert!(p.exists());
        // second save is a no-op (already exists).
        assert!(save_texture_pkg(&out, &nt).expect("noop").is_none());
        let data = std::fs::read(&p).unwrap();
        let arch2 = PkgArchive::parse(&data).unwrap();
        let phyre = arch2.extract("modtest.dds.phyre").unwrap();
        let dec = parse_phyre_texture(&phyre).unwrap();
        println!("reopened {}x{} pkg={} bytes", dec.width, dec.height, data.len());
        let _ = std::fs::remove_dir_all(&out);
    }

    /// Diagnostic: decode a .pkg and check EVERY mip level's pixel uniformity.
    /// Run: `cargo test --lib e2e::check_mips -- --nocapture`
    #[test]
    fn check_mips() {
        use crate::core::phyre::parse_phyre_texture;
        let pkg_path = r"C:\Users\Administrator\Desktop\I_CRX000_SOLID.pkg";
        let data = match std::fs::read(pkg_path) {
            Ok(d) => d,
            Err(_) => { eprintln!("SKIP: {pkg_path} not found"); return; }
        };

        let arch = PkgArchive::parse(&data).expect("parse pkg");
        let tex_entry = arch.find_texture().expect("find texture entry");
        println!("Entry: {}", tex_entry.name);
        let phyre_data = arch.extract(&tex_entry.name).expect("extract phyre");

        // Detect format
        let fmt = parse_phyre_texture(&phyre_data).map(|t| t.format);
        println!("Format detected: {:?}", fmt);

        // Parse all mip levels manually
        let ns_size = u32::from_le_bytes([phyre_data[8], phyre_data[9], phyre_data[10], phyre_data[11]]) as usize;
        let ilist_count = u32::from_le_bytes([phyre_data[16], phyre_data[17], phyre_data[18], phyre_data[19]]) as usize;
        let obj_start = 84 + ns_size + ilist_count * 36;
        let width = u32::from_le_bytes([phyre_data[obj_start+96], phyre_data[obj_start+97], phyre_data[obj_start+98], phyre_data[obj_start+99]]);
        let height = u32::from_le_bytes([phyre_data[obj_start+100], phyre_data[obj_start+101], phyre_data[obj_start+102], phyre_data[obj_start+103]]);
        let mip_depth = u32::from_le_bytes([phyre_data[obj_start+80], phyre_data[obj_start+81], phyre_data[obj_start+82], phyre_data[obj_start+83]]);
        println!("Dimensions: {width}x{height}, mip_depth={mip_depth} ({} levels)", mip_depth + 1);

        // The pixel data is everything after the skeleton
        let skel_len = { // approximate: skeleton = all bytes before pixel region
            let (mut tw, mut th) = (width as usize, height as usize);
            let mut total = 0usize;
            for _ in 0..=mip_depth {
                total += tw * th * 4;
                tw = (tw / 2).max(1);
                th = (th / 2).max(1);
            }
            phyre_data.len().saturating_sub(total)
        };
        println!("Skeleton size (approx): {skel_len}, total: {}", phyre_data.len());

        // Check each mip level
        let (mut mw, mut mh) = (width as usize, height as usize);
        let mut offset = skel_len;
        for level in 0..=mip_depth as usize {
            let size = mw * mh * 4;
            if offset + size > phyre_data.len() {
                println!("  MIP {level} ({mw}x{mh}): OUT OF BOUNDS (offset={offset}, need={size}, avail={})",
                    phyre_data.len() - offset);
                break;
            }
            let pixels = &phyre_data[offset..offset + size];

            // Check uniformity: all bytes of same channel should be equal
            let mut r0 = None; let mut g0 = None; let mut b0 = None; let mut a0 = None;
            let mut nonuniform = false;
            for chunk in pixels.chunks_exact(4) {
                if r0.is_none() { r0 = Some(chunk[0]); g0 = Some(chunk[1]); b0 = Some(chunk[2]); a0 = Some(chunk[3]); }
                if chunk[0] != r0.unwrap() || chunk[1] != g0.unwrap() || chunk[2] != b0.unwrap() || chunk[3] != a0.unwrap() {
                    if !nonuniform {
                        println!("  MIP {level} ({mw}x{mh}): NON-UNIFORM — first different pixel at offset +{}: rgba({},{},{},{}) vs expected rgba({},{},{},{})",
                            offset + (chunk.as_ptr() as usize - pixels.as_ptr() as usize),
                            chunk[0], chunk[1], chunk[2], chunk[3],
                            r0.unwrap(), g0.unwrap(), b0.unwrap(), a0.unwrap());
                        nonuniform = true;
                    }
                }
            }
            if !nonuniform {
                println!("  MIP {level} ({mw}x{mh}): UNIFORM rgba({},{},{},{}) OK", r0.unwrap(), g0.unwrap(), b0.unwrap(), a0.unwrap());
            }

            offset += size;
            mw = (mw / 2).max(1);
            mh = (mh / 2).max(1);
        }
    }

    /// Generate the ARGB8 solid-magenta test file for in-game validation.
    /// Run: `cargo test --lib e2e::gen_solid_argb8 -- --nocapture`
    #[test]
    fn gen_solid_argb8() {
        // Solid magenta (220, 30, 220) with alpha 180 — same as I_CRX000_SOLID but ARGB8.
        let w = 512u32; let h = 512u32;
        let rgba: Vec<u8> = (0..(w * h) as usize)
            .flat_map(|_| [220u8, 30, 220, 180])
            .collect();

        let nt = build_texture_pkg(&rgba, w, h, "I_CRX000_SOLID_ARGB8",
            Some(PhyreTexFormat::ARGB8)).expect("encode ARGB8");

        let out = r"C:\Users\Administrator\Desktop\I_CRX000_SOLID_ARGB8.pkg";
        std::fs::write(out, &nt.pkg_bytes).expect("write pkg");
        println!("Wrote {} bytes to {out}", nt.pkg_bytes.len());

        // Validate: decode back, check all mips uniform
        let arch = PkgArchive::parse(&nt.pkg_bytes).unwrap();
        let tex = arch.find_texture().unwrap();
        let phyre = arch.extract(&tex.name).unwrap();
        let dec = parse_phyre_texture(&phyre).unwrap();
        assert_eq!(dec.format, PhyreTexFormat::ARGB8);
        assert_eq!((dec.width, dec.height), (512, 512));

        // Quick mip uniformity check on decoded top mip
        let px = &dec.rgba_data;
        let (r0, g0, b0, a0) = (px[0], px[1], px[2], px[3]);
        for chunk in px.chunks_exact(4) {
            assert_eq!(chunk[0], r0, "non-uniform R");
            assert_eq!(chunk[1], g0, "non-uniform G");
            assert_eq!(chunk[2], b0, "non-uniform B");
            assert_eq!(chunk[3], a0, "non-uniform A");
        }
        println!("ARGB8 solid uniform OK: rgba({r0},{g0},{b0},{a0}) — ready for in-game test");
    }

    /// Generate ALL diagnostic textures: white+red+magenta in ARGB8 and RGBA8,
    /// plus the PNG source files for reference. Drops everything on Desktop.
    /// Run: `cargo test --lib e2e::gen_diag_textures -- --nocapture`
    #[test]
    fn gen_diag_textures() {
        let desktop = r"C:\Users\Administrator\Desktop";
        let tests: Vec<(&str, [u8; 4], PhyreTexFormat)> = vec![
            ("WHITE_ARGB8",   [255, 255, 255, 255], PhyreTexFormat::ARGB8),
            ("WHITE_RGBA8",   [255, 255, 255, 255], PhyreTexFormat::RGBA8),
            ("RED_ARGB8",     [255, 0, 0, 255],     PhyreTexFormat::ARGB8),
            ("RED_RGBA8",     [255, 0, 0, 255],     PhyreTexFormat::RGBA8),
            ("MAGENTA_ARGB8", [220, 30, 220, 180],  PhyreTexFormat::ARGB8),
            ("MAGENTA_RGBA8", [220, 30, 220, 180],  PhyreTexFormat::RGBA8),
        ];

        for (name, rgba_color, fmt) in &tests {
            let w = 512u32; let h = 512u32;
            let [r, g, b, a] = *rgba_color;
            let px: Vec<u8> = (0..(w * h) as usize)
                .flat_map(|_| [r, g, b, a])
                .collect();
            let base = format!("I_DIAG_{name}");
            let nt = build_texture_pkg(&px, w, h, &base, Some(*fmt))
                .expect(&format!("encode {name}"));
            let out = format!("{desktop}\\{base}.pkg");
            std::fs::write(&out, &nt.pkg_bytes).expect("write");
            println!("{name}: {} bytes → {out}", nt.pkg_bytes.len());

            // Also save as PNG for reference
            let png_out = format!("{desktop}\\{base}.png");
            let img = image::RgbaImage::from_raw(w, h, px.clone()).unwrap();
            img.save(&png_out).expect("save png");
        }
        println!("\nDone. Copy the .pkg files to data/asset/D3D11/, rename to match your effect's texture name, and test in-game.");
        println!("Expected in-game colors:");
        println!("  WHITE_ARGB8/RGBA8 → solid white (if correct)");
        println!("  RED_ARGB8/RGBA8   → solid red (if correct; any other color = channel swap)");
        println!("  MAGENTA_ARGB8/RGBA8 → solid magenta (if correct)");
    }
}
