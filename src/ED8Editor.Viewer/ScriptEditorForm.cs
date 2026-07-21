using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Panneau dédié à l'édition des scripts CS1 (.dat), branché sur le décompilateur natif.
///
/// Trois zones :
///  - en haut à gauche : la liste des scènes (fonctions code) ;
///  - en bas à gauche : les tables du fichier regroupées par catégorie ;
///  - à droite : le flot d'instructions de la scène sélectionnée, sous forme de blocs
///    avec leurs arguments typés et leurs branches (sauts) cliquables.
///
/// Lecture seule pour cette première intégration : l'écriture (déjà gérée par le moteur
/// natif) sera branchée sur les widgets d'édition ensuite.
/// </summary>
public sealed class ScriptEditorForm : Form
{
    private static readonly Color HeaderCode = Color.FromArgb(38, 79, 120);
    private static readonly Color HeaderJump = Color.FromArgb(120, 63, 38);
    private static readonly Color HeaderTarget = Color.FromArgb(46, 92, 46);
    private static readonly Color Bg = Color.FromArgb(30, 30, 34);
    private static readonly Color BlockBg = Color.FromArgb(45, 46, 52);

    private readonly ListBox scenesList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TreeView tablesTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly FlowLayoutPanel blocks = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Bg,
        Padding = new Padding(10),
    };
    private readonly Label rightHeader = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        Padding = new Padding(8, 0, 0, 0),
    };
    private readonly StatusStrip status = new();
    private readonly ToolStripStatusLabel statusLabel = new();

    private DecompiledScript? script;
    private readonly List<DecompiledFunction> codeFunctions = new();
    private readonly Dictionary<int, Panel> blockByIndex = new();

    public ScriptEditorForm()
    {
        BuildUi();
    }

    public ScriptEditorForm(string datPath) : this()
    {
        LoadDat(datPath);
    }

    private void BuildUi()
    {
        Text = "Éditeur de scripts CS1";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("Fichier");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Ouvrir un .dat…", null, (_, _) => OpenDialog()) { ShortcutKeys = Keys.Control | Keys.O });
        menu.Items.Add(fileMenu);

        status.Items.Add(statusLabel);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 5 };
        var leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };

        var scenesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Scènes (fonctions)" };
        scenesGroup.Controls.Add(scenesList);
        leftSplit.Panel1.Controls.Add(scenesGroup);

        var tablesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Tables (par catégorie)" };
        tablesGroup.Controls.Add(tablesTree);
        leftSplit.Panel2.Controls.Add(tablesGroup);

        split.Panel1.Controls.Add(leftSplit);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(blocks);
        rightPanel.Controls.Add(rightHeader);
        split.Panel2.Controls.Add(rightPanel);

        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        // les SplitterDistance sont fixés après le premier layout (évite les exceptions de taille)
        Load += (_, _) =>
        {
            try { split.SplitterDistance = 340; } catch { /* ignore */ }
            try { leftSplit.SplitterDistance = leftSplit.Height / 2; } catch { /* ignore */ }
        };

        scenesList.SelectedIndexChanged += (_, _) => ShowScene(scenesList.SelectedIndex);
        tablesTree.AfterSelect += (_, e) => ShowTable(e.Node);
    }

    private void OpenDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Scripts CS1 (*.dat)|*.dat|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadDat(dlg.FileName);
        }
    }

    public void LoadDat(string datPath)
    {
        DecompiledScript loaded;
        try
        {
            loaded = ScriptDecompiler.Decompile(datPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Décompilation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        script = loaded;
        Text = $"Éditeur de scripts CS1 — {loaded.SceneName}";
        PopulateScenes();
        PopulateTables();
        statusLabel.Text = $"{loaded.SceneName} : {codeFunctions.Count} scènes, " +
                           $"{loaded.Functions.Count(f => f.Table is not null)} tables.";
        if (scenesList.Items.Count > 0)
        {
            scenesList.SelectedIndex = 0;
        }
    }

    private void PopulateScenes()
    {
        codeFunctions.Clear();
        scenesList.BeginUpdate();
        scenesList.Items.Clear();
        foreach (var fn in script!.Functions.Where(f => f.IsCode))
        {
            codeFunctions.Add(fn);
            scenesList.Items.Add($"{fn.Name}  ({fn.Instructions.Count})");
        }
        scenesList.EndUpdate();
    }

    private void PopulateTables()
    {
        tablesTree.BeginUpdate();
        tablesTree.Nodes.Clear();
        var byKind = script!.Functions
            .Where(f => f.Table is not null)
            .GroupBy(f => f.Table!.Kind)
            .OrderBy(g => g.Key);
        foreach (var group in byKind)
        {
            var cat = new TreeNode($"{group.Key}  ({group.Count()})") { Tag = null };
            foreach (var fn in group)
            {
                var label = fn.Name + (fn.Table!.IsStale ? "  ⚠ périmé" : string.Empty);
                cat.Nodes.Add(new TreeNode(label) { Tag = fn, ForeColor = fn.Table!.IsStale ? Color.Firebrick : SystemColors.WindowText });
            }
            tablesTree.Nodes.Add(cat);
        }
        tablesTree.EndUpdate();
    }

    // -------------------------------------------------------------------------
    private void ShowScene(int listIndex)
    {
        blocks.SuspendLayout();
        blocks.Controls.Clear();
        blockByIndex.Clear();
        if (listIndex < 0 || listIndex >= codeFunctions.Count)
        {
            blocks.ResumeLayout();
            return;
        }

        var fn = codeFunctions[listIndex];
        rightHeader.Text = $"Scène : {fn.Name}  —  {fn.Instructions.Count} instructions";
        foreach (var instr in fn.Instructions)
        {
            var block = BuildInstructionBlock(instr);
            blockByIndex[instr.Index] = block;
            blocks.Controls.Add(block);
        }
        blocks.ResumeLayout();
    }

    private Panel BuildInstructionBlock(DecompiledInstruction instr)
    {
        var hasJump = instr.Jumps.Count > 0;
        var block = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = BlockBg,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0),
            MinimumSize = new Size(Math.Max(360, blocks.ClientSize.Width - 44), 0),
        };

        block.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = hasJump ? HeaderJump : HeaderCode,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            Padding = new Padding(6, 3, 6, 3),
            Text = $"#{instr.Index}   {instr.Name}",
            Margin = new Padding(0),
        });

        // arguments (avec regroupement sémantique vecN/position/color)
        var args = instr.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var span = arg.SemSpan > 1 ? arg.SemSpan : 1;
            var group = args.Skip(i).Take(span).ToList();
            block.Controls.Add(BuildArgLabel(arg, group));
            i += span - 1;
        }

        // branches (sauts) cliquables
        foreach (var jump in instr.Jumps)
        {
            var target = jump.TargetInstructionIndex;
            var jl = new LinkLabel
            {
                AutoSize = true,
                Text = target >= 0 ? $"    ↳ branche vers #{target}" : $"    ↳ branche → 0x{jump.TargetOffset:X}",
                LinkColor = Color.Gold,
                ActiveLinkColor = Color.Orange,
                BackColor = BlockBg,
                Font = new Font("Consolas", 9f, FontStyle.Italic),
                Margin = new Padding(0, 1, 0, 1),
            };
            if (target >= 0)
            {
                jl.LinkClicked += (_, _) => ScrollToBlock(target);
            }
            block.Controls.Add(jl);
        }

        return block;
    }

    private Label BuildArgLabel(InstructionArgument arg, List<InstructionArgument> group)
    {
        var label = string.IsNullOrEmpty(arg.Name) ? arg.Type : arg.Name;
        var sem = string.IsNullOrEmpty(arg.Sem) ? string.Empty : $" «{arg.Sem}»";
        string value = group.Count > 1
            ? "(" + string.Join(", ", group.Select(FormatScalar)) + ")"
            : FormatArg(arg);

        return new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            BackColor = BlockBg,
            Font = new Font("Consolas", 9f, FontStyle.Regular),
            Padding = new Padding(8, 1, 6, 1),
            Margin = new Padding(0),
            Text = $"    {label}{sem} : {value}",
        };
    }

    private static string FormatScalar(InstructionArgument a) =>
        a.Type == "f32"
            ? a.FloatValue.ToString("0.###", CultureInfo.InvariantCulture)
            : a.IntValue.ToString(CultureInfo.InvariantCulture);

    private static string FormatArg(InstructionArgument arg)
    {
        switch (arg.Kind)
        {
            case "scalar":
                return FormatScalar(arg);
            case "string":
                return "\"" + DecodeText(arg.Raw) + "\"";
            case "expr":
                return FormatExpr(arg.Expression);
            case "dialog":
                return "[dialogue " + arg.Raw.Length + " o]";
            default:
                return arg.Raw.Length == 0 ? "-" : BitConverter.ToString(arg.Raw).Replace('-', ' ');
        }
    }

    private static string FormatExpr(IReadOnlyList<ExprElement>? expr)
    {
        if (expr is null || expr.Count == 0)
        {
            return "(expr vide)";
        }

        var sb = new StringBuilder();
        foreach (var el in expr)
        {
            if (el.SubOp == 0x01)
            {
                continue; // terminateur
            }
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(!string.IsNullOrEmpty(el.NestedInstruction) ? "call " + el.NestedInstruction : el.Label);
        }
        return sb.Length == 0 ? "(expr)" : sb.ToString();
    }

    private static string DecodeText(byte[] raw)
    {
        var len = raw.Length;
        if (len > 0 && raw[len - 1] == 0)
        {
            len--;
        }
        try
        {
            return Encoding.UTF8.GetString(raw, 0, len);
        }
        catch
        {
            return BitConverter.ToString(raw);
        }
    }

    private void ScrollToBlock(int index)
    {
        if (blockByIndex.TryGetValue(index, out var target))
        {
            blocks.ScrollControlIntoView(target);
            var old = target.BackColor;
            target.BackColor = HeaderTarget;
            var t = new Timer { Interval = 700 };
            t.Tick += (_, _) => { target.BackColor = old; t.Stop(); t.Dispose(); };
            t.Start();
        }
    }

    // -------------------------------------------------------------------------
    private void ShowTable(TreeNode node)
    {
        if (node.Tag is not DecompiledFunction { Table: { } table } fn)
        {
            return;
        }

        blocks.SuspendLayout();
        blocks.Controls.Clear();
        blockByIndex.Clear();
        rightHeader.Text = $"Table : {fn.Name}  —  {table.Kind}" + (table.IsStale ? "  (périmé/malformé)" : string.Empty);

        var grid = new DataGridView
        {
            Width = Math.Max(400, blocks.ClientSize.Width - 40),
            Height = Math.Max(300, blocks.ClientSize.Height - 20),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = BlockBg,
        };
        grid.Columns.Add("idx", "#");
        grid.Columns.Add("type", "Type");
        grid.Columns.Add("val", "Valeur");
        grid.Columns.Add("hex", "Octets");
        foreach (var fld in table.Fields)
        {
            var val = fld.Type switch
            {
                "string" => "\"" + (fld.Text ?? string.Empty) + "\"",
                "f32" => fld.FloatValue.ToString("0.###", CultureInfo.InvariantCulture),
                "bytes" or "fill" => string.Empty,
                _ => fld.IntValue.ToString(CultureInfo.InvariantCulture),
            };
            var hex = fld.Raw.Length <= 16 ? BitConverter.ToString(fld.Raw).Replace('-', ' ') : "…";
            grid.Rows.Add(fld.Index, fld.Type, val, hex);
        }
        blocks.Controls.Add(grid);
        blocks.ResumeLayout();
    }
}
