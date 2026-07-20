#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CS1 Opcode Argument Analyzer
----------------------------
Outil d'analyse interactive des arguments d'opcodes dans les scripts Cold Steel.

Fonctionnalités :
- Parse tous les .dat d'un dossier et extrait les fonctions (pas les tables)
- Pour chaque opcode, affiche chaque opérande sous tous les formats possibles
  (float, int, unsigned int, short, hex, string, byte, unsigned byte, etc.)
- Permet de choisir le format, donner un nom à l'opérande, et nommer l'opcode
- Gère les variantes (octet après l'opcode) pour des noms différents
- Persiste les décisions dans state.json pour ne pas ré-analyser
- Balaye tous les fichiers jusqu'à ce que tout soit « committé »

Basé sur cs1_opcodes.json et cs1_validate.py.
"""

import json
import os
import struct
import sys
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
from pathlib import Path

# ---------------------------------------------------------------------------
# Constantes / chemins
# ---------------------------------------------------------------------------
HERE = os.path.dirname(os.path.abspath(__file__))

# Cherche cs1_opcodes.json : priorité au dossier courant, sinon dans outputs/cs1
OPCODES_PATHS = [
    os.path.join(HERE, "cs1_opcodes.json"),
    os.path.join(HERE, "cs1_opcodes_typed.json"),
    r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\cs1_opcodes_typed.json",
]

STATE_FILE = os.path.join(HERE, "state.json")
SETTINGS_FILE = os.path.join(HERE, "settings.json")

# ---------------------------------------------------------------------------
# Chargement des opcodes
# ---------------------------------------------------------------------------
def find_opcodes_file():
    for p in OPCODES_PATHS:
        if os.path.exists(p):
            return p
    # Cherche aussi dans le workspace
    for root, dirs, files in os.walk(HERE):
        for f in files:
            if f == "cs1_opcodes.json":
                return os.path.join(root, f)
    return None

def load_opcodes(path=None):
    if path is None:
        path = find_opcodes_file()
    if path is None:
        raise FileNotFoundError("cs1_opcodes.json introuvable")
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    # Nouveau format : {"_format":..., "opcodes": {"0": {...}, "1": {...}}}
    if isinstance(data, dict) and "opcodes" in data:
        raw = data["opcodes"]
        table = {}
        oplist = []
        for k, v in raw.items():
            v["op"] = int(k)  # garantir op numérique
            # Normaliser 'read' -> 'prog' pour compatibilité
            v["prog"] = _normalize_read(v.get("read", []))
            table[int(k)] = v
            oplist.append(v)
        oplist.sort(key=lambda x: x["op"])
        return table, oplist
    # Ancien format : liste
    table = {}
    for d in data:
        table[d["op"]] = d
    return table, data

def _normalize_read(read):
    """Convertit le champ 'read' du nouveau format en tokens compatibles.
    Garde les types natifs : u8, s8, u16, s16, u32, s32, f32, ptr32, string, expr, dialog."""
    result = []
    for item in read:
        if isinstance(item, dict):
            t = item.get("t", "UNKNOWN")
            # Types natifs → tokens directs
            native = {"u8", "s8", "u16", "s16", "u32", "s32", "f32", "ptr32",
                      "string", "expr", "dialog"}
            if t in native:
                result.append(t)
            elif t == "fill":
                n = item.get("to", item.get("size", 0))
                result.append(f"FIX:{n}")
            elif t == "bytes":
                n = item.get("size", 0)
                result.append(f"FIX:{n}")
            elif "loop" in item or "switch" in item or "if" in item:
                result.append("UNKNOWN")
            else:
                result.append(t)
        else:
            result.append(str(item))
    return result

# ---------------------------------------------------------------------------
# Parsing binaire (inspiré de cs1_validate.py)
# ---------------------------------------------------------------------------
def u8(b, p):
    return b[p]

def u16(b, p):
    return b[p] | (b[p + 1] << 8)

def i16(b, p):
    v = u16(b, p)
    return v - 0x10000 if v >= 0x8000 else v

def u32(b, p):
    return b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)

def i32(b, p):
    v = u32(b, p)
    return v - 0x100000000 if v >= 0x80000000 else v

def f32(b, p):
    return struct.unpack('<f', bytes(b[p:p+4]))[0]

def read_cstr(b, p):
    q = p
    while q < len(b) and b[q] != 0:
        q += 1
    return (q + 1) - p if q < len(b) else None

# --- expression RPN ---
def expr_subop_size(x):
    if x == 0x00: return 5
    if x == 0x1e: return 3
    if x in (0x1f, 0x20, 0x23): return 2
    if x == 0x21: return 4
    if x == 0x1c: return 5
    return 1

def skip_expr(b, p, table=None):
    """Retourne la taille de l'expression, ou None si erreur."""
    i = p
    while i < len(b):
        x = b[i]
        if x == 0x01:
            return (i + 1) - p
        if x == 0x1c:
            if i + 1 >= len(b):
                return None
            if table:
                # Re-dispatch via la table d'opcodes
                from .instr_len import instr_len_func
                sl = instr_len_func(b, i + 1, table)[0]
            else:
                sl = None
            if not sl or sl <= 0:
                return None
            i += 1 + sl
            continue
        s = expr_subop_size(x)
        if s < 0:
            return None
        i += s
    return None

# --- dialogue ---
def skip_dialog(b, p):
    i = p
    while i < len(b):
        x = b[i]
        if x == 0x00:
            return (i + 1) - p
        if x < 0x20:
            i += 3 if x == 0x10 else (5 if x in (0x11, 0x12) else 1)
        else:
            i += 1
    return None

# --- Handlers spéciaux de longueur (copiés de cs1_validate.py) ---
def _make_special_handlers():
    """Retourne un dict opcode -> fonction de longueur."""
    # Ces fonctions sont identiques à celles de cs1_validate.py
    # (compressées pour lisibilité)

    def len_op145(b, p):
        if p+2>len(b): return None
        return 11 if b[p+1]==0 else 2
    def len_op129(b, p):
        if p+2>len(b): return None
        return 4 if b[p+1]==0 else 2
    def len_op33(b, p):
        if p+2>len(b): return None
        return 16 if b[p+1]==1 else 8
    def len_op60(b, p):
        if p+7>len(b): return None
        n=1+6
        if u16(b,p+3)==0xFFFF: n+=12
        return n
    def len_op118(b, p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (0,1):
            c=read_cstr(b,p+2)
            if c is None: return None
            return 2+c+2
        return 2
    def len_op57(b, p):
        if p+4>len(b): return None
        code=b[p+1]; n=4
        if code not in (0x0C,0x05,0x69,0x0A,0x0B,0xFF,0xFE): n+=4
        return n
    def len_op112(b, p):
        if p+2>len(b): return None
        if b[p+1]==1:
            c=read_cstr(b,p+2); return None if c is None else 2+c+3
        return 2
    def len_op113(b, p):
        if p+2>len(b): return None
        return 12 if b[p+1]==0 else 2
    def len_op137(b, p):
        if p+2>len(b): return None
        return 6 if b[p+1]==0 else 2
    def len_op136(b, p):
        if p+2>len(b): return None
        return 3 if b[p+1]==0 else 2
    def len_op138(b, p):
        if p+2>len(b): return None
        if b[p+1]==0:
            c1=read_cstr(b,p+2)
            if c1 is None: return None
            c2=read_cstr(b,p+2+c1)
            return None if c2 is None else 2+c1+c2
        return 2
    def len_op31_106(b, p):
        if p+3>len(b): return None
        b1=b[p+1]; b2=b[p+2]; n=3
        if b2<4:
            if b1==0x00: n+=6
            elif b1==0x01:
                c=read_cstr(b,p+3); n+= (c+2) if c else 0
                if c is None: return None
            elif b1==0x02: n+=5
            elif b1==0x04: n+=1
            elif b1==0x05: n+=4
            elif b1 in (0x06,0x07): n+=2
        return n
    def len_op95(b, p):
        if p+4>len(b): return None
        t=b[p+3]
        if t in (0,3): return 11
        if t==2: return 16
        return 4
    def len_op63(b, p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (0,1,4,5,6,9,10,11): return 4
        if t==8: return 8
        return 2
    def len_op115(b, p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (0,1): return 2
        if t==3: return 14
        if t==5: return 6
        return 4
    def len_op101(b, p):
        if p+2>len(b): return None
        t=b[p+1]
        return {0:7,1:5,2:6,3:7,4:5,5:7,6:6,7:5,8:8,9:5}.get(t,5)

    OP45_C={0:[],1:[],2:[15],3:[3,'C',14],4:[16],5:[7],6:[],7:[2],8:[3],9:[12],
            10:[],11:[7],12:[15],13:[15],14:[11],15:[14],16:[],17:[16],18:[2],
            19:[18],20:[3,'C',14],21:[10],22:[7],23:[2]}
    OP73_C={0:[32],1:[32],2:[32],3:[32],4:[32],5:[],6:[],7:[],8:[],9:[],10:[32],
            11:[],12:[],13:['C','C',4],14:[],15:[],16:[],17:[],18:[],19:[],
            20:['C','C',10],21:['C'],22:[],23:[5],24:[3],25:['C','C'],26:[],27:[],
            28:['C',4],29:[],30:[],31:[],32:[],33:[4],34:[],35:[2],36:[2],37:[],
            38:[5,'C'],39:[],40:[2],41:[3]}
    OP48_C={0:[13],1:[3],2:[1],3:[7],4:[13],5:[4],6:[2],7:[]}
    OP49_L={1:6,0x33:6,2:4,0x34:4,3:10,0x35:10,5:4,0x37:4,6:4,0x38:4,0x39:4,
            0x3a:12,0x64:8,0x65:58,0x96:4}
    OP36_C={0:[],1:[],2:[],3:[],4:[],5:[],6:[],7:[],8:[],9:[],10:[],11:[]}
    OP40_L={0:7,1:12,2:3,4:3,6:3,14:10,15:6,16:4,17:5,18:4,19:20,20:4,21:24,
            23:4,25:4,26:4,28:4,50:24,52:8,53:20,54:12,55:27,60:14,70:14,72:4,
            90:8,91:7,93:4,94:4,95:4,96:4,97:9,98:5,99:4,101:4,102:4,107:4,108:4,
            109:4,110:18,113:9,114:13,115:4,116:4,117:4,121:4,122:5,123:2,126:9}
    OP72_C={0:[],1:[],2:[],3:[],4:[],5:[],6:[],7:[],8:[],9:[]}
    OP75_C={0:[],1:[],2:[],3:[]}
    OP81_C={0:[],1:[],2:[],3:[],4:[]}
    OP91_L={0:6,1:6,4:6}
    OP92_C={0:[2],1:[2],2:[],3:[]}
    OP94_C={0:[8],1:[2],2:[6],3:[6],4:[2],5:[6],6:[10],7:[2],8:[2],9:[2],
            10:[5],11:[2],12:[4],13:[4]}
    OP96_C={0:[],1:[],2:[],3:[],4:[],5:[],6:[]}

    def _switch(b,p,cases,default=2,base=2):
        if p+2>len(b): return None
        t=b[p+1]; seq=cases.get(t)
        if seq is None: return default
        i=p+base
        for tk in seq:
            if tk=='C':
                c=read_cstr(b,i)
                if c is None: return None
                i+=c
            else: i+=tk
        return i-p
    def len_op45(b,p): return _switch(b,p,OP45_C)
    def len_op73(b,p): return _switch(b,p,OP73_C)
    def len_op48(b,p): return _switch(b,p,OP48_C)
    def len_op49(b,p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (0,0x32):
            c=read_cstr(b,p+38); return None if c is None else 38+c
        if t==0xfe: return 4
        if t>=0xff: return 14
        if t==0xfd: return 3
        return OP49_L.get(t,2)
    def len_op36(b,p): return _switch(b,p,OP36_C,default=14,base=14)
    def len_op40(b,p):
        if p+2>len(b): return None
        return OP40_L.get(b[p+1],2)
    def len_op55(b,p):
        return 4 if p+4<=len(b) else None
    def len_op72(b,p): return _switch(b,p,OP72_C,default=2,base=2)
    def len_op75(b,p): return _switch(b,p,OP75_C,default=24,base=24)
    def len_op81(b,p): return _switch(b,p,OP81_C,default=2,base=2)
    def len_op91(b,p):
        if p+2>len(b): return None
        return OP91_L.get(b[p+1],4)
    def len_op92(b,p): return _switch(b,p,OP92_C,default=4,base=4)
    def len_op94(b,p): return _switch(b,p,OP94_C,default=2,base=2)
    def len_op96(b,p): return _switch(b,p,OP96_C,default=18,base=18)
    def len_op100(b,p):
        return 8 if p+8<=len(b) else None
    def len_op103(b,p):
        if p+4>len(b): return None
        t=b[p+3]
        if t in (1,2): return 6
        if t in (3,4): return 5
        if t==5: return 4
        if t==6: return 6
        return 3
    def len_op105(b,p):
        if p+4>len(b): return None
        t=b[p+3]
        if t==0: return 8
        if t==1: return 4
        if t==2:
            c=read_cstr(b,p+14); return None if c is None else 14+c
        if t==3:
            i=p+4; c=read_cstr(b,i)
            if c is None: return None
            i+=c+8; c=read_cstr(b,i)
            return None if c is None else i+c-p
        return 4
    def len_op121(b,p):
        return 18 if p+18<=len(b) else None
    def len_op25(b,p):
        if p+2>len(b): return None
        return {0:11,1:7,2:6,5:11}.get(b[p+1],2)
    def len_op68(b,p):
        if p+2>len(b): return None
        return {0:20,1:4,2:11,3:23,4:6,5:6}.get(b[p+1],4)
    def len_op54(b,p):
        if p+5>len(b): return None
        u=b[p+3]|(b[p+4]<<8)
        return 28 if u in (0xFE02,0xFE03) else 24
    def len_op50(b,p):
        if p+2>len(b): return None
        t=b[p+1]
        if t==0x01:
            i=p+5; c=read_cstr(b,i)
            if c is None: return None
            i+=c; c=read_cstr(b,i)
            if c is None: return None
            return i+c-p
        if t==0x02: return 8
        if t==0x03:
            i=p+4
            for _ in range(4):
                c=read_cstr(b,i)
                if c is None: return None
                i+=c
            return i-p
        if t==0x04:
            c=read_cstr(b,p+4); return None if c is None else 4+c
        return 4
    def len_op39(b,p):
        c=read_cstr(b,p+4); return None if c is None else 4+c
    def len_op127(b,p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (0,1):
            i=p+6; c=read_cstr(b,i)
            if c is None: return None
            i+=c; c=read_cstr(b,i)
            if c is None: return None
            return i+c-p
        if t in (0xFE,0xFF):
            c=read_cstr(b,p+4); return None if c is None else 4+c+4
        return 2
    def len_op134(b,p):
        if p+2>len(b): return None
        return 6 if b[p+1] in (0,1) else 2
    def len_op154(b,p):
        if p+2>len(b): return None
        t=b[p+1]
        if t in (1,2): return 10
        if t in (3,4): return 20
        return 2
    def len_op93(b,p):
        if p+2>len(b): return None
        t=b[p+1]; c=read_cstr(b,p+2)
        if c is None: return None
        i=p+2+c
        if t in (0,1): return i+2-p
        if t in (2,3,4): return i+12-p
        if t==7: return i+19-p
        if t==8: return i+15-p
        return i-p
    def len_op144(b,p):
        if p+2>len(b): return None
        return 4 if b[p+1]==0 else 2
    def len_op32(b,p):
        if p+4>len(b): return None
        t=b[p+1]; i=p+4
        if t==0:
            c=read_cstr(b,i)
            if c is None: return None
            i+=c; c=read_cstr(b,i)
            if c is None: return None
            return i+c+1-p
        if t in (1,2):
            c=read_cstr(b,i)
            if c is None: return None
            i+=c; c=read_cstr(b,i)
            if c is None: return None
            return i+c+1-p
        return 4
    def len_op19(b,p):
        i=p+3
        for _ in range(3):
            c=read_cstr(b,i)
            if c is None: return None
            i+=c
        i+=33
        for _ in range(2):
            c=read_cstr(b,i)
            if c is None: return None
            i+=c
        i+=15
        return i-p if i<=len(b) else None
    def len_op111(b,p):
        if p+2>len(b): return None
        if b[p+1]==0: return 16
        c=read_cstr(b,p+2)
        return None if c is None else 8+c

    return {
        145:len_op145,129:len_op129,33:len_op33,111:len_op111,127:len_op127,
        32:len_op32,93:len_op93,134:len_op134,154:len_op154,144:len_op144,
        19:len_op19,39:len_op39,50:len_op50,54:len_op54,68:len_op68,25:len_op25,
        40:len_op40,55:len_op55,72:len_op72,75:len_op75,91:len_op91,92:len_op92,
        94:len_op94,96:len_op96,100:len_op100,103:len_op103,105:len_op105,
        121:len_op121,45:len_op45,73:len_op73,48:len_op48,49:len_op49,
        60:len_op60,118:len_op118,57:len_op57,112:len_op112,113:len_op113,
        136:len_op136,137:len_op137,138:len_op138,31:len_op31_106,106:len_op31_106,
        95:len_op95,63:len_op63,115:len_op115,101:len_op101,
    }

SPECIAL = _make_special_handlers()

def instr_len(b, p, table):
    """Retourne (longueur, erreur) pour une instruction à la position p."""
    if p >= len(b):
        return None, "position hors limites"
    op = b[p]
    if op >= 160:
        return None, f"opcode {op} invalide"
    if op in SPECIAL:
        n = SPECIAL[op](b, p)
        return (n, None) if n else (None, f"special op{op} hors limites")
    i = p + 1
    if op not in table:
        return None, f"opcode {op} absent de la table"
    for tok in table[op]["prog"]:
        if tok.startswith("FIX:"):
            i += int(tok[4:])
        elif tok in ("U8", "u8", "s8"):
            i += 1
        elif tok in ("U16", "u16", "s16"):
            i += 2
        elif tok in ("U32", "I32", "u32", "s32", "f32", "ptr32"):
            i += 4
        elif tok in ("CSTR", "string"):
            c = read_cstr(b, i)
            i += c if c else 0
            if c is None:
                return None, "string non terminé"
        elif tok in ("DIALOG", "dialog"):
            s = skip_dialog(b, i)
            if s is None:
                return None, "DIALOG non terminé"
            i += s
        elif tok == "EXPR":
            # Pour l'analyseur, on ne re-dispatch pas le 0x1c
            s = skip_expr(b, i)
            if s is None:
                return None, "EXPR non terminée"
            i += s
        elif tok == "SWITCH":
            if i >= len(b):
                return None, 'SWITCH hors limites'
            n = b[i]
            i += 1 + n * 6 + 4
        elif tok == "UNKNOWN":
            return None, f"opcode {op} non résolu (UNKNOWN)"
        if i > len(b):
            return None, "dépassement buffer"
    return i - p, None


def parse_header(b):
    """Parse le header d'un fichier .dat. Retourne dict avec 'funcs'."""
    nb = u32(b, 0x14)
    ptr_area = u32(b, 0x08)
    name_area = u32(b, 0x04)
    magic0 = u32(b, 0)
    magic2 = u32(b, 0x1c)

    funcs = []
    for k in range(nb):
        start = u32(b, ptr_area + 4 * k)
        npos = i16(b, ptr_area + 4 * nb + 2 * k)
        end = u32(b, ptr_area + 4 * (k + 1)) if k < nb - 1 else len(b)
        nm = ""
        if 0 <= npos < len(b):
            e = npos
            while e < len(b) and b[e] != 0:
                e += 1
            nm = b[npos:e].decode('latin1', 'replace')
        funcs.append((k, nm, start, end))
    return {
        "magic0": magic0, "magic2": magic2,
        "nb": nb, "name_pos": name_area, "ptr_area": ptr_area,
        "funcs": funcs
    }


# ---------------------------------------------------------------------------
# Représentation multi-format d'un opérande
# ---------------------------------------------------------------------------
def operand_representations(raw_bytes):
    """Retourne un dict de représentations possibles pour des bytes bruts."""
    n = len(raw_bytes)
    reps = {}

    # Toujours afficher l'hex
    reps["hex"] = " ".join(f"{b:02X}" for b in raw_bytes)

    if n == 1:
        reps["u8"] = str(raw_bytes[0])
        reps["s8"] = str(raw_bytes[0] - 256 if raw_bytes[0] >= 128 else raw_bytes[0])
    elif n == 2:
        val = raw_bytes[0] | (raw_bytes[1] << 8)
        reps["u16"] = str(val)
        reps["s16"] = str(val - 65536 if val >= 32768 else val)
        reps["hex"] += f"  (0x{val:04X})"
        # ASCII 2 chars
        try:
            ascii2 = bytes(raw_bytes).decode('ascii')
            if all(0x20 <= c < 0x7F for c in raw_bytes):
                reps["ascii"] = repr(ascii2)
        except:
            pass
    elif n == 4:
        val = raw_bytes[0] | (raw_bytes[1] << 8) | (raw_bytes[2] << 16) | (raw_bytes[3] << 24)
        reps["u32"] = str(val)
        reps["s32"] = str(val - 4294967296 if val >= 2147483648 else val)
        reps["hex"] += f"  (0x{val:08X})"
        try:
            f = struct.unpack('<f', bytes(raw_bytes))[0]
            reps["float"] = f"{f:.6f}"
        except:
            pass
        # ASCII 4 chars
        try:
            ascii4 = bytes(raw_bytes).decode('ascii')
            if all(0x20 <= c < 0x7F for c in raw_bytes):
                reps["ascii"] = repr(ascii4)
        except:
            pass
    elif n >= 4 and n % 4 == 0:
        # Plusieurs valeurs 32-bit
        for i in range(0, n, 4):
            chunk = raw_bytes[i:i+4]
            reps[f"u32[{i//4}]"] = str(u32(chunk, 0))
            try:
                reps[f"float[{i//4}]"] = f"{struct.unpack('<f', bytes(chunk))[0]:.6f}"
            except:
                pass
    elif n > 4:
        reps["hex"] = " ".join(f"{b:02X}" for b in raw_bytes)

    # Essayer de décoder comme chaîne
    try:
        s = bytes(raw_bytes).decode('latin1')
        if all(c >= 0x20 and c < 0x7F or c in (0x0A, 0x0D) for c in raw_bytes):
            reps["string"] = repr(s)
        elif all(c >= 0x20 or c == 0 for c in raw_bytes):
            s2 = s.rstrip('\x00')
            if s2:
                reps["string"] = repr(s2)
    except:
        pass

    return reps


def operand_formats_for_type(prog_type):
    """Suggère les formats pertinents selon le type de prog."""
    if prog_type == "U8":
        return ["u8", "s8", "hex", "ascii"]
    elif prog_type == "U16":
        return ["u16", "s16", "hex", "ascii"]
    elif prog_type in ("U32", "I32"):
        return ["u32", "s32", "float", "hex", "ascii"]
    elif prog_type == "CSTR":
        return ["string", "hex"]
    elif prog_type.startswith("FIX:"):
        n = int(prog_type[4:])
        if n == 1:
            return ["u8", "s8", "hex", "ascii"]
        elif n == 2:
            return ["u16", "s16", "hex", "ascii"]
        elif n == 4:
            return ["u32", "s32", "float", "hex", "ascii"]
        else:
            return ["hex"]
    else:
        return ["hex", "u32", "s32", "float"]

# ---------------------------------------------------------------------------
# Extraction des opérandes BRUTS d'une instruction
# ---------------------------------------------------------------------------
def extract_operands(b, p, table):
    """Extrait les bytes bruts de chaque opérande d'une instruction.
    Retourne [(prog_type, raw_bytes, offset), ...]."""
    op = b[p]
    if op >= 160 or op not in table:
        return []

    info = table[op]
    operands = []
    i = p + 1  # après l'opcode

    for tok in info["prog"]:
        if i > len(b):
            break
        offset = i
        if tok.startswith("FIX:"):
            n = int(tok[4:])
            operands.append((tok, list(b[i:i+n]), offset))
            i += n
        elif tok in ("U8", "u8", "s8"):
            operands.append((tok, list(b[i:i+1]), offset))
            i += 1
        elif tok in ("U16", "u16", "s16"):
            operands.append((tok, list(b[i:i+2]), offset))
            i += 2
        elif tok in ("U32", "I32", "u32", "s32", "f32", "ptr32"):
            operands.append((tok, list(b[i:i+4]), offset))
            i += 4
        elif tok in ("CSTR", "string"):
            c = read_cstr(b, i)
            if c:
                operands.append((tok, list(b[i:i+c]), offset))  # inclut le \0
                i += c
            else:
                operands.append((tok, list(b[i:i+1]), offset))
                i += 1
        elif tok == "DIALOG":
            s = skip_dialog(b, i)
            if s:
                operands.append((tok, list(b[i:i+s]), offset))
                i += s
            else:
                break
        elif tok == "EXPR":
            s = skip_expr(b, i)
            if s:
                operands.append((tok, list(b[i:i+s]), offset))
                i += s
            else:
                break
        elif tok == "SWITCH":
            if i < len(b):
                n = b[i]
                total = 1 + n * 6 + 4
                operands.append((tok, list(b[i:i+total]), offset))
                i += total
            else:
                break
        elif tok == "UNKNOWN":
            break

    return operands


# ---------------------------------------------------------------------------
# Gestion de l'état (state.json)
# ---------------------------------------------------------------------------
def load_state():
    if os.path.exists(STATE_FILE):
        try:
            with open(STATE_FILE, "r", encoding="utf-8") as f:
                content = f.read().strip()
                if not content:
                    return {"opcode_names": {}, "operand_defs": {}, "committed": []}
                return json.loads(content)
        except (json.JSONDecodeError, Exception):
            return {"opcode_names": {}, "operand_defs": {}, "committed": []}
    return {"opcode_names": {}, "operand_defs": {}, "committed": []}

def save_state(state):
    with open(STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2, ensure_ascii=False)

def load_settings():
    if os.path.exists(SETTINGS_FILE):
        try:
            with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
                content = f.read().strip()
                if not content:
                    return {"script_folder": "", "last_file": ""}
                return json.loads(content)
        except (json.JSONDecodeError, Exception):
            return {"script_folder": "", "last_file": ""}
    return {"script_folder": "", "last_file": ""}

def save_settings(settings):
    with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
        json.dump(settings, f, indent=2)

def make_key(opcode, variant, table):
    """Retourne la clé unique : juste l'opcode (plus de variant)."""
    return str(opcode)

# ---------------------------------------------------------------------------
# Collecte des données de tous les scripts
# ---------------------------------------------------------------------------
def scan_scripts(folder, table, state, progress_callback=None):
    """Scanne tous les .dat et retourne les instructions non committées.
    Retourne: list de (filename, func_name, func_idx, offset, opcode, variant, operands)
    où operands = [(prog_type, raw_bytes, offset), ...]
    """
    committed = set(state.get("committed", []))
    results = []

    dat_files = sorted(Path(folder).glob("*.dat"))
    total = len(dat_files)

    for fi, fpath in enumerate(dat_files):
        if progress_callback:
            progress_callback(fi, total, fpath.name)

        try:
            with open(fpath, "rb") as f:
                b = bytearray(f.read())
        except Exception:
            continue

        try:
            h = parse_header(b)
        except Exception:
            continue

        for func_idx, func_name, start, end in h["funcs"]:
            # Ignorer les tables : pas de nom, ou préfixes de table
            if not func_name or func_name.startswith("__"):
                continue

            p = start
            while p < end:
                opcode = b[p]
                variant = b[p + 1] if p + 1 < end else 0
                key = make_key(opcode, variant, table)

                if key in committed:
                    # Trouver la longueur et sauter
                    L, err = instr_len(b, p, table)
                    if L and L > 0:
                        p += L
                        continue

                # Extraire les opérandes
                operands = extract_operands(b, p, table)

                # Filtrer : ignorer les opcodes UNKNOWN ou à 1 seul octet (variantes pures)
                if operands:
                    has_unknown = any(t == "UNKNOWN" for t, _, _ in operands)
                    total_bytes = sum(len(raw) for _, raw, _ in operands)
                    is_single_byte = (total_bytes <= 1 and len(operands) <= 1)

                    if not has_unknown and not is_single_byte:
                        results.append((
                            fpath.name, func_name, func_idx,
                            p, opcode, variant, operands
                        ))

                L, err = instr_len(b, p, table)
                if not L or L <= 0:
                    break
                p += L

    return results


# ---------------------------------------------------------------------------
# Interface Tkinter
# ---------------------------------------------------------------------------
class OpcodeAnalyzerApp:
    def __init__(self, root):
        self.root = root
        self.root.title("CS1 Opcode Argument Analyzer")
        self.root.geometry("1400x900")

        # Charger les opcodes
        self.table, self.opcodes_list = load_opcodes()

        # État
        self.state = load_state()
        self.settings = load_settings()
        self.all_instructions = []
        self.current_idx = 0
        self._hex_selected = None
        self.script_folder = self.settings.get("script_folder", "")

        # Construction UI
        self._build_ui()

        # Charger automatiquement si dossier connu
        if self.script_folder and os.path.isdir(self.script_folder):
            self._scan()

    def _build_ui(self):
        # --- Barre supérieure ---
        top_frame = ttk.Frame(self.root, padding=5)
        top_frame.pack(fill=tk.X)

        ttk.Label(top_frame, text="Dossier scripts:").pack(side=tk.LEFT)
        self.folder_var = tk.StringVar(value=self.script_folder)
        ttk.Entry(top_frame, textvariable=self.folder_var, width=60).pack(side=tk.LEFT, padx=5)
        ttk.Button(top_frame, text="Parcourir...", command=self._browse_folder).pack(side=tk.LEFT, padx=2)
        ttk.Button(top_frame, text="Scanner", command=self._scan).pack(side=tk.LEFT, padx=2)

        ttk.Separator(self.root, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=5)

        # --- PanedWindow principal ---
        self.pw = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        self.pw.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)

        # --- Panneau gauche : liste des instructions ---
        left_frame = ttk.Frame(self.pw)
        self.pw.add(left_frame, weight=1)

        ttk.Label(left_frame, text="Instructions à analyser:", font=("", 10, "bold")).pack(anchor=tk.W)

        # === Barre de filtres ===
        filter_bar = ttk.Frame(left_frame)
        filter_bar.pack(fill=tk.X, pady=2)

        # Filtre texte libre
        self.filter_var = tk.StringVar()
        self.filter_var.trace_add("write", lambda *a: self._refresh_list())
        ttk.Label(filter_bar, text="Texte:").grid(row=0, column=0, sticky=tk.W)
        self.filter_entry = ttk.Entry(filter_bar, textvariable=self.filter_var, width=18)
        self.filter_entry.grid(row=0, column=1, sticky=tk.W, padx=2)

        # Filtre par opcode
        ttk.Label(filter_bar, text="Opcode:").grid(row=1, column=0, sticky=tk.W, pady=2)
        self.opcode_filter_var = tk.StringVar(value="Tous")
        opcode_choices = ["Tous"] + [f"{op} - {self.table[op]['name']}" for op in sorted(self.table.keys())]
        self.opcode_combo = ttk.Combobox(filter_bar, textvariable=self.opcode_filter_var,
                                         values=opcode_choices, width=20, state="readonly")
        self.opcode_combo.set("Tous")
        self.opcode_combo.grid(row=1, column=1, sticky=tk.W, padx=2, pady=2)
        self.opcode_combo.bind("<<ComboboxSelected>>", lambda e: self._refresh_list())

        # Treeview pour la liste
        # Treeview pour la liste
        cols = ("file", "func", "offset", "op", "name", "prog")
        self.tree = ttk.Treeview(left_frame, columns=cols, show="headings", height=20)
        self.tree.heading("file", text="Fichier")
        self.tree.heading("func", text="Fonction")
        self.tree.heading("offset", text="Offset")
        self.tree.heading("op", text="Op")
        self.tree.heading("name", text="Nom")
        self.tree.heading("prog", text="Structure")
        self.tree.column("file", width=70)
        self.tree.column("func", width=110)
        self.tree.column("offset", width=55)
        self.tree.column("op", width=35)
        self.tree.column("name", width=100)
        self.tree.column("prog", width=220)
        self.tree.pack(fill=tk.BOTH, expand=True)

        scrollbar = ttk.Scrollbar(left_frame, orient=tk.VERTICAL, command=self.tree.yview)
        self.tree.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        self.tree.bind("<Double-1>", lambda e: self._commit_current())

        # Stats
        self.stats_var = tk.StringVar(value="Prêt")
        ttk.Label(left_frame, textvariable=self.stats_var).pack(anchor=tk.W, pady=2)

        # --- Panneau droit : analyse ---
        right_frame = ttk.Frame(self.pw)
        self.pw.add(right_frame, weight=3)

        # Info instruction
        info_frame = ttk.LabelFrame(right_frame, text="Instruction courante", padding=5)
        info_frame.pack(fill=tk.X, pady=5)

        self.info_text = tk.Text(info_frame, height=4, wrap=tk.WORD, state=tk.DISABLED)
        self.info_text.pack(fill=tk.X)

        # Canvas scrollable pour les opérandes (double scroll)
        canvas_container = ttk.Frame(right_frame)
        canvas_container.pack(fill=tk.BOTH, expand=True)

        self.operand_canvas = tk.Canvas(canvas_container, borderwidth=0, highlightthickness=0)
        self.h_scrollbar = ttk.Scrollbar(canvas_container, orient=tk.HORIZONTAL, command=self.operand_canvas.xview)
        self.v_scrollbar = ttk.Scrollbar(canvas_container, orient=tk.VERTICAL, command=self.operand_canvas.yview)
        self.operand_frame = ttk.Frame(self.operand_canvas)

        self.operand_canvas.configure(xscrollcommand=self.h_scrollbar.set, yscrollcommand=self.v_scrollbar.set)

        # Pack layout: canvas fills, scrollbars on edges
        self.h_scrollbar.pack(side=tk.BOTTOM, fill=tk.X)
        self.v_scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self.operand_canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        self.canvas_window = self.operand_canvas.create_window((0, 0), window=self.operand_frame, anchor=tk.NW)
        self.operand_frame.bind("<Configure>", self._on_frame_configure)
        self.operand_canvas.bind("<Configure>", self._on_canvas_configure)

        # Boutons d'action en bas
        action_frame = ttk.Frame(right_frame)
        action_frame.pack(fill=tk.X, pady=5)

        ttk.Button(action_frame, text="◀ Précédent", command=self._prev_instr).pack(side=tk.LEFT, padx=2)
        ttk.Button(action_frame, text="Suivant ▶", command=self._next_instr).pack(side=tk.LEFT, padx=2)
        ttk.Button(action_frame, text="✓ Committer cette instruction", command=self._commit_current).pack(side=tk.LEFT, padx=10)
        ttk.Button(action_frame, text="Tout committer (opcode+variant)", command=self._commit_all_current).pack(side=tk.LEFT, padx=2)

        # Barre de progression
        self.progress_var = tk.StringVar(value="")
        ttk.Label(right_frame, textvariable=self.progress_var).pack(anchor=tk.E, pady=2)

        # Status bar
        self.status_var = tk.StringVar(value="Prêt — sélectionnez un dossier et cliquez Scanner")
        ttk.Label(self.root, textvariable=self.status_var, relief=tk.SUNKEN, anchor=tk.W).pack(fill=tk.X, side=tk.BOTTOM)

    def _on_frame_configure(self, event):
        self.operand_canvas.configure(scrollregion=self.operand_canvas.bbox("all"))

    def _on_canvas_configure(self, event):
        # Toujours donner au contenu la largeur max(canvas, contenu réel)
        canvas_width = event.width
        self.operand_frame.update_idletasks()
        content_width = self.operand_frame.winfo_reqwidth()
        self.operand_canvas.itemconfig(self.canvas_window, width=max(canvas_width, content_width))

    def _browse_folder(self):
        folder = filedialog.askdirectory(title="Dossier des scripts .dat")
        if folder:
            self.folder_var.set(folder)
            self.script_folder = folder
            self.settings["script_folder"] = folder
            save_settings(self.settings)

    def _scan(self):
        folder = self.folder_var.get()
        if not folder or not os.path.isdir(folder):
            messagebox.showerror("Erreur", "Dossier invalide")
            return

        self.script_folder = folder
        self.settings["script_folder"] = folder
        save_settings(self.settings)

        self.status_var.set("Scan en cours...")
        self.root.update()

        self.all_instructions = scan_scripts(folder, self.table, self.state)
        self.current_idx = 0

        # Purger les déjà committés (au cas où le scan les aurait laissés passer)
        committed_keys = set(self.state.get("committed", []))
        if committed_keys:
            before = len(self.all_instructions)
            self.all_instructions = [
                instr for instr in self.all_instructions
                if make_key(instr[4], instr[5], self.table) not in committed_keys
            ]
            if before != len(self.all_instructions):
                self.status_var.set(f"Scan: {before} → {len(self.all_instructions)} instructions ({before - len(self.all_instructions)} déjà résolues ignorées)")

        self._refresh_list()
        self.status_var.set(self.status_var.get() + f" | {len(self.all_instructions)} instructions à analyser")

    def _refresh_list(self):
        self.tree.delete(*self.tree.get_children())
        filt = self.filter_var.get().lower().strip()

        # Extraire le filtre opcode
        opcode_filter_str = self.opcode_filter_var.get()
        if opcode_filter_str == "Tous" or not opcode_filter_str:
            filter_opcode = None
        else:
            # Format: "45 - OP45_NAME"
            try:
                filter_opcode = int(opcode_filter_str.split(" - ")[0])
            except ValueError:
                filter_opcode = None

        for i, (fname, fn_name, fn_idx, offset, opcode, variant, operands) in enumerate(self.all_instructions):
            op_info = self.table.get(opcode, {})
            op_name = op_info.get("name", f"OP{opcode}")
            prog_str = " ".join(t for t, _, _ in operands)

            # Filtre opcode
            if filter_opcode is not None and opcode != filter_opcode:
                continue

            # Appliquer filtre texte
            if filt:
                search = f"{fname} {fn_name} {opcode} {op_name} {prog_str}".lower()
                if filt not in search:
                    continue

            self.tree.insert("", tk.END, iid=str(i),
                             values=(fname, fn_name, f"0x{offset:04X}", str(opcode),
                                     op_name, prog_str))

        committed_count = len(self.state.get("committed", []))
        total = len(self.all_instructions)
        unique = len(set(f"{op}_{var}" for _, _, _, _, op, var, _ in self.all_instructions))
        visible = len(self.tree.get_children())
        remaining = unique - committed_count
        self.stats_var.set(f"Total occurrences: {total} | Affichées: {visible} | Uniques: {unique} | Résolus: {committed_count} | Restants: {remaining}")

    def _on_select(self, event):
        sel = self.tree.selection()
        if not sel:
            return
        self.current_idx = int(sel[0])
        self._show_current()

    def _compute_minmax(self, opcode, variant):
        """Calcule min/max pour chaque opérande d'un (opcode, variant)
        à travers toutes les occurrences dans all_instructions.
        Retourne une liste de dicts {min_hex, max_hex, min_val, max_val, count} par opérande."""
        # Collecter toutes les occurrences
        occurrences = []
        for (fname, fn_name, fn_idx, offset, op, var, operands) in self.all_instructions:
            if op == opcode and var == variant:
                occurrences.append(operands)

        if not occurrences:
            return []

        # Déterminer le nombre max d'opérandes
        max_ops = max(len(ops) for ops in occurrences) if occurrences else 0
        result = []

        for oi in range(max_ops):
            values = []
            prog_type = None
            for ops in occurrences:
                if oi < len(ops):
                    pt, raw, off = ops[oi]
                    if prog_type is None:
                        prog_type = pt
                    values.append((pt, raw))

            if not values:
                result.append({"count": 0})
                continue

            # Déterminer comment comparer selon le type
            pt = values[0][0]
            raws = [raw for _, raw in values]

            # Fonction pour convertir en valeur comparable (robuste)
            def to_comparable(raw):
                n = len(raw)
                if n == 0:
                    return 0
                if n == 1:
                    return raw[0]
                elif n == 2:
                    return raw[0] | (raw[1] << 8)
                elif n == 4:
                    return raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24)
                else:
                    # N > 4 ou taille variable : entier little-endian
                    val = 0
                    for k, b in enumerate(raw):
                        val |= b << (8 * k)
                    return val

            comps = [to_comparable(r) for r in raws]
            min_val = min(comps)
            max_val = max(comps)

            # Formater en hex (robuste, basé sur la taille réelle)
            def fmt_hex(val):
                if isinstance(val, int):
                    n = len(raws[0]) if raws else 1
                    if n == 1: return f"0x{val:02X}"
                    elif n == 2: return f"0x{val:04X}"
                    elif n == 4: return f"0x{val:08X}"
                    else: return f"0x{val:0{n*2}X}"
                else:
                    return " ".join(f"{b:02X}" for b in val)

            entry = {
                "count": len(values),
                "min_hex": fmt_hex(min_val),
                "max_hex": fmt_hex(max_val),
                "min_val": min_val,
                "max_val": max_val,
                "prog_type": pt,
            }

            # Min/max float pour les tailles 4 octets
            if any(len(r) == 4 for r in raws):
                try:
                    all_floats = []
                    for raw in raws:
                        if len(raw) == 4:
                            f = struct.unpack('<f', bytes(raw))[0]
                            all_floats.append(f)
                    if all_floats:
                        entry["min_float"] = f"{min(all_floats):.4f}"
                        entry["max_float"] = f"{max(all_floats):.4f}"
                except:
                    pass

            result.append(entry)

        return result

    def _show_current(self):
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        fname, fn_name, fn_idx, offset, opcode, variant, operands = self.all_instructions[self.current_idx]
        op_info = self.table.get(opcode, {})
        default_name = op_info.get("name", f"OP{opcode}")
        key = make_key(opcode, variant, self.table)

        # Info
        self.info_text.configure(state=tk.NORMAL)
        self.info_text.delete(1.0, tk.END)
        info = f"Fichier: {fname}  |  Fonction: {fn_name} (#{fn_idx})\n"
        info += f"Offset: 0x{offset:04X}  |  Opcode: {opcode} (0x{opcode:02X})\n"
        info += f"Catégorie: {op_info.get('cat', '?')}  |  Confiance: {op_info.get('conf', '?')}\n"
        info += f"Note: {op_info.get('note', '—')}\n"
        self.info_text.insert(1.0, info)
        self.info_text.configure(state=tk.DISABLED)

        # Opérandes
        for w in self.operand_frame.winfo_children():
            w.destroy()

        if not operands:
            ttk.Label(self.operand_frame, text="(aucun opérande)", foreground="gray").pack(pady=10)
        else:
            self._build_hex_view(operands, opcode, variant, key)

        self.progress_var.set(f"Instruction {self.current_idx + 1} / {len(self.all_instructions)}")

    # ===================================================================
    #  Vue hex dump interactive
    # ===================================================================

    COLORS = {
        "U8": "#BBDEFB", "u8": "#BBDEFB", "s8": "#BBDEFB",
        "U16": "#C8E6C9", "u16": "#C8E6C9", "s16": "#C8E6C9",
        "U32": "#FFF9C4", "I32": "#FFF9C4", "u32": "#FFF9C4", "s32": "#FFF9C4",
        "f32": "#FFECB3", "ptr32": "#FFCC80",
        "FIX:": "#E0E0E0", "CSTR": "#F8BBD0", "string": "#F8BBD0",
        "EXPR": "#E1BEE7", "expr": "#E1BEE7",
        "SWITCH": "#FFE0B2", "DIALOG": "#B2EBF2", "dialog": "#B2EBF2",
        "UNKNOWN": "#FFCDD2",
    }

    def _color_for(self, prog_type):
        for prefix, color in self.COLORS.items():
            if prog_type.startswith(prefix):
                return color
        return "#E0E0E0"

    def _build_hex_view(self, operands, opcode, variant, key):
        """Affiche tous les opérandes comme un hex dump interactif."""

        hv_frame = ttk.LabelFrame(self.operand_frame,
                                  text="Hex dump — clic entre octets = split | clic séparateur = merge | drag séparateur = resize",
                                  padding=5)
        hv_frame.pack(fill=tk.X, pady=3, padx=2)

        all_bytes = []
        boundaries = [0]
        for prog_type, raw, off in operands:
            all_bytes.extend(raw)
            boundaries.append(len(all_bytes))

        n = len(all_bytes)
        if n == 0:
            ttk.Label(hv_frame, text="(vide)").pack()
            return

        CHAR_W = 32
        GAP = 2
        ROW_H = 30
        PAD = 14
        BYTES_PER_ROW = 32  # retour à la ligne tous les 32 octets

        total_rows = (n + BYTES_PER_ROW - 1) // BYTES_PER_ROW
        can_width = BYTES_PER_ROW * CHAR_W + 40
        can_height = total_rows * ROW_H + PAD * 2 + 40

        self._hex_canvas = tk.Canvas(hv_frame, width=can_width, height=can_height,
                                     borderwidth=0, highlightthickness=0)
        self._hex_canvas.pack(fill=tk.X, pady=5)

        # Dessiner les blocs (vert si validé) - multi-lignes
        for bi in range(len(boundaries) - 1):
            start = boundaries[bi]
            end = boundaries[bi + 1]
            if start >= end: continue
            prog_type = operands[bi][0]

            opdef_key = f"{make_key(opcode, variant, self.table)}_{bi}"
            d = self.state.get("operand_defs", {}).get(opdef_key, {})
            if d.get("format"):
                color = "#A5D6A7"
                outline = "#2E7D32"
                lbl = d["format"]
                # Label sur la première ligne du bloc
                first_row = start // BYTES_PER_ROW
                first_col = start % BYTES_PER_ROW
                lx = PAD + first_col * CHAR_W + CHAR_W // 2
                ly = PAD + first_row * ROW_H - 8
                self._hex_canvas.create_text(lx, ly, text=lbl, font=("", 7, "bold"),
                                             fill="#2E7D32", anchor=tk.S, tags=("label",))
            else:
                color = self._color_for(prog_type)
                outline = "#999"

            # Dessiner un seul rectangle par ligne pour ce bloc
            for byte_i in range(start, end):
                row = byte_i // BYTES_PER_ROW
                col = byte_i % BYTES_PER_ROW
                if col == 0 or byte_i == start or (byte_i - 1) // BYTES_PER_ROW != row:
                    last = min(BYTES_PER_ROW - 1, (end - 1) % BYTES_PER_ROW if (end - 1) // BYTES_PER_ROW == row else BYTES_PER_ROW - 1)
                    x1 = PAD + col * CHAR_W - 1
                    x2 = PAD + last * CHAR_W + CHAR_W - GAP + 1
                    y1 = PAD + row * ROW_H
                    y2 = PAD + row * ROW_H + ROW_H
                    self._hex_canvas.create_rectangle(x1, y1, x2, y2,
                                                     fill=color, outline=outline, width=1, tags=("block",))

        # Texte hex avec retour à la ligne
        for i, b in enumerate(all_bytes):
            row = i // BYTES_PER_ROW
            col = i % BYTES_PER_ROW
            x = PAD + col * CHAR_W + CHAR_W // 2
            y = PAD + row * ROW_H + ROW_H // 2
            self._hex_canvas.create_text(x, y, text=f"{b:02X}",
                                         font=("Consolas", 10, "bold"),
                                         anchor=tk.CENTER, tags=("hex", f"byte{i}"))

        # Séparateurs multi-lignes
        for bi in range(1, len(boundaries) - 1):
            pos = boundaries[bi]
            if pos <= 0 or pos >= n: continue
            # Ligne verticale traversant toutes les rows concernées
            start_row = (pos - 1) // BYTES_PER_ROW
            end_row = pos // BYTES_PER_ROW
            col = pos % BYTES_PER_ROW
            if col == 0 and pos > 0:
                col = BYTES_PER_ROW
                start_row = (pos - 1) // BYTES_PER_ROW
                end_row = start_row
            x = PAD + col * CHAR_W - GAP // 2
            y1 = PAD + start_row * ROW_H
            y2 = PAD + end_row * ROW_H + ROW_H
            line = self._hex_canvas.create_line(x, y1, x, y2,
                                                fill="#1565C0", width=3, tags=("sep", f"sep{bi}"))
            self._hex_canvas.tag_bind(line, "<Button-3>",
                                      lambda e, b=bi: self._merge_at_boundary(b, operands, opcode, variant, key))
            self._hex_canvas.tag_bind(line, "<B1-Motion>",
                                      lambda e, b=bi: self._drag_boundary(e, b, all_bytes, boundaries, operands, opcode, variant, key))

        # Clic gauche = sélection, clic droit = split/merge
        self._hex_canvas.bind("<Button-1>",
                              lambda e: self._on_hex_select(e, all_bytes, boundaries, operands, opcode, variant, key))
        self._hex_canvas.bind("<Button-3>",
                              lambda e: self._on_hex_right_click(e, all_bytes, boundaries, operands, opcode, variant, key))

        # Texte range (en dessous des octets)
        range_y = PAD + total_rows * ROW_H + 8
        self._hex_canvas.create_text(PAD, range_y,
                                     text="", font=("Consolas", 9),
                                     anchor=tk.W, tags=("range_text",),
                                     fill="#006600")

        # Barre d'outils
        self._hex_tools_frame = ttk.Frame(hv_frame)
        self._hex_tools_frame.pack(fill=tk.X, pady=(5, 0))
        self._refresh_type_buttons(operands, opcode, variant, key)

        # Légende
        legend_frame = ttk.Frame(hv_frame)
        legend_frame.pack(fill=tk.X, pady=(3, 0))
        for prog_type, color in [("U8", "#BBDEFB"), ("U16", "#C8E6C9"), ("U32", "#FFF9C4"),
                                  ("FIX", "#E0E0E0"), ("CSTR", "#F8BBD0"), ("EXPR", "#E1BEE7")]:
            lf = tk.Frame(legend_frame, bg=color, width=12, height=12)
            lf.pack(side=tk.LEFT, padx=1)
            ttk.Label(legend_frame, text=prog_type, font=("", 7)).pack(side=tk.LEFT, padx=(0, 5))

        # Stocker l'état
        self._hex_boundaries = boundaries
        self._hex_operands = operands
        self._hex_selected = None
        self._hex_all_bytes = all_bytes

    def _refresh_type_buttons(self, operands, opcode, variant, key):
        """Met à jour les boutons de type selon la sélection."""
        for w in self._hex_tools_frame.winfo_children():
            w.destroy()

        # Taille de l'opérande sélectionné
        sel_size = 0
        if self._hex_selected is not None and self._hex_selected < len(operands):
            sel_size = len(operands[self._hex_selected][1])

        # Boutons adaptés à la taille
        if sel_size == 1:
            types = ["u8", "s8"]
        elif sel_size == 2:
            types = ["u16", "s16", "ptr16"]
        elif sel_size == 4:
            types = ["u32", "s32", "float", "ptr32"]
        else:
            types = ["str", "str_raw"]
        types.append("hex")  # toujours disponible

        ttk.Label(self._hex_tools_frame, text="Type:").pack(side=tk.LEFT, padx=2)
        for t in types:
            ttk.Button(self._hex_tools_frame, text=t, width=6,
                       command=lambda fmt=t: self._apply_type_to_selected(fmt, operands, opcode, variant, key)
                       ).pack(side=tk.LEFT, padx=1)

        if sel_size > 0:
            ttk.Label(self._hex_tools_frame, text=f"  ({sel_size} octets)",
                      font=("", 8), foreground="gray").pack(side=tk.LEFT, padx=5)

        # Panneau d'info : valeur multi-format + min/max
        if self._hex_selected is not None and self._hex_selected < len(operands):
            self._show_operand_info(operands[self._hex_selected], opcode, variant, key)

    def _show_operand_info(self, operand, opcode, variant, key):
        """Affiche les interprétations pour l'opérande sélectionné."""
        info_frame = ttk.Frame(self._hex_tools_frame)
        info_frame.pack(fill=tk.X, pady=(3, 0))

        prog_type, raw_bytes, off = operand
        reps = operand_representations(raw_bytes)

        values_text = " | ".join(f"{k}: {v}" for k, v in reps.items() if k != "hex")
        if values_text:
            ttk.Label(info_frame, text=values_text,
                      font=("Consolas", 9), foreground="#333").pack(anchor=tk.W, padx=2)

    def _compute_range_for_bytes(self, opcode, variant, byte_start, byte_end):
        """Calcule min/max pour la plage d'octets [byte_start:byte_end] à travers
        toutes les occurrences du même (opcode, variant)."""
        all_values = []
        n_bytes = byte_end - byte_start

        for instr in self.all_instructions:
            if instr[4] == opcode and instr[5] == variant:
                # Concaténer tous les bytes de cette instruction
                all_raw = []
                for _, raw, _ in instr[6]:
                    all_raw.extend(raw)
                if byte_end <= len(all_raw):
                    chunk = all_raw[byte_start:byte_end]
                    # Convertir en entier little-endian
                    val = 0
                    for k, b in enumerate(chunk):
                        val |= b << (8 * k)
                    all_values.append(val)

        if len(all_values) <= 1:
            return {"count": len(all_values)}

        min_val = min(all_values)
        max_val = max(all_values)

        def fmt_hex(v, n=n_bytes):
            return f"0x{v:0{n*2}X}"

        result = {
            "count": len(all_values),
            "min_hex": fmt_hex(min_val),
            "max_hex": fmt_hex(max_val),
        }

        # Float min/max pour 4 octets
        if n_bytes == 4:
            try:
                all_floats = []
                for v in all_values:
                    f = struct.unpack('<f', struct.pack('<I', v))[0]
                    all_floats.append(f)
                result["min_float"] = f"{min(all_floats):.4f}"
                result["max_float"] = f"{max(all_floats):.4f}"
            except:
                pass

        return result

    def _on_hex_select(self, event, all_bytes, boundaries, operands, opcode, variant, key):
        """Clic gauche : sélectionne un opérande."""
        CHAR_W = 32
        GAP = 2
        PAD = 14
        ROW_H = 30
        BYTES_PER_ROW = 32
        n = len(all_bytes)

        row = (event.y - PAD) // ROW_H
        if row < 0: row = 0
        col = (event.x - PAD) // CHAR_W
        if col < 0: col = 0
        if col >= BYTES_PER_ROW: col = BYTES_PER_ROW - 1

        byte_idx = row * BYTES_PER_ROW + col
        if byte_idx < 0: byte_idx = 0
        if byte_idx >= n: byte_idx = n - 1

        op_idx = 0
        for bi in range(len(boundaries) - 1):
            if boundaries[bi] <= byte_idx < boundaries[bi + 1]:
                op_idx = bi
                break

        self._hex_selected = op_idx
        self._hex_canvas.delete("sel")
        # Rectangle de sélection : un par ligne du bloc
        for byte_i in range(boundaries[op_idx], boundaries[op_idx + 1]):
            r = byte_i // BYTES_PER_ROW
            c = byte_i % BYTES_PER_ROW
            if c == 0 or byte_i == boundaries[op_idx] or (byte_i - 1) // BYTES_PER_ROW != r:
                last_c = min(BYTES_PER_ROW - 1, (boundaries[op_idx + 1] - 1) % BYTES_PER_ROW if (boundaries[op_idx + 1] - 1) // BYTES_PER_ROW == r else BYTES_PER_ROW - 1)
                x1 = PAD + c * CHAR_W - 2
                x2 = PAD + last_c * CHAR_W + CHAR_W - GAP + 2
                y1 = PAD + r * ROW_H - 1
                y2 = PAD + r * ROW_H + ROW_H + 1
                self._hex_canvas.create_rectangle(x1, y1, x2, y2,
                                                  outline="#FF5722", width=2, tags=("sel",))
        self._refresh_type_buttons(operands, opcode, variant, key)
        self._update_range_display(opcode, variant)
        self.status_var.set(f"#{op_idx} ({len(operands[op_idx][1])}o) sélectionné — clic droit pour split/merge")

    def _update_range_display(self, opcode, variant):
        """Met à jour le texte de range sur le canvas."""
        boundaries = self._hex_boundaries
        oi = self._hex_selected
        if oi is None or oi >= len(boundaries) - 1:
            self._hex_canvas.itemconfig("range_text", text="")
            return
        byte_start = boundaries[oi]
        byte_end = boundaries[oi + 1]
        mm = self._compute_range_for_bytes(opcode, variant, byte_start, byte_end)
        if mm and mm.get("count", 0) > 1:
            mm_text = f"Range: {mm['min_hex']} .. {mm['max_hex']}"
            if "min_float" in mm:
                mm_text += f"  |  f32: [{mm['min_float']} .. {mm['max_float']}]"
            mm_text += f"  ({mm['count']} occ)"
        elif mm.get("count") == 1:
            mm_text = "(1 seule occurrence)"
        else:
            mm_text = ""
        self._hex_canvas.itemconfig("range_text", text=mm_text)

    def _on_hex_right_click(self, event, all_bytes, boundaries, operands, opcode, variant, key):
        """Clic droit : split entre octets, ou merge sur séparateur."""
        CHAR_W = 32
        GAP = 2
        PAD = 14
        ROW_H = 30
        BYTES_PER_ROW = 32
        n = len(all_bytes)

        row = max(0, (event.y - PAD) // ROW_H)
        col = (event.x - PAD) // CHAR_W
        if col < 0: col = 0
        if col >= BYTES_PER_ROW: col = BYTES_PER_ROW - 1
        byte_idx = row * BYTES_PER_ROW + col
        if byte_idx < 0: byte_idx = 0
        if byte_idx >= n: byte_idx = n - 1
        x_in_block = (event.x - PAD - col * CHAR_W)
        in_gap = (x_in_block > CHAR_W - 12)  # 12px de zone de split à droite

        op_idx = 0
        for bi in range(len(boundaries) - 1):
            if boundaries[bi] <= byte_idx < boundaries[bi + 1]:
                op_idx = bi
                break

        # Clic droit dans le gap + pas au dernier octet de l'opérande → split
        if in_gap and byte_idx + 1 < boundaries[op_idx + 1]:
            self._split_at(op_idx, byte_idx + 1, operands, opcode, variant, key)
            return

        # Clic droit sur le premier octet d'un bloc (sauf premier) → merge
        if byte_idx == boundaries[op_idx] and op_idx > 0:
            self._merge_at_boundary(op_idx, operands, opcode, variant, key)
            return

        # Sinon, sélectionner aussi (pratique)
        self._on_hex_select(event, all_bytes, boundaries, operands, opcode, variant, key)

    def _split_at(self, op_idx, split_pos, operands, opcode, variant, key):
        """Split l'opérande op_idx (préserve les autres defs)."""
        raw = operands[op_idx][1]
        boundaries = self._hex_boundaries
        rel_pos = split_pos - boundaries[op_idx]
        if rel_pos <= 0 or rel_pos >= len(raw):
            return

        left = raw[:rel_pos]
        right = raw[rel_pos:]
        off = operands[op_idx][2]

        new_ops = list(operands)
        new_ops[op_idx] = (f"FIX:{len(left)}", list(left), off)
        new_ops.insert(op_idx + 1, (f"FIX:{len(right)}", list(right), off + rel_pos))

        key_str = make_key(opcode, variant, self.table)
        if "operand_defs" in self.state:
            defs = self.state["operand_defs"]
            # Supprimer l'ancien, décaler les suivants vers la droite
            defs.pop(f"{key_str}_{op_idx}", None)
            to_shift = [(k, v) for k, v in list(defs.items()) if k.startswith(f"{key_str}_")]
            for k, v in reversed(to_shift):  # reversed pour éviter d'écraser
                old_idx = int(k.rsplit("_", 1)[-1])
                if old_idx > op_idx:
                    del defs[k]
                    defs[f"{key_str}_{old_idx + 1}"] = v
        if "committed" in self.state and key_str in self.state["committed"]:
            self.state["committed"].remove(key_str)
        save_state(self.state)

        inst = list(self.all_instructions[self.current_idx])
        inst[6] = new_ops
        self.all_instructions[self.current_idx] = tuple(inst)
        self._show_current()

    def _drag_boundary(self, event, bi, all_bytes, boundaries, operands, opcode, variant, key):
        """Drag d'un séparateur (reconstruit tout, wipe toutes les defs)."""
        CHAR_W = 32
        PAD = 14
        n = len(all_bytes)
        new_pos = max(1, min(n - 1, (event.x - PAD + CHAR_W//2) // CHAR_W))
        if bi > 0 and new_pos <= boundaries[bi - 1]:
            new_pos = boundaries[bi - 1] + 1
        if bi < len(boundaries) - 1 and new_pos >= boundaries[bi + 1]:
            new_pos = boundaries[bi + 1] - 1
        if new_pos == boundaries[bi]:
            return

        boundaries[bi] = new_pos
        new_ops = []
        for i in range(len(boundaries) - 1):
            s = boundaries[i]; e = boundaries[i + 1]
            chunk = all_bytes[s:e]
            new_ops.append((f"FIX:{len(chunk)}", list(chunk), operands[i][2] if i < len(operands) else 0))

        key_str = make_key(opcode, variant, self.table)
        self._wipe_key_defs(key_str)  # drag change tout, on wipe
        save_state(self.state)

        inst = list(self.all_instructions[self.current_idx])
        inst[6] = new_ops
        self.all_instructions[self.current_idx] = tuple(inst)
        self._show_current()

    # ===================================================================
    #  Helpers pour préserver les defs lors des merge/split/drag
    # ===================================================================

    def _wipe_key_defs(self, key_str):
        """Efface TOUTES les defs pour une clé (utilisé quand les indices sont tous décalés)."""
        if "operand_defs" in self.state:
            to_delete = [k for k in self.state["operand_defs"] if k.startswith(key_str + "_")]
            for k in to_delete:
                del self.state["operand_defs"][k]
        if "committed" in self.state and key_str in self.state["committed"]:
            self.state["committed"].remove(key_str)

    def _merge_at_boundary(self, bi, operands, opcode, variant, key):
        """Merge deux opérandes adjacents au boundary bi (préserve les autres defs)."""
        if bi <= 0 or bi >= len(operands):
            return
        new_ops = list(operands)
        raw1 = new_ops[bi - 1][1]
        raw2 = new_ops[bi][1]
        combined = list(raw1) + list(raw2)
        new_ops[bi - 1] = (f"FIX:{len(combined)}", combined, new_ops[bi - 1][2])
        del new_ops[bi]

        key_str = make_key(opcode, variant, self.table)
        # Re-indexer les defs : supprimer bi-1 et bi, décaler les suivants vers la gauche
        if "operand_defs" in self.state:
            defs = self.state["operand_defs"]
            defs.pop(f"{key_str}_{bi-1}", None)
            defs.pop(f"{key_str}_{bi}", None)
            to_shift = [(k, v) for k, v in list(defs.items()) if k.startswith(f"{key_str}_")]
            for k, v in to_shift:
                old_idx = int(k.rsplit("_", 1)[-1])
                if old_idx > bi:
                    del defs[k]
                    defs[f"{key_str}_{old_idx - 1}"] = v
        if "committed" in self.state and key_str in self.state["committed"]:
            self.state["committed"].remove(key_str)
        save_state(self.state)

        inst = list(self.all_instructions[self.current_idx])
        inst[6] = new_ops
        self.all_instructions[self.current_idx] = tuple(inst)
        self._show_current()

    def _apply_type_to_selected(self, fmt, operands, opcode, variant, key):
        """Applique le type choisi à l'opérande sélectionné."""
        if self._hex_selected is None:
            self.status_var.set("⚠ Sélectionne d'abord un opérande")
            return
        oi = self._hex_selected
        opdef_key = f"{make_key(opcode, variant, self.table)}_{oi}"

        if "operand_defs" not in self.state:
            self.state["operand_defs"] = {}
        self.state["operand_defs"][opdef_key] = {"format": fmt, "name": ""}
        save_state(self.state)
        self._auto_commit_if_done(make_key(opcode, variant, self.table), opcode, variant)
        self._show_current()

    def _apply_name_to_selected(self, operands, opcode, variant, key):
        """Applique juste le nom à l'opérande sélectionné."""
        if self._hex_selected is None:
            return
        oi = self._hex_selected
        opdef_key = f"{make_key(opcode, variant, self.table)}_{oi}"
        name = self._hex_name_var.get().strip()
        d = self.state.get("operand_defs", {}).get(opdef_key, {})
        if d:
            d["name"] = name
            self.state["operand_defs"][opdef_key] = d
        else:
            if "operand_defs" not in self.state:
                self.state["operand_defs"] = {}
            self.state["operand_defs"][opdef_key] = {"format": "hex", "name": name}
        save_state(self.state)
        self.status_var.set(f"✓ Nom appliqué: {name}")

    # ===================================================================
    #  Anciennes méthodes conservées pour compatibilité
    # ===================================================================

    def _build_operand_section(self, oi, prog_type, raw_bytes, off, opcode, variant, key, minmax=None, prev_committed=False):
        """Fallback: construit le widget pour un opérande (ancienne interface)."""
        pass  # N'est plus utilisée, le hex view la remplace
        if minmax is None:
            minmax = {}

        opdef_key = f"{key}_{oi}"
        stored_def = self.state.get("operand_defs", {}).get(opdef_key, {})
        is_fix = prog_type.startswith("FIX:")
        is_multi = len(raw_bytes) >= 2   # parser pas-à-pas dispo pour tout type multi-octets

        frame = ttk.LabelFrame(self.operand_frame,
                               text=f"Opérande #{oi} — {prog_type} @ 0x{off:04X}",
                               padding=5)
        frame.pack(fill=tk.X, pady=3, padx=2)

        # --- Barre d'actions EN HAUT (toujours visible) ---
        action_bar = ttk.Frame(frame)
        action_bar.pack(fill=tk.X, pady=(0, 3))

        ttk.Label(action_bar, text=f"#{oi}", font=("", 8), foreground="gray").pack(side=tk.LEFT, padx=2)

        # Merge avec le précédent (si précédent est committé)
        if prev_committed:
            ttk.Button(action_bar, text="⇄ Merge avec le précédent",
                       command=lambda f=frame, o=oi: self._merge_with_prev(f, o)).pack(side=tk.LEFT, padx=5)

        # Merge avec le suivant
        is_last = False
        if self.all_instructions and self.current_idx < len(self.all_instructions):
            num_ops = len(self.all_instructions[self.current_idx][6])
            is_last = (oi >= num_ops - 1)
    # ===================================================================
    #  Anciennes méthodes conservées pour compatibilité
    # ===================================================================

    def _build_operand_section(self, oi, prog_type, raw_bytes, off, opcode, variant, key, minmax=None, prev_committed=False):
        pass  # Remplacé par _build_hex_view

    def _commit_opcode_name(self):
        """Committe uniquement le nom de l'opcode."""
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, opcode, variant, _ = self.all_instructions[self.current_idx]
        key = make_key(opcode, variant, self.table)
        opcode_name = self.opcode_name_var.get().strip()

        if not opcode_name:
            messagebox.showwarning("Incomplet", "Donne un nom à l'opcode.")
            return

        if "opcode_names" not in self.state:
            self.state["opcode_names"] = {}
        self.state["opcode_names"][key] = opcode_name
        save_state(self.state)

        self.current_name_var.set(opcode_name)
        # Mise à jour locale sans rebuild
        self.status_var.set(f"✓ Opcode {key} renommé: {opcode_name}")

    def _commit_single_operand(self, frame):
        """Committe uniquement l'opérande courant (format + nom)."""
        if not hasattr(frame, 'opdef_key'):
            return

        opdef_key = frame.opdef_key
        fmt = frame.format_var.get()
        name = frame.name_var.get().strip()

        if not fmt:
            messagebox.showwarning("Incomplet", "Sélectionne un format avant de commiter.")
            return

        if "operand_defs" not in self.state:
            self.state["operand_defs"] = {}
        self.state["operand_defs"][opdef_key] = {"format": fmt, "name": name}
        save_state(self.state)

        # Sauver aussi le nom d'opcode si fourni
        opcode_name = self.opcode_name_var.get().strip()
        if opcode_name:
            if "opcode_names" not in self.state:
                self.state["opcode_names"] = {}
            self.state["opcode_names"][frame.key] = opcode_name
            save_state(self.state)

        # Auto-commit si tous les opérandes sont définis
        self._auto_commit_if_done(frame.key, frame.opcode, frame.variant)

        self._show_current()
        self.status_var.set(f"✓ {opdef_key} committé: {fmt}" + (f" ({name})" if name else ""))

    def _auto_commit_if_done(self, key, opcode, variant):
        """Si tous les opérandes de ce (opcode, variant) sont définis, auto-commit."""
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return
        operands = self.all_instructions[self.current_idx][6]
        num_ops = len(operands)
        all_defined = True
        for oi in range(num_ops):
            opdef_key = f"{key}_{oi}"
            d = self.state.get("operand_defs", {}).get(opdef_key, {})
            fmt = d.get("format", "")
            if not fmt:
                all_defined = False
                break
        if all_defined and num_ops > 0:
            if "committed" not in self.state:
                self.state["committed"] = []
            if key not in self.state["committed"]:
                self.state["committed"].append(key)
                save_state(self.state)
                # Purger de all_instructions TOUTES les occurrences
                before = len(self.all_instructions)
                self.all_instructions = [
                    instr for instr in self.all_instructions
                    if make_key(instr[4], instr[5], self.table) != key
                ]
                after = len(self.all_instructions)
                # Recaler current_idx
                if self.current_idx >= after:
                    self.current_idx = max(0, after - 1)
                self._refresh_list()
                self.status_var.set(
                    f"✓ AUTO-COMMIT: {key} — {before - after} occurrences retirées "
                    f"({len(self.state['committed'])}/{len(set(make_key(i[4], i[5], self.table) for i in self.all_instructions)) + len(self.state['committed'])} résolus)"
                )

    # ===================================================================
    #  Parser pas-à-pas pour FIX:N (ou blocs mergés)
    # ===================================================================

    def _build_step_parser(self, frame, raw_bytes, key, oi, opcode, variant):
        """Construit le parser pas-à-pas dans frame."""
        n = len(raw_bytes)
        opdef_key = f"{key}_{oi}"

        parser_frame = ttk.LabelFrame(frame, text=f"Parser pas-à-pas — {n} octets", padding=3)
        parser_frame.pack(fill=tk.X, pady=(5, 0), padx=2)

        # Stocker l'état du parser dans frame
        frame._parser_bytes = raw_bytes
        frame._parser_subfields = []  # [(offset, size, format, name), ...]
        frame._parser_pos = 0
        frame._parser_frame = parser_frame
        frame._parser_opdef_key = opdef_key

        # Restaurer depuis le state si déjà défini
        stored_def = self.state.get("operand_defs", {}).get(opdef_key, {})
        if stored_def.get("format") == "composite" and stored_def.get("subfields"):
            frame._parser_subfields = [(sf["offset"], sf["size"], sf["format"], sf.get("name", ""))
                                        for sf in stored_def["subfields"]]
            # Calculer la position
            if frame._parser_subfields:
                last = frame._parser_subfields[-1]
                frame._parser_pos = last[0] + last[1]

        self._refresh_step_parser(frame)

    def _refresh_step_parser(self, frame):
        """Redessine le parser pas-à-pas."""
        pf = frame._parser_frame
        for w in pf.winfo_children():
            w.destroy()

        raw_bytes = frame._parser_bytes
        n = len(raw_bytes)
        pos = frame._parser_pos
        remaining = n - pos

        # --- Barre de progression ---
        prog_bar = ttk.Frame(pf)
        prog_bar.pack(fill=tk.X, pady=2)
        ttk.Label(prog_bar, text=f"Position: {pos}/{n} octets",
                  font=("", 9, "bold")).pack(side=tk.LEFT, padx=2)
        # Barre visuelle
        bar_canvas = tk.Canvas(prog_bar, height=14, borderwidth=1, relief=tk.SUNKEN)
        bar_canvas.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=5)
        if n > 0:
            fill_pct = pos / n
            bar_canvas.create_rectangle(0, 0, 200 * fill_pct, 14, fill="#4CAF50", outline="")
        bar_canvas.configure(width=200)

        # Hex des bytes avec contexte
        if remaining > 0:
            ctx_start = max(0, pos - 4)
            ctx_end = min(n, pos + 36)
            parts = []
            for j in range(ctx_start, ctx_end):
                b = raw_bytes[j]
                if j < pos:
                    parts.append(f"[{b:02X}]")  # déjà parsé = entre crochets
                elif j == pos:
                    parts.append(f">>{b:02X}<<")  # position courante
                else:
                    parts.append(f"{b:02X}")
            hex_ctx = " ".join(parts)
            ttk.Label(pf, text=f"Hex: {hex_ctx}",
                      font=("Consolas", 9), foreground="blue").pack(anchor=tk.W, padx=2, pady=2)
            if ctx_end < n:
                ttk.Label(pf, text=f"  ... (+{n - ctx_end} octets)",
                          font=("Consolas", 8), foreground="gray").pack(anchor=tk.W, padx=2)

        # --- Sous-champs déjà parsés ---
        if frame._parser_subfields:
            ttk.Label(pf, text="Sous-champs accumulés:", font=("", 8, "bold")).pack(anchor=tk.W, padx=2, pady=(5,0))
            for i, (off, sz, fmt, name) in enumerate(frame._parser_subfields):
                chunk = raw_bytes[off:off+sz]
                val = self._format_chunk(chunk, fmt)
                sf_row = ttk.Frame(pf)
                sf_row.pack(fill=tk.X, pady=1)
                ttk.Label(sf_row, text=f"[{off}:{off+sz}]", width=7, font=("Consolas", 8)).pack(side=tk.LEFT, padx=2)
                ttk.Label(sf_row, text=f"{fmt}", width=6, font=("Consolas", 8, "bold"),
                          foreground="#CC6600").pack(side=tk.LEFT, padx=2)
                ttk.Label(sf_row, text=val, font=("Consolas", 9)).pack(side=tk.LEFT, padx=5)
                ttk.Label(sf_row, text=name, foreground="#555").pack(side=tk.LEFT, padx=10)
                # Bouton retirer
                ttk.Button(sf_row, text="✕", width=2,
                           command=lambda f=frame, idx=i: self._remove_subfield(f, idx)).pack(side=tk.RIGHT, padx=2)
                # Bouton renommer rapide (utilise le name_var du frame principal)
                ttk.Button(sf_row, text="✎", width=2,
                           command=lambda f=frame, idx=i: self._rename_subfield(f, idx)).pack(side=tk.RIGHT, padx=1)

        # --- Si terminé : bouton commit (ou "déjà committé") ---
        if remaining == 0 and frame._parser_subfields:
            done_row = ttk.Frame(pf)
            done_row.pack(fill=tk.X, pady=(5, 0))

            # Vérifier si déjà sauvegardé dans le state
            opdef_key = frame._parser_opdef_key
            stored_def = self.state.get("operand_defs", {}).get(opdef_key, {})
            already_saved = (stored_def.get("format") == "composite" and
                           stored_def.get("subfields") is not None)

            if already_saved:
                ttk.Label(done_row, text="✓ Déjà committé",
                          font=("", 9, "bold"), foreground="green").pack(side=tk.LEFT, padx=5)
                ttk.Button(done_row, text="↺ Reset (modifier)",
                           command=lambda f=frame: self._reset_parser(f)).pack(side=tk.LEFT, padx=5)
            else:
                ttk.Label(done_row, text="✓ Tous les octets sont couverts !",
                          font=("", 9, "bold"), foreground="green").pack(side=tk.LEFT, padx=5)
                ttk.Button(done_row, text="✓ Commit sub-fields",
                           command=lambda f=frame: self._commit_parsed_subfields(f)).pack(side=tk.LEFT, padx=10)
                ttk.Button(done_row, text="↺ Reset",
                           command=lambda f=frame: self._reset_parser(f)).pack(side=tk.LEFT, padx=2)
            return

        if remaining == 0:
            ttk.Label(pf, text="Aucun octet restant. Clique Reset pour recommencer.",
                      font=("", 9), foreground="gray").pack(pady=5)
            ttk.Button(pf, text="↺ Reset",
                       command=lambda f=frame: self._reset_parser(f)).pack(pady=2)
            return

        # --- Options pour le prochain chunk ---
        ttk.Label(pf, text="Prochain champ — choisir le format:",
                  font=("", 9, "bold")).pack(anchor=tk.W, padx=2, pady=(8, 3))

        options_frame = ttk.Frame(pf)
        options_frame.pack(fill=tk.X, pady=2)

        def make_opt_btn(parent, row, col, label, sz, fmt):
            chunk = raw_bytes[pos:pos+sz]
            preview = self._format_chunk(chunk, fmt)
            btn_frame = ttk.Frame(parent)
            btn_frame.grid(row=row, column=col, padx=3, pady=2, sticky="nsew")
            btn = ttk.Button(btn_frame, text=f"{label}\n{preview}",
                             command=lambda f=frame, s=sz, fm=fmt: self._add_subfield(f, s, fm))
            btn.pack()

        # Ligne 0: u8, s8, u16, s16, u32, s32, float
        row = 0
        make_opt_btn(options_frame, row, 0, "u8", 1, "u8")
        make_opt_btn(options_frame, row, 1, "s8", 1, "s8")
        if remaining >= 2:
            make_opt_btn(options_frame, row, 2, "u16", 2, "u16")
            make_opt_btn(options_frame, row, 3, "s16", 2, "s16")
            make_opt_btn(options_frame, row, 4, "ptr16", 2, "ptr16")
        if remaining >= 4:
            make_opt_btn(options_frame, row, 5, "u32", 4, "u32")
            make_opt_btn(options_frame, row, 6, "s32", 4, "s32")
            make_opt_btn(options_frame, row, 7, "float", 4, "float")
            make_opt_btn(options_frame, row, 8, "ptr32", 4, "ptr32")

        # Ligne 1: string + FIX skip
        row = 1
        # String (cherche null terminator)
        str_end = pos
        while str_end < n and raw_bytes[str_end] != 0:
            str_end += 1
        has_null = (str_end < n)  # un \0 a été trouvé
        str_len = (str_end - pos) + (1 if has_null else 0)
        str_label = f"str({str_len})" + (" ✓\\0" if has_null else " ?")
        if str_len > 0:
            make_opt_btn(options_frame, row, 0, str_label, str_len, "string")

        col = 1
        ttk.Separator(options_frame, orient=tk.VERTICAL).grid(row=row, column=col, sticky="ns", padx=8, pady=2)
        col += 1
        ttk.Label(options_frame, text="Skip:", font=("", 8, "bold")).grid(row=row, column=col, sticky=tk.W, padx=2)
        col += 1
        for skip_sz in [1, 2, 4]:
            if remaining >= skip_sz:
                make_opt_btn(options_frame, row, col, f"FIX:{skip_sz}", skip_sz, "hex")
                col += 1

        # --- Bouton "Merge le reste avec le suivant" ---
        if frame._parser_subfields and remaining > 0:
            merge_rest_row = ttk.Frame(pf)
            merge_rest_row.pack(fill=tk.X, pady=(8, 0))
            ttk.Label(merge_rest_row,
                      text=f"⚠ Il reste {remaining} octets. Si ça appartient à l'opérande suivant :",
                      font=("", 8), foreground="#CC6600").pack(side=tk.LEFT, padx=2)
            ttk.Button(merge_rest_row, text=f"⇄ Merger le reste ({remaining}o) avec le suivant",
                       command=lambda f=frame: self._merge_remainder_with_next(f)).pack(side=tk.LEFT, padx=5)

    def _merge_remainder_with_next(self, frame):
        """Merge les octets restants du parser avec l'opérande suivant."""
        oi = frame.oi
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, opcode, variant, operands = self.all_instructions[self.current_idx]
        if oi >= len(operands) - 1:
            return

        # Octets restants dans le parser
        pos = frame._parser_pos
        raw_bytes = frame._parser_bytes
        remaining_bytes = raw_bytes[pos:]

        # Octets de l'opérande suivant
        prog_next, raw_next, off_next = operands[oi + 1]
        combined = list(remaining_bytes) + list(raw_next)

        # Commiter les sous-champs déjà parsés
        if frame._parser_subfields:
            self._commit_parsed_subfields(frame)

        # Remplacer l'opérande suivant par le merge
        new_operands = list(operands)
        new_operands[oi + 1] = (f"FIX:{len(combined)}", combined, off_next)

        # Réduire l'opérande courant à ce qui a été parsé (si des sous-champs existent)
        if frame._parser_subfields:
            parsed_bytes = raw_bytes[:pos]
            new_operands[oi] = (f"FIX:{len(parsed_bytes)}", list(parsed_bytes), operands[oi][2])

        inst_list = list(self.all_instructions[self.current_idx])
        inst_list[6] = new_operands
        self.all_instructions[self.current_idx] = tuple(inst_list)

        self._show_current()
        self.status_var.set(f"⇄ Reste ({len(remaining_bytes)}o) mergé avec opérande {oi+1}")

    def _add_subfield(self, frame, size, fmt):
        """Ajoute un sous-champ au parser."""
        pos = frame._parser_pos
        name = f"f{len(frame._parser_subfields)}"
        frame._parser_subfields.append((pos, size, fmt, name))
        frame._parser_pos = pos + size
        self._refresh_step_parser(frame)

    def _remove_subfield(self, frame, idx):
        """Retire un sous-champ et recalcule les positions."""
        del frame._parser_subfields[idx]
        # Recalculer les offsets
        pos = 0
        new_sfs = []
        for _, sz, fmt, name in frame._parser_subfields:
            new_sfs.append((pos, sz, fmt, name))
            pos += sz
        frame._parser_subfields = new_sfs
        frame._parser_pos = pos
        self._refresh_step_parser(frame)

    def _rename_subfield(self, frame, idx):
        """Renomme un sous-champ via popup."""
        from tkinter import simpledialog
        old_name = frame._parser_subfields[idx][3]
        new_name = simpledialog.askstring("Renommer", f"Nom du sous-champ #{idx}:", initialvalue=old_name)
        if new_name is not None:
            off, sz, fmt, _ = frame._parser_subfields[idx]
            frame._parser_subfields[idx] = (off, sz, fmt, new_name.strip())
            self._refresh_step_parser(frame)

    def _reset_parser(self, frame):
        """Reset le parser et efface le state associé."""
        frame._parser_subfields = []
        frame._parser_pos = 0
        # Effacer du state
        opdef_key = frame._parser_opdef_key
        if "operand_defs" in self.state and opdef_key in self.state["operand_defs"]:
            del self.state["operand_defs"][opdef_key]
            save_state(self.state)
        self._refresh_step_parser(frame)

    def _commit_parsed_subfields(self, frame):
        """Committe les sous-champs parsés."""
        opdef_key = frame._parser_opdef_key
        subfields = []
        for off, sz, fmt, name in frame._parser_subfields:
            subfields.append({"offset": off, "size": sz, "format": fmt, "name": name})

        if "operand_defs" not in self.state:
            self.state["operand_defs"] = {}
        self.state["operand_defs"][opdef_key] = {
            "format": "composite",
            "name": "",
            "subfields": subfields
        }
        save_state(self.state)

        # Auto-commit si tous les opérandes sont définis
        key = make_key(frame.opcode, frame.variant, self.table)
        self._auto_commit_if_done(key, frame.opcode, frame.variant)

        self._show_current()
        self.status_var.set(f"✓ {opdef_key}: {len(subfields)} sous-champs committés")

    # ===================================================================
    #  Merge d'opérandes
    # ===================================================================

    def _format_chunk(self, chunk, fmt):
        """Formate un chunk de bytes selon le format."""
        if fmt == "u8":
            return f"0x{chunk[0]:02X} ({chunk[0]})"
        elif fmt == "s8":
            sv = chunk[0] - 256 if chunk[0] >= 128 else chunk[0]
            return f"0x{chunk[0]:02X} ({sv})"
        elif fmt == "u16":
            v = chunk[0] | (chunk[1] << 8)
            return f"0x{v:04X} ({v})"
        elif fmt == "u32":
            v = chunk[0] | (chunk[1] << 8) | (chunk[2] << 16) | (chunk[3] << 24)
            return f"0x{v:08X} ({v})"
        elif fmt == "s16":
            v = chunk[0] | (chunk[1] << 8)
            sv = v - 65536 if v >= 32768 else v
            return f"{sv}"
        elif fmt == "s32":
            v = chunk[0] | (chunk[1] << 8) | (chunk[2] << 16) | (chunk[3] << 24)
            sv = v - 4294967296 if v >= 2147483648 else v
            return f"{sv}"
        elif fmt == "float":
            try:
                fv = struct.unpack('<f', bytes(chunk))[0]
                return f"{fv:.6f}"
            except:
                return "?"
        elif fmt == "ptr16":
            v = chunk[0] | (chunk[1] << 8)
            sv = v - 65536 if v >= 32768 else v
            sign = "+" if sv >= 0 else ""
            return f"{sign}{sv}  (0x{v:04X})"
        elif fmt == "ptr32":
            v = chunk[0] | (chunk[1] << 8) | (chunk[2] << 16) | (chunk[3] << 24)
            sv = v - 4294967296 if v >= 2147483648 else v
            sign = "+" if sv >= 0 else ""
            return f"{sign}{sv}  (0x{v:08X})"
        elif fmt == "string":
            try:
                has_null = (len(chunk) > 0 and chunk[-1] == 0)
                s = bytes(chunk).decode('latin1').rstrip('\x00')
                suffix = " \\0" if has_null else " (no null)"
                return repr(s) + suffix if s else ("(vide)" + suffix)
            except:
                return " ".join(f"{b:02X}" for b in chunk)
        elif fmt == "str_raw":
            try:
                s = bytes(chunk).decode('latin1')
                return repr(s)
            except:
                return " ".join(f"{b:02X}" for b in chunk)
        elif fmt == "hex":
            return " ".join(f"{b:02X}" for b in chunk)
        elif fmt == "ascii":
            try:
                s = bytes(chunk).decode('ascii')
                if all(0x20 <= c < 0x7F for c in chunk):
                    return repr(s)
                return " ".join(f"{b:02X}" for b in chunk) + " (non-ASCII)"
            except:
                return " ".join(f"{b:02X}" for b in chunk)
        return " ".join(f"{b:02X}" for b in chunk)

    def _merge_with_prev(self, frame, oi):
        """Merge l'opérande courant avec le précédent."""
        if oi <= 0:
            return
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, _, _, operands = self.all_instructions[self.current_idx]
        prog1, raw1, off1 = operands[oi - 1]
        prog2, raw2, off2 = operands[oi]
        combined = list(raw1) + list(raw2)

        new_operands = list(operands)
        new_operands[oi - 1] = (f"FIX:{len(combined)}", combined, off1)
        del new_operands[oi]

        inst_list = list(self.all_instructions[self.current_idx])
        inst_list[6] = new_operands
        self.all_instructions[self.current_idx] = tuple(inst_list)

        # Effacer TOUTES les définitions (les indices ont changé)
        key = make_key(frame.opcode, frame.variant, self.table)
        self._wipe_key_defs(key)
        save_state(self.state)

        self._show_current()
        self.status_var.set(f"⇄ Mergé opérandes {oi-1}+{oi} → FIX:{len(combined)}")

    def _toggle_mode(self, edit_key, edit_mode):
        """Bascule entre mode Aperçu et Édition."""
        if not hasattr(self, '_edit_mode'):
            self._edit_mode = {}
        self._edit_mode[edit_key] = edit_mode
        self._show_current()

    def _uncommit_all_operands(self, key, operands):
        """Ré-affiche tous les opérandes (annule le masquage)."""
        for oi in range(len(operands)):
            opdef_key = f"{key}_{oi}"
            if "operand_defs" in self.state and opdef_key in self.state["operand_defs"]:
                del self.state["operand_defs"][opdef_key]
        # Retirer du committed si présent
        if "committed" in self.state and key in self.state["committed"]:
            self.state["committed"].remove(key)
        save_state(self.state)
        self._show_current()
        self.status_var.set(f"✎ Tous les opérandes de {key} sont ré-affichés")

    def _merge_with_next(self, frame, oi):
        """Merge l'opérande courant avec le suivant."""
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, opcode, variant, operands = self.all_instructions[self.current_idx]
        if oi >= len(operands) - 1:
            return

        # Combiner les bytes
        prog1, raw1, off1 = operands[oi]
        prog2, raw2, off2 = operands[oi + 1]
        combined = list(raw1) + list(raw2)
        combined_off = off1

        # Effacer TOUTES les définitions (indices décalés)
        key = make_key(opcode, variant, self.table)
        if "operand_defs" in self.state:
            to_delete = [k for k in self.state["operand_defs"] if k.startswith(key + "_")]
            for k in to_delete:
                del self.state["operand_defs"][k]
        if "committed" in self.state and key in self.state["committed"]:
            self.state["committed"].remove(key)
        if "merge_defs" in self.state:
            to_del = [k for k in self.state["merge_defs"] if k.startswith(key + "_")]
            for k in to_del:
                del self.state["merge_defs"][k]
        save_state(self.state)

        # Remplacer les opérandes
        new_operands = list(operands)
        new_operands[oi] = (f"FIX:{len(combined)}", combined, combined_off)
        del new_operands[oi + 1]

        inst_list = list(self.all_instructions[self.current_idx])
        inst_list[6] = new_operands
        self.all_instructions[self.current_idx] = tuple(inst_list)

        self._show_current()
        self.status_var.set(f"⇄ Mergé opérandes {oi}+{oi+1} → FIX:{len(combined)}")

    def _get_operand_choices(self):
        """Récupère les choix de format/nom depuis les widgets."""
        choices = {}
        for child in self.operand_frame.winfo_children():
            if hasattr(child, 'opdef_key'):
                choices[child.opdef_key] = {
                    "format": child.format_var.get(),
                    "name": child.name_var.get().strip()
                }
        return choices

    def _commit_current(self):
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, opcode, variant, _ = self.all_instructions[self.current_idx]
        key = make_key(opcode, variant, self.table)

        # Récupérer le nom
        opcode_name = self.opcode_name_var.get().strip()

        # Récupérer les choix d'opérandes
        choices = self._get_operand_choices()

        # Mettre à jour l'état
        if opcode_name:
            if "opcode_names" not in self.state:
                self.state["opcode_names"] = {}
            self.state["opcode_names"][key] = opcode_name

        if "operand_defs" not in self.state:
            self.state["operand_defs"] = {}
        for k, v in choices.items():
            self.state["operand_defs"][k] = v

        # Vérifier si tout est rempli pour committer
        all_filled = bool(opcode_name)
        for k, v in choices.items():
            if not v.get("format"):
                all_filled = False
                break

        if all_filled:
            if "committed" not in self.state:
                self.state["committed"] = []
            if key not in self.state["committed"]:
                self.state["committed"].append(key)

        save_state(self.state)
        self._refresh_list()
        self._next_instr()
        self.status_var.set(f"✓ Commit: opcode {opcode} variant 0x{variant:02X} → {key}")

    def _commit_all_current(self):
        """Committe toutes les occurrences du même opcode+variant."""
        if not self.all_instructions or self.current_idx >= len(self.all_instructions):
            return

        _, _, _, _, opcode, variant, _ = self.all_instructions[self.current_idx]
        key = make_key(opcode, variant, self.table)
        opcode_name = self.opcode_name_var.get().strip()
        choices = self._get_operand_choices()

        if "opcode_names" not in self.state:
            self.state["opcode_names"] = {}
        if opcode_name:
            self.state["opcode_names"][key] = opcode_name

        if "operand_defs" not in self.state:
            self.state["operand_defs"] = {}
        for k, v in choices.items():
            self.state["operand_defs"][k] = v

        if "committed" not in self.state:
            self.state["committed"] = []
        if key not in self.state["committed"]:
            self.state["committed"].append(key)

        save_state(self.state)
        self._refresh_list()
        self._next_instr()
        self.status_var.set(f"✓ Commit ALL: {key} (toutes occurrences ignorées maintenant)")

    def _next_instr(self):
        if self.current_idx < len(self.all_instructions) - 1:
            self.current_idx += 1
            self._show_current()
            # Sélectionner dans la treeview
            self.tree.selection_set(str(self.current_idx))
            self.tree.see(str(self.current_idx))

    def _prev_instr(self):
        if self.current_idx > 0:
            self.current_idx -= 1
            self._show_current()
            self.tree.selection_set(str(self.current_idx))
            self.tree.see(str(self.current_idx))


# ---------------------------------------------------------------------------
# Point d'entrée
# ---------------------------------------------------------------------------
def main():
    root = tk.Tk()
    app = OpcodeAnalyzerApp(root)
    root.mainloop()

if __name__ == "__main__":
    main()
