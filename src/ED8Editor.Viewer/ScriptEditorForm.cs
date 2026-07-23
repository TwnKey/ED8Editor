using System.Globalization;
using System.Text;
using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// Visual editor for CS1 scripts. Every editable widget writes to the persistent native
/// document; saving delegates relocation and encoding to the decompiler engine.
/// </summary>
public sealed class ScriptEditorForm : Form
{
    private static readonly ExpressionTokenChoice[] ExpressionTokens =
    {
        new("Constant (0x00)", 0x00),
        new("Equal == (0x02)", 0x02), new("Not equal != (0x03)", 0x03),
        new("Less than < (0x04)", 0x04), new("Greater than > (0x05)", 0x05),
        new("Less/equal <= (0x06)", 0x06), new("Greater/equal >= (0x07)", 0x07),
        new("Equal zero (0x08)", 0x08), new("Logical AND (0x09)", 0x09),
        new("Bitwise AND (0x0A)", 0x0a), new("Bitwise OR (0x0B)", 0x0b),
        new("Add (0x0C)", 0x0c), new("Subtract (0x0D)", 0x0d),
        new("Negate (0x0E)", 0x0e), new("XOR (0x0F)", 0x0f),
        new("Multiply (0x10)", 0x10), new("Divide (0x11)", 0x11),
        new("Modulo (0x12)", 0x12), new("NOP (0x13)", 0x13),
        new("Multiply variant (0x14)", 0x14), new("Divide variant (0x15)", 0x15),
        new("Modulo variant (0x16)", 0x16), new("Add variant (0x17)", 0x17),
        new("Subtract variant (0x18)", 0x18), new("AND variant (0x19)", 0x19),
        new("XOR variant (0x1A)", 0x1a), new("OR variant (0x1B)", 0x1b),
        new("Bitwise NOT (0x1D)", 0x1d), new("Flag (0x1E)", 0x1e),
        new("Register (0x1F)", 0x1f), new("System value (0x20)", 0x20),
        new("Query (0x21)", 0x21), new("Random (0x22)", 0x22),
        new("Work value (0x23)", 0x23),
    };
    private static readonly Color HeaderTarget = Color.FromArgb(46, 92, 46);
    private static readonly Color Background = Color.FromArgb(30, 30, 34);
    private static readonly Color BlockBackground = Color.FromArgb(45, 46, 52);

    private readonly ListBox scenesList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TreeView tablesTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ScriptFlowPanel blocks = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Background,
    };
    private readonly Label rightHeader = new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        Padding = new Padding(8, 0, 0, 0),
    };
    private readonly ToolStrip editorTools = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripComboBox instructionTypes = new()
    {
        AutoSize = false,
        Width = 230,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
    };
    private readonly ToolStripButton addInstructionButton = new("Add at end");
    private readonly ToolStripButton addAfterButton = new("Insert after");
    private readonly ToolStripButton moveUpButton = new("Move up");
    private readonly ToolStripButton moveDownButton = new("Move down");
    private readonly ToolStripButton deleteInstructionButton = new("Delete");
    private readonly Button navigatorButton = new()
    {
        Dock = DockStyle.Left,
        Width = 24,
        Text = "◀",
        FlatStyle = FlatStyle.Flat,
        TabStop = false,
    };
    private readonly SplitContainer navigationSplit = new()
    {
        Dock = DockStyle.Fill,
        SplitterWidth = 5,
    };
    private readonly StatusStrip status = new();
    private readonly ToolStripStatusLabel statusLabel = new();
    private readonly List<DecompiledFunction> codeFunctions = new();
    private readonly Dictionary<int, Control> blockByIndex = new();
    private readonly HashSet<Keys> forwardedViewportKeys = new();

    private ScriptEditorDocument? document;
    private DecompiledScript? script;
    private int selectedFunctionIndex = -1;
    private int? selectedInstructionIndex;
    private Form? activeInstructionEditor;
    private readonly Func<Cs1TableReference, IReadOnlyList<Cs1TableChoice>>? tableChoices;
    private readonly ScriptEditorSemanticContext? semanticContext;
    private readonly string? instructionDefinitionsPath;
    private readonly Dictionary<string, Dictionary<int, Dictionary<string, string>>> bitmaskDefs = new();

    public ScriptEditorForm(
        Func<Cs1TableReference, IReadOnlyList<Cs1TableChoice>>? tableChoices = null,
        ScriptEditorSemanticContext? semanticContext = null,
        string? instructionDefinitionsPath = null)
    {
        this.tableChoices = tableChoices;
        this.semanticContext = semanticContext;
        this.instructionDefinitionsPath = instructionDefinitionsPath;
        LoadBitmaskDefs();
        KeyPreview = true;
        BuildUi();
        blocks.MoveRequested += (from, to) => MoveInstruction(from, to);
        blocks.JumpEditRequested += (instruction, argument) => OpenJumpEditor(instruction, argument);
        Deactivate += (_, _) => ReleaseViewportKeys();
    }

    public ScriptEditorForm(string datPath) : this() => LoadDat(datPath);

    public event Action<Keys>? ViewportKeyDown;

    public event Action<Keys>? ViewportKeyUp;

    public event Action<DecompiledFunction, DecompiledInstruction>? InstructionSelected;

    private void LoadBitmaskDefs()
    {
        try
        {
            var path = instructionDefinitionsPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "cs1_instructions.json");
            if (!System.IO.File.Exists(path)) return;
            var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("instructions", out var instrs)) return;
            foreach (var instr in instrs.EnumerateArray())
            {
                var name = instr.GetProperty("name").GetString();
                if (name is null) continue;
                var read = instr.GetProperty("read");
                var argIdx = 0;
                foreach (var arg in read.EnumerateArray())
                {
                    if (arg.TryGetProperty("bits", out var bits))
                    {
                        var map = new Dictionary<string, string>();
                        foreach (var bit in bits.EnumerateObject())
                            map[bit.Name] = bit.Value.GetString() ?? bit.Name;
                        if (!bitmaskDefs.ContainsKey(name))
                            bitmaskDefs[name] = new();
                        bitmaskDefs[name][argIdx] = map;
                    }
                    argIdx++;
                }
            }
        }
        catch { }
    }

    private void BuildUi()
    {
        Text = "CS1 Script Editor";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Open .dat…", null, (_, _) => OpenDialog())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Save", null, (_, _) => Save(saveAs: false))
        {
            ShortcutKeys = Keys.Control | Keys.S,
        });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Save As…", null, (_, _) => Save(saveAs: true))
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.S,
        });
        menu.Items.Add(fileMenu);

        editorTools.Items.Add(new ToolStripLabel("Instruction:"));
        editorTools.Items.Add(instructionTypes);
        editorTools.Items.Add(addInstructionButton);
        editorTools.Items.Add(new ToolStripSeparator());
        editorTools.Items.Add(addAfterButton);
        editorTools.Items.Add(moveUpButton);
        editorTools.Items.Add(moveDownButton);
        editorTools.Items.Add(deleteInstructionButton);
        addInstructionButton.Click += (_, _) => InsertInstruction(position: null);
        addAfterButton.Click += (_, _) =>
        {
            if (selectedInstructionIndex is { } index) InsertInstruction(index + 1);
        };
        moveUpButton.Click += (_, _) =>
        {
            if (selectedInstructionIndex is { } index) MoveInstruction(index, index - 1);
        };
        moveDownButton.Click += (_, _) =>
        {
            if (selectedInstructionIndex is { } index) MoveInstruction(index, index + 1);
        };
        deleteInstructionButton.Click += (_, _) =>
        {
            if (GetSelectedInstruction() is { } instruction) RemoveInstruction(instruction);
        };
        navigatorButton.Click += (_, _) => SetNavigatorVisible(navigationSplit.Panel1Collapsed);
        SetInstructionToolsEnabled(false);

        status.Items.Add(statusLabel);
        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
        };
        var scenesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Scenes (functions)" };
        scenesGroup.Controls.Add(scenesList);
        leftSplit.Panel1.Controls.Add(scenesGroup);
        var tablesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Tables (double-click to edit)" };
        tablesGroup.Controls.Add(tablesTree);
        leftSplit.Panel2.Controls.Add(tablesGroup);
        navigationSplit.Panel1.Controls.Add(leftSplit);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(blocks);
        rightPanel.Controls.Add(navigatorButton);
        rightPanel.Controls.Add(rightHeader);
        navigationSplit.Panel2.Controls.Add(rightPanel);

        Controls.Add(navigationSplit);
        Controls.Add(status);
        Controls.Add(editorTools);
        Controls.Add(menu);
        MainMenuStrip = menu;

        Load += (_, _) =>
        {
            try
            {
                navigationSplit.SplitterDistance = Math.Clamp((int)(navigationSplit.Width * 0.3f), 170, 280);
            }
            catch (InvalidOperationException) { }
            try { leftSplit.SplitterDistance = leftSplit.Height / 2; } catch (InvalidOperationException) { }
        };
        scenesList.SelectedIndexChanged += (_, _) => ShowSelectedScene();
        tablesTree.AfterSelect += (_, eventArgs) =>
        {
            if (eventArgs.Node?.Tag is DecompiledFunction { Table: { } table } function)
                statusLabel.Text = $"{function.Name}: {table.Kind} — double-click to edit";
        };
        tablesTree.NodeMouseDoubleClick += (_, eventArgs) => ShowTable(eventArgs.Node);
    }

    private void OpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "CS1 scripts (*.dat)|*.dat|All files|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadDat(dialog.FileName);
    }

    public void LoadDat(string datPath)
    {
        if (document is not null
            && string.Equals(document.SourcePath, Path.GetFullPath(datPath), StringComparison.OrdinalIgnoreCase))
        {
            BringToFront();
            return;
        }
        if (!ConfirmCloseDocument()) return;

        ScriptEditorDocument loadedDocument;
        DecompiledScript loadedScript;
        try
        {
            loadedDocument = ScriptEditorDocument.Open(datPath, instructionDefinitionsPath);
            loadedScript = loadedDocument.Snapshot;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MessageBox.Show(this, exception.Message, "Decompilation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        document?.Dispose();
        document = loadedDocument;
        script = loadedScript;
        Text = $"CS1 Script Editor — {script.SceneName}";
        PopulateInstructionTypes();
        RefreshDocument(selectedFunction: null, selectedInstruction: null);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!ConfirmCloseDocument())
        {
            eventArgs.Cancel = true;
            return;
        }
        base.OnFormClosing(eventArgs);
    }

    public bool ConfirmClose() => ConfirmCloseDocument();

    public bool SaveCurrent(bool saveAs = false) => Save(saveAs);

    internal void VerifyEmbeddedInteractionSmoke()
    {
        var originalCollapse = navigationSplit.Panel1Collapsed;
        navigatorButton.PerformClick();
        if (navigationSplit.Panel1Collapsed == originalCollapse)
            throw new InvalidOperationException("The script navigator toggle did not change its collapsed state.");
        navigatorButton.PerformClick();
        if (navigationSplit.Panel1Collapsed != originalCollapse)
            throw new InvalidOperationException("The script navigator toggle did not restore its collapsed state.");

        if (script is not null && selectedFunctionIndex >= 0)
        {
            var function = script.Functions[selectedFunctionIndex];
            if (function.Instructions.Where(value => value.Opcode == 5)
                .Any(value => blockByIndex.ContainsKey(value.Index)))
                throw new InvalidOperationException("A conditional jump is still represented by a block.");
        }

        var forwarded = false;
        void Observe(Keys key) => forwarded |= key == Keys.Z;
        ViewportKeyDown += Observe;
        try
        {
            var down = Message.Create(Handle, 0x0100, (IntPtr)Keys.Z, IntPtr.Zero);
            if (!ProcessKeyPreview(ref down) || !forwarded)
                throw new InvalidOperationException("The script editor did not forward viewport navigation input.");
            var up = Message.Create(Handle, 0x0101, (IntPtr)Keys.Z, IntPtr.Zero);
            ProcessKeyPreview(ref up);
        }
        finally
        {
            ViewportKeyDown -= Observe;
            ReleaseViewportKeys();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            document?.Dispose();
            document = null;
        }
        base.Dispose(disposing);
    }

    private bool ConfirmCloseDocument()
    {
        if (document is not { IsDirty: true }) return true;
        var result = MessageBox.Show(this, "Save the script changes?", "Modified script",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return result switch
        {
            DialogResult.Yes => Save(saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    private bool Save(bool saveAs)
    {
        if (document is null) return false;
        var target = saveAs ? null : document.SavedPath;
        if (string.IsNullOrEmpty(target))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save edited script",
                Filter = "CS1 scripts (*.dat)|*.dat|All files|*.*",
                InitialDirectory = Path.GetDirectoryName(document.SourcePath),
                FileName = $"{Path.GetFileNameWithoutExtension(document.SourcePath)}.edited.dat",
                DefaultExt = "dat",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            target = dialog.FileName;
        }

        try
        {
            document.Save(target);
            statusLabel.Text = $"Saved: {target}";
            UpdateTitle();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Cannot save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void PopulateInstructionTypes()
    {
        instructionTypes.Items.Clear();
        foreach (var name in ScriptEditorDocument.GetInstructionNames(instructionDefinitionsPath)) instructionTypes.Items.Add(name);
        if (instructionTypes.Items.Count > 0) instructionTypes.SelectedIndex = 0;
    }

    private void RefreshDocument(int? selectedFunction, int? selectedInstruction)
    {
        if (document is null) return;
        script = document.Snapshot;
        PopulateScenes(selectedFunction);
        PopulateTables();
        UpdateTitle();
        statusLabel.Text = $"{script.SceneName}: {codeFunctions.Count} scenes, " +
            $"{script.Functions.Count(function => function.Table is not null)} tables" +
            (document.IsDirty ? " — modified" : string.Empty);
        if (selectedInstruction is { } index)
        {
            var function = script.Functions.FirstOrDefault(value => value.Index == selectedFunctionIndex);
            var instruction = function?.Instructions.ElementAtOrDefault(index);
            if (function is not null && instruction is not null)
            {
                SelectInstruction(function, instruction);
                ScrollToBlock(index);
            }
        }
    }

    private void PopulateScenes(int? selectedFunction)
    {
        var desiredFunction = selectedFunction ?? selectedFunctionIndex;
        codeFunctions.Clear();
        scenesList.BeginUpdate();
        scenesList.Items.Clear();
        foreach (var function in script!.Functions.Where(function => function.IsCode))
        {
            codeFunctions.Add(function);
            scenesList.Items.Add($"{function.Name}  ({function.Instructions.Count})");
        }
        scenesList.EndUpdate();
        var listIndex = codeFunctions.FindIndex(function => function.Index == desiredFunction);
        if (listIndex < 0)
            listIndex = codeFunctions.FindIndex(function => function.Instructions.Any(instruction => instruction.Jumps.Count > 0));
        if (listIndex < 0 && codeFunctions.Count > 0) listIndex = 0;
        scenesList.SelectedIndex = listIndex;
        if (listIndex >= 0) ShowSelectedScene();
    }

    private void PopulateTables()
    {
        tablesTree.BeginUpdate();
        tablesTree.Nodes.Clear();
        foreach (var group in script!.Functions.Where(function => function.Table is not null)
                     .GroupBy(function => function.Table!.Kind).OrderBy(group => group.Key))
        {
            var category = new TreeNode($"{group.Key}  ({group.Count()})");
            foreach (var function in group)
            {
                var stale = function.Table!.IsStale;
                category.Nodes.Add(new TreeNode(function.Name + (stale ? "  ⚠ stale" : string.Empty))
                {
                    Tag = function,
                    ForeColor = stale ? Color.Firebrick : SystemColors.WindowText,
                });
            }
            tablesTree.Nodes.Add(category);
        }
        tablesTree.EndUpdate();
    }

    private void ShowSelectedScene()
    {
        if (scenesList.SelectedIndex < 0 || scenesList.SelectedIndex >= codeFunctions.Count) return;
        var function = codeFunctions[scenesList.SelectedIndex];
        selectedFunctionIndex = function.Index;
        selectedInstructionIndex = null;
        SetInstructionToolsEnabled(true);
        ClearInstructionInspector();
        blocks.SuspendLayout();
        blocks.ClearSelection();
        blocks.Controls.Clear();
        blockByIndex.Clear();
        rightHeader.Text = $"Scene: {function.Name} — {function.Instructions.Count} instructions";
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == 5) continue;
            var block = BuildCompactInstructionBlock(function, instruction);
            blockByIndex[instruction.Index] = block;
            blocks.Controls.Add(block);
        }
        blocks.SetGraph(blockByIndex, function);
        blocks.ResumeLayout();
    }

    private Panel BuildCompactInstructionBlock(
        DecompiledFunction function, DecompiledInstruction instruction)
    {
        var block = new Panel
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = BlockBackground,
            Margin = Padding.Empty,
            MinimumSize = new Size(330, 64),
        };
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 29,
            BackColor = GetInstructionColor(instruction.Name),
            ForeColor = Color.White,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            Padding = new Padding(7, 0, 4, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"#{instruction.Index}   {instruction.Name}",
        };
        var summary = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 8.5f),
            Padding = new Padding(7, 4, 5, 3),
            AutoEllipsis = true,
            Text = BuildInstructionSummary(function, instruction),
        };
        block.Controls.Add(summary);
        block.Controls.Add(header);
        WireCompactBlockSelection(block, function, instruction);
        return block;
    }

    private string BuildInstructionSummary(DecompiledFunction function, DecompiledInstruction instruction)
    {
        var hasDrawableJump = instruction.Jumps.Any(jump => jump.TargetFunctionIndex == function.Index
            && jump.TargetInstructionIndex >= 0);
        var values = instruction.Arguments
            .Where(argument => !hasDrawableJump || instruction.Opcode is not (3 or 5)
                || argument.Type != "ptr32" && !(instruction.Opcode == 5 && argument.Kind == "expr"))
            .Select(argument =>
        {
            var label = string.IsNullOrEmpty(argument.Name) ? argument.Type : argument.Name;
            return $"{label}: {FormatArg(argument)}";
        }).ToList();
        foreach (var jump in instruction.Jumps)
        {
            if (instruction.Opcode is 3 or 5 && jump.TargetFunctionIndex == function.Index
                && jump.TargetInstructionIndex >= 0) continue;
            if (jump.TargetFunctionIndex >= 0 && jump.TargetFunctionIndex < script!.Functions.Count)
                values.Add($"jump -> {script.Functions[jump.TargetFunctionIndex].Name}" +
                    (jump.TargetInstructionIndex >= 0 ? $" / #{jump.TargetInstructionIndex}" : " / end"));
            else
                values.Add($"jump -> unresolved 0x{jump.TargetOffset:X}");
        }
        return values.Count == 0 ? "No arguments" : string.Join("   |   ", values);
    }

    private void WireCompactBlockSelection(
        Control root, DecompiledFunction function, DecompiledInstruction instruction)
    {
        Point? dragOrigin = null;
        root.MouseDown += (_, eventArgs) =>
        {
            blocks.Focus();
            SelectInstruction(function, instruction);
            if (eventArgs.Button == MouseButtons.Left) dragOrigin = eventArgs.Location;
        };
        root.MouseUp += (_, _) => dragOrigin = null;
        root.DoubleClick += (_, _) => OpenInstructionEditor(function, instruction);
        root.MouseMove += (_, eventArgs) =>
        {
            if (dragOrigin is not { } origin || eventArgs.Button != MouseButtons.Left) return;
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(eventArgs.X - origin.X) < Math.Max(2, dragSize.Width / 2)
                && Math.Abs(eventArgs.Y - origin.Y) < Math.Max(2, dragSize.Height / 2)) return;
            dragOrigin = null;
            blocks.BeginInstructionDrag(root, instruction.Index);
        };
        foreach (Control child in root.Controls)
            WireCompactBlockSelection(child, function, instruction);
    }

    private void SelectInstruction(DecompiledFunction function, DecompiledInstruction instruction)
    {
        selectedInstructionIndex = instruction.Index;
        blocks.SelectInstruction(instruction.Index);
        SetSelectedInstructionToolsEnabled(instruction.Index, function.Instructions.Count);
        InstructionSelected?.Invoke(function, instruction);
    }

    private DecompiledInstruction? GetSelectedInstruction()
    {
        if (script is null || selectedFunctionIndex < 0 || selectedInstructionIndex is not { } index)
            return null;
        return script.Functions[selectedFunctionIndex].Instructions.ElementAtOrDefault(index);
    }

    private void ClearInstructionInspector()
    {
        selectedInstructionIndex = null;
        SetSelectedInstructionToolsEnabled(-1, 0);
    }

    private void SetNavigatorVisible(bool visible)
    {
        navigationSplit.Panel1Collapsed = !visible;
        navigatorButton.Text = visible ? "◀" : "▶";
        navigatorButton.AccessibleDescription = visible
            ? "Hide the scene and table navigator"
            : "Show the scene and table navigator";
        navigationSplit.PerformLayout();
    }

    protected override bool ProcessKeyPreview(ref Message message)
    {
        const int KeyDownMessage = 0x0100;
        const int KeyUpMessage = 0x0101;
        const int SystemKeyDownMessage = 0x0104;
        const int SystemKeyUpMessage = 0x0105;
        var key = (Keys)(int)message.WParam & Keys.KeyCode;
        if (message.Msg is KeyDownMessage or SystemKeyDownMessage && ForwardViewportKeyDown(key)) return true;
        if (message.Msg is KeyUpMessage or SystemKeyUpMessage && ForwardViewportKeyUp(key)) return true;
        return base.ProcessKeyPreview(ref message);
    }

    private void OpenJumpEditor(int instructionIndex, int argumentIndex)
    {
        if (script is null || selectedFunctionIndex < 0) return;
        var function = script.Functions[selectedFunctionIndex];
        var instruction = function.Instructions.ElementAtOrDefault(instructionIndex);
        if (instruction is null) return;
        OpenInstructionEditor(function, instruction, branchOnly: true, argumentIndex);
    }

    private void OpenInstructionEditor(
        DecompiledFunction function,
        DecompiledInstruction instruction,
        bool branchOnly = false,
        int branchArgumentIndex = -1)
    {
        activeInstructionEditor?.Close();
        SelectInstruction(function, instruction);
        var editor = new Form
        {
            Text = branchOnly
                ? $"Branch: #{instruction.Index} {instruction.Name}"
                : $"Instruction: #{instruction.Index} {instruction.Name}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(620, 340),
            ClientSize = new Size(760, 480),
            ShowInTaskbar = false,
        };
        var fields = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = BlockBackground,
            Padding = new Padding(8),
        };
        var arguments = instruction.Arguments;
        if (!branchOnly && semanticContext is not null
            && HasCameraCapture(instruction, semanticContext.GetCameraSnapshot()))
        {
            fields.Controls.Add(BuildCameraCaptureEditor(instruction));
        }
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var include = !branchOnly || argument.Index == branchArgumentIndex
                || instruction.Opcode == 5 && argument.Kind == "expr";
            var span = Math.Max(1, argument.SemSpan);
            var group = arguments.Skip(index).Take(span).ToArray();
            if (include)
            {
                var field = group.Length > 1
                    && group.All(value => value.Kind == "scalar" && value.Type != "ptr32")
                        ? BuildScalarEditor(instruction, group)
                        : BuildArgumentEditor(function, instruction, argument);
                fields.Controls.Add(field);
            }
            index += group.Length - 1;
        }

        if (branchOnly && instruction.Opcode == 3
            && instruction.Jumps.FirstOrDefault(value => value.ArgumentIndex == branchArgumentIndex) is { } directJump)
        {
            var addCondition = new Button
            {
                AutoSize = true,
                Text = "Add condition (convert to JMP_IF_FALSE)",
                Margin = new Padding(10),
            };
            addCondition.Click += (_, _) =>
            {
                var converted = RunEdit(() =>
                {
                    document!.ReplaceInstruction(function.Index, instruction.Index, "JMP_IF_FALSE");
                    document.SetJump(function.Index, instruction.Index, 1,
                        directJump.TargetFunctionIndex, directJump.TargetInstructionIndex);
                }, instruction.Index);
                if (converted) BeginInvoke(() => OpenJumpEditor(instruction.Index, 1));
            };
            fields.Controls.Add(addCondition);
        }
        if (fields.Controls.Count == 0)
        {
            fields.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Text = "This instruction has no editable fields.",
                Padding = new Padding(6),
            });
        }
        editor.Controls.Add(fields);
        editor.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(activeInstructionEditor, editor)) activeInstructionEditor = null;
        };
        activeInstructionEditor = editor;
        editor.Show(this);
    }

    private void SetSelectedInstructionToolsEnabled(int index, int instructionCount)
    {
        var selected = index >= 0 && index < instructionCount;
        addAfterButton.Enabled = selected;
        moveUpButton.Enabled = selected && index > 0;
        moveDownButton.Enabled = selected && index + 1 < instructionCount;
        deleteInstructionButton.Enabled = selected;
    }

    private Control BuildScalarEditor(DecompiledInstruction instruction, IReadOnlyList<InstructionArgument> arguments)
    {
        var first = arguments[0];
        var row = CreateArgumentRow(first);
        var fields = new List<TextBox>(arguments.Count);
        foreach (var argument in arguments)
        {
            var field = new TextBox { Width = 105, Text = FormatScalar(argument) };
            fields.Add(field);
            row.Controls.Add(field);
        }
        if (ScriptSemanticValueConverter.IsColor(arguments))
        {
            var choose = new Button { AutoSize = true, Text = "Choose color…" };
            choose.Click += (_, _) =>
            {
                using var dialog = new ColorDialog
                {
                    AnyColor = true,
                    FullOpen = true,
                    Color = ScriptSemanticValueConverter.ReadColor(arguments),
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    ApplySemanticValues(instruction, arguments,
                        ScriptSemanticValueConverter.WriteColor(dialog.Color, arguments));
            };
            row.Controls.Add(choose);
        }
        var apply = new Button { AutoSize = true, Text = "Apply" };
        apply.Click += (_, _) => RunEdit(() =>
        {
            for (var index = 0; index < arguments.Count; index++)
                SetScalar(instruction.Index, arguments[index], fields[index].Text);
        }, instruction.Index);
        row.Controls.Add(apply);
        return row;
    }

    private Control BuildCameraCaptureEditor(DecompiledInstruction instruction)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.FromArgb(41, 54, 68),
            Margin = new Padding(8, 6, 8, 10),
            Padding = new Padding(8),
        };
        var apply = new Button { AutoSize = true, Text = "Apply current viewport camera" };
        apply.Click += (_, _) =>
        {
            var snapshot = semanticContext!.GetCameraSnapshot();
            var bindings = GetCameraBindings(instruction.Arguments, snapshot);
            var byteUpdates = ScriptCameraInstructionCodec.Capture(instruction, snapshot);
            RunEdit(() =>
            {
                foreach (var binding in bindings)
                {
                    for (var index = 0; index < binding.Arguments.Count; index++)
                        SetScalar(instruction.Index, binding.Arguments[index], binding.Values[index]);
                }
                foreach (var update in byteUpdates)
                    document!.SetBytes(selectedFunctionIndex, instruction.Index, update.ArgumentIndex, update.Value);
            }, instruction.Index);
        };
        var initialBindings = GetCameraBindings(instruction.Arguments, semanticContext!.GetCameraSnapshot());
        var initialByteUpdates = ScriptCameraInstructionCodec.Capture(instruction, semanticContext.GetCameraSnapshot());
        panel.Controls.Add(apply);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(6, 7, 0, 0),
            Text = $"Copies: {string.Join(", ", initialBindings.Select(value => value.Component)
                .Concat(initialByteUpdates.Select(value => value.Component)).Distinct())}",
        });
        return panel;
    }

    private static IReadOnlyList<CameraSemanticBinding> GetCameraBindings(
        IReadOnlyList<InstructionArgument> arguments,
        ScriptCameraSnapshot snapshot)
    {
        var bindings = new List<CameraSemanticBinding>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var span = Math.Max(1, arguments[index].SemSpan);
            var group = arguments.Skip(index).Take(span).ToArray();
            if (ScriptSemanticValueConverter.TryWriteCamera(group, snapshot, out var component, out var values))
                bindings.Add(new CameraSemanticBinding(group, values, component));
            index += group.Length - 1;
        }
        return bindings;
    }

    private static bool HasCameraCapture(DecompiledInstruction instruction, ScriptCameraSnapshot snapshot)
        => GetCameraBindings(instruction.Arguments, snapshot).Count > 0
            || ScriptCameraInstructionCodec.Capture(instruction, snapshot).Count > 0;

    private void ApplySemanticValues(
        DecompiledInstruction instruction,
        IReadOnlyList<InstructionArgument> arguments,
        IReadOnlyList<string> values)
    {
        if (arguments.Count != values.Count) throw new ArgumentException("The semantic value does not match its operand span.");
        RunEdit(() =>
        {
            for (var index = 0; index < arguments.Count; index++)
                SetScalar(instruction.Index, arguments[index], values[index]);
        }, instruction.Index);
    }

    private sealed record CameraSemanticBinding(
        IReadOnlyList<InstructionArgument> Arguments,
        IReadOnlyList<string> Values,
        string Component);

    private Control BuildArgumentEditor(
        DecompiledFunction function, DecompiledInstruction instruction, InstructionArgument argument)
    {
        if (bitmaskDefs.TryGetValue(instruction.Name, out var argBits)
            && argBits.TryGetValue(argument.Index, out var bits))
            return BuildBitmaskEditor(instruction, argument, bits);

        // Editeur de bits generique (sem=bitmask sans nommage des bits)
        if (argument.Kind == "scalar" && argument.Sem == "bitmask")
            return BuildGenericBitmaskEditor(instruction, argument);

        if (argument.Kind == "scalar" && argument.Type != "ptr32"
            && argument.Sem == "tbl"
            && Cs1TableReference.TryParse(argument.SemArg, out var reference)
            && reference is not null)
            return BuildTableReferenceEditor(instruction, argument, reference);

        if (argument.Kind == "scalar" && argument.Type != "ptr32")
            return BuildScalarEditor(instruction, new[] { argument });

        var row = CreateArgumentRow(argument);
        if (argument.Kind == "string")
        {
            var field = new TextBox { Width = 320, Text = DecodeText(argument.Raw) };
            var apply = new Button { AutoSize = true, Text = "Apply" };
            apply.Click += (_, _) => RunEdit(
                () => document!.SetString(function.Index, instruction.Index, argument.Index, field.Text), instruction.Index);
            row.Controls.Add(field);
            row.Controls.Add(apply);
            return row;
        }

        if (argument.Type == "ptr32")
        {
            var jump = instruction.Jumps.First(value => value.ArgumentIndex == argument.Index);
            var functionChoices = script!.Functions.Where(value => value.IsCode)
                .Select(value => new FunctionChoice(value.Name, value.Index))
                .ToList();
            var targetFunction = new ComboBox
            {
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DataSource = functionChoices,
                DisplayMember = nameof(FunctionChoice.Label),
            };
            var target = new ComboBox
            {
                Width = 240,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = nameof(JumpChoice.Label),
            };
            void PopulateTargets()
            {
                if (targetFunction.SelectedItem is not FunctionChoice selectedFunction) return;
                var targetDefinition = script.Functions[selectedFunction.FunctionIndex];
                var choices = targetDefinition.Instructions
                    .Select(value => new JumpChoice($"#{value.Index} — {value.Name}", value.Index)).ToList();
                choices.Add(new JumpChoice("End of function", -1));
                if (jump.TargetInstructionIndex == -2 && selectedFunction.FunctionIndex == function.Index)
                    choices.Insert(0, new JumpChoice($"Raw address 0x{jump.TargetOffset:X} (unresolved)", int.MinValue));
                target.DataSource = choices;
                if (selectedFunction.FunctionIndex == jump.TargetFunctionIndex)
                {
                    target.SelectedItem = choices.FirstOrDefault(value =>
                        value.InstructionIndex == jump.TargetInstructionIndex) ?? choices[0];
                }
            }
            targetFunction.SelectedIndexChanged += (_, _) => PopulateTargets();
            targetFunction.SelectedItem = functionChoices.FirstOrDefault(value =>
                value.FunctionIndex == jump.TargetFunctionIndex)
                ?? functionChoices.First(value => value.FunctionIndex == function.Index);
            PopulateTargets();
            var apply = new Button { AutoSize = true, Text = "Apply" };
            apply.Click += (_, _) =>
            {
                if (targetFunction.SelectedItem is not FunctionChoice selectedFunction
                    || target.SelectedItem is not JumpChoice choice
                    || choice.InstructionIndex == int.MinValue) return;
                RunEdit(() => document!.SetJump(function.Index, instruction.Index, argument.Index,
                    selectedFunction.FunctionIndex, choice.InstructionIndex), instruction.Index);
            };
            row.Controls.Add(targetFunction);
            row.Controls.Add(target);
            row.Controls.Add(apply);
            return row;
        }

        if (argument.Kind == "expr") return BuildExpressionEditor(function, instruction, argument);

        row.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Text = FormatArg(argument) + "  (structured editor not connected yet)",
            Padding = new Padding(3, 5, 3, 3),
        });
        return row;
    }

    private Control BuildBitmaskEditor(DecompiledInstruction instruction, InstructionArgument argument, Dictionary<string, string> bits)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4) };
        var label = new Label { Text = argument.Name ?? "Flags", Font = new Font("Segoe UI", 9, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(label);

        var currentValue = argument.Raw is { Length: >= 2 } r ? r[0] | (r[1] << 8) : (ushort)argument.IntValue;
        var checkboxes = new List<CheckBox>();

        foreach (var kv in bits)
        {
            var mask = Convert.ToInt32(kv.Key, 16);
            var cb = new CheckBox
            {
                Text = kv.Value + " (0x" + mask.ToString("X2") + ")",
                AutoSize = true,
                Checked = (currentValue & mask) != 0,
                Tag = mask
            };
            cb.CheckedChanged += (_, _) =>
            {
                ushort val = 0;
                foreach (var c in checkboxes)
                    if (c.Checked && c.Tag is int m) val |= (ushort)m;
                var raw = new[] { (byte)(val & 0xFF), (byte)(val >> 8) };
                RunEdit(() => document!.SetBytes(selectedFunctionIndex, instruction.Index, argument.Index, raw), instruction.Index);
            };
            checkboxes.Add(cb);
            panel.Controls.Add(cb);
        }
        return panel;
    }

    private Control BuildGenericBitmaskEditor(DecompiledInstruction instruction, InstructionArgument argument)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4) };
        var label = new Label { Text = argument.Name ?? "Flags", Font = new Font("Segoe UI", 9, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(label);

        var bitCount = argument.Type switch { "u8" => 8, "u16" => 16, "u32" => 32, _ => 16 };
        var rawLen = bitCount / 8;
        var currentValue = argument.Raw is { Length: >= 2 } r
            ? (int)(r[0] | (r[1] << 8) | (rawLen >= 4 ? r[2] << 16 | r[3] << 24 : 0))
            : argument.IntValue;
        var checkboxes = new List<CheckBox>();

        for (var bit = 0; bit < bitCount; bit++)
        {
            var mask = 1 << bit;
            var cb = new CheckBox
            {
                Text = $"bit {bit}  (0x{mask:X})",
                AutoSize = true,
                Checked = (currentValue & mask) != 0,
                Tag = mask
            };
            cb.CheckedChanged += (_, _) =>
            {
                int val = 0;
                foreach (var c in checkboxes)
                    if (c.Checked && c.Tag is int m) val |= m;
                var raw = new byte[rawLen];
                for (var b = 0; b < rawLen; b++)
                    raw[b] = (byte)(val >> (b * 8));
                RunEdit(() => document!.SetBytes(selectedFunctionIndex, instruction.Index, argument.Index, raw), instruction.Index);
            };
            checkboxes.Add(cb);
            panel.Controls.Add(cb);
        }
        return panel;
    }

    private Control BuildTableReferenceEditor(
        DecompiledInstruction instruction, InstructionArgument argument, Cs1TableReference reference)
    {
        var row = CreateArgumentRow(argument);
        var choices = tableChoices?.Invoke(reference).ToList() ?? new List<Cs1TableChoice>();
        if (choices.All(value => value.Value != argument.IntValue))
        {
            choices.Insert(0, new Cs1TableChoice(argument.IntValue,
                $"{argument.IntValue} — current value (entry unavailable)",
                new Cs1TableEntry(reference.Category, Array.Empty<byte>())));
        }
        var selector = new ComboBox
        {
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DataSource = choices,
            DisplayMember = nameof(Cs1TableChoice.Label),
        };
        selector.SelectedItem = choices.FirstOrDefault(value => value.Value == argument.IntValue);
        var apply = new Button { AutoSize = true, Text = "Apply" };
        apply.Click += (_, _) =>
        {
            if (selector.SelectedItem is not Cs1TableChoice choice) return;
            RunEdit(() => SetScalar(instruction.Index, argument,
                choice.Value.ToString(CultureInfo.InvariantCulture)), instruction.Index);
        };
        row.Controls.Add(selector);
        row.Controls.Add(apply);
        return row;
    }

    private Control BuildExpressionEditor(
        DecompiledFunction function, DecompiledInstruction instruction, InstructionArgument argument)
    {
        var expression = argument.Expression ?? Array.Empty<ExprElement>();
        if (expression.Any(element => element.SubOp == 0x1c))
        {
            var readOnly = CreateArgumentRow(argument);
            readOnly.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Text = FormatExpression(expression) + "  (contains a nested instruction and is read-only)",
                Padding = new Padding(3, 5, 3, 3),
            });
            return readOnly;
        }

        var group = new GroupBox
        {
            Text = string.IsNullOrEmpty(argument.Name) ? "Condition (postfix expression)" : argument.Name,
            Width = 700,
            Height = 260,
            ForeColor = Color.Gainsboro,
            Margin = new Padding(8),
        };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = BlockBackground,
        };
        var tokenColumn = new DataGridViewComboBoxColumn
        {
            Name = "Token",
            HeaderText = "Token",
            DataSource = ExpressionTokens,
            DisplayMember = nameof(ExpressionTokenChoice.Label),
            ValueMember = nameof(ExpressionTokenChoice.SubOp),
        };
        grid.Columns.Add(tokenColumn);
        grid.Columns.Add("Value", "Value");
        foreach (var element in expression.Where(value => value.SubOp != 0x01))
            grid.Rows.Add(element.SubOp, element.Value);

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            WrapContents = false,
            Padding = new Padding(4),
        };
        var moveUp = new Button { AutoSize = true, Text = "Move token up" };
        var moveDown = new Button { AutoSize = true, Text = "Move token down" };
        var apply = new Button { AutoSize = true, Text = "Apply condition" };
        moveUp.Click += (_, _) => MoveExpressionRow(grid, -1);
        moveDown.Click += (_, _) => MoveExpressionRow(grid, 1);
        apply.Click += (_, _) => RunEdit(() =>
        {
            var tokens = new List<ScriptExpressionToken>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                if (row.Cells[0].Value is not int subOp)
                    throw new ArgumentException("Every expression row must have a token.");
                var rawValue = row.Cells[1].Value?.ToString();
                var value = string.IsNullOrWhiteSpace(rawValue) ? 0
                    : int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : throw new ArgumentException($"'{rawValue}' is not a valid expression value.");
                tokens.Add(new ScriptExpressionToken(subOp, value));
            }
            document!.ReplaceExpression(function.Index, instruction.Index, argument.Index, tokens);
        }, instruction.Index);
        tools.Controls.Add(moveUp);
        tools.Controls.Add(moveDown);
        tools.Controls.Add(apply);
        group.Controls.Add(grid);
        group.Controls.Add(tools);
        return group;
    }

    private static void MoveExpressionRow(DataGridView grid, int delta)
    {
        if (grid.CurrentRow is not { IsNewRow: false } row) return;
        var target = row.Index + delta;
        if (target < 0 || target >= grid.Rows.Count || grid.Rows[target].IsNewRow) return;
        var token = row.Cells[0].Value;
        var value = row.Cells[1].Value;
        grid.Rows.RemoveAt(row.Index);
        grid.Rows.Insert(target, token, value);
        grid.CurrentCell = grid.Rows[target].Cells[0];
    }

    private static FlowLayoutPanel CreateArgumentRow(InstructionArgument argument)
    {
        var label = string.IsNullOrEmpty(argument.Name)
            ? argument.Type == "ptr32" ? "Target" : argument.Type
            : argument.Name;
        var semantic = string.IsNullOrEmpty(argument.Sem)
            ? string.Empty
            : $" «{argument.Sem}{(string.IsNullOrEmpty(argument.SemArg) ? string.Empty : $":{argument.SemArg}")}»";
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = BlockBackground,
            Margin = Padding.Empty,
            Padding = new Padding(8, 2, 6, 2),
        };
        row.Controls.Add(new Label
        {
            Width = 205,
            Height = 27,
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"{label}{semantic}",
        });
        return row;
    }

    private void SetScalar(int instruction, InstructionArgument argument, string text)
    {
        if (argument.Type == "f32")
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                throw new ArgumentException($"'{text}' is not a valid floating-point value.");
            document!.SetFloat(selectedFunctionIndex, instruction, argument.Index, value);
            return;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            throw new ArgumentException($"'{text}' is not a valid integer.");
        (long minimum, long maximum) = argument.Type switch
        {
            "u8" => (0L, (long)byte.MaxValue),
            "s8" => ((long)sbyte.MinValue, (long)sbyte.MaxValue),
            "u16" => (0L, (long)ushort.MaxValue),
            "s16" => ((long)short.MinValue, (long)short.MaxValue),
            "u32" => (0L, uint.MaxValue),
            _ => ((long)int.MinValue, (long)int.MaxValue),
        };
        if (integer < minimum || integer > maximum)
            throw new ArgumentOutOfRangeException(nameof(text), $"Value {integer} is outside the {argument.Type} range.");
        document!.SetInteger(selectedFunctionIndex, instruction, argument.Index, unchecked((int)integer));
    }

    private bool RunEdit(Action edit, int? selectedInstruction)
    {
        try
        {
            edit();
            activeInstructionEditor?.Close();
            RefreshDocument(selectedFunctionIndex, selectedInstruction);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Cannot edit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void InsertInstruction(int? position)
    {
        if (document is null || selectedFunctionIndex < 0) return;
        var name = instructionTypes.Text.Trim();
        if (name.Length == 0) return;
        var function = script!.Functions[selectedFunctionIndex];
        var insertion = position ?? function.Instructions.Count;
        RunEdit(() => document.InsertInstruction(selectedFunctionIndex, insertion, name), insertion);
    }

    private void MoveInstruction(int from, int to)
    {
        if (to < 0) return;
        RunEdit(() => document!.MoveInstruction(selectedFunctionIndex, from, to), to);
    }

    private void RemoveInstruction(DecompiledInstruction instruction)
    {
        var result = MessageBox.Show(this, $"Delete #{instruction.Index} {instruction.Name}?",
            "Delete instruction", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        RunEdit(() => document!.RemoveInstruction(selectedFunctionIndex, instruction.Index),
            Math.Max(0, instruction.Index - 1));
    }

    private void ShowTable(TreeNode node)
    {
        if (document is null || node.Tag is not DecompiledFunction { Table: not null } function) return;
        using var editor = new TableEditorForm(document, function.Index);
        editor.TableChanged += (_, _) => RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
        editor.ShowDialog(this);
    }

    private void ShowTableInGraph(TreeNode node)
    {
        if (node.Tag is not DecompiledFunction { Table: { } table } function) return;
        selectedFunctionIndex = -1;
        SetInstructionToolsEnabled(false);
        ClearInstructionInspector();
        blocks.SuspendLayout();
        blocks.Controls.Clear();
        blockByIndex.Clear();
        rightHeader.Text = $"Table: {function.Name} — {table.Kind}" +
            (table.IsStale ? "  (stale/malformed)" : string.Empty);
        if (CreateMonstersTableReader.TryRead(table, out var monsters) && monsters is not null)
        {
            rightHeader.Text += $" — map {monsters.MapAsset}, {monsters.Encounters.Count} encounters";
            var monsterGrid = new DataGridView
            {
                Width = Math.Max(400, blocks.ClientSize.Width - 40),
                Height = Math.Max(300, blocks.ClientSize.Height - 20),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = BlockBackground,
            };
            monsterGrid.Columns.Add("encounter", "Encounter");
            monsterGrid.Columns.Add("slot", "Slot");
            monsterGrid.Columns.Add("asset", "Monster asset");
            monsterGrid.Columns.Add("weight", "Weight");
            monsterGrid.Columns.Add("auxiliary", "Auxiliary asset");
            foreach (var encounter in monsters.Encounters)
            {
                for (var slot = 0; slot < encounter.MonsterAssets.Count; slot++)
                {
                    monsterGrid.Rows.Add(
                        encounter.Id,
                        slot,
                        encounter.MonsterAssets[slot],
                        encounter.Weights[slot],
                        slot == 0 ? encounter.AuxiliaryAsset ?? string.Empty : string.Empty);
                }
            }
            blocks.Controls.Add(monsterGrid);
            blocks.ResumeLayout();
            return;
        }
        var grid = new DataGridView
        {
            Width = Math.Max(400, blocks.ClientSize.Width - 40),
            Height = Math.Max(300, blocks.ClientSize.Height - 20),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = BlockBackground,
        };
        grid.Columns.Add("idx", "#");
        grid.Columns.Add("type", "Type");
        grid.Columns.Add("val", "Value");
        grid.Columns.Add("hex", "Bytes");
        foreach (var field in table.Fields)
        {
            var value = field.Type switch
            {
                "string" => $"\"{field.Text ?? string.Empty}\"",
                "f32" => field.FloatValue.ToString("0.###", CultureInfo.InvariantCulture),
                "bytes" or "fill" => string.Empty,
                _ => field.IntValue.ToString(CultureInfo.InvariantCulture),
            };
            var hex = field.Raw.Length <= 16 ? BitConverter.ToString(field.Raw).Replace('-', ' ') : "…";
            grid.Rows.Add(field.Index, field.Type, value, hex);
        }
        blocks.Controls.Add(grid);
        blocks.ResumeLayout();
    }

    private void ScrollToBlock(int index)
    {
        if (!blockByIndex.TryGetValue(index, out var target)) return;
        blocks.SelectInstruction(index);
        blocks.ScrollControlIntoView(target);
        var old = target.BackColor;
        target.BackColor = HeaderTarget;
        var timer = new System.Windows.Forms.Timer { Interval = 700 };
        timer.Tick += (_, _) =>
        {
            target.BackColor = old;
            timer.Stop();
            timer.Dispose();
        };
        timer.Start();
    }

    private void SetInstructionToolsEnabled(bool enabled)
    {
        instructionTypes.Enabled = enabled;
        addInstructionButton.Enabled = enabled;
        if (!enabled) SetSelectedInstructionToolsEnabled(-1, 0);
    }

    private bool ForwardViewportKeyDown(Keys key)
    {
        if (!IsPotentialViewportKey(key)
            || (ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.None
            || IsTextEntryFocused()) return false;
        if (forwardedViewportKeys.Add(key)) ViewportKeyDown?.Invoke(key);
        return true;
    }

    private bool ForwardViewportKeyUp(Keys key)
    {
        if (!forwardedViewportKeys.Remove(key)) return false;
        ViewportKeyUp?.Invoke(key);
        return true;
    }

    private void ReleaseViewportKeys()
    {
        foreach (var key in forwardedViewportKeys.ToArray()) ViewportKeyUp?.Invoke(key);
        forwardedViewportKeys.Clear();
    }

    private bool IsTextEntryFocused()
    {
        Control? focused = ActiveControl;
        while (focused is ContainerControl container && container.ActiveControl is not null)
            focused = container.ActiveControl;
        return instructionTypes.ComboBox.ContainsFocus
            || focused is TextBoxBase or ComboBox or DataGridView;
    }

    private static bool IsPotentialViewportKey(Keys key) => key is Keys.Z or Keys.W
        or Keys.S or Keys.D or Keys.Q or Keys.A or Keys.E or Keys.C or Keys.Space or Keys.ShiftKey;

    private void UpdateTitle()
    {
        if (script is null) return;
        Text = $"CS1 Script Editor — {script.SceneName}" + (document?.IsDirty == true ? " *" : string.Empty);
    }

    private static string FormatScalar(InstructionArgument argument) => argument.Type switch
    {
        "f32" => argument.FloatValue.ToString("0.###", CultureInfo.InvariantCulture),
        "u32" => unchecked((uint)argument.IntValue).ToString(CultureInfo.InvariantCulture),
        _ => argument.IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatArg(InstructionArgument argument) => argument.Kind switch
    {
        "scalar" => FormatScalar(argument),
        "string" => $"\"{DecodeText(argument.Raw)}\"",
        "expr" => FormatExpression(argument.Expression),
        "dialog" => $"[dialogue {argument.Raw.Length} o]",
        _ => argument.Raw.Length == 0 ? "-" : BitConverter.ToString(argument.Raw).Replace('-', ' '),
    };

    private static string FormatExpression(IReadOnlyList<ExprElement>? expression)
    {
        if (expression is null || expression.Count == 0) return "(empty expression)";
        return string.Join(" ", expression.Where(element => element.SubOp != 0x01)
            .Select(element => !string.IsNullOrEmpty(element.NestedInstruction)
                ? "call " + element.NestedInstruction
                : element.Label));
    }

    private static string DecodeText(byte[] raw)
    {
        var length = raw.Length;
        if (length > 0 && raw[length - 1] == 0) length--;
        return Encoding.UTF8.GetString(raw, 0, length);
    }

    private static Color GetInstructionColor(string instructionName)
    {
        uint hash = 2166136261;
        foreach (var character in instructionName)
        {
            hash ^= character;
            hash *= 16777619;
        }
        var hue = hash % 360;
        return ColorFromHsv(hue, 0.55f, 0.58f);
    }

    private static Color ColorFromHsv(float hue, float saturation, float value)
    {
        var chroma = value * saturation;
        var section = hue / 60f;
        var secondary = chroma * (1f - MathF.Abs(section % 2f - 1f));
        var (red, green, blue) = section switch
        {
            < 1f => (chroma, secondary, 0f),
            < 2f => (secondary, chroma, 0f),
            < 3f => (0f, chroma, secondary),
            < 4f => (0f, secondary, chroma),
            < 5f => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary),
        };
        var match = value - chroma;
        return Color.FromArgb(
            (int)((red + match) * 255f),
            (int)((green + match) * 255f),
            (int)((blue + match) * 255f));
    }

    private sealed record FunctionChoice(string Label, int FunctionIndex);
    private sealed record JumpChoice(string Label, int InstructionIndex);
    private sealed record ExpressionTokenChoice(string Label, int SubOp);
}
