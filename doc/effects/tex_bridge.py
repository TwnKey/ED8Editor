#!/usr/bin/env python3
"""
Bridge script: extracts a texture from a .pkg file and outputs raw RGBA8 pixels.

Usage: python tex_bridge.py <path-to-pkg> [texture_index]
Output to stdout: 4 bytes width (u32 LE) + 4 bytes height (u32 LE) + RGBA8 pixels
"""
import sys, os, io, struct

# Add ed8pkg2gltf to path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(SCRIPT_DIR, "ed8pkg2gltf-main"))

# These imports trigger the full Phyre pipeline - but we only need texture extraction
# We'll use the lower-level functions directly

def nislzss_decompress(data, expected_size):
    """Minimal NISLZSS decompressor (same algorithm as unpackpkg)."""
    if len(data) < 13:
        return None
    src = io.BytesIO(data)
    des = struct.unpack("<I", src.read(4))[0]
    cms = struct.unpack("<I", src.read(4))[0]
    num3 = struct.unpack("<I", src.read(4))[0]
    fin = src.tell() + cms - 13
    target = max(des, expected_size)
    cd = bytearray(target)
    num4 = 0
    while src.tell() <= fin and num4 < target:
        b = src.read(1)
        if not b:
            break
        b = b[0]
        if b == num3:
            b2 = src.read(1)
            if not b2:
                break
            b2 = b2[0]
            if b2 != num3:
                if b2 >= num3:
                    b2 -= 1
                b3 = src.read(1)
                if not b3:
                    break
                b3 = b3[0]
                if b2 < b3:
                    for _ in range(b3):
                        if num4 > 0 and num4 <= len(cd):
                            cd[num4] = cd[num4 - b2]
                        num4 += 1
                        if num4 >= target:
                            break
                else:
                    for j in range(b3):
                        if num4 - b2 + j < len(cd) and num4 + j < len(cd):
                            cd[num4 + j] = cd[num4 - b2 + j]
                    num4 += b3
            else:
                if num4 < len(cd):
                    cd[num4] = b2
                num4 += 1
        else:
            if num4 < len(cd):
                cd[num4] = b
            num4 += 1
    return bytes(cd[:target])

def parse_pkg(data):
    """Parse .pkg and return list of (name, decompressed_data)."""
    if len(data) < 8:
        return []
    magic = struct.unpack("<I", data[0:4])[0]
    count = struct.unpack("<I", data[4:8])[0]
    if count > 1000:
        return []
    entries = []
    for i in range(count):
        off = 8 + i * 80
        if off + 80 > len(data):
            break
        name_bytes = data[off:off+64]
        name = name_bytes.split(b'\x00')[0].decode('ascii', errors='replace')
        unc = struct.unpack("<I", data[off+64:off+68])[0]
        cmp = struct.unpack("<I", data[off+68:off+72])[0]
        foff = struct.unpack("<I", data[off+72:off+76])[0]
        flags = struct.unpack("<I", data[off+76:off+80])[0]
        entries.append((name, unc, cmp, foff, flags))
    
    results = []
    for name, unc, cmp, foff, flags in entries:
        raw = data[foff:foff+cmp]
        if flags & 1 and cmp < unc:
            dec = nislzss_decompress(raw, unc)
            if dec:
                results.append((name, dec))
            else:
                results.append((name, raw))
        else:
            results.append((name, raw))
    return results

def extract_texture_d3d11(phyre_data):
    """
    Extract RGBA8 from PhyreEngine D3D11 texture bytecode.
    
    The PhyreEngine D3D11 texture format:
    - Header contains serialized texture metadata
    - For D3D11, pixel data is stored linearly (no swizzle)
    - We need to find: format, width, height, and pixel data offset
    
    Strategy: parse the Phyre header looking for DXGI format, dimensions,
    and then locate the raw DDS pixel data.
    """
    if len(phyre_data) < 128:
        return None
    
    # Check PHYRT magic
    magic = phyre_data[0:4]
    if magic != b'RYHP':  # "PHYR" little-endian
        return None
    
    ptype = phyre_data[4]
    if ptype != ord('T'):
        return None
    
    # For D3D11 textures in CS1/CS2 PC, the format is simpler than PS4/Vita.
    # The pixel data is often raw DDS pixel data (no DDS header) stored
    # at a fixed offset after the Phyre metadata.
    
    # Try to find DDS-like patterns after the header.
    # Common texture format: DXT1/DXT5/RGBA8
    
    # Heuristic: look for the DDS pixel format fourCC in the header area
    # and extract dimensions from nearby
    data = phyre_data
    
    # Look for common texture dimensions in the first 256 bytes
    # Format info is usually stored as a DXGI format enum (u32)
    # For D3D11 CS1: common formats are DXT1 (BC1=71), DXT5 (BC3=77)
    
    # The D3D11 Phyre header structure (offset from PHYRT):
    # +0: "PHYR"
    # +4: 'T' + 3 zero bytes
    # +8: total_data_size (u32)
    # Then texture metadata...
    
    # For CS1 PC D3D11 textures, the actual DDS data (without header)
    # starts at a known offset. Let's scan for it.
    
    # First, try to find the texture dimensions by looking for
    # reasonable width/height values (powers of 2, 16-4096)
    total_size = struct.unpack_from("<I", data, 8)[0]
    
    # The D3D11 texture descriptor has:
    # - DXGI format at some offset
    # - Width/Height as u16 or u32
    # - Mip levels
    # - Then raw texture data
    
    # For CS1 PC, the format descriptor is at a known position
    # Let's look for the DXGI format value in the first 256 bytes
    # Common DXGI formats: 71=BC1, 74=BC2, 77=BC3, 28=RGBA8
    
    # Scan for format + dimension pattern
    for off in range(32, min(512, len(data) - 16)):
        # Look for a u32 that matches a known DXGI format
        fmt = struct.unpack_from("<I", data, off)[0]
        if fmt in (71, 74, 77, 28, 87, 95, 98):  # BC1, BC2, BC3, RGBA8, etc.
            # Found format! Check nearby for dimensions
            for dim_off in (off-8, off-4, off+4, off+8, off-12, off+12):
                if dim_off >= 0 and dim_off + 8 <= len(data):
                    w = struct.unpack_from("<I", data, dim_off)[0]
                    h = struct.unpack_from("<I", data, dim_off+4)[0]
                    if 16 <= w <= 4096 and 16 <= h <= 4096 and (w & (w-1)) == 0:
                        # Power of 2 dimensions found!
                        return reconstruct_texture(data, fmt, w, h, off, dim_off)
    
    return None

def reconstruct_texture(data, dxgi_format, width, height, fmt_off, dim_off):
    """Reconstruct RGBA8 from raw texture data."""
    # DXGI format -> bpp
    format_map = {
        28: ('RGBA8', 4, False),    # DXGI_FORMAT_R8G8B8A8_UNORM
        71: ('DXT1', 8, True),      # DXGI_FORMAT_BC1_UNORM  
        74: ('DXT3', 16, True),     # DXGI_FORMAT_BC2_UNORM
        77: ('DXT5', 16, True),     # DXGI_FORMAT_BC3_UNORM
        87: ('RGBA8', 4, False),    # DXGI_FORMAT_B8G8R8A8_UNORM
    }
    
    if dxgi_format not in format_map:
        return None
    
    fmt_name, bpp, is_dxt = format_map[dxgi_format]
    
    # Calculate pixel data offset:
    # The pixel data starts after the Phyre metadata.
    # For D3D11, the metadata size is typically the value at offset 8
    # minus some header overhead, or we can scan from after the metadata.
    
    # The Phyre header is at data[0], total_data_size at data[8:12]
    total_data_size = struct.unpack_from("<I", data, 8)[0]
    
    # The metadata section ends and pixel data begins.
    # The pixel data offset is typically right after the serialized objects.
    # For texture-only files, this is right after the header block.
    
    # Heuristic: total_data_size from Phyre header often points to
    # the end of metadata / start of pixel data
    pixel_offset = total_data_size if total_data_size < len(data) else 256
    
    # But total_data_size might be the size of serialized data, not the offset.
    # Let's try scanning for the actual start: pixel data for D3D11 textures
    # is aligned and has a characteristic pattern.
    
    # For DXT formats, the pixel data size per mip level is:
    # max(1, (w+3)//4) * max(1, (h+3)//4) * bpp
    
    # Try different offsets
    for try_off in [total_data_size, 256, 512, 128, 64, 32]:
        if try_off >= len(data):
            continue
        
        # Calculate expected size for main mip level
        if is_dxt:
            blocks_w = (width + 3) // 4
            blocks_h = (height + 3) // 4
            expected = blocks_w * blocks_h * bpp
        else:
            expected = width * height * bpp
        
        if try_off + expected <= len(data):
            pixel_data = data[try_off:try_off+expected]
            
            if is_dxt:
                if dxgi_format == 71:  # DXT1
                    rgba = decode_dxt1(pixel_data, width, height)
                else:  # DXT3/DXT5
                    rgba = decode_dxt5(pixel_data, width, height)
            else:
                rgba = decode_rgba8(pixel_data, width, height, dxgi_format)
            
            if rgba and len(rgba) == width * height * 4:
                return (width, height, rgba)
    
    return None

def decode_dxt1(data, width, height):
    """Decode DXT1 to RGBA8."""
    w, h = width, height
    expected = ((w + 3) // 4) * ((h + 3) // 4) * 8
    if len(data) < expected:
        return None
    out = bytearray(w * h * 4)
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            bi = ((by // 4) * ((w + 3) // 4) + (bx // 4)) * 8
            if bi + 8 > len(data):
                continue
            c0 = struct.unpack_from("<H", data, bi)[0]
            c1 = struct.unpack_from("<H", data, bi+2)[0]
            r0, g0, b0 = (c0 >> 11) & 0x1F, (c0 >> 5) & 0x3F, c0 & 0x1F
            r1, g1, b1 = (c1 >> 11) & 0x1F, (c1 >> 5) & 0x3F, c1 & 0x1F
            r0, g0, b0 = (r0<<3)|(r0>>2), (g0<<2)|(g0>>4), (b0<<3)|(b0>>2)
            r1, g1, b1 = (r1<<3)|(r1>>2), (g1<<2)|(g1>>4), (b1<<3)|(b1>>2)
            colors = [
                (r0, g0, b0, 255),
                (r1, g1, b1, 255),
                ((2*r0+r1)//3, (2*g0+g1)//3, (2*b0+b1)//3, 255),
                ((r0+2*r1)//3, (g0+2*g1)//3, (b0+2*b1)//3, 255),
            ] if c0 > c1 else [
                (r0, g0, b0, 255),
                (r1, g1, b1, 255),
                ((r0+r1)//2, (g0+g1)//2, (b0+b1)//2, 255),
                (0, 0, 0, 0),
            ]
            indices = struct.unpack_from("<I", data, bi+4)[0]
            for y in range(4):
                for x in range(4):
                    px, py = bx+x, by+y
                    if px < w and py < h:
                        ci = (indices >> ((y*4+x)*2)) & 3
                        off = (py*w+px)*4
                        out[off:off+4] = bytes(colors[ci])
    return bytes(out)

def decode_dxt5(data, width, height):
    """Decode DXT5 to RGBA8."""
    w, h = width, height
    expected = ((w + 3) // 4) * ((h + 3) // 4) * 16
    if len(data) < expected:
        return None
    out = bytearray(w * h * 4)
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            bi = ((by // 4) * ((w + 3) // 4) + (bx // 4)) * 16
            if bi + 16 > len(data):
                continue
            a0, a1 = data[bi], data[bi+1]
            alpha_bits = struct.unpack_from("<Q", data, bi)[0] >> 16
            c0 = struct.unpack_from("<H", data, bi+8)[0]
            c1 = struct.unpack_from("<H", data, bi+10)[0]
            r0, g0, b0 = (c0>>11)&0x1F, (c0>>5)&0x3F, c0&0x1F
            r1, g1, b1 = (c1>>11)&0x1F, (c1>>5)&0x3F, c1&0x1F
            r0, g0, b0 = (r0<<3)|(r0>>2), (g0<<2)|(g0>>4), (b0<<3)|(b0>>2)
            r1, g1, b1 = (r1<<3)|(r1>>2), (g1<<2)|(g1>>4), (b1<<3)|(b1>>2)
            colors = [(r0,g0,b0), (r1,g1,b1),
                      ((2*r0+r1)//3,(2*g0+g1)//3,(2*b0+b1)//3),
                      ((r0+2*r1)//3,(g0+2*g1)//3,(b0+2*b1)//3)]
            color_idx = struct.unpack_from("<I", data, bi+12)[0]
            for y in range(4):
                for x in range(4):
                    px, py = bx+x, by+y
                    if px < w and py < h:
                        ai = (alpha_bits >> ((y*4+x)*3)) & 7
                        if a0 > a1:
                            alphas = [a0, a1, (6*a0+1*a1)//7, (5*a0+2*a1)//7,
                                      (4*a0+3*a1)//7, (3*a0+4*a1)//7,
                                      (2*a0+5*a1)//7, (1*a0+6*a1)//7]
                        else:
                            alphas = [a0, a1, (4*a0+1*a1)//5, (3*a0+2*a1)//5,
                                      (2*a0+3*a1)//5, (1*a0+4*a1)//5, 0, 255]
                        ci = (color_idx >> ((y*4+x)*2)) & 3
                        off = (py*w+px)*4
                        out[off:off+3] = bytes(colors[ci])
                        out[off+3] = alphas[ai]
    return bytes(out)

def decode_rgba8(data, width, height, dxgi_format):
    """Convert raw RGBA8/BGRA8 to RGBA8."""
    expected = width * height * 4
    if len(data) < expected:
        return None
    if dxgi_format == 87:  # B8G8R8A8
        out = bytearray(expected)
        for i in range(0, expected, 4):
            out[i] = data[i+2]
            out[i+1] = data[i+1]
            out[i+2] = data[i]
            out[i+3] = data[i+3]
        return bytes(out)
    return data[:expected]

def main():
    if len(sys.argv) < 2:
        sys.stderr.write("Usage: tex_bridge.py <path-to-pkg>\n")
        sys.stderr.write("Output: width(u32LE) height(u32LE) RGBA8[]\n")
        sys.exit(1)
    
    pkg_path = sys.argv[1]
    
    with open(pkg_path, 'rb') as f:
        pkg_data = f.read()
    
    entries = parse_pkg(pkg_data)
    
    # Find the texture entry (.dds.phyre)
    tex_data = None
    for name, data in entries:
        if '.dds.phyre' in name.lower() or '.dds' in name.lower():
            tex_data = data
            break
    
    if not tex_data:
        sys.stderr.write("No texture found in pkg\n")
        sys.exit(1)
    
    result = extract_texture_d3d11(tex_data)
    if not result:
        sys.stderr.write("Failed to extract texture\n")
        sys.exit(1)
    
    width, height, rgba = result
    
    # Write to stdout
    sys.stdout.buffer.write(struct.pack("<II", width, height))
    sys.stdout.buffer.write(rgba)

if __name__ == '__main__':
    main()
