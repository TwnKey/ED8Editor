"""Test parsing de plusieurs fichiers."""
import sys
sys.path.insert(0, 'c:/Users/Administrator/Desktop/ED8Editor/opcode_analyzer')
from opcode_analyzer import load_opcodes, parse_header, instr_len

table, _ = load_opcodes()
folder = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\dat_us"

import os
for fname in sorted(os.listdir(folder))[:10]:
    if not fname.endswith('.dat'): continue
    path = os.path.join(folder, fname)
    with open(path, 'rb') as f:
        b = bytearray(f.read())
    h = parse_header(b)
    named = [(k,nm,s,e) for k,nm,s,e in h['funcs'] if nm and not nm.startswith('__')]
    unnamed = [(k,nm,s,e) for k,nm,s,e in h['funcs'] if not nm or nm.startswith('__')]
    print(f'{fname}: {len(h["funcs"])} entries, {len(named)} named, {len(unnamed)} unnamed')
    for k, nm, s, e in named[:5]:
        print(f'  fn{k} "{nm}" 0x{s:04X}-0x{e:04X} ({e-s} octets)')
