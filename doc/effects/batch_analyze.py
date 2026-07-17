"""Batch analyze all .eff files in the game directory to find patterns."""
import json, struct, os, glob, sys
from collections import Counter, defaultdict

EFFECTS_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects"
def trim_bytes_to_nullterm(instr):
    idx = instr.find(b"\x00")
    return instr[:idx] if idx >= 0 else instr

def parse_eff_inplace(path):
    """Parse an .eff file and return the dict."""
    with open(path, "rb") as f:
        eff_root = {}
        ver, = struct.unpack("I", f.read(4))
        if not ((ver >= 0x6A and ver <= 0x6D) or ver == 4):
            return None
        eff_root["version"] = ver
        unk1, = struct.unpack("I", f.read(4))
        eff_root["unk1"] = unk1
        effect_name_length = 16
        if ver >= 0x6D:
            effect_name_length, = struct.unpack("I", f.read(4))
        effect_name_untrimmed = f.read(effect_name_length)
        effect_name = trim_bytes_to_nullterm(effect_name_untrimmed).decode(encoding="ms932", errors="replace")
        eff_root["effect_name"] = effect_name

        # v26
        v26, = struct.unpack("I", f.read(4))
        v26_list = []
        for _ in range(v26):
            v26_list.append(trim_bytes_to_nullterm(f.read(20)).decode("ASCII"))
        eff_root["v26_list"] = v26_list

        # v40
        v40, = struct.unpack("I", f.read(4))
        v40_list = []
        for _ in range(v40):
            v40_list.append(trim_bytes_to_nullterm(f.read(36)).decode("ASCII"))
        eff_root["v40_list"] = v40_list

        # v310 segments - quick parse, just segment names and data presence
        v310, = struct.unpack("I", f.read(4))
        segments = []
        for _ in range(v310):
            seg = {}
            seg_name_raw = trim_bytes_to_nullterm(f.read(16))
            seg["segment_name"] = seg_name_raw.decode(encoding="ms932", errors="replace")
            seg["fn_name_1"] = trim_bytes_to_nullterm(f.read(16)).decode("ASCII")
            seg["fn_name_2"] = trim_bytes_to_nullterm(f.read(16)).decode("ASCII")
            
            struct_flags = 0
            if ver >= 0x6A:
                tmp = f.read(16)
                _, struct_flags, _, _ = struct.unpack("IIII", tmp)
            seg["struct_flags"] = struct_flags
            
            # Skip remaining data based on flags - we only care about structure for now
            f.read(32)  # data_02
            if ver >= 0x6B:
                f.read(8)   # data_03
            f.read(48)  # data_04
            if ver < 0x6B:
                f.read(12)  # data_05
            f.read(36)  # data_06
            if ver >= 0x6C:
                f.read(16)  # data_07
            f.read(32)  # data_08
            
            # Array blocks 09-0E
            for _ in range(6):
                cnt, = struct.unpack("I", f.read(4))
                f.read(cnt * 48)
            
            # Conditional blocks 0F-12
            for flag in [0x1000000, 0x4000000, 0x8000000, 0x20000000]:
                if struct_flags & flag:
                    cnt, = struct.unpack("I", f.read(4))
                    f.read(cnt * 48)
            
            # data_13 nested
            if struct_flags & 0x02000000:
                cnt, = struct.unpack("I", f.read(4))
                for _ in range(cnt):
                    subcnt, = struct.unpack("I", f.read(4))
                    f.read(subcnt * 48)
            
            # data_14
            cnt, = struct.unpack("I", f.read(4))
            f.read(cnt * 48)
            
            # data_15-21 conditional
            if ver <= 4:
                f.read(8)
                struct_flags = 3
            if struct_flags & 0x002:
                f.read(64)
            if struct_flags & 0x001:
                if ver >= 0x6B:
                    cnt, = struct.unpack("I", f.read(4))
                    f.read(cnt * 72)
                else:
                    f.read(16)
            if struct_flags & 0x010:
                f.read(16)
            if struct_flags & 0x004:
                f.read(32)
            if struct_flags & 0x008:
                f.read(96)
            if ver >= 0x6A:
                cnt, = struct.unpack("I", f.read(4))
                f.read(cnt * 12)
            if struct_flags & 0x020:
                f.read(24)
            if struct_flags & 0x040:
                f.read(16)
            if struct_flags & 0x080:
                f.read(32)
            if struct_flags & 0x100:
                f.read(8)
            if struct_flags & 0x200:
                f.read(52)
            
            segments.append(seg)
        
        eff_root["v310_list"] = segments
        return eff_root

# Collect all .eff files recursively
eff_files = glob.glob(os.path.join(EFFECTS_DIR, "**", "*.eff"), recursive=True)
print(f"Found {len(eff_files)} .eff files")

# Parse all
all_data = {}
versions = Counter()
all_seg_names = Counter()
all_fns = Counter()
all_textures = Counter()
seg_count_dist = Counter()

for fp in sorted(eff_files):
    try:
        data = parse_eff_inplace(fp)
        if data:
            name = os.path.basename(fp)
            all_data[name] = data
            versions[data["version"]] += 1
            seg_count_dist[len(data["v310_list"])] += 1
            for t in data["v26_list"]:
                all_textures[t] += 1
            for seg in data["v310_list"]:
                all_seg_names[seg["segment_name"]] += 1
                if seg["fn_name_1"]:
                    all_fns[seg["fn_name_1"]] += 1
                if seg["fn_name_2"]:
                    all_fns[seg["fn_name_2"]] += 1
    except Exception as e:
        print(f"  SKIP {fp}: {e}")

print(f"\nSuccessfully parsed: {len(all_data)} files")
print(f"\n=== VERSIONS ===")
for v, c in versions.most_common():
    label = {0x6A:"CS1", 0x6B:"CS2", 0x6C:"CS3", 0x6D:"CS4", 4:"Reverie"}.get(v, "???")
    print(f"  0x{v:08X} ({label}): {c} files")

print(f"\n=== SEGMENT COUNTS ===")
for cnt, c in sorted(seg_count_dist.items()):
    print(f"  {cnt} segments: {c} files")

print(f"\n=== TOP SEGMENT NAMES ===")
for name, c in all_seg_names.most_common(50):
    print(f"  {c:4d}x '{name}'")

print(f"\n=== TOP FUNCTION NAMES ===")
for name, c in all_fns.most_common(30):
    print(f"  {c:4d}x '{name}'")

print(f"\n=== TOP TEXTURES ===")
for name, c in all_textures.most_common(30):
    print(f"  {c:4d}x '{name}'")

# Now deep-analyze a few representative files
print(f"\n\n=== DEEP ANALYSIS OF REPRESENTATIVE FILES ===")
sample_files = ["break03.eff", "default.eff", "heal01.eff", "fire01.eff", "c005.eff"]
for sf in sample_files:
    if sf in all_data:
        d = all_data[sf]
        print(f"\n--- {sf} (v={d['version']}, {len(d['v310_list'])} segments) ---")
        for seg in d["v310_list"]:
            sf_str = f"0x{seg['struct_flags']:08X}" if seg['struct_flags'] else "none"
            print(f"  [{seg['segment_name']}] fn1={seg['fn_name_1']}, fn2={seg['fn_name_2']}, flags={sf_str}")
