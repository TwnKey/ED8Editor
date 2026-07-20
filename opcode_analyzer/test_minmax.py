"""Test min/max compute."""
import sys
sys.path.insert(0, 'c:/Users/Administrator/Desktop/ED8Editor/opcode_analyzer')
from opcode_analyzer import load_opcodes, scan_scripts

table, _ = load_opcodes()
folder = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\dat_us"
results = scan_scripts(folder, table, {})

# Trouver les opcodes les plus fréquents
from collections import Counter
opvar_counts = Counter((op, var) for _, _, _, _, op, var, _ in results)
print("Top 10 (opcode, variant):")
for (op, var), count in opvar_counts.most_common(10):
    name = table.get(op, {}).get('name', f'OP{op}')
    print(f"  op{op} ({name}) var=0x{var:02X}: {count} occurrences")

# Choisir un opcode fréquent avec plusieurs opérandes
for (op, var), count in opvar_counts.most_common(50):
    if count > 5:
        # Trouver une occurrence
        for fname, fn_name, fn_idx, offset, o, v, operands in results:
            if o == op and v == var and len(operands) >= 2:
                print(f"\n--- Test op{op} var=0x{var:02X} ({count} occ, {len(operands)} operands) ---")
                # Simuler _compute_minmax
                from opcode_analyzer import u8, u16, u32, struct
                
                all_ops = [(oo, vv, ops) for _, _, _, _, oo, vv, ops in results if oo == op and vv == var]
                for oi in range(len(operands)):
                    raws = []
                    pt = None
                    for _, _, ops in all_ops:
                        if oi < len(ops):
                            ptt, raw, off = ops[oi]
                            if pt is None: pt = ptt
                            raws.append(raw)
                    
                    if pt == "U8":
                        vals = [r[0] for r in raws]
                    elif pt == "U16":
                        vals = [r[0] | (r[1] << 8) for r in raws]
                    elif pt in ("U32", "I32"):
                        vals = [r[0] | (r[1] << 8) | (r[2] << 16) | (r[3] << 24) for r in raws]
                    elif pt == "CSTR":
                        vals = [len(r) for r in raws]
                    else:
                        vals = [len(r) for r in raws]
                    
                    print(f"  Operand #{oi} ({pt}): min=0x{min(vals):X} max=0x{max(vals):X} ({len(raws)} values)")
                break
