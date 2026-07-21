# instr_editor.py — Editeur d'instructions CS1, centre sur cs1_instructions.json.
# Parcourir les 495 instructions -> voir leurs champs + des octets d'exemple du corpus
# + des statistiques heuristiques (float/ascii/min-max) -> retyper / redecouper la
# sequence d'octets -> sauver directement dans cs1_instructions.json.
#
# Lancement : python instr_editor.py   (ou run_editor.cmd)
import os, sys, json, glob
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import instr_model as M

SETTINGS = os.path.join(HERE, "editor_settings.json")
DEFAULT_JSON = os.path.join(HERE, "cs1_instructions.json")

TYPE_CHOICES = ["u8", "s8", "u16", "s16", "u32", "s32", "f32", "ptr32", "string", "expr", "dialog"]
COLORS = {
    "u8": "#BBDEFB", "s8": "#BBDEFB",
    "u16": "#C8E6C9", "s16": "#C8E6C9",
    "u32": "#FFF9C4", "s32": "#FFF9C4",
    "f32": "#FFECB3", "ptr32": "#FFCC80",
    "string": "#F8BBD0", "expr": "#E1BEE7", "dialog": "#B2EBF2",
    "bytes": "#E0E0E0", "fill": "#ECECEC",
}
ROLE_OUTLINE = {"selector": "#8E24AA", "sel16": "#6A1B9A", "peek": "#AD1457"}


def load_settings():
    try:
        with open(SETTINGS, "r", encoding="utf-8") as f: return json.load(f)
    except Exception:
        return {}

def save_settings(s):
    try:
        with open(SETTINGS, "w", encoding="utf-8") as f: json.dump(s, f, indent=1)
    except Exception:
        pass


class InstrEditor:
    def __init__(self, root):
        self.root = root
        root.title("CS1 — Editeur d'instructions (cs1_instructions.json)")
        root.geometry("1500x920")
        self.settings = load_settings()
        self.json_path = self.settings.get("json_path", DEFAULT_JSON)
        self.folder = self.settings.get("script_folder", "")
        self.model = None
        self.counts = {}
        self.cur_name = None
        self.sample_idx = 0
        self.sel_field = None
        self._drag_a = None
        self._drag_b = None
        self._field_paths = {}
        self._build_ui()
        if os.path.exists(self.json_path):
            self._load_json(self.json_path)
        if self.folder and os.path.isdir(self.folder):
            self.root.after(200, self._scan)

    # ---------- UI ----------
    def _build_ui(self):
        top = ttk.Frame(self.root, padding=5); top.pack(fill=tk.X)
        ttk.Label(top, text="JSON:").pack(side=tk.LEFT)
        self.json_var = tk.StringVar(value=self.json_path)
        ttk.Entry(top, textvariable=self.json_var, width=45).pack(side=tk.LEFT, padx=3)
        ttk.Button(top, text="Charger", command=lambda: self._load_json(self.json_var.get())).pack(side=tk.LEFT)
        ttk.Separator(top, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Label(top, text="Corpus:").pack(side=tk.LEFT)
        self.folder_var = tk.StringVar(value=self.folder)
        ttk.Entry(top, textvariable=self.folder_var, width=42).pack(side=tk.LEFT, padx=3)
        ttk.Button(top, text="...", command=self._browse).pack(side=tk.LEFT)
        ttk.Button(top, text="Scanner", command=self._scan).pack(side=tk.LEFT, padx=3)
        ttk.Button(top, text="📊 Tables", command=self._open_tables_view).pack(side=tk.LEFT, padx=3)
        ttk.Button(top, text="🧬 Schémas tables", command=self._open_schema_editor).pack(side=tk.LEFT, padx=3)
        ttk.Separator(top, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(top, text="⬇ Sauver le JSON", command=self._save).pack(side=tk.LEFT)

        pw = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL); pw.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)

        # gauche : liste instructions
        left = ttk.Frame(pw); pw.add(left, weight=1)
        fb = ttk.Frame(left); fb.pack(fill=tk.X)
        ttk.Label(fb, text="Filtre:").pack(side=tk.LEFT)
        self.filter_var = tk.StringVar(); self.filter_var.trace_add("write", lambda *a: self._refresh_list())
        ttk.Entry(fb, textvariable=self.filter_var, width=16).pack(side=tk.LEFT, padx=3)
        self.only_sug = tk.BooleanVar(value=False)
        ttk.Checkbutton(fb, text="suggestions", variable=self.only_sug, command=self._refresh_list).pack(side=tk.LEFT)
        cols = ("name", "op", "n", "val", "sug")
        self.tree = ttk.Treeview(left, columns=cols, show="headings", height=30)
        for c, t, w in (("name", "Instruction", 150), ("op", "Op", 40), ("n", "Ech.", 55), ("val", "✓", 26), ("sug", "!", 26)):
            self.tree.heading(c, text=t); self.tree.column(c, width=w)
        self.tree.pack(fill=tk.BOTH, expand=True, pady=2)
        sb = ttk.Scrollbar(left, orient=tk.VERTICAL, command=self.tree.yview); self.tree.configure(yscrollcommand=sb.set)
        sb.pack(side=tk.RIGHT, fill=tk.Y)
        self.tree.bind("<<TreeviewSelect>>", self._on_pick_instr)

        # droite
        right = ttk.Frame(pw); pw.add(right, weight=3)
        self.hdr = tk.Text(right, height=4, wrap=tk.WORD, state=tk.DISABLED); self.hdr.pack(fill=tk.X, pady=2)

        nav = ttk.Frame(right); nav.pack(fill=tk.X)
        ttk.Button(nav, text="◀ ech.", command=lambda: self._nav_sample(-1)).pack(side=tk.LEFT)
        ttk.Button(nav, text="ech. ▶", command=lambda: self._nav_sample(1)).pack(side=tk.LEFT, padx=3)
        self.sample_lbl = tk.StringVar(value="")
        ttk.Label(nav, textvariable=self.sample_lbl).pack(side=tk.LEFT, padx=8)
        ttk.Button(nav, text="🔍 Voir la fonction (hex)", command=self._open_function_view).pack(side=tk.RIGHT, padx=4)

        self.hex_canvas = tk.Canvas(right, height=120, bg="white", highlightthickness=0)
        self.hex_canvas.pack(fill=tk.X, pady=4)
        self.hex_canvas.bind("<ButtonPress-1>", self._on_hex_press)
        self.hex_canvas.bind("<B1-Motion>", self._on_hex_drag)
        self.hex_canvas.bind("<ButtonRelease-1>", self._on_hex_release)
        ttk.Label(right, text="Astuce: glisse le clic gauche pour sélectionner des octets, puis « Créer opérande ».",
                  foreground="gray").pack(anchor=tk.W)

        # affichage lisible d'une expression selectionnee
        self.expr_var = tk.StringVar(value="")
        ttk.Label(right, textvariable=self.expr_var, foreground="#6A1B9A",
                  wraplength=1050, justify=tk.LEFT).pack(fill=tk.X, pady=1)

        # table des champs
        cols2 = ("idx", "type", "role", "w", "stats", "sug")
        self.ftree = ttk.Treeview(right, columns=cols2, show="headings", height=14)
        for c, t, w in (("idx", "#", 30), ("type", "Type", 70), ("role", "Role", 70),
                        ("w", "Larg.", 45), ("stats", "Stats (sur les echantillons)", 480), ("sug", "Suggestion", 90)):
            self.ftree.heading(c, text=t); self.ftree.column(c, width=w)
        self.ftree.pack(fill=tk.BOTH, expand=True, pady=3)
        self.ftree.bind("<<TreeviewSelect>>", self._on_pick_field)

        # barre d'edition
        edit = ttk.LabelFrame(right, text="Editer le champ selectionne", padding=5); edit.pack(fill=tk.X, pady=3)
        ttk.Label(edit, text="Retyper en:").pack(side=tk.LEFT)
        self.type_var = tk.StringVar(value="f32")
        ttk.Combobox(edit, textvariable=self.type_var, values=TYPE_CHOICES, width=8, state="readonly").pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Retyper", command=self._do_retype).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Label(edit, text="Decouper apres N octets:").pack(side=tk.LEFT)
        self.split_var = tk.StringVar(value="4")
        ttk.Entry(edit, textvariable=self.split_var, width=5).pack(side=tk.LEFT, padx=3)
        ttk.Button(edit, text="Decouper", command=self._do_split).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(edit, text="Fusionner avec le suivant", command=self._do_merge).pack(side=tk.LEFT, padx=3)
        ttk.Separator(edit, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=8)
        ttk.Button(edit, text="✂ Créer opérande (sélection)", command=self._do_split_selection).pack(side=tk.LEFT, padx=3)

        # barre semantique (name + sem + parametre + span)
        sem = ttk.LabelFrame(right, text="Sémantique de l'opérande (pour l'éditeur : sélecteur adapté)", padding=5)
        sem.pack(fill=tk.X, pady=3)
        ttk.Label(sem, text="Nom:").pack(side=tk.LEFT)
        self.opname_var = tk.StringVar()
        ttk.Entry(sem, textvariable=self.opname_var, width=16).pack(side=tk.LEFT, padx=3)
        ttk.Label(sem, text="Sem:").pack(side=tk.LEFT)
        self.sem_var = tk.StringVar(value="")
        ttk.Combobox(sem, textvariable=self.sem_var, values=list(M.Model.SEM_TYPES), width=11, state="readonly").pack(side=tk.LEFT, padx=3)
        ttk.Label(sem, text="Param (ext / tbl:type):").pack(side=tk.LEFT)
        self.semarg_var = tk.StringVar()
        ttk.Entry(sem, textvariable=self.semarg_var, width=14).pack(side=tk.LEFT, padx=3)
        ttk.Label(sem, text="Span:").pack(side=tk.LEFT)
        self.semspan_var = tk.StringVar(value="1")
        ttk.Spinbox(sem, from_=1, to=16, textvariable=self.semspan_var, width=4).pack(side=tk.LEFT, padx=3)
        ttk.Button(sem, text="Appliquer", command=self._do_set_sem).pack(side=tk.LEFT, padx=6)

        val = ttk.Frame(right); val.pack(fill=tk.X, pady=2)
        ttk.Button(val, text="✓✓ Valider cette instruction (définitif)", command=self._do_validate).pack(side=tk.LEFT)
        self.valid_var = tk.StringVar(value="")
        ttk.Label(val, textvariable=self.valid_var, foreground="#2E7D32").pack(side=tk.LEFT, padx=8)

        self.status = tk.StringVar(value="Pret.")
        ttk.Label(self.root, textvariable=self.status, relief=tk.SUNKEN, anchor=tk.W).pack(fill=tk.X, side=tk.BOTTOM)

    # ---------- chargement / scan ----------
    def _load_json(self, path):
        try:
            self.model = M.Model(path); self.json_path = path
            self.json_var.set(path); self.settings["json_path"] = path; save_settings(self.settings)
            self.counts = {}
            self._refresh_list()
            self.status.set("Charge : %d instructions. Scanne un dossier pour les echantillons." % len(self.model.instructions))
        except Exception as ex:
            messagebox.showerror("Erreur", "Chargement JSON: %s" % ex)

    def _browse(self):
        d = filedialog.askdirectory(initialdir=self.folder or HERE)
        if d: self.folder_var.set(d)

    def _scan(self):
        if not self.model: return
        folder = self.folder_var.get().strip()
        if not os.path.isdir(folder):
            messagebox.showwarning("Corpus", "Dossier introuvable."); return
        self.folder = folder; self.settings["script_folder"] = folder; save_settings(self.settings)
        def prog(i, n, name): self.status.set("Scan %d/%d (sous-dossiers inclus) : %s" % (i+1, n, name)); self.root.update_idletasks()
        self.counts = self.model.scan_corpus(folder, max_samples=300, progress=prog, recursive=True)
        nf = len(glob.glob(os.path.join(folder, '**', '*.dat'), recursive=True))
        self.status.set("Scan termine : %d .dat (tous sous-dossiers), %d instructions avec echantillons." % (nf, len(self.counts)))
        self._refresh_list()

    # ---------- liste instructions ----------
    def _has_suggestion(self, name):
        if name not in self.counts: return False
        for st in self.model.field_stats(name):
            if st.get("suggestion"): return True
        return False

    def _refresh_list(self):
        self.tree.delete(*self.tree.get_children())
        if not self.model: return
        flt = self.filter_var.get().lower()
        only = self.only_sug.get()
        for ins in self.model.instructions:
            name = ins["name"]
            if flt and flt not in name.lower(): continue
            n = self.counts.get(name, 0)
            sug = "!" if (n and self._has_suggestion(name)) else ""
            if only and not sug: continue
            val = "✓" if self.model.is_validated(name) else ""
            self.tree.insert("", tk.END, iid=name, values=(name, ins["op"], n, val, sug))

    def _on_pick_instr(self, _e):
        sel = self.tree.selection()
        if not sel: return
        self.cur_name = sel[0]; self.sample_idx = 0; self.sel_field = None
        self._show_instruction()

    # ---------- affichage instruction ----------
    def _cur_instr(self):
        return self.model.instructions[self.model.idx_by_name[self.cur_name]]

    def _show_instruction(self):
        ins = self._cur_instr()
        self.hdr.configure(state=tk.NORMAL); self.hdr.delete(1.0, tk.END)
        sels = " / ".join("%s=%s" % (s.get("kind"), s.get("value")) for s in ins["selectors"]) or "(aucun)"
        self.hdr.insert(1.0, "%s   (op %d, %s)\nSelecteurs: %s\nEchantillons: %d\n%s" % (
            ins["name"], ins["op"], ins.get("opname") or "", sels,
            self.counts.get(ins["name"], 0), ins.get("note", "")))
        self.hdr.configure(state=tk.DISABLED)
        self.valid_var.set("✓ VALIDÉE" if self.model.is_validated(ins["name"]) else "non validée")
        self._draw_hex()
        self._fill_field_table()

    def _samples(self):
        return self.model.samples.get(self.cur_name, [])

    def _nav_sample(self, d):
        s = self._samples()
        if not s: return
        self.sample_idx = (self.sample_idx + d) % len(s)
        self._draw_hex()

    def _draw_hex(self):
        c = self.hex_canvas; c.delete("all")
        s = self._samples()
        if not s:
            c.create_text(10, 20, anchor=tk.W, text="(aucun echantillon — scanne le corpus)", fill="gray"); return
        smp = s[self.sample_idx % len(s)]
        self.sample_lbl.set("ech. %d/%d — %s : %s @0x%X" % (self.sample_idx % len(s) + 1, len(s), smp.file, smp.func, smp.offset))
        data = smp.data
        CW = 26; x0 = 10; y0 = 12; row_h = 30; per = 40
        # opcode (octet 0)
        # champs : liste des (start,end,label,color,outline) en coords data
        segs = []
        segs.append((0, 1, "op%d" % data[0], "#CFD8DC", "#607D8B"))
        for f in smp.fields:
            t = f.node.get("t"); role = f.node.get("role")
            col = COLORS.get(t, "#E0E0E0")
            outl = ROLE_OUTLINE.get(role, "#999")
            lbl = t + (("/" + role) if role else "")
            segs.append((f.off, f.off + f.length, lbl, col, outl))
        n = len(data)
        for i in range(n):
            row = i // per; cx = x0 + (i % per) * CW; cy = y0 + row * row_h
            # fond du segment
            for (a, b_, lbl, col, outl) in segs:
                if a <= i < b_:
                    c.create_rectangle(cx - 1, cy, cx + CW - 3, cy + 20, fill=col, outline=outl)
                    if i == a:
                        c.create_text(cx, cy - 2, anchor=tk.SW, text=lbl, font=("", 6), fill=outl)
                    break
            c.create_text(cx + CW // 2 - 2, cy + 10, text="%02X" % data[i], font=("Consolas", 9))
        # surlignage champ selectionne
        if self.sel_field is not None and self.sel_field < len(smp.fields):
            f = smp.fields[self.sel_field]
            for i in range(f.off, f.off + f.length):
                row = i // per; cx = x0 + (i % per) * CW; cy = y0 + row * row_h
                c.create_rectangle(cx - 1, cy, cx + CW - 3, cy + 20, outline="red", width=2)
        # surlignage de la selection d'octets (glisser)
        if self._drag_a is not None and self._drag_b is not None:
            a = min(self._drag_a, self._drag_b); b = max(self._drag_a, self._drag_b)
            for i in range(a, b + 1):
                if i >= n: break
                row = i // per; cx = x0 + (i % per) * CW; cy = y0 + row * row_h
                c.create_rectangle(cx, cy + 1, cx + CW - 4, cy + 19, outline="#1565C0", width=2)
        c.configure(height=y0 + ((n // per) + 1) * row_h + 10)
        self._seg_map = (segs, x0, CW, y0, row_h, per, n)
        self._update_expr_view()

    def _update_expr_view(self):
        self.expr_var.set("")
        s = self._samples()
        if self.sel_field is None or not s:
            return
        smp = s[self.sample_idx % len(s)]
        if self.sel_field >= len(smp.fields):
            return
        f = smp.fields[self.sel_field]
        if f.node.get("t") == "expr":
            toks = M.decode_expr_tokens(f.raw, self.model)
            self.expr_var.set("Expression:  " + "   ".join(toks))

    def _byte_at(self, e):
        if not hasattr(self, "_seg_map"): return -1
        segs, x0, CW, y0, row_h, per, n = self._seg_map
        col = int((e.x - x0) // CW); row = int((e.y - y0) // row_h)
        if col < 0: col = 0
        if col >= per: col = per - 1
        idx = row * per + col
        return idx if 0 <= idx < n else -1

    def _field_at(self, idx):
        s = self._samples()
        if not s: return None
        smp = s[self.sample_idx % len(s)]
        for fi, f in enumerate(smp.fields):
            if f.off <= idx < f.off + f.length: return fi
        return None

    def _on_hex_press(self, e):
        idx = self._byte_at(e)
        if idx < 0: return
        self._drag_a = idx; self._drag_b = idx
        fi = self._field_at(idx)
        if fi is not None:
            self.sel_field = fi
            self.ftree.selection_set(str(fi)); self.ftree.see(str(fi))
        self._draw_hex()

    def _on_hex_drag(self, e):
        idx = self._byte_at(e)
        if idx < 0 or self._drag_a is None: return
        self._drag_b = idx
        self._draw_hex()

    def _on_hex_release(self, e):
        # si c'est juste un clic (start==end), on garde la selection de champ ; sinon on garde la selection d'octets
        if self._drag_a is not None and self._drag_b is not None and self._drag_a == self._drag_b:
            self._drag_a = self._drag_b = None
            self._draw_hex()

    def _fill_field_table(self):
        self.ftree.delete(*self.ftree.get_children())
        self._field_paths = {}
        if not self.cur_name: return
        ins = self._cur_instr()
        leaves = self.model.flat_read(ins["read"])  # deplie les if/loop
        stats = self.model.field_stats(self.cur_name) if self.counts.get(self.cur_name) else None
        for fi, (path, node) in enumerate(leaves):
            self._field_paths[fi] = path
            t = node.get("t"); role = node.get("role") or ""
            w = M.FIXW.get(t, node.get("size", "")) if t != "string" else "var"
            # indente si le champ est dans un if (chemin plus long que 1)
            depth = (len(path) - 1) // 2
            label = ("  " * depth) + str(t)
            st = stats[fi] if stats and fi < len(stats) else None
            statstr = ""; sug = ""
            if st:
                parts = ["n=%d" % st["n"], "distinct=%d" % st.get("distinct", 0)]
                if "min" in st: parts.append("min=%d max=%d" % (st["min"], st["max"]))
                if "float_frac" in st: parts.append("float=%.0f%%" % (st["float_frac"] * 100))
                if "ascii_frac" in st: parts.append("ascii=%.0f%%" % (st["ascii_frac"] * 100))
                if st.get("float_examples"): parts.append("ex=%s" % st["float_examples"])
                statstr = "  ".join(parts); sug = st.get("suggestion") or ""
            self.ftree.insert("", tk.END, iid=str(fi), values=(fi, label, role, w, statstr, sug))

    def _on_pick_field(self, _e):
        sel = self.ftree.selection()
        if not sel: return
        try: self.sel_field = int(sel[0])
        except Exception: self.sel_field = None
        # charge les annotations semantiques du champ selectionne
        try:
            p = self._path()
            if p is not None and self.cur_name:
                m = self.model.field_meta(self.cur_name, p)
                self.opname_var.set(m['name']); self.sem_var.set(m['sem'])
                self.semarg_var.set(m['sem_arg']); self.semspan_var.set(str(m['sem_span']))
        except Exception:
            pass
        self._draw_hex()

    def _do_set_sem(self):
        if not self._need_field(): return
        try:
            self.model.set_field_meta(self.cur_name, self._path(),
                                      label=self.opname_var.get(), sem=self.sem_var.get(),
                                      sem_arg=self.semarg_var.get(), sem_span=self.semspan_var.get())
            self._after_edit()
            self.status.set("Sémantique posée. Clique « Sauver le JSON » pour écrire.")
        except Exception as ex:
            messagebox.showwarning("Sémantique", str(ex))

    # ---------- editions ----------
    def _need_field(self):
        if self.cur_name is None or self.sel_field is None:
            messagebox.showinfo("Edition", "Selectionne d'abord un champ."); return False
        return True

    def _after_edit(self):
        self.model.redecode_samples(self.cur_name)
        self._fill_field_table(); self._draw_hex()
        self.status.set("Modifie (non sauve). N'oublie pas de sauver le JSON.")

    def _path(self):
        return self._field_paths.get(self.sel_field)

    def _do_retype(self):
        if not self._need_field(): return
        try:
            self.model.retype(self.cur_name, self._path(), self.type_var.get()); self._after_edit()
        except Exception as ex:
            messagebox.showwarning("Retype", str(ex))

    def _do_split(self):
        if not self._need_field(): return
        try:
            self.model.split(self.cur_name, self._path(), int(self.split_var.get())); self._after_edit()
        except Exception as ex:
            messagebox.showwarning("Split", str(ex))

    def _do_merge(self):
        if not self._need_field(): return
        try:
            self.model.merge(self.cur_name, self._path()); self._after_edit()
        except Exception as ex:
            messagebox.showwarning("Merge", str(ex))

    def _do_split_selection(self):
        if self.cur_name is None or self._drag_a is None or self._drag_b is None:
            messagebox.showinfo("Sélection", "Glisse le clic gauche pour sélectionner des octets d'abord."); return
        a = min(self._drag_a, self._drag_b); b = max(self._drag_a, self._drag_b) + 1  # b exclusif
        fi = self._field_at(a); fj = self._field_at(b - 1)
        if fi is None or fi != fj:
            messagebox.showwarning("Sélection", "La sélection doit rester dans un seul champ."); return
        smp = self._samples()[self.sample_idx % len(self._samples())]
        f = smp.fields[fi]
        ra = a - f.off; rb = b - f.off
        try:
            midpath = self.model.split_range(self.cur_name, self._field_paths[fi], ra, rb)
            self._drag_a = self._drag_b = None
            self._after_edit()
            # retrouve l'index plat du morceau du milieu apres redecoupage
            leaves = self.model.flat_read(self._cur_instr()["read"])
            self.sel_field = next((i for i, (p, _) in enumerate(leaves) if p == midpath), fi)
            self.ftree.selection_set(str(self.sel_field)); self.ftree.see(str(self.sel_field))
            self._draw_hex()
            self.status.set("Nouvel opérande créé — type-le maintenant.")
        except Exception as ex:
            messagebox.showwarning("Découpe", str(ex))

    def _open_function_view(self):
        s = self._samples()
        if not s:
            messagebox.showinfo("Fonction", "Aucun échantillon."); return
        smp = s[self.sample_idx % len(s)]
        path = smp.path or (os.path.join(self.folder, smp.file) if self.folder else None)
        if not path or not os.path.exists(path):
            messagebox.showwarning("Fonction", "Fichier introuvable: %s" % (path or "?")); return
        try:
            data = bytearray(open(path, "rb").read())
        except Exception as ex:
            messagebox.showwarning("Fonction", str(ex)); return
        fs, fe = smp.fstart, smp.fend
        rows, fail, pad_start = self.model.decode_function_trace(data, fs, fe, smp.ui)

        win = tk.Toplevel(self.root)
        win.title("Fonction %s  (%s)  0x%X-0x%X" % (smp.func, smp.file, fs, fe))
        win.geometry("1000x640")
        info = "Instructions lues: %d" % len(rows)
        if fail is not None:
            info += "   —   DÉCROCHAGE à 0x%X (opcode 0x%02X non décodable)" % (fail, data[fail])
            infocol = "#B71C1C"
        elif pad_start is not None:
            info += "   —   code lu ✓, padding d'alignement à partir de 0x%X" % pad_start
            infocol = "#1B5E20"
        else:
            info += "   —   fonction lue jusqu'au bout ✓"
            infocol = "#1B5E20"
        ttk.Label(win, text=info, foreground=infocol, font=("", 10, "bold")).pack(anchor=tk.W, padx=6, pady=3)

        frame = ttk.Frame(win); frame.pack(fill=tk.BOTH, expand=True)
        canvas = tk.Canvas(frame, bg="white", highlightthickness=0)
        vs = ttk.Scrollbar(frame, orient=tk.VERTICAL, command=canvas.yview)
        canvas.configure(yscrollcommand=vs.set)
        vs.pack(side=tk.RIGHT, fill=tk.Y); canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        # carte octet -> (index instruction, name) ; -1 = zone non lue (apres decrochage)
        owner = [-1] * (fe - fs)
        names = {}
        for idx, (name, off, length, opc) in enumerate(rows):
            names[idx] = name
            for i in range(off - fs, min(off - fs + length, fe - fs)):
                owner[i] = idx
        cur_idx = None
        for idx, (name, off, length, opc) in enumerate(rows):
            if off <= smp.offset < off + length:
                cur_idx = idx; break

        PER = 24; CW = 26; RH = 26; X0 = 46; Y0 = 8
        shades = ("#E3F2FD", "#FFF9C4")  # alternance pour distinguer les instructions
        n = fe - fs
        for i in range(n):
            row = i // PER; col = i % PER
            cx = X0 + col * CW; cy = Y0 + row * RH
            oidx = owner[i]
            if oidx < 0:
                abs_off = fs + i
                if pad_start is not None and abs_off >= pad_start:
                    fill = "#ECEFF1"  # padding d'alignement en gris
                else:
                    fill = "#FFCDD2"  # zone non lue (data / apres decrochage) en rouge clair
            else:
                fill = shades[oidx % 2]
            outline = "#BBB"
            if cur_idx is not None and oidx == cur_idx:
                outline = "red"
            canvas.create_rectangle(cx, cy, cx + CW - 2, cy + RH - 6, fill=fill,
                                    outline=outline, width=(2 if outline == "red" else 1))
            canvas.create_text(cx + CW // 2 - 1, cy + (RH - 6) // 2, text="%02X" % data[fs + i],
                               font=("Consolas", 9))
            # etiquette du nom au debut de chaque instruction
            if oidx >= 0 and (i == 0 or owner[i - 1] != oidx):
                canvas.create_text(cx + 1, cy - 1, anchor=tk.SW, text=names.get(oidx, ""),
                                   font=("", 6), fill="#333")
            # offset en debut de ligne
            if col == 0:
                canvas.create_text(2, cy + (RH - 6) // 2, anchor=tk.W,
                                   text="%05X" % (fs + i), font=("Consolas", 7), fill="#888")
        # marqueur de decrochage
        if fail is not None:
            i = fail - fs
            row = i // PER; col = i % PER
            cx = X0 + col * CW; cy = Y0 + row * RH
            canvas.create_line(cx, cy - 3, cx, cy + RH - 3, fill="#B71C1C", width=3)
            canvas.create_text(cx + 2, cy - 3, anchor=tk.SW, text="DÉCROCHAGE", font=("", 7, "bold"), fill="#B71C1C")
        total_rows = (n + PER - 1) // PER
        canvas.configure(scrollregion=(0, 0, X0 + PER * CW + 10, Y0 + total_rows * RH + 20))

    def _open_tables_view(self):
        if not self.model:
            messagebox.showinfo("Tables", "Charge d'abord le JSON."); return
        folder = self.folder_var.get().strip() or self.folder
        if not folder or not os.path.isdir(folder):
            messagebox.showwarning("Tables", "Choisis un dossier corpus d'abord."); return

        win = tk.Toplevel(self.root)
        win.title("Tables de données — %s" % folder)
        win.geometry("1080x680")

        # index : fichiers contenant au moins une table (scan recursif)
        files = sorted(glob.glob(os.path.join(folder, '**', '*.dat'), recursive=True))

        pw = ttk.PanedWindow(win, orient=tk.HORIZONTAL); pw.pack(fill=tk.BOTH, expand=True, padx=4, pady=4)
        left = ttk.Frame(pw); pw.add(left, weight=1)
        ttk.Label(left, text="Fichiers (double-clic)").pack(anchor=tk.W)
        flt = tk.StringVar()
        ttk.Entry(left, textvariable=flt, width=24).pack(fill=tk.X)
        flist = tk.Listbox(left, width=30, activestyle="none")
        flist.pack(fill=tk.BOTH, expand=True)
        fsb = ttk.Scrollbar(left, orient=tk.VERTICAL, command=flist.yview); flist.configure(yscrollcommand=fsb.set)
        fsb.pack(side=tk.RIGHT, fill=tk.Y)

        right = ttk.Frame(pw); pw.add(right, weight=3)
        hdr = ttk.Label(right, text="Sélectionne un fichier", font=("", 10, "bold"))
        hdr.pack(anchor=tk.W, padx=4, pady=2)
        cols = ("val", "type", "value", "hex")
        tv = ttk.Treeview(right, columns=cols, show="tree headings")
        tv.heading("#0", text="Table / champ"); tv.column("#0", width=280)
        for c, t, w in (("val", "détail", 150), ("type", "type", 70), ("value", "valeur", 150), ("hex", "octets", 320)):
            tv.heading(c, text=t); tv.column(c, width=w)
        tv.pack(fill=tk.BOTH, expand=True, padx=2)
        tsb = ttk.Scrollbar(right, orient=tk.VERTICAL, command=tv.yview); tv.configure(yscrollcommand=tsb.set)
        tsb.pack(side=tk.RIGHT, fill=tk.Y)
        tv.tag_configure("stale", foreground="#B71C1C")
        tv.tag_configure("ok", foreground="#1B5E20")

        # cache : quels fichiers ont des tables (calcul paresseux a l'affichage de la liste)
        def refill_list(*_):
            needle = flt.get().lower()
            flist.delete(0, tk.END)
            self._tbl_files = []
            for fp in files:
                bn = os.path.basename(fp)
                if needle and needle not in bn.lower():
                    continue
                flist.insert(tk.END, bn); self._tbl_files.append(fp)
        flt.trace_add("write", refill_list)
        refill_list()

        def show_file(evt=None):
            sel = flist.curselection()
            if not sel:
                return
            fp = self._tbl_files[sel[0]]
            tv.delete(*tv.get_children())
            try:
                tables = self.model.tables_in_file(fp)
            except Exception as ex:
                hdr.config(text="Erreur: %s" % ex); return
            ns = sum(1 for t in tables if t.get('stale'))
            hdr.config(text="%s — %d table(s), dont %d périmée(s)" % (os.path.basename(fp), len(tables), ns))
            for t in tables:
                stale = t.get('stale')
                badge = "  ⚠ PÉRIMÉ/MALFORMÉ" if stale else ""
                node = tv.insert("", tk.END,
                                 text="%s @0x%X%s" % (t['name'], t['off'], badge),
                                 values=("%d champs" % len(t['fields']), t['kind'], "stale" if stale else "conforme", ""),
                                 open=not stale, tags=("stale" if stale else "ok",))
                for j, f in enumerate(t['fields']):
                    raw = f.get('raw', b'')
                    hexs = raw.hex() if len(raw) <= 24 else raw[:24].hex() + "…"
                    if f['type'] == 'string':
                        val = '"%s"' % f.get('text', '')
                    elif f['type'] == 'f32':
                        val = "%g" % f.get('value', 0)
                    elif 'value' in f:
                        val = str(f['value'])
                    else:
                        val = ""
                    tv.insert(node, tk.END, text="  [%d]" % j,
                              values=("%d o" % len(raw), f['type'], val, hexs))
        flist.bind("<<ListboxSelect>>", show_file)
        flist.bind("<Double-Button-1>", show_file)

    def _open_schema_editor(self):
        import table_parsers as TP
        win = tk.Toplevel(self.root); win.title("Schémas de tables (retypage des blocs fixes)")
        win.geometry("780x520")
        top = ttk.Frame(win); top.pack(fill=tk.X, padx=4, pady=4)
        ttk.Label(top, text="Schéma:").pack(side=tk.LEFT)
        names = TP.editable_schemas()
        svar = tk.StringVar(value=names[0])
        ttk.Combobox(top, textvariable=svar, values=names, width=22, state="readonly").pack(side=tk.LEFT, padx=4)
        info = ttk.Label(win, text="", foreground="#555"); info.pack(anchor=tk.W, padx=6)

        cols = ("idx", "off", "type", "size")
        tv = ttk.Treeview(win, columns=cols, show="headings", height=14)
        for c, t, w in (("idx", "#", 40), ("off", "offset", 70), ("type", "type", 120), ("size", "taille", 70)):
            tv.heading(c, text=t); tv.column(c, width=w)
        tv.pack(fill=tk.BOTH, expand=True, padx=6, pady=4)

        TYPES = ["u8", "s8", "u16", "s16", "u32", "s32", "f32", "bytes"]

        def refresh():
            nm = svar.get(); tv.delete(*tv.get_children())
            off = 0
            for i, (typ, sz) in enumerate(TP.get_schema(nm)):
                tv.insert("", tk.END, iid=str(i), values=(i, "0x%X" % off, typ, sz)); off += sz
            tot = TP.schema_len(nm)
            ov = nm in TP._SCHEMA_OVERRIDE
            info.config(text="Longueur du record : %d octets%s" % (tot, "   —   (personnalisé)" if ov else "   —   (défaut Ghidra)"))
        svar.trace_add("write", lambda *a: refresh()); refresh()

        def sel_idx():
            s = tv.selection(); return int(s[0]) if s else None

        def do_retype():
            i = sel_idx()
            if i is None: return
            nt = tvar.get()
            try: TP.schema_retype(svar.get(), i, nt); refresh()
            except Exception as ex: messagebox.showwarning("Retyper", str(ex))
        def do_split():
            i = sel_idx()
            if i is None: return
            try: n = int(splen.get())
            except Exception: return
            try: TP.schema_split(svar.get(), i, n); refresh()
            except Exception as ex: messagebox.showwarning("Découper", str(ex))
        def do_merge():
            i = sel_idx()
            if i is None: return
            try: TP.schema_merge(svar.get(), i); refresh()
            except Exception as ex: messagebox.showwarning("Fusionner", str(ex))
        def do_reset():
            TP._SCHEMA_OVERRIDE.pop(svar.get(), None); refresh()
        def do_save():
            try:
                TP.save_schema(self.model.tables_schema_path)
                messagebox.showinfo("Schémas", "Écrit dans:\n%s" % self.model.tables_schema_path)
            except Exception as ex: messagebox.showerror("Schémas", str(ex))

        bar = ttk.Frame(win); bar.pack(fill=tk.X, padx=6, pady=4)
        tvar = tk.StringVar(value="f32")
        ttk.Label(bar, text="→").pack(side=tk.LEFT)
        ttk.Combobox(bar, textvariable=tvar, values=TYPES, width=7, state="readonly").pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="Retyper", command=do_retype).pack(side=tk.LEFT, padx=2)
        ttk.Separator(bar, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=6)
        ttk.Label(bar, text="découpe à:").pack(side=tk.LEFT); splen = tk.StringVar(value="4")
        ttk.Entry(bar, textvariable=splen, width=5).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="Découper", command=do_split).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="Fusionner+suivant", command=do_merge).pack(side=tk.LEFT, padx=2)
        ttk.Separator(bar, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=6)
        ttk.Button(bar, text="Défaut", command=do_reset).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="⬇ Sauver cs1_tables.json", command=do_save).pack(side=tk.RIGHT, padx=2)

    def _do_validate(self):
        if not self.cur_name: return
        self.model.set_validated(self.cur_name, True)
        self._refresh_list(); self._show_instruction()
        self.status.set("%s validée (définitif). Clique « Sauver le JSON » pour écrire sur disque." % self.cur_name)

    def _save(self):
        if not self.model: return
        try:
            self.model.save(self.json_path)
            self.status.set("Sauve dans %s" % self.json_path)
            messagebox.showinfo("Sauvegarde", "Ecrit dans:\n%s" % self.json_path)
        except Exception as ex:
            messagebox.showerror("Sauvegarde", str(ex))


def main():
    root = tk.Tk()
    InstrEditor(root)
    root.mainloop()

if __name__ == "__main__":
    main()
