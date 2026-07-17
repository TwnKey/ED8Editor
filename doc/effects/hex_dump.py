"""Hex dump data_0b records from a .eff file for Cheat Engine."""
import sys
import struct
import json

def dump_data_0b(filepath, segment_idx=None, segment_name=None):
    with open(filepath, 'rb') as f:
        data = f.read()
    
    # Parse JSON from binary (the .eff is JSON with some binary embedded?)
    # Actually, let's read the file as-is
    print(f"File: {filepath}")
    print(f"Size: {len(data)} bytes")
    
    # Try to read as JSON first (eff_editor saves as JSON)
    try:
        obj = json.loads(data.decode('utf-8'))
        segments = obj.get('segments', [])
        print(f"Segments: {len(segments)}")
        
        for i, seg in enumerate(segments):
            if segment_idx is not None and i != segment_idx:
                continue
            if segment_name and seg.get('name', '') != segment_name:
                continue
            
            print(f"\n=== Segment [{i}] {seg.get('name', '?')} ===")
            d0b = seg.get('data_0b', [])
            if not d0b:
                print("  (no data_0b)")
                continue
            
            print(f"  records: {len(d0b)}")
            print(f"  {'#':>4} {'f[0]':>10} {'f[1]':>10} {'f[2]':>10} {'f[3]':>10} {'t(f[8])':>10} {'i[0]':>6} {'i[1]':>6} {'trail':>10}")
            for ri, r in enumerate(d0b):
                floats = r.get('floats', [0]*9)
                ints = r.get('ints', [0, 0])
                trailing = r.get('trailing', 0.0)
                print(f"  {ri:>4} {floats[0]:10.4f} {floats[1]:10.4f} {floats[2]:10.4f} {floats[3]:10.4f} {floats[8]:10.4f} {ints[0]:>6} {ints[1]:>6} {trailing:10.4f}")
                
                # Raw hex: 9 f32 + 2 u32 + 1 f32 = 48 bytes
                raw = b''
                for fv in floats:
                    raw += struct.pack('<f', fv)
                for iv in ints:
                    raw += struct.pack('<I', iv)
                raw += struct.pack('<f', trailing)
                print(f"       raw: {' '.join(f'{b:02X}' for b in raw)}")
            
            print()
    except Exception as e:
        print(f"JSON parse failed ({e}), trying raw...")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python hex_dump.py <file.eff> [segment_idx] [segment_name]")
        sys.exit(1)
    
    idx = int(sys.argv[2]) if len(sys.argv) > 2 else None
    name = sys.argv[3] if len(sys.argv) > 3 else None
    dump_data_0b(sys.argv[1], idx, name)
