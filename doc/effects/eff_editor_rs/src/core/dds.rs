//! Minimal DDS decoder for RGBA8, DXT1, and DXT5 formats.

/// DDS compression format.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DdsFormat {
    Rgba8,
    Dxt1,
    Dxt5,
}

/// Decode raw DDS pixel data (without DDS header) to RGBA8.
pub fn decode_dds_to_rgba_from_raw(
    data: &[u8], width: u32, height: u32, format: DdsFormat,
) -> Option<Vec<u8>> {
    match format {
        DdsFormat::Rgba8 => {
            let expected = (width * height * 4) as usize;
            if data.len() < expected { return None; }
            Some(data[..expected].to_vec())
        }
        DdsFormat::Dxt1 => decode_dxt1(width, height, data),
        DdsFormat::Dxt5 => decode_dxt5(width, height, data),
    }
}

/// Decode a DDS file (starting from the "DDS " magic) to RGBA8 pixels.
/// Returns (width, height, rgba8_data).
pub fn decode_dds_to_rgba8(data: &[u8]) -> Option<(u32, u32, Vec<u8>)> {
    // Find DDS magic
    let start = data.windows(4).position(|w| w == b"DDS ")?;
    let dds = &data[start..];

    if dds.len() < 128 { return None; } // DDS header is 128 bytes

    let height = u32::from_le_bytes([dds[12], dds[13], dds[14], dds[15]]);
    let width = u32::from_le_bytes([dds[16], dds[17], dds[18], dds[19]]);
    let _pitch = u32::from_le_bytes([dds[20], dds[21], dds[22], dds[23]]);

    // Pixel format (bytes 76-107)
    let pf_fourcc = u32::from_le_bytes([dds[84], dds[85], dds[86], dds[87]]);
    let pf_rgb_bits = u32::from_le_bytes([dds[88], dds[89], dds[90], dds[91]]);
    let pf_r_mask = u32::from_le_bytes([dds[92], dds[93], dds[94], dds[95]]);
    let pf_g_mask = u32::from_le_bytes([dds[96], dds[97], dds[98], dds[99]]);
    let pf_b_mask = u32::from_le_bytes([dds[100], dds[101], dds[102], dds[103]]);
    let pf_a_mask = u32::from_le_bytes([dds[104], dds[105], dds[106], dds[107]]);

    let pixel_data = &dds[128..];

    if width == 0 || height == 0 || width > 8192 || height > 8192 {
        return None;
    }

    let rgba = match pf_fourcc {
        // DXT1
        0x31545844 => decode_dxt1(width, height, pixel_data),
        // DXT5
        0x35545844 => decode_dxt5(width, height, pixel_data),
        // Uncompressed RGBA
        0 => {
            if pf_rgb_bits == 32 && pf_r_mask == 0x00FF0000 && pf_g_mask == 0x0000FF00
                && pf_b_mask == 0x000000FF && pf_a_mask == 0xFF000000 {
                // BGRA → RGBA
                Some(convert_bgra_to_rgba(pixel_data, width, height))
            } else if pf_rgb_bits == 32 && pf_r_mask == 0x000000FF && pf_g_mask == 0x0000FF00
                && pf_b_mask == 0x00FF0000 && pf_a_mask == 0xFF000000 {
                // Already RGBA
                Some(pixel_data.to_vec())
            } else {
                None
            }
        }
        _ => None,
    }?;

    Some((width, height, rgba))
}

fn convert_bgra_to_rgba(data: &[u8], width: u32, height: u32) -> Vec<u8> {
    let size = (width * height * 4) as usize;
    let mut out = vec![0u8; size];
    let len = data.len().min(size);
    for i in (0..len).step_by(4) {
        out[i] = data[i + 2];     // R
        out[i + 1] = data[i + 1]; // G
        out[i + 2] = data[i];     // B
        out[i + 3] = *data.get(i + 3).unwrap_or(&255); // A
    }
    out
}

/// Decode DXT1 (BC1) to RGBA8. 8 bytes per 4×4 block.
fn decode_dxt1(width: u32, height: u32, data: &[u8]) -> Option<Vec<u8>> {
    let w = width as usize;
    let h = height as usize;
    let expected = ((w + 3) / 4) * ((h + 3) / 4) * 8;
    if data.len() < expected { return None; }

    let mut out = vec![0u8; w * h * 4];

    for by in (0..h).step_by(4) {
        for bx in (0..w).step_by(4) {
            let block_idx = ((by / 4) * ((w + 3) / 4) + (bx / 4)) * 8;
            if block_idx + 8 > data.len() { continue; }

            let c0 = u16::from_le_bytes([data[block_idx], data[block_idx + 1]]);
            let c1 = u16::from_le_bytes([data[block_idx + 2], data[block_idx + 3]]);

            let (r0, g0, b0) = rgb565(c0);
            let (r1, g1, b1) = rgb565(c1);

            let colors: [[u8; 4]; 4] = [
                [r0, g0, b0, 255],
                [r1, g1, b1, 255],
                if c0 > c1 {
                    [((2 * r0 as u16 + r1 as u16) / 3) as u8, ((2 * g0 as u16 + g1 as u16) / 3) as u8, ((2 * b0 as u16 + b1 as u16) / 3) as u8, 255]
                } else {
                    [((r0 as u16 + r1 as u16) / 2) as u8, ((g0 as u16 + g1 as u16) / 2) as u8, ((b0 as u16 + b1 as u16) / 2) as u8, 0]
                },
                if c0 > c1 {
                    [((r0 as u16 + 2 * r1 as u16) / 3) as u8, ((g0 as u16 + 2 * g1 as u16) / 3) as u8, ((b0 as u16 + 2 * b1 as u16) / 3) as u8, 255]
                } else {
                    [0, 0, 0, 0]
                },
            ];

            let indices = u32::from_le_bytes([
                data[block_idx + 4], data[block_idx + 5],
                data[block_idx + 6], data[block_idx + 7],
            ]);

            for y in 0..4 {
                for x in 0..4 {
                    let px = bx + x;
                    let py = by + y;
                    if px < w && py < h {
                        let idx = ((indices >> ((y * 4 + x) * 2)) & 3) as usize;
                        let off = (py * w + px) * 4;
                        out[off..off + 4].copy_from_slice(&colors[idx]);
                    }
                }
            }
        }
    }
    Some(out)
}

/// Decode DXT5 (BC3) to RGBA8. 16 bytes per 4×4 block.
fn decode_dxt5(width: u32, height: u32, data: &[u8]) -> Option<Vec<u8>> {
    let w = width as usize;
    let h = height as usize;
    let expected = ((w + 3) / 4) * ((h + 3) / 4) * 16;
    if data.len() < expected { return None; }

    let mut out = vec![0u8; w * h * 4];

    for by in (0..h).step_by(4) {
        for bx in (0..w).step_by(4) {
            let block_idx = ((by / 4) * ((w + 3) / 4) + (bx / 4)) * 16;
            if block_idx + 16 > data.len() { continue; }

            let alpha0 = data[block_idx];
            let alpha1 = data[block_idx + 1];

            // Alpha indices: 6 bytes = 48 bits for 16 pixels (3 bits each)
            let alpha_indices = {
                let mut buf = [0u8; 8];
                buf[..6].copy_from_slice(&data[block_idx + 2..block_idx + 8]);
                u64::from_le_bytes(buf)
            };

            let c0 = u16::from_le_bytes([data[block_idx + 8], data[block_idx + 9]]);
            let c1 = u16::from_le_bytes([data[block_idx + 10], data[block_idx + 11]]);
            let (r0, g0, b0) = rgb565(c0);
            let (r1, g1, b1) = rgb565(c1);

            let colors: [[u8; 3]; 4] = [
                [r0, g0, b0],
                [r1, g1, b1],
                [((2 * r0 as u16 + r1 as u16) / 3) as u8, ((2 * g0 as u16 + g1 as u16) / 3) as u8, ((2 * b0 as u16 + b1 as u16) / 3) as u8],
                [((r0 as u16 + 2 * r1 as u16) / 3) as u8, ((g0 as u16 + 2 * g1 as u16) / 3) as u8, ((b0 as u16 + 2 * b1 as u16) / 3) as u8],
            ];

            let color_indices = u32::from_le_bytes([
                data[block_idx + 12], data[block_idx + 13],
                data[block_idx + 14], data[block_idx + 15],
            ]);

            for y in 0..4 {
                for x in 0..4 {
                    let px = bx + x;
                    let py = by + y;
                    if px < w && py < h {
                        let ai = ((alpha_indices >> ((y * 4 + x) * 3)) & 7) as u8;
                        let alpha: u8 = if alpha0 > alpha1 {
                            match ai {
                                0 => alpha0,
                                1 => alpha1,
                                n => ((alpha0 as u16 * (8 - n as u16) + alpha1 as u16 * (n as u16 - 1)) / 7) as u8,
                            }
                        } else {
                            match ai {
                                0 => alpha0,
                                1 => alpha1,
                                n if n <= 6 => ((alpha0 as u16 * (6 - n as u16) + alpha1 as u16 * (n as u16 - 1)) / 5) as u8,
                                7 => 0,
                                _ => 255,
                            }
                        };

                        let ci = ((color_indices >> ((y * 4 + x) * 2)) & 3) as usize;
                        let off = (py * w + px) * 4;
                        out[off] = colors[ci][0];
                        out[off + 1] = colors[ci][1];
                        out[off + 2] = colors[ci][2];
                        out[off + 3] = alpha;
                    }
                }
            }
        }
    }
    Some(out)
}

fn rgb565(val: u16) -> (u8, u8, u8) {
    let r = ((val >> 11) & 0x1F) as u8;
    let g = ((val >> 5) & 0x3F) as u8;
    let b = (val & 0x1F) as u8;
    ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))
}
