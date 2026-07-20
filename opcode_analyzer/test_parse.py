"""Test rapide du parsing."""
import sys
sys.path.insert(0, 'c:/Users/Administrator/Desktop/ED8Editor/opcode_analyzer')
from opcode_analyzer import load_opcodes, parse_header, instr_len, extract_operands

table, _ = load_opcodes()
path = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\dat_us\a0000.dat"
with open(path, 'rb') as f:
    b = bytearray(f.read())

h = parse_header(b)
print(f'Fichier a0000.dat: {len(b)} octets')
print(f'Fonctions trouvees: {len(h["funcs"])}')
for k, nm, start, end in h['funcs'][:5]:
    print(f'  fn{k} "{nm}" 0x{start:04X}-0x{end:04X} ({end-start} octets)')
    p = start
    for _ in range(5):
        if p >= end: break
        op = b[p]
        var = b[p+1] if p+1 < end else 0
        L, err = instr_len(b, p, table)
        ops = extract_operands(b, p, table)
        print(f'    @0x{p:04X} op{op} ({table.get(op,{}).get("name","?")}) var=0x{var:02X} len={L} operands={len(ops)}')
        if err: print(f'      ERR: {err}')
        if L and L > 0: p += L
        else: break
