# tbl_editor.py — Analyseur/éditeur de fichiers TBL CS1.
# Ouvre un .tbl, affiche les entrées, le hex dump, et permet de
# découper/typer les champs de chaque catégorie.
#
# Lancement : python tbl_editor.py   (ou drag'n'drop un .tbl dessus)
import os, sys, json, struct, glob
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

HERE = os.path.dirname(os.path.abspath(__file__))

FIXW = {'u8': 1, 's8': 1, 'u16': 2, 's16': 2, 'u32': 4, 's32': 4, 'f32': 4}
TYPE_CHOICES = ['u8', 's8', 'u16', 's16', 'u32', 's32', 'f32', 'string', 'cutf8', 'bytes']
COLORS = {
    'u8': '#BBDEFB', 's8': '#BBDEFB',
    'u16': '#C8E6C9', 's16': '#C8E6C9',
    'u32': '#FFF9C4', 's32': '#FFF9C4',
    'f32': '#FFECB3',
    'string': '#F8BBD0', 'cutf8': '#F8BBD0', 'bytes': '#E0E0E0',
}
CHAR_W = 30
GAP = 2
BYTES_PER_ROW = 32


# --- TBL binary I/O ---
def read_tbl(path):
    """Retourne {'count': n, 'entries': [{'cat': str, 'len': int, 'raw': bytes}, ...]}"""
    with open(path, 'rb') as f:
        data = f.read()
    count = data[0] | (data[1] << 8)
    entries = []
    pos = 2
    for _ in range(count):
        # null-terminated category
        end = data.index(0, pos)
        cat = data[pos:end].decode('latin1')
        pos = end + 1
        # declared length
        decl_len = data[pos] | (data[pos + 1] << 8)
        pos += 2
        # For known variable-length categories, find actual payload end
        payload_len = decl_len
        if cat == 'item':
            payload_len = _measure_item(data, pos)
        elif cat == 'magic':
            payload_len = _measure_magic(data, pos)
        elif cat == 'QSText':
            payload_len = _measure_qstext(data, pos)
        raw = data[pos:pos + payload_len]
        entries.append({'cat': cat, 'len': decl_len, 'raw': raw})
        pos += payload_len
    return {'count': count, 'entries': entries, 'path': path}


def write_tbl(tbl, path):
    """Écrit le TBL sur disque."""
    with open(path, 'wb') as f:
        f.write(struct.pack('<H', len(tbl['entries'])))
        for e in tbl['entries']:
            f.write(e['cat'].encode('latin1') + b'\x00')
            f.write(struct.pack('<H', e['len']))
            f.write(e['raw'])


def _cstr_end(data, pos):
    try:
        return data.index(0, pos) - pos
    except ValueError:
        return len(data) - pos


def _measure_item(data, pos):
    p = pos
    p += 4  # skip u32
    p += _cstr_end(data, p) + 1  # name
    p += 46  # fixed block
    p += _cstr_end(data, p) + 1  # desc
    p += _cstr_end(data, p) + 1  # desc2
    return p - pos


def _measure_magic(data, pos):
    p = pos
    p += 4
    p += _cstr_end(data, p) + 1
    p += 24
    p += _cstr_end(data, p) + 1
    p += _cstr_end(data, p) + 1
    p += _cstr_end(data, p) + 1
    return p - pos


def _measure_qstext(data, pos):
    p = pos
    p += 3  # u16 + u8
    p += _cstr_end(data, p) + 1
    p += 1  # u8
    return p - pos


# --- Value formatting ---
def fmt_scalar(raw, typ):
    if typ == 'u8':
        return str(raw[0])
    if typ == 's8':
        v = raw[0]; return str(v - 256 if v >= 128 else v)
    if typ == 'u16' and len(raw) >= 2:
        return str(raw[0] | (raw[1] << 8))
    if typ == 's16' and len(raw) >= 2:
        v = raw[0] | (raw[1] << 8); return str(v - 65536 if v >= 32768 else v)
    if typ == 'u32' and len(raw) >= 4:
        return str(raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24))
    if typ == 's32' and len(raw) >= 4:
        v = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24)
        return str(v - 4294967296 if v >= 2147483648 else v)
    if typ == 'f32' and len(raw) >= 4:
        return f"{struct.unpack('<f', raw[:4])[0]:.6g}"
    if typ in ('string', 'cutf8'):
        try:
            end = raw.index(0)
            return raw[:end].decode('utf-8' if typ == 'cutf8' else 'latin1')
        except ValueError:
            return raw.decode('utf-8' if typ == 'cutf8' else 'latin1', errors='replace')
    return raw.hex(' ').upper()


# --- Schema persistence ---
def load_schema(schema_path):
    try:
        with open(schema_path, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return {}


def save_schema(schema_path, schema):
    with open(schema_path, 'w', encoding='utf-8') as f:
        json.dump(schema, f, indent=1, ensure_ascii=False)


# ================================================================
class TblEditor:
    def __init__(self, root, path=None):
        self.root = root
        root.title("TBL Editor — Analyseur de tables CS1")
        root.geometry("1400x900")
        self.tbl = None  # single-file mode (legacy)
        self.all_entries = []  # [{file, idx, cat, len, raw}, ...] — multi-file scan
        self.sel_entry_idx = -1  # index dans all_entries
        self.sel_field_idx = -1
        self.fields = []
        self._drag_start = None
        self.schema_path = os.path.join(HERE, "tbl_schemas.json")
        self.schemas = load_schema(self.schema_path)
        self.folder = ""

        self._build_ui()
        # Auto-scan if default folder exists
        default = r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\text\dat_us"
        if os.path.isdir(default):
            self.folder_var.set(default)
            self.root.after(200, self._scan_folder)

    # --- UI ---
    def _build_ui(self):
        top = ttk.Frame(self.root, padding=5); top.pack(fill=tk.X)

        # Ligne 1 : dossier + scan
        row1 = ttk.Frame(top); row1.pack(fill=tk.X)
        ttk.Label(row1, text="Dossier:").pack(side=tk.LEFT)
        self.folder_var = tk.StringVar()
        ttk.Entry(row1, textvariable=self.folder_var, width=55).pack(side=tk.LEFT, padx=3)
        ttk.Button(row1, text="...", command=self._browse_folder).pack(side=tk.LEFT)
        ttk.Button(row1, text="🔍 Scanner", command=self._scan_folder).pack(side=tk.LEFT, padx=3)
        ttk.Separator(row1, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(row1, text="📥 Importer CS1 schemas", command=self._import_cs1_schemas).pack(side=tk.LEFT, padx=3)

        # Ligne 2 : filtre + catégorie + schéma
        row2 = ttk.Frame(top); row2.pack(fill=tk.X, pady=(3, 0))
        ttk.Label(row2, text="Filtre:").pack(side=tk.LEFT)
        self.filter_var = tk.StringVar()
        self.filter_var.trace_add('write', lambda *a: self._refresh_entries())
        ttk.Entry(row2, textvariable=self.filter_var, width=14).pack(side=tk.LEFT, padx=3)
        ttk.Label(row2, text="Catégorie:").pack(side=tk.LEFT, padx=(8, 0))
        self.cat_var = tk.StringVar()
        ttk.Entry(row2, textvariable=self.cat_var, width=14).pack(side=tk.LEFT, padx=3)
        ttk.Button(row2, text="Appliquer schéma", command=self._apply_schema).pack(side=tk.LEFT, padx=3)
        ttk.Button(row2, text="💾 Sauver schéma", command=self._save_schema).pack(side=tk.LEFT, padx=3)
        ttk.Separator(row2, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(row2, text="⬇ Sauver .tbl", command=self._save_dialog).pack(side=tk.LEFT, padx=3)
        self.status_var = tk.StringVar(value="Prêt — Scanner un dossier de .tbl...")
        ttk.Label(row2, textvariable=self.status_var).pack(side=tk.LEFT, padx=12)

        pw = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL); pw.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)

        # gauche : liste entrées
        left = ttk.Frame(pw); pw.add(left, weight=1)
        ttk.Label(left, text="Entrées").pack(anchor=tk.W)
        cols = ('idx', 'file', 'cat', 'size')
        self.etree = ttk.Treeview(left, columns=cols, show='headings', height=30)
        for c, t, w in (('idx', '#', 35), ('file', 'Fichier', 80), ('cat', 'Catégorie', 110), ('size', 'Taille', 55)):
            self.etree.heading(c, text=t); self.etree.column(c, width=w)
        self.etree.pack(fill=tk.BOTH, expand=True)
        self.etree.bind('<<TreeviewSelect>>', self._on_pick_entry)

        # milieu : hex dump
        mid = ttk.Frame(pw); pw.add(mid, weight=3)
        self.hex_canvas = tk.Canvas(mid, height=140, bg='white', highlightthickness=0)
        self.hex_canvas.pack(fill=tk.X, pady=2)
        self.hex_canvas.bind('<ButtonPress-1>', self._on_hex_press)
        self.hex_canvas.bind('<B1-Motion>', self._on_hex_drag)
        self.hex_canvas.bind('<ButtonRelease-1>', self._on_hex_release)
        ttk.Label(mid, text="Glisser pour sélectionner → ✂ Créer champ",
                  foreground='gray').pack(anchor=tk.W)

        # droite : champs
        right = ttk.Frame(pw); pw.add(right, weight=3)
        cols2 = ('idx', 'name', 'type', 'off', 'size', 'value')
        self.ftree = ttk.Treeview(right, columns=cols2, show='headings', height=18)
        for c, t, w in (('idx', '#', 30), ('name', 'Nom', 110), ('type', 'Type', 55),
                        ('off', 'Off', 42), ('size', 'Sz', 35), ('value', 'Valeur', 260)):
            self.ftree.heading(c, text=t); self.ftree.column(c, width=w)
        self.ftree.pack(fill=tk.BOTH, expand=True, pady=2)
        self.ftree.bind('<<TreeviewSelect>>', self._on_pick_field)

        # barre d'édition des champs
        edit = ttk.LabelFrame(self.root, text="Éditer le champ", padding=5); edit.pack(fill=tk.X, padx=5)
        ttk.Label(edit, text="Type:").pack(side=tk.LEFT)
        self.type_var = tk.StringVar(value='u16')
        ttk.Combobox(edit, textvariable=self.type_var, values=TYPE_CHOICES, width=8, state='readonly').pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Retyper", command=self._do_retype).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Label(edit, text="Nom:").pack(side=tk.LEFT)
        self.name_var = tk.StringVar()
        ttk.Entry(edit, textvariable=self.name_var, width=16).pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Renommer", command=self._do_rename).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Label(edit, text="Split après N:").pack(side=tk.LEFT)
        self.split_var = tk.StringVar(value='4')
        ttk.Entry(edit, textvariable=self.split_var, width=5).pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Split", command=self._do_split).pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Merge ↓", command=self._do_merge).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(edit, text="✂ Créer champ (sélection)", command=self._do_create_field).pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="🗑 Supprimer champ", command=self._do_delete_field).pack(side=tk.LEFT, padx=3)

    # --- I/O ---
    def _browse_folder(self):
        d = filedialog.askdirectory(initialdir=self.folder_var.get() or HERE)
        if d: self.folder_var.set(d)

    def _scan_folder(self):
        folder = self.folder_var.get().strip()
        if not os.path.isdir(folder):
            messagebox.showwarning("Dossier", f"Dossier introuvable :\n{folder}")
            return
        self.folder = folder
        self.all_entries = []
        tbl_files = sorted(glob.glob(os.path.join(folder, '*.tbl')))
        if not tbl_files:
            messagebox.showwarning("Dossier", f"Aucun .tbl trouvé dans\n{folder}")
            self._refresh_entries()
            return

        self.status_var.set(f"Scan de {len(tbl_files)} fichiers .tbl...")
        self.root.update_idletasks()
        total = 0
        cats = set()
        for fp in tbl_files:
            try:
                tbl = read_tbl(fp)
                fname = os.path.basename(fp)
                for e in tbl['entries']:
                    self.all_entries.append({
                        'file': fname, 'idx': len(self.all_entries),
                        'cat': e['cat'], 'len': e['len'], 'raw': e['raw'],
                        '_source': fp, '_entry_idx': e.get('_idx', total),
                    })
                    cats.add(e['cat'])
                    total += 1
            except Exception as ex:
                self.status_var.set(f"Erreur sur {os.path.basename(fp)}: {ex}")
        self.root.title(f"TBL Editor — {len(tbl_files)} fichiers, {total} entrées, {len(cats)} catégories")
        self.status_var.set(f"{total} entrées dans {len(tbl_files)} .tbl — {len(cats)} catégories")
        self._refresh_entries()

    def _open_dialog(self):
        p = filedialog.askopenfilename(filetypes=[('TBL files', '*.tbl'), ('All files', '*.*')])
        if p:
            self._open(p)

    def _open(self, path):
        """Single-file mode (legacy)."""
        try:
            self.tbl = read_tbl(path)
            self.all_entries = []
            for e in self.tbl['entries']:
                self.all_entries.append({
                    'file': os.path.basename(path), 'idx': len(self.all_entries),
                    'cat': e['cat'], 'len': e['len'], 'raw': e['raw'],
                    '_source': path,
                })
            cats = set(e['cat'] for e in self.all_entries)
            self.root.title(f"TBL Editor — {os.path.basename(path)} ({len(self.all_entries)} entrées)")
            self.status_var.set(f"Chargé: {os.path.basename(path)} — {len(self.all_entries)} entrées, {len(cats)} catégories")
            self._refresh_entries()
        except Exception as ex:
            self.all_entries = []
            self._refresh_entries()
            messagebox.showerror("Erreur", f"Lecture TBL: {ex}")

    def _save_dialog(self):
        if not self.all_entries or self.sel_entry_idx < 0: return
        entry = self.all_entries[self.sel_entry_idx]
        src = entry.get('_source', '')
        p = filedialog.asksaveasfilename(
            defaultextension='.tbl',
            initialdir=os.path.dirname(src) if src else HERE,
            initialfile=os.path.basename(src) if src else '',
            filetypes=[('TBL files', '*.tbl')])
        if p:
            try:
                write_tbl(self.tbl, p)
                self.status_var.set(f"Sauvé: {os.path.basename(p)}")
            except Exception as ex:
                messagebox.showerror("Erreur", f"Écriture: {ex}")

    def _refresh_entries(self):
        self.etree.delete(*self.etree.get_children())
        if not self.all_entries:
            self.etree.insert('', tk.END, values=('—', 'Aucune entrée', '—', '—'))
            self.fields = []
            self._refresh_fields()
            return
        filt = (self.filter_var.get() or '').lower()
        for e in self.all_entries:
            if filt and filt not in e['cat'].lower() and filt not in e['file'].lower():
                continue
            self.etree.insert('', tk.END, values=(e['idx'], e['file'], e['cat'], e['len']))

    # --- Sélection ---
    def _on_pick_entry(self, _ev):
        sel = self.etree.selection()
        if not sel: return
        self.sel_entry_idx = int(self.etree.item(sel[0], 'values')[0])
        entry = self.all_entries[self.sel_entry_idx]
        self.cat_var.set(entry['cat'])
        cat = entry['cat']
        if cat in self.schemas:
            self._decode_with_schema(cat)
        else:
            self.fields = [{'type': 'bytes', 'off': 0, 'size': len(entry['raw']), 'raw': entry['raw'], 'name': ''}]
            self.sel_field_idx = -1
        self._draw_hex()
        self._refresh_fields()
        self.status_var.set(f"#{self.sel_entry_idx}  {entry['file']}  {entry['cat']}  {len(entry['raw'])} octets")

    def _on_pick_field(self, _ev):
        sel = self.ftree.selection()
        if sel:
            self.sel_field_idx = int(self.ftree.item(sel[0], 'values')[0])
            f = self.fields[self.sel_field_idx]
            self.type_var.set(f['type'])
            self.name_var.set(f.get('name', ''))
        else:
            self.sel_field_idx = -1

    # --- Hex dump ---
    def _draw_hex(self):
        self.hex_canvas.delete('all')
        if self.sel_entry_idx < 0 or self.sel_entry_idx >= len(self.all_entries):
            self.hex_canvas.create_text(550, 60, text="Scanner un dossier puis cliquer sur une entrée",
                                        font=('Segoe UI', 11), fill='#999')
            return
        raw = self.all_entries[self.sel_entry_idx]['raw']
        y = 4
        for row_start in range(0, len(raw), BYTES_PER_ROW):
            # offset label
            self.hex_canvas.create_text(8, y + 10, text=f"{row_start:04X}", anchor=tk.W,
                                        font=('Consolas', 9), fill='#666')
            for i in range(BYTES_PER_ROW):
                idx = row_start + i
                if idx >= len(raw): break
                x = 60 + i * CHAR_W
                b = raw[idx]
                # color by field
                color = '#333'
                bg = '#F5F5F5'
                for f in self.fields:
                    if f['off'] <= idx < f['off'] + f['size']:
                        bg = COLORS.get(f['type'], '#E0E0E0')
                        break
                self.hex_canvas.create_rectangle(x - 1, y, x + CHAR_W - GAP - 1, y + 22, fill=bg, outline='')
                self.hex_canvas.create_text(x + 9, y + 11, text=f"{b:02X}",
                                            font=('Consolas', 9, 'bold'), fill=color)
            y += 24
        self.hex_canvas.config(scrollregion=(0, 0, 1100, y + 4))

    def _byte_at(self, x, y):
        """Convertit coordonnées canvas → index d'octet."""
        if not self.all_entries or self.sel_entry_idx < 0 or self.sel_entry_idx >= len(self.all_entries):
            return -1
        row = y // 24
        col = (x - 60) // CHAR_W
        idx = row * BYTES_PER_ROW + col
        raw = self.all_entries[self.sel_entry_idx]['raw']
        if 0 <= idx < len(raw):
            return idx
        return -1

    def _on_hex_press(self, ev):
        if not self.all_entries:
            return
        idx = self._byte_at(ev.x, ev.y)
        if idx >= 0:
            # Smart select: cliquer sur un octet → sélectionner tout le champ parent
            field = self._field_at(idx)
            if field:
                self._drag_start = field['off']
                self._drag_end = field['off'] + field['size'] - 1
            else:
                self._drag_start = idx
                self._drag_end = idx
            self._draw_hex()
            self._draw_selection()

    def _field_at(self, byte_idx):
        """Retourne le champ contenant l'octet donné, ou None."""
        for f in self.fields:
            if f['off'] <= byte_idx < f['off'] + f['size']:
                return f
        return None

    def _on_hex_drag(self, ev):
        if self._drag_start is None or not self.all_entries:
            return
        idx = self._byte_at(ev.x, ev.y)
        if idx >= 0:
            self._drag_end = idx
            self._draw_hex()
            self._draw_selection()

    def _on_hex_release(self, _ev):
        if self._drag_start is not None and self._drag_end is not None:
            a = min(self._drag_start, self._drag_end)
            b = max(self._drag_start, self._drag_end)
            self.status_var.set(f"Sélection: [{a}, {b}] ({b - a + 1} octets)")
        self._drag_start = None
        self._drag_end = None

    def _draw_selection(self):
        if self._drag_start is None or self._drag_end is None: return
        a = min(self._drag_start, self._drag_end)
        b = max(self._drag_start, self._drag_end)
        for idx in range(a, b + 1):
            row = idx // BYTES_PER_ROW
            col = idx % BYTES_PER_ROW
            x = 60 + col * CHAR_W
            y = 4 + row * 24
            self.hex_canvas.create_rectangle(x - 1, y, x + CHAR_W - GAP - 1, y + 22,
                                             outline='#E91E63', width=2)

    # --- Champ table ---
    def _refresh_fields(self):
        self.ftree.delete(*self.ftree.get_children())
        for i, f in enumerate(self.fields):
            val = fmt_scalar(f['raw'], f['type'])
            self.ftree.insert('', tk.END, values=(i, f.get('name', ''), f['type'], f['off'], f['size'], val))
        self._draw_hex()

    def _do_retype(self):
        if self.sel_field_idx < 0 or self.sel_field_idx >= len(self.fields): return
        new_type = self.type_var.get()
        f = self.fields[self.sel_field_idx]
        if new_type in FIXW:
            need = FIXW[new_type]
            if f['size'] < need:
                # Essayer de merger avec les champs suivants
                merged_size = f['size']
                end_idx = self.sel_field_idx
                while merged_size < need and end_idx + 1 < len(self.fields):
                    end_idx += 1
                    merged_size += self.fields[end_idx]['size']
                if merged_size < need:
                    messagebox.showwarning("Taille", f"Il faut {need} octets pour {new_type}, "
                                           f"le champ et ses suivants n'en font que {merged_size}.")
                    return
                # Fusionner f .. end_idx
                merged_raw = b''.join(self.fields[i]['raw'] for i in range(self.sel_field_idx, end_idx + 1))
                f['size'] = need
                f['raw'] = merged_raw[:need]
                f['type'] = new_type
                # Créer un champ bytes avec le reste si besoin
                remainder = merged_raw[need:]
                new_fields = [f]
                if remainder:
                    new_fields.append({'type': 'bytes', 'off': 0, 'size': len(remainder), 'raw': remainder})
                self.fields[self.sel_field_idx:end_idx + 1] = new_fields
                self._reindex_fields()
                return
            if f['size'] > need:
                # Split: garder 'need' octets, le reste devient un champ bytes
                remainder = f['raw'][need:]
                f['size'] = need
                f['raw'] = f['raw'][:need]
                f['type'] = new_type
                rest_field = {'type': 'bytes', 'off': 0, 'size': len(remainder), 'raw': remainder}
                self.fields.insert(self.sel_field_idx + 1, rest_field)
                self._reindex_fields()
                return
            f['size'] = need
            f['raw'] = f['raw'][:need]
        elif new_type in ('string', 'cutf8'):
            # find null terminator
            try:
                end = f['raw'].index(0)
                f['size'] = end + 1
                f['raw'] = f['raw'][:f['size']]
            except ValueError:
                pass  # keep as-is
        f['type'] = new_type
        self._refresh_fields()

    def _do_rename(self):
        if self.sel_field_idx < 0 or self.sel_field_idx >= len(self.fields): return
        self.fields[self.sel_field_idx]['name'] = self.name_var.get().strip()
        self._refresh_fields()

    def _do_split(self):
        if self.sel_field_idx < 0 or self.sel_field_idx >= len(self.fields): return
        try:
            n = int(self.split_var.get())
        except ValueError:
            return
        f = self.fields[self.sel_field_idx]
        if n <= 0 or n >= f['size']:
            messagebox.showwarning("Split", f"Split après {n} impossible (taille={f['size']}).")
            return
        left_raw = f['raw'][:n]
        right_raw = f['raw'][n:]
        left = {'type': 'bytes', 'off': f['off'], 'size': n, 'raw': left_raw}
        right = {'type': 'bytes', 'off': f['off'] + n, 'size': f['size'] - n, 'raw': right_raw}
        self.fields[self.sel_field_idx:self.sel_field_idx + 1] = [left, right]
        self._reindex_fields()

    def _do_merge(self):
        if self.sel_field_idx < 0 or self.sel_field_idx + 1 >= len(self.fields): return
        a = self.fields[self.sel_field_idx]
        b = self.fields[self.sel_field_idx + 1]
        merged = {
            'type': 'bytes', 'off': a['off'],
            'size': a['size'] + b['size'],
            'raw': a['raw'] + b['raw'],
        }
        self.fields[self.sel_field_idx:self.sel_field_idx + 2] = [merged]
        self._reindex_fields()

    def _do_create_field(self):
        """Crée un champ à partir de la sélection drag."""
        if self._drag_start is None and self._drag_end is None:
            # peut-être que la sélection précédente est encore visible
            messagebox.showwarning("Sélection", "Sélectionnez d'abord des octets dans le hex dump.")
            return
        a = min(self._drag_start, self._drag_end) if self._drag_start is not None and self._drag_end is not None else -1
        b = max(self._drag_start, self._drag_end) if self._drag_start is not None and self._drag_end is not None else -1
        if a < 0:
            return
        raw = self.all_entries[self.sel_entry_idx]['raw']
        # Find which existing field(s) this overlaps, split them
        new_fields = []
        consumed = False
        for f in self.fields:
            f_end = f['off'] + f['size']
            if f_end <= a or f['off'] >= b + 1:
                new_fields.append(f)
                continue
            if consumed:
                continue
            # this field overlaps: split into before / selected / after
            if f['off'] < a:
                new_fields.append({'type': 'bytes', 'off': f['off'], 'size': a - f['off'],
                                   'raw': raw[f['off']:a]})
            new_fields.append({'type': 'bytes', 'off': a, 'size': b - a + 1,
                               'raw': raw[a:b + 1]})
            if b + 1 < f_end:
                new_fields.append({'type': 'bytes', 'off': b + 1, 'size': f_end - (b + 1),
                                   'raw': raw[b + 1:f_end]})
            consumed = True
        if not consumed:
            # no overlap, insert as new
            new_fields.append({'type': 'bytes', 'off': a, 'size': b - a + 1, 'raw': raw[a:b + 1]})
            new_fields.sort(key=lambda x: x['off'])
        self.fields = new_fields
        self._reindex_fields()

    def _do_delete_field(self):
        if self.sel_field_idx < 0 or self.sel_field_idx >= len(self.fields): return
        f = self.fields[self.sel_field_idx]
        # merge with previous or next
        if self.sel_field_idx > 0:
            prev = self.fields[self.sel_field_idx - 1]
            prev['size'] += f['size']
            prev['raw'] += f['raw']
            prev['type'] = 'bytes'
            del self.fields[self.sel_field_idx]
        elif self.sel_field_idx + 1 < len(self.fields):
            nxt = self.fields[self.sel_field_idx + 1]
            nxt['off'] = f['off']
            nxt['size'] += f['size']
            nxt['raw'] = f['raw'] + nxt['raw']
            nxt['type'] = 'bytes'
            del self.fields[self.sel_field_idx]
        else:
            # seul champ: le garder en bytes
            f['type'] = 'bytes'
        self._reindex_fields()

    def _reindex_fields(self):
        """Recalcule les offsets et sauvegarde dans l'entrée."""
        # Reconstruire le raw de l'entrée à partir des champs
        new_raw = b''.join(f['raw'] for f in self.fields)
        self.all_entries[self.sel_entry_idx]['raw'] = new_raw
        self.all_entries[self.sel_entry_idx]['len'] = len(new_raw)
        # Recalculer les offsets
        off = 0
        for f in self.fields:
            f['off'] = off
            f['raw'] = new_raw[off:off + f['size']]
            off += f['size']
        self.sel_field_idx = min(self.sel_field_idx, len(self.fields) - 1)
        self._refresh_fields()

    # --- Schéma ---
    def _decode_with_schema(self, cat):
        """Applique le schéma connu pour cette catégorie."""
        schema_fields = self.schemas[cat]
        raw = self.all_entries[self.sel_entry_idx]['raw']
        self.fields = []
        pos = 0
        for sf in schema_fields:
            typ = sf['type']
            name = sf.get('name', '')
            if typ in FIXW:
                size = FIXW[typ]
            elif typ in ('string', 'cutf8'):
                try:
                    end = raw.index(0, pos)
                    size = end - pos + 1
                except ValueError:
                    size = len(raw) - pos
            else:
                size = sf.get('size', len(raw) - pos)
            if pos + size > len(raw):
                size = len(raw) - pos
            if size <= 0:
                break
            self.fields.append({'type': typ, 'off': pos, 'size': size, 'raw': raw[pos:pos + size], 'name': name})
            pos += size
        self._refresh_fields()

    def _apply_schema(self):
        """Applique le schéma connu pour la catégorie courante."""
        cat = self.cat_var.get().strip()
        if cat in self.schemas:
            self._decode_with_schema(cat)
            self.status_var.set(f"Schéma appliqué: {cat}")
        else:
            messagebox.showinfo("Schéma", f"Pas de schéma pour '{cat}'.")

    def _save_schema(self):
        """Sauvegarde le layout courant comme schéma pour cette catégorie."""
        if self.sel_entry_idx < 0 or not self.all_entries:
            return
        cat = self.all_entries[self.sel_entry_idx]['cat']
        schema_fields = []
        for f in self.fields:
            sf = {'type': f['type']}
            if f.get('name'):
                sf['name'] = f['name']
            if f['type'] not in FIXW and f['type'] not in ('string', 'cutf8'):
                sf['size'] = f['size']
            schema_fields.append(sf)
        self.schemas[cat] = schema_fields
        save_schema(self.schema_path, self.schemas)
        self.status_var.set(f"Schéma sauvegardé pour '{cat}' ({len(schema_fields)} champs)")

    def _import_cs1_schemas(self):
        """Importe les définitions depuis un cs1_tbl_schemas.json (format C# ED8Editor)."""
        default = os.path.normpath(os.path.join(
            HERE, '..', 'src', 'ED8Editor.Tables', 'cs1_tbl_schemas.json'))
        if not os.path.isfile(default):
            default = os.path.normpath(os.path.join(HERE, '..', 'publish', 'cs1_tbl_schemas.json'))
        p = filedialog.askopenfilename(
            title="Importer cs1_tbl_schemas.json",
            initialdir=os.path.dirname(default) if os.path.isfile(default) else HERE,
            initialfile=os.path.basename(default) if os.path.isfile(default) else '',
            filetypes=[('JSON', '*.json'), ('All', '*.*')])
        if not p:
            return
        try:
            with open(p, 'r', encoding='utf-8') as f:
                cs1 = json.load(f)
        except Exception as ex:
            messagebox.showerror("Erreur", f"Lecture JSON: {ex}")
            return
        imported = 0
        for cat, schema in cs1.get('entries', {}).items():
            fields = []
            for sf in schema.get('fields', []):
                typ = sf['type']
                count = sf.get('count', 1)
                for _ in range(count):
                    field_def = {'type': typ}
                    if typ not in FIXW and typ not in ('string', 'cutf8'):
                        field_def['size'] = sf.get('size', 4)
                    fields.append(field_def)
            if fields:
                self.schemas[cat] = fields
                imported += 1
        # Also import common schemas
        for cat, schema in cs1.get('common', {}).items():
            fields = []
            for sf in schema.get('fields', []):
                typ = sf['type']
                count = sf.get('count', 1)
                for _ in range(count):
                    field_def = {'type': typ}
                    if typ not in FIXW and typ not in ('string', 'cutf8'):
                        field_def['size'] = sf.get('size', 4)
                    fields.append(field_def)
            if fields and cat not in self.schemas:
                self.schemas[cat] = fields
                imported += 1
        save_schema(self.schema_path, self.schemas)
        messagebox.showinfo("Schémas importés",
            f"{imported} catégories chargées depuis\n{os.path.basename(p)}.\n\n"
            f"Clique sur une entrée pour voir le décodage automatique.")
        self.status_var.set(f"Importé {imported} schémas depuis {os.path.basename(p)}")


def main():
    root = tk.Tk()
    path = sys.argv[1] if len(sys.argv) > 1 and os.path.isfile(sys.argv[1]) else None
    TblEditor(root, path)
    root.mainloop()


if __name__ == '__main__':
    main()
