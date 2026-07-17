//! PhyreEngine bytecode parser — extracts m_width/m_height deterministically.
//! Based on the ed8pkg2gltf Python implementation.

use std::collections::HashMap;

const NOEPY_HEADER_LE: u32 = 0x50485952; // "RYHP" as little-endian u32 = 1346918738

/// Parse the PhyreEngine cluster to find the PTexture2D dimensions.
/// Returns (width, height) if found.
pub fn parse_texture_dimensions(data: &[u8]) -> Option<(u32, u32)> {
    if data.len() < 84 { return None; }

    // ── ClusterClusterHeader (starts at offset 0) ──
    let cluster_marker = read_u32(data, 0);
    if cluster_marker != NOEPY_HEADER_LE { return None; }

    let is_be = false; // cluster_marker != 1381582928 (BE)
    let _size = read_u32_endian(data, 4, is_be) as usize;
    let _packed_ns_size = read_u32_endian(data, 8, is_be);
    let _platform = read_u32_endian(data, 12, is_be);
    let instance_list_count = read_u32_endian(data, 16, is_be) as usize;
    let _array_fixup_size = read_u32_endian(data, 20, is_be);
    let _array_fixup_count = read_u32_endian(data, 24, is_be);
    let _ptr_fixup_size = read_u32_endian(data, 28, is_be);
    let _ptr_fixup_count = read_u32_endian(data, 32, is_be);
    let _ptr_array_fixup_size = read_u32_endian(data, 36, is_be);
    let _ptr_array_fixup_count = read_u32_endian(data, 40, is_be);
    let _ptrs_in_arrays = read_u32_endian(data, 44, is_be);
    let user_fixup_count = read_u32_endian(data, 48, is_be) as usize;
    let user_fixup_data_size = read_u32_endian(data, 52, is_be) as usize;
    let total_data_size = read_u32_endian(data, 56, is_be) as usize;
    let header_class_inst_count = read_u32_endian(data, 60, is_be) as usize;
    let header_class_child_count = read_u32_endian(data, 64, is_be) as usize;

    // Cluster header is 68 bytes, but self.size includes padding
    let cluster_hdr_end = 84; // self.size value

    // ── PackedNamespace (at cluster_hdr_end) ──
    if cluster_hdr_end + 32 > data.len() { return None; }
    let ns_off = cluster_hdr_end;
    let _ns_header = read_u32_endian(data, ns_off, is_be);
    let _ns_size = read_u32_endian(data, ns_off + 4, is_be);
    let type_count = read_u32_endian(data, ns_off + 8, is_be) as usize;
    let class_count = read_u32_endian(data, ns_off + 12, is_be) as usize;
    let member_count = read_u32_endian(data, ns_off + 16, is_be) as usize;
    let string_table_size = read_u32_endian(data, ns_off + 20, is_be) as usize;
    let _def_buf_count = read_u32_endian(data, ns_off + 24, is_be);
    let _def_buf_size = read_u32_endian(data, ns_off + 28, is_be);

    // After namespace: type_ids, class descriptors, data members, string table
    let after_ns = ns_off + 32;

    // Skip type_ids: type_count × 4
    let after_types = after_ns + type_count * 4;

    // Class descriptors: class_count × 36 bytes each
    let after_classes = after_types + class_count * 36;

    // Data members: member_count × 24 bytes each
    let after_members = after_classes + member_count * 24;

    // String table
    let string_table_start = after_members;
    let _string_table_end = string_table_start + string_table_size;

    // label_offset = after namespace + type_ids + class_descriptors + data_members
    // In Python: g.tell() is after reading type_ids (after_ns + type_count*4)
    let label_offset = after_types + class_count * 36 + member_count * 24;

    // ── Parse class descriptors to find PTexture2D ──
    let mut ptex2d_class_id: Option<usize> = None;
    let mut ptex2d_member_offset: usize = 0;
    let mut ptex2d_member_count: usize = 0;

    let mut running_member_offset = 0usize;
    for i in 0..class_count {
        let cd_off = after_types + i * 36;
        if cd_off + 36 > data.len() { return None; }

        let _super_class_id = read_u32_endian(data, cd_off, is_be);
        let size_and_align = read_u32_endian(data, cd_off + 4, is_be);
        let _size_in_bytes = size_and_align & 0x0FFFFFFF;
        let name_off = read_u32_endian(data, cd_off + 8, is_be) as usize;
        let dm_count = read_u32_endian(data, cd_off + 12, is_be) as usize;
        // ... more fields we don't need

        // Read class name from string table
        let name = read_string_at(data, label_offset as isize + name_off as isize);

        // Find the texture class (could be PTexture2D, PTexture2DD3D11, etc.)
        if name.starts_with("PTexture2D") && ptex2d_class_id.is_none() {
            ptex2d_class_id = Some(i + 1);
            ptex2d_member_offset = running_member_offset;
            ptex2d_member_count = dm_count;
        }

        running_member_offset += dm_count;
    }

    let ptex2d_id = ptex2d_class_id?;

    // Build inheritance chain (base classes + most derived)
    let mut chain: Vec<usize> = Vec::new();
    let mut cid = ptex2d_id;
    while cid > 0 && cid <= class_count {
        if !chain.contains(&cid) { chain.push(cid); } else { break; }
        let cd_off = after_types + (cid - 1) * 36;
        cid = read_u32_endian(data, cd_off, is_be) as usize;
    }
    chain.reverse();
    for i in 0..class_count {
        let cd_off = after_types + i * 36;
        if read_u32_endian(data, cd_off, is_be) as usize == ptex2d_id {
            let cid = i + 1;
            if !chain.contains(&cid) { chain.push(cid); }
            break;
        }
    }

    // Find instance and scan for (w,h) matching buffer size at offset 80
    let after_str = string_table_start + string_table_size;
    let obj_start = after_str + instance_list_count * 36;
    let top_mip = read_u32_endian(data, 80, is_be) as usize;
    let mut running = 0usize;
    for i in 0..instance_list_count {
        let il_off = after_str + i * 36;
        let class_id = read_u32_endian(data, il_off, is_be) as usize;
        let obj_size = read_u32_endian(data, il_off + 12, is_be) as usize;
        if chain.contains(&class_id) {
            let inst = obj_start + running;
            // Try bpp=4 (ARGB/RGBA), bpp=1 (DXT5), bpp=0.5 (DXT1)
            for &div in &[4usize, 1, 0] {
                let tgt = if div == 0 { top_mip * 2 } else { top_mip / div };
                if tgt == 0 { continue; }
                for j in (0..obj_size.saturating_sub(8)).step_by(4) {
                    let w = read_u32_endian(data, inst + j, is_be) as usize;
                    let h = read_u32_endian(data, inst + j + 4, is_be) as usize;
                    if w > 0 && h > 0 && w <= 4096 && h <= 4096 && w * h == tgt {
                        return Some((w as u32, h as u32));
                    }
                }
            }
            break;
        }
        running += obj_size;
    }
    None
}

fn read_u32(data: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([data[off], data[off+1], data[off+2], data[off+3]])
}

fn read_u32_endian(data: &[u8], off: usize, big_endian: bool) -> u32 {
    if big_endian {
        u32::from_be_bytes([data[off], data[off+1], data[off+2], data[off+3]])
    } else {
        u32::from_le_bytes([data[off], data[off+1], data[off+2], data[off+3]])
    }
}

fn read_string_at(data: &[u8], offset: isize) -> String {
    if offset < 0 || offset as usize >= data.len() {
        return String::new();
    }
    let mut off = offset as usize;
    let mut bytes = Vec::new();
    while off < data.len() && data[off] != 0 {
        bytes.push(data[off]);
        off += 1;
    }
    String::from_utf8_lossy(&bytes).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::pkg::PkgArchive;
    use std::fs;

    #[test]
    fn test_parse_dims_992() {
        let path = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11\I_EFTEX992.pkg";
        let data = fs::read(path).expect("read pkg");
        let pkg = PkgArchive::parse(&data).expect("parse");
        let tex = pkg.find_texture().expect("find");
        let dec = pkg.extract(&tex.name).expect("decompress");
        
        // Debug: print class names found
        let cluster_marker = read_u32(&dec, 0);
        println!("cluster_marker: 0x{:08X}", cluster_marker);
        let is_be = false;
        let ns_off = 84;
        let after_ns = ns_off + 32;
        let type_count = read_u32_endian(&dec, ns_off + 8, is_be) as usize;
        let class_count = read_u32_endian(&dec, ns_off + 12, is_be) as usize;
        let member_count = read_u32_endian(&dec, ns_off + 16, is_be) as usize;
        let string_table_size = read_u32_endian(&dec, ns_off + 20, is_be) as usize;
        println!("type_count={} class_count={} member_count={} str_size={}", type_count, class_count, member_count, string_table_size);
        
        let after_types = after_ns + type_count * 4;
        let after_classes = after_types + class_count * 36;
        let label_offset = after_types + class_count * 36 + member_count * 24;
        let after_members = after_classes + member_count * 24;
        println!("label_offset={} after_members={} str_size={}", label_offset, after_members, string_table_size);
        
        // Hex dump of string table (first 128 bytes)
        print!("String table hex: ");
        for i in 0..128.min(string_table_size) {
            print!("{:02X} ", dec[after_members + i]);
        }
        println!();

        // Print class names with their raw name_offset
        let mut running_mo = 0;
        for i in 0..class_count {
            let cd_off = after_types + i * 36;
            let dm_count = read_u32_endian(&dec, cd_off + 12, is_be) as usize;
            let raw_name_off = read_u32_endian(&dec, cd_off + 8, is_be) as usize;
            let abs_name_off = label_offset as isize + raw_name_off as isize;
            let name = read_string_at(&dec, abs_name_off);
            println!("  class[{}]: raw_name_off={} abs={} '{}' members={}", 
                i, raw_name_off, abs_name_off, name, dm_count);
            if name.contains("PTexture") || name.contains("PBase") {
                println!("  class[{}]: '{}' members={} super_class={}", i, name, dm_count,
                    read_u32_endian(&dec, cd_off, is_be));
                for m in 0..dm_count {
                    let dm_off = after_classes + (running_mo + m) * 24;
                    let dm_name_off = read_u32_endian(&dec, dm_off, is_be) as usize;
                    let dm_val_off = read_u32_endian(&dec, dm_off + 8, is_be);
                    let dm_name = read_string_at(&dec, label_offset as isize + dm_name_off as isize);
                    println!("    member '{}' value_offset={}", dm_name, dm_val_off);
                }
            }
            running_mo += dm_count;
        }

        match parse_texture_dimensions(&dec) {
            Some((w, h)) => println!("I_EFTEX992: {}x{}", w, h),
            None => println!("I_EFTEX992: FAILED"),
        }
    }

    #[test]
    fn test_scan_instance() {
        let path = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11\I_EFTEX992.pkg";
        let data = fs::read(path).expect("read pkg");
        let pkg = PkgArchive::parse(&data).expect("parse");
        let tex = pkg.find_texture().expect("find");
        let dec = pkg.extract(&tex.name).expect("decompress");

        // Manually compute offsets
        let is_be = false;
        let ns_off = 84;
        let type_count = read_u32_endian(&dec, ns_off + 8, is_be) as usize;
        let class_count = read_u32_endian(&dec, ns_off + 12, is_be) as usize;
        let member_count = read_u32_endian(&dec, ns_off + 16, is_be) as usize;
        let str_size = read_u32_endian(&dec, ns_off + 20, is_be) as usize;
        let after_ns = ns_off + 32;
        let after_types = after_ns + type_count * 4;
        let after_classes = after_types + class_count * 36;
        let after_members = after_classes + member_count * 24;
        let str_start = after_members;
        let after_str = str_start + str_size;
        let obj_data_start = after_str + 2 * 36; // 2 instance list headers

        // PAssetReference size from class descriptor
        let cd0_off = after_types + 0 * 36;
        let cd0_sz = read_u32_endian(&dec, cd0_off + 4, is_be) & 0x0FFFFFFF;
        println!("PAssetReference size: {}", cd0_sz);

        let inst_start = obj_data_start + cd0_sz as usize;
        println!("Instance at {}, {} bytes:", inst_start, 112);

        for i in 0..112 {
            if i % 16 == 0 { print!("  [{:4}] ", inst_start + i); }
            print!("{:02X} ", dec[inst_start + i]);
            if i % 16 == 15 { println!(); }
        }
        println!();

        // Scan for 512 and 128
        for i in (0..108).step_by(4) {
            let v = read_u32_endian(&dec, inst_start + i, is_be);
            if v == 512 || v == 128 || v == 256 { println!("  u32 at +{}: {}", i, v); }
        }
        // Also scan as u16
        for i in (0..110).step_by(2) {
            let v = u16::from_le_bytes([dec[inst_start+i], dec[inst_start+i+1]]) as u32;
            if v == 512 || v == 128 { println!("  u16 at +{}: {}", i, v); }
        }
    }
    #[test]
    fn test_parse_dims_000() {
        let path = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11\I_EFTEX000.pkg";
        let data = fs::read(path).expect("read pkg");
        let pkg = PkgArchive::parse(&data).expect("parse");
        let tex = pkg.find_texture().expect("find");
        let dec = pkg.extract(&tex.name).expect("decompress");
        match parse_texture_dimensions(&dec) {
            Some((w, h)) => println!("I_EFTEX000: {}x{}", w, h),
            None => println!("I_EFTEX000: FAILED"),
        }
    }
}
