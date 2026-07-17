import json, struct, sys

path = sys.argv[1] if len(sys.argv) > 1 else "docs/break03.json"

with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)

print("=== TOP LEVEL ===")
print(f"Version: {data['version']} (0x{data['version']:08X})")
print(f"Effect name: {data['effect_name']}")
print(f"unk1: {data['unk1']}")
print(f"v26_list (textures): {len(data['v26_list'])} items -> {data['v26_list']}")
print(f"v40_list: {len(data['v40_list'])} items")
print(f"v310_list (segments): {len(data['v310_list'])} items")
print()

for i, seg in enumerate(data["v310_list"]):
    keys = [k for k in seg if k.startswith("data_")]
    print(f"[Segment {i}] name={seg['segment_name']}, fn1={seg['fn_name_1']}, fn2={seg['fn_name_2']}")
    for k in sorted(keys):
        v = seg[k]
        if isinstance(v, list):
            if len(v) > 0 and isinstance(v[0], list):
                print(f"  {k}: array[{len(v)}] of {len(v[0])}-field records")
                if len(v) <= 3:
                    for j, item in enumerate(v):
                        print(f"    [{j}]: {item}")
            else:
                print(f"  {k}: [{len(v)} fields]")
                if len(v) <= 12:
                    print(f"    {v}")
                else:
                    print(f"    {v[:8]}...")
        else:
            print(f"  {k}: {v}")
    print()
