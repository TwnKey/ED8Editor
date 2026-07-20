# instr_model.py — couche de donnees (sans GUI) pour l'editeur d'instructions CS1.
# Source de verite : cs1_instructions.json (495 instructions a plat, une par branche).
# Fournit : chargement, decodage par matching de branches (avec plages d'octets par champ),
# collecte d'echantillons depuis un corpus, statistiques heuristiques par champ,
# edition du 'read' (retype / split / merge, largeur preservee), sauvegarde.
import json, struct, os, glob

FIXW = {'u8':1,'s8':1,'u16':2,'s16':2,'u32':4,'s32':4,'f32':4,'ptr32':4}
SCALARS = set(FIXW)

# ---- helpers binaires (identiques a l'outil/au jeu) ----
def read_cstr(b, p):
    q = p
    while q < len(b) and b[q] != 0:
        q += 1
    return (q + 1) - p if q < len(b) else None

def _expr_sub(x):
    if x == 0x00: return 5
    if x == 0x1e: return 3
    if x in (0x1f, 0x20, 0x23): return 2
    if x == 0x21: return 4
    if x == 0x1c: return 5
    return 1

def skip_expr(b, p):
    i = p
    while i < len(b):
        x = b[i]
        if x == 0x01: return (i + 1) - p
        s = _expr_sub(x)
        if s < 0: return None
        i += s
    return None

def skip_dialog(b, p):
    i = p
    while i < len(b):
        x = b[i]
        if x == 0x00: return (i + 1) - p
        if x < 0x20: i += 3 if x == 0x10 else (5 if x in (0x11, 0x12) else 1)
        else: i += 1
    return None

def u16(b,p): return b[p]|(b[p+1]<<8)
def i16(b,p):
    v=u16(b,p); return v-0x10000 if v>=0x8000 else v
def u32(b,p): return b[p]|(b[p+1]<<8)|(b[p+2]<<16)|(b[p+3]<<24)

def parse_header(b):
    nb = u32(b, 0x14); ptr_area = u32(b, 0x08)
    funcs = []
    for k in range(nb):
        start = u32(b, ptr_area + 4 * k)
        npos = i16(b, ptr_area + 4 * nb + 2 * k)
        end = u32(b, ptr_area + 4 * (k + 1)) if k < nb - 1 else len(b)
        nm = ""
        if 0 <= npos < len(b):
            e = npos
            while e < len(b) and b[e] != 0: e += 1
            nm = b[npos:e].decode('latin1', 'replace')
        funcs.append((k, nm, start, end))
    return {"nb": nb, "funcs": funcs}

# --- expression : table complete verifiee sur le VM (FUN_00652c20) ---
# Operateurs (pop/push sur la pile, 0 octet de charge). Les doublons = alias du VM.
EXPR_OPS = {
    0x02:'==', 0x03:'!=', 0x04:'<', 0x05:'>', 0x06:'<=', 0x07:'>=',
    0x08:'==0',          # not logique : pousse (sommet == 0)
    0x09:'&&',
    0x0a:'&', 0x19:'&',  # ET binaire (alias)
    0x0b:'|', 0x1b:'|',  # OU binaire
    0x0c:'+', 0x17:'+',
    0x0d:'-', 0x18:'-',
    0x0e:'neg',          # moins unaire
    0x0f:'^', 0x1a:'^',  # XOR
    0x10:'*',            # mul signe
    0x14:'*',            # mul non signe
    0x11:'/', 0x15:'/',  # division
    0x12:'%', 0x16:'%',  # modulo
    0x1d:'~',            # NOT binaire unaire
    0x13:'nop',          # pas de case dans le VM -> no-op
    0x22:'rand',         # abs(random), pousse une valeur
}
# Sous-ops "push" avec charge (nombre d'octets de charge apres le sous-op).
# 0x1c = redispatch (instruction imbriquee, longueur variable) ; 0x01 = END.
EXPR_PAYLOAD = {0x00:4, 0x1e:2, 0x1f:1, 0x20:1, 0x23:1, 0x21:3}
# Tout sous-op absent de EXPR_OPS/EXPR_PAYLOAD/{0x01,0x1c} = no-op 1 octet (aucun case dans le VM).

def decode_expr_tokens(raw, mdl=None):
    """Decode une expression (octets) en liste de tokens lisibles. 0x1c = instruction imbriquee."""
    raw = bytes(raw); i = 0; toks = []
    while i < len(raw):
        x = raw[i]
        if x == 0x01:
            toks.append('END'); break
        if x == 0x1c:
            if mdl is not None:
                r = mdl.decode_instr(bytearray(raw), i+1, len(raw), False)
                if r:
                    ri, fields, L = r
                    toks.append('call %s' % mdl.instructions[ri]['name']); i += 1 + L; continue
            toks.append('call ?'); break
        pl = EXPR_PAYLOAD.get(x, 0)
        pay = raw[i+1:i+1+pl]
        if x == 0x00: toks.append('push %d' % int.from_bytes(pay, 'little'))
        elif x == 0x1e: toks.append('flag[%d]' % int.from_bytes(pay, 'little'))
        elif x == 0x1f: toks.append('reg[%d]' % (pay[0] if pay else 0))
        elif x == 0x23: toks.append('work[%d]' % (pay[0] if pay else 0))
        elif x == 0x20: toks.append('sys[%d]' % (pay[0] if pay else 0))
        elif x == 0x21: toks.append('query[%d,%d]' % (int.from_bytes(pay[:2], 'little'), pay[2] if len(pay) > 2 else 0))
        elif x in EXPR_OPS: toks.append(EXPR_OPS[x])
        else: toks.append('op%02x' % x)
        i += 1 + pl
    return toks


def scalar_value(t, raw):
    """Renvoie (int_val, float_val)."""
    v = 0
    for k in range(len(raw)): v |= raw[k] << (8*k)
    if t == 's8' and v & 0x80: v -= 0x100
    elif t == 's16' and v & 0x8000: v -= 0x10000
    elif t == 's32' and v & 0x80000000: v -= 0x100000000
    fv = None
    if len(raw) == 4:
        try: fv = struct.unpack('<f', bytes(raw))[0]
        except Exception: fv = None
    return v, fv

# ---- modele ----
class Field:
    __slots__ = ('node','off','length','raw')
    def __init__(self, node, off, length, raw):
        self.node = node; self.off = off; self.length = length; self.raw = raw

class Sample:
    __slots__ = ('file','func','offset','fields','data','ui')
    def __init__(self, file, func, offset, fields, data, ui):
        self.file = file; self.func = func; self.offset = offset; self.fields = fields
        self.data = data   # octets de l'instruction complete (opcode + operandes)
        self.ui = ui

class Model:
    def __init__(self, json_path):
        self.path = json_path
        with open(json_path, 'r', encoding='utf-8') as f:
            d = json.load(f)
        self.ui_files = set(d.get('ui_files', []))
        self.note = d.get('_note', '')
        self.instructions = d['instructions']   # liste de dicts (name, op, read, selectors, scope...)
        self.samples = {}                        # name -> [Sample]
        self._reindex()

    def _reindex(self):
        self.by_op = {}; self.by_op_ui = {}
        self.idx_by_name = {}
        for i, ins in enumerate(self.instructions):
            self.idx_by_name[ins['name']] = i
            ui = ins.get('scope') == 'ui_files'
            (self.by_op_ui if ui else self.by_op).setdefault(ins['op'], []).append(i)
        def nsent(ins): return sum(1 for s in ins['selectors'] if isinstance(s.get('value'), str))
        for m in (self.by_op, self.by_op_ui):
            for k in m: m[k].sort(key=lambda idx: nsent(self.instructions[idx]))

    # --- decodage d'une branche : renvoie (length, lvl) | 'MISMATCH' | None(err) ---
    def _decode_nodes(self, nodes, path, lvl, b, p, e, ctx, out_fields):
        i0 = p
        for n in nodes:
            if 'loop' in n:
                cnt = ctx.get('count', 0); lp = n['loop']
                grp_off = p
                for _ in range(cnt):
                    r = self._decode_nodes(lp['body'], path, lvl, b, p, e, ctx, out_fields)
                    if r == 'MISMATCH' or r is None: return r
                    L, lvl = r; p += L
                # champ synthetique 'loop' couvrant tout le groupe (non editable finement ici)
                continue
            if 'switch' in n or 'switch_peek' in n or 'if' in n or 'ifval' in n:
                return None  # ne doit pas arriver dans un read a plat
            t = n['t']; role = n.get('role')
            if t in FIXW:
                w = FIXW[t]
                if p + w > e: return None
                raw = list(b[p:p+w]); val, _ = scalar_value(t, raw)
                if role in ('selector', 'sel16', 'peek'):
                    want = path[lvl] if lvl < len(path) else None; lvl += 1
                    if isinstance(want, int) and val != want: return 'MISMATCH'
                if role == 'count': ctx['count'] = raw[0]
                if role == 'sel16': ctx['sel16'] = raw[0] | (raw[1] << 8)
                if t == 's16' and 'control' not in ctx: ctx['control'] = raw[0] | (raw[1] << 8)
                out_fields.append(Field(n, p, w, raw)); p += w
            elif t == 'string':
                c = read_cstr(b, p)
                if c is None: return None
                out_fields.append(Field(n, p, c, list(b[p:p+c]))); ctx['laststr'] = c; p += c
            elif t == 'expr':
                c = skip_expr(b, p)
                if c is None: return None
                out_fields.append(Field(n, p, c, list(b[p:p+c]))); p += c
            elif t == 'dialog':
                c = skip_dialog(b, p)
                if c is None: return None
                out_fields.append(Field(n, p, c, list(b[p:p+c]))); p += c
            elif t == 'bytes':
                w = n['size']
                if p + w > e: return None
                out_fields.append(Field(n, p, w, list(b[p:p+w]))); p += w
            elif t == 'fill':
                L = max(n['to'] - ctx.get('laststr', 0), 0)
                if p + L > e: return None
                out_fields.append(Field(n, p, L, list(b[p:p+L]))); p += L
            else:
                return None
        return (p - i0, lvl)

    def decode_instr(self, b, p, e, ui):
        """Decode l'instruction a l'offset p. Renvoie (instr_index, [Field], length) ou None."""
        o = b[p]
        cands = None
        if ui: cands = self.by_op_ui.get(o)
        if cands is None: cands = self.by_op.get(o)
        if not cands: return None
        for ri in cands:
            ins = self.instructions[ri]
            path = [s.get('value') if isinstance(s.get('value'), int) else s.get('value') for s in ins['selectors']]
            path = [v if isinstance(v, int) else v for v in path]
            fields = []; ctx = {}
            r = self._decode_nodes(ins['read'], path, 0, b, p+1, e, ctx, fields)
            if r == 'MISMATCH': continue
            if r is None: return None   # selecteurs OK mais corps invalide
            L, _ = r
            return (ri, fields, 1 + L)
        return None

    def decode_function(self, b, s, e, ui):
        """Decode une fonction entiere. Renvoie liste [(instr_index,[Field],offset)] ou None si echec."""
        out = []; p = s
        while p < e:
            r = self.decode_instr(b, p, e, ui)
            if r is None: return None
            ri, fields, L = r
            if L <= 0: return None
            out.append((ri, fields, p)); p += L
        return out if p == e else None

    # --- scan corpus : remplit self.samples ---
    def scan_corpus(self, folder, max_samples=200, progress=None):
        self.samples = {ins['name']: [] for ins in self.instructions}
        files = sorted(glob.glob(os.path.join(folder, '*.dat')))
        for fi, fp in enumerate(files):
            if progress: progress(fi, len(files), os.path.basename(fp))
            try:
                b = bytearray(open(fp, 'rb').read()); h = parse_header(b)
            except Exception:
                continue
            base = os.path.basename(fp).rsplit('.', 1)[0]
            ui = base in self.ui_files
            for (k, nm, s, e) in h['funcs']:
                if e > len(b) or s >= e: continue
                dec = self.decode_function(b, s, e, ui)
                if dec is None: continue      # fonction data/table -> ignore
                for (ri, fields, off) in dec:
                    name = self.instructions[ri]['name']
                    lst = self.samples[name]
                    if len(lst) < max_samples:
                        end = fields[-1].off + fields[-1].length if fields else off + 1
                        data = bytes(b[off:end])
                        # re-decode sur data -> offsets 0-based coherents
                        rr = self.decode_instr(bytearray(data), 0, len(data), ui)
                        f0 = rr[1] if rr else fields
                        lst.append(Sample(os.path.basename(fp), nm, off, f0, data, ui))
        return {n: len(v) for n, v in self.samples.items() if v}

    def redecode_samples(self, name):
        """Recalcule les plages de champs des echantillons apres une edition du read."""
        for smp in self.samples.get(name, []):
            b = bytearray(smp.data)
            r = self.decode_instr(b, 0, len(b), smp.ui)
            if r is not None:
                _, fields, _ = r
                smp.fields = fields

    # --- statistiques heuristiques par champ (sur les echantillons) ---
    FLOAT_HI = set(range(0x3A, 0x46)) | set(range(0xBA, 0xC6))
    def field_stats(self, name):
        """Renvoie une liste (une entree par champ du read) de dicts de stats/suggestions."""
        ins = self.instructions[self.idx_by_name[name]]
        samples = self.samples.get(name, [])
        # nombre de champs = longueur du read a plat (les loop comptent comme 1, ignore ici)
        nread = len(ins['read'])
        stats = []
        # aligne par index de champ decode (les fields decodes suivent l'ordre du read, hors loop)
        for fi in range(nread):
            node = ins['read'][fi]
            t = node.get('t'); role = node.get('role')
            col = []
            for smp in samples:
                if fi < len(smp.fields): col.append(smp.fields[fi].raw)
            st = {'idx': fi, 'type': t, 'role': role, 'n': len(col)}
            if not col:
                stats.append(st); continue
            widths = set(len(r) for r in col)
            st['width'] = (list(widths)[0] if len(widths) == 1 else None)
            # ascii
            allb = [x for r in col for x in r]
            if allb:
                st['ascii_frac'] = sum(1 for x in allb if 0x20 <= x < 0x7f) / len(allb)
            # scalaires 4 octets : float-likeness + min/max
            if st['width'] == 4:
                hi = [r[3] for r in col]
                st['float_frac'] = sum(1 for x in hi if x in self.FLOAT_HI) / len(hi)
                ints = [scalar_value('s32', r)[0] for r in col]
                st['min'] = min(ints); st['max'] = max(ints)
                fs = [scalar_value('f32', r)[1] for r in col]
                st['float_examples'] = [round(x, 4) for x in fs[:5] if x is not None]
            elif st['width'] in (1, 2):
                ints = [scalar_value('s'+str(st['width']*8), r)[0] for r in col]
                st['min'] = min(ints); st['max'] = max(ints)
            st['distinct'] = len(set(bytes(r) for r in col))
            # suggestion
            sug = None
            if st.get('width') == 4 and t in ('s32', 'u32', 'bytes'):
                if st.get('float_frac', 0) > 0.7: sug = 'f32'
            if t == 'bytes' and st.get('ascii_frac', 0) > 0.85: sug = 'string?'
            st['suggestion'] = sug
            stats.append(st)
        return stats

    # --- editions du read (largeur preservee => roundtrip garanti) ---
    def retype(self, name, field_idx, new_type):
        node = self.instructions[self.idx_by_name[name]]['read'][field_idx]
        old = node.get('t')
        if old in SCALARS and new_type in SCALARS and FIXW[old] != FIXW[new_type]:
            raise ValueError("retype scalaire de largeur differente : utilise split/merge")
        if old == 'bytes' and new_type in SCALARS and node.get('size') != FIXW[new_type]:
            raise ValueError("bytes de taille %d != %s : split d'abord" % (node.get('size'), new_type))
        node.pop('size', None)
        node['t'] = new_type
        if new_type == 'bytes':  # cas inverse gere par split/merge
            raise ValueError("pour creer un bytes, utilise merge")
        return True

    def split(self, name, field_idx, first_len):
        """Coupe un champ fixe (bytes ou scalaire) en deux a l'octet first_len."""
        read = self.instructions[self.idx_by_name[name]]['read']
        node = read[field_idx]; t = node.get('t')
        if t in SCALARS: w = FIXW[t]
        elif t == 'bytes': w = node['size']
        else: raise ValueError("champ non fractionnable (%s)" % t)
        if not (0 < first_len < w): raise ValueError("position de coupe invalide")
        a = {'t': 'bytes', 'size': first_len} if first_len != 1 else {'t': 'u8'}
        rem = w - first_len
        bnode = {'t': 'bytes', 'size': rem} if rem != 1 else {'t': 'u8'}
        # preserve un role eventuel sur le 1er morceau uniquement
        if node.get('role'): a['role'] = node['role']
        read[field_idx:field_idx+1] = [a, bnode]
        return True

    def split_range(self, name, field_idx, a, b):
        """Coupe un champ fixe en [0:a][a:b][b:w]. a,b = octets dans le champ.
        Le morceau du milieu [a:b] est le nouvel operande a typer. Renvoie son index."""
        read = self.instructions[self.idx_by_name[name]]['read']
        node = read[field_idx]; t = node.get('t')
        if t in SCALARS: w = FIXW[t]
        elif t == 'bytes': w = node['size']
        else: raise ValueError("champ non fractionnable (%s)" % t)
        if not (0 <= a < b <= w): raise ValueError("selection invalide (%d..%d sur %d)" % (a, b, w))
        def mk(n): return {'t': 'u8'} if n == 1 else {'t': 'bytes', 'size': n}
        pieces = []
        mid_idx = field_idx
        if a > 0:
            p0 = mk(a)
            if node.get('role'): p0['role'] = node['role']   # le role reste sur le 1er morceau
            pieces.append(p0); mid_idx += 1
        pieces.append(mk(b - a))            # nouvel operande (milieu)
        if b < w: pieces.append(mk(w - b))
        read[field_idx:field_idx+1] = pieces
        return mid_idx

    def set_validated(self, name, val=True):
        self.instructions[self.idx_by_name[name]]['validated'] = bool(val)

    def is_validated(self, name):
        return bool(self.instructions[self.idx_by_name[name]].get('validated'))

    def merge(self, name, field_idx):
        """Fusionne field_idx et field_idx+1 en un bloc bytes (les deux doivent etre de largeur fixe)."""
        read = self.instructions[self.idx_by_name[name]]['read']
        if field_idx+1 >= len(read): raise ValueError("pas de champ suivant")
        def wof(n):
            if n.get('t') in SCALARS: return FIXW[n['t']]
            if n.get('t') == 'bytes': return n['size']
            return None
        w1 = wof(read[field_idx]); w2 = wof(read[field_idx+1])
        if w1 is None or w2 is None: raise ValueError("fusion possible seulement entre champs de largeur fixe")
        merged = {'t': 'bytes', 'size': w1 + w2}
        if read[field_idx].get('role'): merged['role'] = read[field_idx]['role']
        read[field_idx:field_idx+2] = [merged]
        return True

    def save(self, path=None):
        path = path or self.path
        out = {'_note': self.note, 'ui_files': sorted(self.ui_files), 'instructions': self.instructions}
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(out, f, ensure_ascii=False, indent=1)
        return path
