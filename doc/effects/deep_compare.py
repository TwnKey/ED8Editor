"""Deep comparison of .eff variants to identify field semantics."""
import json, struct, os, glob, sys
from collections import defaultdict, Counter

EFFECTS_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects"

def trim_bytes_to_nullterm(instr):
    idx = instr.find(b"\x00")
    return instr[:idx] if idx >= 0 else instr

def parse_eff_full(path):
    """Full parse of .eff file into dict (like dumpjson does)."""
    with open(path, "rb") as f:
        eff = {}
        ver, = struct.unpack("I", f.read(4))
        if not ((ver >= 0x6A and ver <= 0x6D) or ver == 4):
            return None
        eff["version"] = ver
        eff["unk1"], = struct.unpack("I", f.read(4))
        
        name_len = 16
        if ver >= 0x6D:
            name_len, = struct.unpack("I", f.read(4))
        name_raw = f.read(name_len)
        eff["effect_name"] = trim_bytes_to_nullterm(name_raw).decode("ms932", errors="replace")
        
        v26, = struct.unpack("I", f.read(4))
        eff["textures"] = [trim_bytes_to_nullterm(f.read(20)).decode("ASCII") for _ in range(v26)]
        
        v40, = struct.unpack("I", f.read(4))
        eff["v40"] = [trim_bytes_to_nullterm(f.read(36)).decode("ASCII") for _ in range(v40)]
        
        v310, = struct.unpack("I", f.read(4))
        segments = []
        
        for _ in range(v310):
            seg = {}
            seg["name"] = trim_bytes_to_nullterm(f.read(16)).decode("ms932", errors="replace")
            seg["fn1"] = trim_bytes_to_nullterm(f.read(16)).decode("ASCII")
            seg["fn2"] = trim_bytes_to_nullterm(f.read(16)).decode("ASCII")
            
            sflags = 0
            if ver >= 0x6A:
                tmp = f.read(16)
                _, sflags, _, _ = struct.unpack("IIII", tmp)
            seg["struct_flags"] = sflags
            
            # data_02: 8 uint32s
            seg["d02"] = list(struct.unpack("IIIIIIII", f.read(32)))
            
            if ver >= 0x6B:
                seg["d03"] = list(struct.unpack("ff", f.read(8)))
            seg["d04"] = list(struct.unpack("ffffffffffff", f.read(48)))
            if ver < 0x6B:
                seg["d05"] = list(struct.unpack("fff", f.read(12)))
            seg["d06"] = list(struct.unpack("fffffffff", f.read(36)))
            if ver >= 0x6C:
                seg["d07"] = list(struct.unpack("ffff", f.read(16)))
            seg["d08"] = list(struct.unpack("ffffffff", f.read(32)))
            
            # Arrays: 09-0E
            def read_arr_48():
                cnt, = struct.unpack("I", f.read(4))
                return [list(struct.unpack("fffffffffIIf", f.read(48))) for _ in range(cnt)]
            
            seg["d09"] = read_arr_48()
            seg["d0A"] = read_arr_48()
            seg["d0B"] = read_arr_48()
            seg["d0C"] = read_arr_48()
            seg["d0D"] = read_arr_48()
            seg["d0E"] = read_arr_48()
            
            for flag in [0x1000000, 0x4000000, 0x8000000, 0x20000000]:
                if sflags & flag:
                    read_arr_48()  # skip
            
            if sflags & 0x02000000:
                cnt, = struct.unpack("I", f.read(4))
                for _ in range(cnt):
                    subcnt, = struct.unpack("I", f.read(4))
                    f.read(subcnt * 48)
            
            seg["d14"] = [list(struct.unpack("IIIfIIIIffff", f.read(48))) for _ in range(struct.unpack("I", f.read(4))[0])]
            
            if ver <= 4:
                seg["d15"] = list(struct.unpack("ff", f.read(8)))
                sflags = 3
            if sflags & 0x002:
                seg["d16"] = list(struct.unpack("ffffffffffffffff", f.read(64)))
            if sflags & 0x001:
                if ver >= 0x6B:
                    cnt, = struct.unpack("I", f.read(4))
                    f.read(cnt * 72)
                else:
                    f.read(16)
            if sflags & 0x010:
                f.read(16)
            if sflags & 0x004:
                f.read(32)
            if sflags & 0x008:
                f.read(96)
            if ver >= 0x6A:
                cnt, = struct.unpack("I", f.read(4))
                f.read(cnt * 12)
            if sflags & 0x020:
                f.read(24)
            if sflags & 0x040:
                f.read(16)
            if sflags & 0x080:
                f.read(32)
            if sflags & 0x100:
                f.read(8)
            if sflags & 0x200:
                f.read(52)
            
            segments.append(seg)
        
        eff["segments"] = segments
        return eff

def compare_effects(eff1, eff2, name1, name2):
    """Compare two effects and report differences."""
    diffs = []
    segs1 = eff1["segments"]
    segs2 = eff2["segments"]
    
    for i, (s1, s2) in enumerate(zip(segs1, segs2)):
        prefix = f"  seg[{i}] '{s1['name']}'"
        # Compare d02
        if s1["d02"] != s2["d02"]:
            for j, (a, b) in enumerate(zip(s1["d02"], s2["d02"])):
                if a != b:
                    diffs.append(f"{prefix} d02[{j}]: {a} -> {b}")
        # Compare d04
        if s1["d04"] != s2["d04"]:
            for j, (a, b) in enumerate(zip(s1["d04"], s2["d04"])):
                if abs(a - b) > 0.0001:
                    diffs.append(f"{prefix} d04[{j}]: {a:.4f} -> {b:.4f}")
        # Compare d06
        if "d06" in s1 and "d06" in s2:
            if s1["d06"] != s2["d06"]:
                for j, (a, b) in enumerate(zip(s1["d06"], s2["d06"])):
                    if abs(a - b) > 0.0001:
                        diffs.append(f"{prefix} d06[{j}]: {a:.4f} -> {b:.4f}")
        # Compare d08
        if s1["d08"] != s2["d08"]:
            for j, (a, b) in enumerate(zip(s1["d08"], s2["d08"])):
                if abs(a - b) > 0.0001:
                    diffs.append(f"{prefix} d08[{j}]: {a:.4f} -> {b:.4f}")
        # Compare d09
        if len(s1["d09"]) != len(s2["d09"]):
            diffs.append(f"{prefix} d09 count: {len(s1['d09'])} -> {len(s2['d09'])}")
        # Compare d0B
        if s1["d0B"] != s2["d0B"]:
            if len(s1["d0B"]) == len(s2["d0B"]):
                for k, (a, b) in enumerate(zip(s1["d0B"], s2["d0B"])):
                    for j, (va, vb) in enumerate(zip(a, b)):
                        if isinstance(va, float) and abs(va - vb) > 0.0001:
                            diffs.append(f"{prefix} d0B[{k}][{j}]: {va:.4f} -> {vb:.4f}")
                        elif va != vb:
                            diffs.append(f"{prefix} d0B[{k}][{j}]: {va} -> {vb}")
            else:
                diffs.append(f"{prefix} d0B count: {len(s1['d0B'])} -> {len(s2['d0B'])}")
    return diffs

# Find related files (same base name with different suffixes)
all_files = glob.glob(os.path.join(EFFECTS_DIR, "**", "*.eff"), recursive=True)
by_base = defaultdict(list)
for fp in all_files:
    name = os.path.splitext(os.path.basename(fp))[0]
    # Group by removing trailing letters/numbers
    base = name.rstrip("abcdefghijklmnopqrstuvwxyz0123456789_")
    if base != name:
        by_base[base].append(fp)

print("=== COMPARING EFFECT VARIANTS (same base name) ===\n")

compared = set()
for base, files in sorted(by_base.items()):
    if len(files) < 2:
        continue
    # Parse and compare first two
    try:
        e1 = parse_eff_full(files[0])
        e2 = parse_eff_full(files[1])
        if not e1 or not e2:
            continue
        n1 = os.path.basename(files[0])
        n2 = os.path.basename(files[1])
        diffs = compare_effects(e1, e2, n1, n2)
        if diffs:
            print(f"\n{'='*60}")
            print(f"  {n1}  vs  {n2}")
            print(f"  ({e1['effect_name']}  vs  {e2['effect_name']})")
            print(f"  Textures: {e1['textures']}  vs  {e2['textures']}")
            print(f"  Segments: {len(e1['segments'])}  vs  {len(e2['segments'])}")
            for d in diffs[:30]:  # Limit output
                print(d)
            if len(diffs) > 30:
                print(f"  ... and {len(diffs)-30} more differences")
            compared.add(base)
    except Exception as ex:
        print(f"  ERROR comparing {base}: {ex}")

print(f"\n\nCompared {len(compared)} variant groups")

# Now analyze structure: what d02 values are common?
print("\n\n=== D02 PATTERNS (first 3 uint32s of each segment) ===\n")
d02_patterns = defaultdict(list)
all_effs = []
for fp in all_files:
    try:
        e = parse_eff_full(fp)
        if e:
            all_effs.append((os.path.basename(fp), e))
    except:
        pass

for name, eff in all_effs[:500]:  # Sample 500
    for seg in eff["segments"]:
        key = (seg["d02"][0], seg["d02"][1], seg["d02"][2])
        d02_patterns[key].append((name, seg["name"]))

# Print most common d02 patterns
print("Top d02[0], d02[1], d02[2] combinations:")
for (v0, v1, v2), examples in sorted(d02_patterns.items(), key=lambda x: -len(x[1]))[:20]:
    sample_names = set()
    for fn, sn in examples[:5]:
        sample_names.add(f"{fn}/{sn}")
    print(f"  ({v0:3d}, {v1:3d}, {v2:6d}): {len(examples):4d}x  ex: {', '.join(list(sample_names)[:3])}")

# Analyze d04 patterns
print("\n\n=== D04 PATTERNS (first 4 floats) ===\n")
d04_patterns = defaultdict(list)
for name, eff in all_effs[:500]:
    for seg in eff["segments"]:
        key = tuple(round(v, 2) for v in seg["d04"][:4])
        d04_patterns[key].append(seg["name"])

for key, examples in sorted(d04_patterns.items(), key=lambda x: -len(x[1]))[:15]:
    uniq = list(set(examples))[:5]
    print(f"  {key}: {len(examples)}x  ({', '.join(uniq)})")

# Analyze struct_flags
print("\n\n=== STRUCTURE FLAGS ===\n")
flag_counts = Counter()
for name, eff in all_effs:
    for seg in eff["segments"]:
        flag_counts[seg["struct_flags"]] += 1

for flag, cnt in flag_counts.most_common(20):
    print(f"  0x{flag:08X}: {cnt}x")
