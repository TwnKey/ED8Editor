# -*- coding: utf-8 -*-
"""
Parseurs de TABLES de donnees CS1 (portage fidele des classes de CS1InstructionsSet.h).

Les fonctions dont le nom correspond a une table (voir guess_type_by_name du decompilo)
ne sont PAS du code opcode : ce sont des structures de donnees. On les parse ici en une
liste de champs types. Chaque champ conserve ses octets bruts -> re-serialisation = concat
= roundtrip 0-diff garanti, independamment de la finesse du parse.

Chaque parseur recoit (b, pos, end) et renvoie (fields, new_pos) ou leve TableParseError.
'end' = adresse de fin de fonction (goal). Le terminateur d'une table est l'opcode 1 (RET,
octet 0x01) suivi du padding 0x00 jusqu'a end : le parseur data s'arrete AVANT ce 0x01.

field = dict{ 'type': str, 'off': int, 'size': int, 'raw': bytes, ['value': int|float],
              ['text': str], ['fill': int] }
type in: u8, s16, s32, f32, string, fill, bytes
"""
import struct


class TableParseError(Exception):
    pass


# ------- noms de tables -> (id, parseur) : d'apres guess_type_by_name --------
def table_type_for_name(nm):
    if not nm:
        return None
    if nm == 'CreateMonsters':      return ('CreateMonsters', 256)
    if nm == 'EffectsInstr':        return ('EffectsInstr', 257)
    if nm == 'ActionTable':         return ('ActionTable', 258)
    if nm == 'AlgoTable':           return ('AlgoTable', 259)
    if nm == 'WeaponAttTable':      return ('WeaponAttTable', 260)
    if nm == 'BreakTable':          return ('BreakTable', 261)
    if nm == 'SummonTable':         return ('SummonTable', 262)
    if nm == 'ReactionTable':       return ('ReactionTable', 263)
    if nm == 'PartTable':           return ('PartTable', 264)
    if nm == 'AnimeClipTable':      return ('AnimeClipTable', 265)
    if nm == 'FieldMonsterData':    return ('FieldMonsterData', 266)
    if nm == 'FieldFollowData':     return ('FieldFollowData', 267)
    if nm.startswith('FC_auto'):    return ('FC_autoX', 268)
    if nm.startswith('BookData'):
        # BookData<N>_<M> : M==99 -> Book99 sinon BookX
        import re
        m = re.match(r'BookData(\d+[A-Za-z]?)_(\d+)', nm)
        if m and m.group(2) == '99':
            return ('BookData99', 269)
        return ('BookDataX', 270)
    if nm == 'AddCollision':        return ('AddCollision', 271)
    return None


# ---------------------------------- Reader ----------------------------------
class _R:
    __slots__ = ('b', 'p', 'end')

    def __init__(self, b, pos, end):
        self.b = b
        self.p = pos
        self.end = end

    def _chk(self, n):
        if self.p + n > len(self.b) or self.p + n > self.end + 8:
            raise TableParseError("lecture hors limites @%d (+%d)" % (self.p, n))

    def raw(self, n, typ='bytes'):
        if n < 0:
            raise TableParseError("taille negative %d @%d" % (n, self.p))
        self._chk(n)
        off = self.p
        r = bytes(self.b[off:off + n])
        self.p += n
        return {'type': typ, 'off': off, 'size': n, 'raw': r}

    def u8(self):
        f = self.raw(1, 'u8'); f['value'] = f['raw'][0]; return f

    def s16(self):
        f = self.raw(2, 's16'); f['value'] = struct.unpack('<h', f['raw'])[0]; return f

    def s32(self):
        f = self.raw(4, 's32'); f['value'] = struct.unpack('<i', f['raw'])[0]; return f

    def f32(self):
        f = self.raw(4, 'f32'); f['value'] = struct.unpack('<f', f['raw'])[0]; return f

    def string(self):
        off = self.p
        while self.p < len(self.b) and self.b[self.p] != 0:
            self.p += 1
        if self.p >= len(self.b):
            raise TableParseError("string non terminee @%d" % off)
        self.p += 1  # consomme le null (comme ReadStringSubByteArray)
        r = bytes(self.b[off:self.p])
        return {'type': 'string', 'off': off, 'size': len(r), 'raw': r,
                'text': r[:-1].decode('utf-8', 'replace')}

    def strfill(self, width, fill_type='fill'):
        """string + padding jusqu'a 'width' octets au total (champ largeur fixe)."""
        s = self.string()
        pad = width - s['size']
        if pad < 0:
            raise TableParseError("string(%d) > largeur %d @%d" % (s['size'], width, s['off']))
        f = self.raw(pad, fill_type); f['fill'] = width
        return [s, f]

    # --- peeks (n'avancent pas) ---
    def peek_u8(self):
        if self.p >= len(self.b): raise TableParseError("peek hors limites")
        return self.b[self.p]

    def peek_s16(self):
        self._chk(2); return struct.unpack('<h', self.b[self.p:self.p + 2])[0]

    def peek_u16(self):
        self._chk(2); return struct.unpack('<H', self.b[self.p:self.p + 2])[0]

    def peek_s32(self):
        self._chk(4); return struct.unpack('<i', self.b[self.p:self.p + 4])[0]


# -------- schemas de record (editables : retypage/decoupe des blocs fixes) --------
# Les blocs de taille fixe des tables (record AlgoTable, bloc 42o ActionTable, prefixe
# FieldMonster, lignes AddCollision/Reaction/FieldFollow) sont decodes selon un SCHEMA
# = liste ordonnee de (type,taille). Le schema par defaut vient du Ghidra ; il peut etre
# raffine (retyper un blob 'bytes' en champs types, decouper) via cs1_tables.json.
# Invariant : la somme des tailles d'un schema == longueur du record (immuable).
_TYPE_SIZE = {'u8': 1, 's8': 1, 'u16': 2, 's16': 2, 'u32': 4, 's32': 4, 'f32': 4}

_DEFAULT_SCHEMAS = {
    'AlgoTable':          [('s16', 2), ('s16', 2), ('s16', 2), ('bytes', 14), ('s32', 4), ('s32', 4), ('bytes', 4)],
    'ActionTableFixed':   [('bytes', 42)],
    'FieldMonsterPrefix': [('s32', 4), ('s16', 2), ('s16', 2)],
    'FieldFollowData':    [('f32', 4), ('f32', 4), ('f32', 4), ('f32', 4), ('f32', 4)],
    'AddCollisionRow':    [('s32', 4), ('f32', 4), ('f32', 4), ('f32', 4), ('f32', 4), ('f32', 4)],
    'ReactionRow':        [('s16', 2), ('s16', 2), ('s16', 2), ('s16', 2), ('s16', 2), ('s16', 2)],
}
_SCHEMA_OVERRIDE = {}   # nom -> [(type,size),...] ; charge depuis cs1_tables.json


def schema_len(name):
    return sum(sz for _, sz in _DEFAULT_SCHEMAS[name])


def record_schema(name):
    """Schema effectif d'un record (override si valide, sinon defaut)."""
    ov = _SCHEMA_OVERRIDE.get(name)
    if ov and sum(sz for _, sz in ov) == schema_len(name):
        return ov
    return _DEFAULT_SCHEMAS[name]


def set_schema_overrides(d):
    global _SCHEMA_OVERRIDE
    _SCHEMA_OVERRIDE = {k: [tuple(x) for x in v] for k, v in (d or {}).items()}


def load_schema(path):
    import json, os
    if path and os.path.exists(path):
        try:
            set_schema_overrides(json.load(open(path)))
        except Exception:
            pass


def save_schema(path):
    import json
    json.dump(_SCHEMA_OVERRIDE, open(path, 'w'), indent=1)


def editable_schemas():
    """Noms des schemas de record retypables (bloc fixe des tables)."""
    return list(_DEFAULT_SCHEMAS.keys())


def get_schema(name):
    """Copie mutable du schema effectif (override ou defaut)."""
    return [list(x) for x in record_schema(name)]


def set_schema(name, schema):
    """Definit un override (ou le retire s'il == defaut). Valide la somme des tailles."""
    schema = [(t, int(s)) for t, s in schema]
    if sum(s for _, s in schema) != schema_len(name):
        raise ValueError("somme des tailles (%d) != longueur du record (%d)"
                         % (sum(s for _, s in schema), schema_len(name)))
    for t, _ in schema:
        if t not in _TYPE_SIZE and t != 'bytes':
            raise ValueError("type inconnu: %s" % t)
    if schema == [tuple(x) for x in _DEFAULT_SCHEMAS[name]]:
        _SCHEMA_OVERRIDE.pop(name, None)
    else:
        _SCHEMA_OVERRIDE[name] = schema


def schema_retype(name, i, new_type):
    """Retype le champ i (taille inchangee : scalaire<->scalaire meme taille, ou
    bytes-de-taille-N -> scalaire de meme taille)."""
    sc = get_schema(name)
    if not (0 <= i < len(sc)):
        raise ValueError("index hors bornes")
    cur_sz = sc[i][1]
    new_sz = _TYPE_SIZE.get(new_type, cur_sz if new_type == 'bytes' else None)
    if new_sz is None:
        raise ValueError("type inconnu")
    if new_type != 'bytes' and new_sz != cur_sz:
        raise ValueError("le retypage doit conserver la taille (%d o) — decoupe d'abord" % cur_sz)
    sc[i] = [new_type, cur_sz]
    set_schema(name, sc)


def schema_split(name, i, first_len):
    """Decoupe le champ 'bytes' i en deux : (bytes, first_len) + (bytes, reste)."""
    sc = get_schema(name)
    if not (0 <= i < len(sc)) or sc[i][0] != 'bytes':
        raise ValueError("le champ doit etre 'bytes'")
    tot = sc[i][1]
    if not (0 < first_len < tot):
        raise ValueError("longueur de decoupe invalide")
    sc[i:i + 1] = [['bytes', first_len], ['bytes', tot - first_len]]
    set_schema(name, sc)


def schema_merge(name, i):
    """Fusionne les champs i et i+1 en un seul 'bytes'."""
    sc = get_schema(name)
    if not (0 <= i < len(sc) - 1):
        raise ValueError("index hors bornes")
    sc[i:i + 2] = [['bytes', sc[i][1] + sc[i + 1][1]]]
    set_schema(name, sc)


def _read_typed(r, typ, size):
    f = r.raw(size, typ)
    raw = f['raw']
    if len(raw) < size:
        return f
    try:
        if typ == 'u8':   f['value'] = raw[0]
        elif typ == 's8': f['value'] = struct.unpack('<b', raw)[0]
        elif typ == 'u16': f['value'] = struct.unpack('<H', raw)[0]
        elif typ == 's16': f['value'] = struct.unpack('<h', raw)[0]
        elif typ == 'u32': f['value'] = struct.unpack('<I', raw)[0]
        elif typ == 's32': f['value'] = struct.unpack('<i', raw)[0]
        elif typ == 'f32': f['value'] = struct.unpack('<f', raw)[0]
    except struct.error:
        pass
    return f


def _read_schema(r, name, F):
    for (typ, size) in record_schema(name):
        F.append(_read_typed(r, typ, size))


# --------------------------------- parseurs ---------------------------------
def _CreateMonsters(b, pos, end):
    r = _R(b, pos, end); F = []; initial = pos
    first = r.peek_s32()
    if first == -1:
        F.append(r.raw(0x1C, 'bytes'))
        return F, r.p
    F.extend(r.strfill(0x10))                 # map (string+fill 0x10)
    F.append(r.s32())
    for _ in range(6):
        F.append(r.s16())
    cnt = 0
    while True:
        F.append(r.s32())                     # array int
        for _c in range(8):                   # 8 noms de monstres largeur 0x10
            F.extend(r.strfill(0x10))
        for _ib in range(8):                  # 8 octets
            F.append(r.u8())
        if r.peek_u8() == 0:
            F.append(r.raw(8, 'bytes'))
        else:
            F.extend(r.strfill(12, 'bytes'))
        first = r.peek_s32()
        cnt += 1
        if not ((first != -1) and (r.p != end - 4)):
            break
    if r.p == end - 4:
        if r.peek_s32() != 1:
            raise TableParseError("CreateMonsters : terminateur attendu 1 @%d" % r.p)
        return F, r.p
    F.append(r.raw(0x1C, 'bytes'))
    return F, r.p


def _EffectsInstr(b, pos, end):
    r = _R(b, pos, end); F = []
    while r.peek_u8() != 0x01:
        F.append(r.s16()); F.append(r.s16()); F.append(r.s32())
        F.extend(r.strfill(0x20))
    return F, r.p


def _ActionTable(b, pos, end):
    # Decompile Ghidra : byte(count), puis par ligne 42 octets fixes (0x2a),
    # string champ-largeur 0x20, string champ-largeur 0x30. Strictement le Ghidra.
    r = _R(b, pos, end); F = []
    n = r.peek_u8(); F.append(r.u8())
    for _ in range(n):
        _read_schema(r, 'ActionTableFixed', F)  # bloc fixe 42o (retypable via schema)
        F.extend(r.strfill(0x20))               # 1er string, champ largeur 0x20
        F.extend(r.strfill(0x30))               # 2e string, champ largeur 0x30
    return F, r.p


def _AlgoTable(b, pos, end):
    # Decompile Ghidra (FUN_0048d2c0) : byte(count), puis count records fixes de
    # 0x20 octets. Le decoupage du record (0x20o) est pilote par le schema 'AlgoTable'
    # (retypable). Defaut Ghidra : s16@0 id, s16@2 ~0x6400, s16@4 ~0x01ff, bytes@6,
    # s32@0x14, s32@0x18 ~0xff, bytes@0x1C.
    r = _R(b, pos, end); F = []
    n = r.peek_u8(); F.append(r.u8())
    for _ in range(n):
        _read_schema(r, 'AlgoTable', F)
    return F, r.p


def _WeaponAttTable(b, pos, end):
    r = _R(b, pos, end); F = [r.raw(4, 'bytes')]
    return F, r.p


def _BreakTable(b, pos, end):
    r = _R(b, pos, end); F = []; cnt = 0
    while True:
        sf = r.s16(); F.append(sf)
        if sf['value'] == 0:
            break
        F.append(r.s16())
        cnt += 1
        if cnt >= 0x40:
            break
    F.append(r.raw(0x2, 'bytes'))
    return F, r.p


def _SummonTable(b, pos, end):
    r = _R(b, pos, end); F = []
    n = r.peek_u8(); F.append(r.u8()); cnt = 0
    while cnt < n:
        sh = r.peek_u16(); F.append(r.s16())
        if sh == 0xFFFF:
            break
        F.append(r.u8()); F.append(r.u8())
        F.extend(r.strfill(0x20))
        cnt += 1
    return F, r.p


def _ReactionTable(b, pos, end):
    r = _R(b, pos, end); F = []
    n = r.peek_u16(); F.append(r.s16())
    for _ in range(n):
        _read_schema(r, 'ReactionRow', F)
    return F, r.p


def _PartTable(b, pos, end):
    r = _R(b, pos, end); F = []
    n = r.peek_u8(); F.append(r.u8())
    for _ in range(n):
        F.append(r.s32())
        F.extend(r.strfill(0x20))
        F.extend(r.strfill(0x20))
    return F, r.p


def _AnimeClipTable(b, pos, end):
    r = _R(b, pos, end); F = []
    while r.peek_s32() != 0:
        F.append(r.s32())
        F.extend(r.strfill(0x20))
        F.extend(r.strfill(0x20))
    F.append(r.s32())
    F.append(r.s16())
    return F, r.p


def _FieldMonsterData(b, pos, end):
    r = _R(b, pos, end); F = []
    _read_schema(r, 'FieldMonsterPrefix', F)
    while r.peek_s32() != 1:
        F.append(r.f32())
    return F, r.p


def _FieldFollowData(b, pos, end):
    r = _R(b, pos, end); F = []
    _read_schema(r, 'FieldFollowData', F)
    return F, r.p


def _FC_autoX(b, pos, end):
    r = _R(b, pos, end); F = [r.string()]
    return F, r.p


def _BookData99(b, pos, end):
    r = _R(b, pos, end); F = [r.s16(), r.s16()]
    return F, r.p


def _BookDataX(b, pos, end):
    r = _R(b, pos, end); F = []
    ctrl = r.peek_s16(); F.append(r.s16())
    if ctrl > 0:
        F.append(r.s16())
        F.extend(r.strfill(0x10, 'bytes'))
        for _ in range(10):
            F.append(r.s16())
        F.append(r.string())
    else:
        if r.peek_u8() != 1:
            F.append(r.string())
    return F, r.p


def _AddCollision(b, pos, end):
    r = _R(b, pos, end); F = []
    n = r.peek_u8(); F.append(r.u8())
    for _ in range(n):
        _read_schema(r, 'AddCollisionRow', F)
    return F, r.p


_PARSERS = {
    'CreateMonsters': _CreateMonsters, 'EffectsInstr': _EffectsInstr,
    'ActionTable': _ActionTable, 'AlgoTable': _AlgoTable,
    'WeaponAttTable': _WeaponAttTable, 'BreakTable': _BreakTable,
    'SummonTable': _SummonTable, 'ReactionTable': _ReactionTable,
    'PartTable': _PartTable, 'AnimeClipTable': _AnimeClipTable,
    'FieldMonsterData': _FieldMonsterData, 'FieldFollowData': _FieldFollowData,
    'FC_autoX': _FC_autoX, 'BookData99': _BookData99, 'BookDataX': _BookDataX,
    'AddCollision': _AddCollision,
}


def parse_table(name, b, pos, end):
    """Parse la table 'name' -> (kind, table_id, fields, new_pos). Leve TableParseError."""
    tt = table_type_for_name(name)
    if tt is None:
        raise TableParseError("'%s' n'est pas une table connue" % name)
    kind, tid = tt
    fields, np = _PARSERS[kind](b, pos, end)
    return kind, tid, fields, np
