using System.Globalization;
using System.Numerics;
using System.Text;
using ED8Editor.Application;
using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// Visual editor for CS1 scripts. Every editable widget writes to the persistent native
/// document; saving delegates relocation and encoding to the decompiler engine.
/// </summary>
public sealed class ScriptEditorForm : Form, IProjectDocumentEditor
{
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Font DialogFont = new("Consolas", 9.5f);

    // Dialogue text follows the script's locale: UTF-8 under dat_us, cp932 for
    // the Japanese originals. Set when a script is loaded.
    private static Encoding DialogEncoding = CreateScriptEncoding();
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
    private readonly Button newSceneButton = new() { AutoSize = true, Text = "New scene…" };
    private readonly Button deleteSceneButton = new() { AutoSize = true, Text = "Delete scene" };
    private readonly TreeView tablesTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListBox entitiesList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ScriptFlowPanel blocks = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Background,
    };
    /// <summary>
    /// Conditions met along the active path: each line shows the condition read by the
    /// function and the branch taken. Double-click flips to the other branch (the path
    /// and the dimming are recomputed downstream).
    /// </summary>
    private readonly ListBox flagContextList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        BackColor = Color.FromArgb(38, 38, 42),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 8.5f),
    };
    /// <summary>
    /// The conditions the active path runs under, with no heading over them.
    ///
    /// A list of conditions under a label reading "Active path conditions" spends a
    /// line of a panel that is already short on them to say what the list already
    /// shows. What the heading did carry — that double-clicking takes the other
    /// branch — is on the list itself, where it is read at the moment it is useful.
    /// </summary>
    private readonly Panel flagContextGroup = new()
    {
        Dock = DockStyle.Bottom,
        Height = 88,
        Padding = new Padding(0, 4, 0, 0),
    };
    private readonly ToolStrip editorTools = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripButton playFunctionButton = new("▶ Play scene")
    {
        ToolTipText = "Run the whole scene on a loop, so waits, movements and effects play in order",
    };
    private readonly ToolStripButton stopPlaybackButton = new("■ Stop")
    {
        ToolTipText = "Stop the playback and go back to the selected instruction",
    };
    private readonly ToolStripButton skipWaitButton = new("⏭ Next command")
    {
        ToolTipText = "Jump to the next command instead of sitting through the wait before it",
    };
    private readonly ToolStripComboBox playbackSpeed = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 60,
        ToolTipText = "How fast the scene runs; the script's own timing is unchanged",
    };
    private readonly ToolStripLabel playbackPosition = new(string.Empty)
    {
        AutoSize = false,
        Width = 190,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    /// <summary>
    /// Which scene the canvas is showing, at the top where it is being looked at.
    ///
    /// The toolbar used to carry an opcode picker: a list of instruction names that
    /// only mattered at the moment of adding one, sitting permanently above a canvas
    /// whose most-asked question is "which function am I in". Adding an instruction
    /// asks for its kind when it is asked for; this says where you are all the time.
    /// </summary>
    private readonly ToolStripComboBox functionSelector = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260,
        ToolTipText = "The scene being shown",
    };

    private readonly ToolStripButton addInstructionButton = new("Add at end");
    private readonly ToolStripButton addAfterButton = new("Insert after");
    private readonly ToolStripButton moveUpButton = new("Move up");
    private readonly ToolStripButton moveDownButton = new("Move down");
    private readonly ToolStripButton copyInstructionsButton = new("Copy")
    {
        ToolTipText = "Copy the selected instruction blocks (Ctrl+C)",
    };
    private readonly ToolStripButton pasteInstructionsButton = new("Paste after")
    {
        ToolTipText = "Paste copied instructions after the primary selected block (Ctrl+V)",
    };
    private readonly ToolStripButton placeFieldMonsterButton = new("Place field monster…")
    {
        ToolTipText = "Duplicate a known field-monster instruction and place it on the map",
    };
    private readonly ToolStripTextBox blockSearch = new()
    {
        AutoSize = false,
        Width = 180,
        ToolTipText = "Find an instruction by opcode or operand value",
    };
    private readonly ToolStripButton findNextButton = new("Find next");
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
    private readonly HashSet<int> blockByIndex = new();
    private readonly HashSet<Keys> forwardedViewportKeys = new();

    private ScriptEditorDocument? document;
    private DecompiledScript? script;
    private int selectedFunctionIndex = -1;
    private bool syncingFunctionSelector;

    /// <summary>What was added last, offered again first: edits come in runs.</summary>
    private string? lastInsertedInstruction;

    /// <summary>The canvas menu currently or most recently shown.</summary>
    private ContextMenuStrip? flowMenu;
    private int? selectedInstructionIndex;
    private readonly SortedSet<int> selectedInstructionIndices = new();
    private bool suppressInstructionSelected;
    private bool suppressFunctionSelected;
    private Form? activeInstructionEditor;
    private readonly Func<Cs1TableReference, IReadOnlyList<Cs1TableChoice>>? tableChoices;
    private readonly ScriptEditorSemanticContext? semanticContext;
    private readonly string? instructionDefinitionsPath;
    private readonly IReadOnlyList<MonsterTableChoice> monsterChoices;
    private readonly IReadOnlyList<BattleMapAssetEntry> battleMapAssets;
    private readonly Func<IWin32Window, BattleMapAssetEntry?>? createBattleMapAsset;
    private readonly IReadOnlyList<BattleScenarioEntry> battleScenarios;
    private readonly Action<int>? openBattleScript;
    private readonly Dictionary<string, Dictionary<int, Dictionary<string, string>>> bitmaskDefs = new();

    public ScriptEditorForm(
        Func<Cs1TableReference, IReadOnlyList<Cs1TableChoice>>? tableChoices = null,
        ScriptEditorSemanticContext? semanticContext = null,
        string? instructionDefinitionsPath = null,
        IReadOnlyList<MonsterTableChoice>? monsterChoices = null,
        IReadOnlyList<BattleMapAssetEntry>? battleMapAssets = null,
        Func<IWin32Window, BattleMapAssetEntry?>? createBattleMapAsset = null,
        IReadOnlyList<BattleScenarioEntry>? battleScenarios = null,
        Action<int>? openBattleScript = null)
    {
        this.tableChoices = tableChoices;
        this.semanticContext = semanticContext;
        this.instructionDefinitionsPath = instructionDefinitionsPath;
        this.monsterChoices = monsterChoices ?? Array.Empty<MonsterTableChoice>();
        this.battleMapAssets = battleMapAssets ?? Array.Empty<BattleMapAssetEntry>();
        this.createBattleMapAsset = createBattleMapAsset;
        this.battleScenarios = battleScenarios ?? Array.Empty<BattleScenarioEntry>();
        this.openBattleScript = openBattleScript;
        LoadBitmaskDefs();
        KeyPreview = true;
        BuildUi();
        blocks.MoveRequested += (from, to) => MoveInstruction(from, to);
        blocks.InstructionSelectionChanged += SelectDrawnInstructions;
        blocks.InstructionActivated += index => ActivateDrawnInstruction(index);
        blocks.JumpEditRequested += (instruction, argument) => OpenJumpEditor(instruction, argument);
        blocks.ContextRequested += ShowFlowMenu;
        Deactivate += (_, _) => ReleaseViewportKeys();
    }

    public ScriptEditorForm(string datPath) : this() => LoadDat(datPath);

    public event Action<Keys>? ViewportKeyDown;

    public event Action<Keys>? ViewportKeyUp;

    public event Action<DecompiledFunction, DecompiledInstruction>? InstructionSelected;

    public event Action<DecompiledFunction>? FunctionSelected;

    /// <summary>
    /// Raised after loading or structurally editing the active document. Hosts
    /// use the immutable snapshot to refresh map-level script objects.
    /// </summary>
    public event Action<DecompiledScript>? ScriptChanged;

    public event Action<int>? EntityActivated;

    /// <summary>Raised with the target path just before a script is written.</summary>
    /// <summary>
    /// Asks the host to open another script. Opening a .dat is not an editor-only
    /// affair: the viewport's actor, the entity the script calls -2, its
    /// animation library and the replay all belong to the file, so the host
    /// switches the whole session instead of just swapping the text.
    /// </summary>
    public event Action<string>? OpenRequested;

    public event Action<string>? FileSaving;

    /// <summary>Raised with the target path once a script has been written.</summary>
    public event Action<string>? FileSaved;

    public DecompiledScript? CurrentScript => script;
    public string? CurrentPath => document?.SourcePath;

    public void OpenCreateMonstersEditor()
    {
        if (document is null || script is null) return;
        var function = script.Functions.FirstOrDefault(value =>
            value.Table is { Kind: "CreateMonsters", IsStale: false });
        if (function is null)
        {
            MessageBox.Show(
                this,
                "The current script contains no valid CreateMonsters table.",
                "CreateMonsters",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using var editor = new TableEditorForm(
            document,
            function.Index,
            monsterChoices,
            battleMapAssets: battleMapAssets,
            createBattleMapAsset: createBattleMapAsset,
            battleScenarios: battleScenarios,
            openBattleScript: openBattleScript);
        editor.TableChanged += (_, _) =>
            RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
        editor.ShowDialog(this);
    }

    public void StartFieldMonsterPlacement()
    {
        if (script is null || document is null) return;
        BeginFieldMonsterPlacement();
    }

    public void EditEncounter(int tableFunctionIndex, int encounterIndex)
    {
        if (document is null || script is null) return;
        var function = script.Functions.ElementAtOrDefault(tableFunctionIndex);
        if (function?.Table is null
            || !CreateMonstersTableReader.TryRead(function.Table, out var table)
            || table is null
            || encounterIndex < 0
            || encounterIndex >= table.Encounters.Count)
        {
            return;
        }
        using var editor = new TableEditorForm(
            document,
            tableFunctionIndex,
            monsterChoices,
            encounterIndex,
            battleMapAssets,
            createBattleMapAsset,
            battleScenarios,
            openBattleScript);
        editor.TableChanged += (_, _) =>
            RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
        editor.ShowDialog(this);
        RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
    }

    public void CreateEncounter()
    {
        if (document is null || script is null) return;
        var tables = EncounterTables();
        var tableChoice = ChooseItem(
            "New encounter",
            "Create the encounter in this battle table:",
            tables);
        if (tableChoice is null) return;
        var encounterId = tableChoice.Table.Encounters.Count == 0
            ? 0
            : tableChoice.Table.Encounters.Max(value => value.Id) + 1;
        var previousFunction = selectedFunctionIndex;
        try
        {
            CreateMonstersTableEditor.AddEncounter(
                document,
                tableChoice.FunctionIndex,
                tableChoice.Table.Encounters.Count,
                encounterId,
                Array.Empty<string>(),
                Array.Empty<int>());
            RefreshDocument(previousFunction, selectedInstructionIndex);
            EditEncounter(
                tableChoice.FunctionIndex,
                tableChoice.Table.Encounters.Count);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot create encounter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void InstantiateEncounter(int tableFunctionIndex, int encounterIndex)
    {
        if (document is null || script is null) return;
        var choice = EncounterTables().FirstOrDefault(value =>
            value.FunctionIndex == tableFunctionIndex);
        var encounter = choice?.Table.Encounters.ElementAtOrDefault(encounterIndex);
        if (choice is null || encounter is null) return;
        BeginEncounterPlacement(choice, encounter);
    }

    public void GoToInstruction(int functionIndex, int instructionIndex)
    {
        if (script is null) return;
        var function = script.Functions.ElementAtOrDefault(functionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value =>
            value.Index == instructionIndex);
        if (function is null || instruction is null || !function.IsCode) return;
        PopulateScenes(functionIndex);
        SelectInstruction(function, instruction);
        blocks.ScrollInstructionIntoView(instructionIndex);
    }

    public void DeleteFieldMonster(ScriptMonsterSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        if (document is null || script is null)
            throw new InvalidOperationException("No script is open.");
        var function = script.Functions.FirstOrDefault(value => value.Index == spawn.SourceFunctionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value =>
            value.Index == spawn.SourceInstructionIndex);
        if (function is null || instruction is null || instruction.Opcode != 0x13)
            throw new InvalidOperationException("The field-monster instruction no longer exists.");
        RunEdit(
            () => document.RemoveInstruction(spawn.SourceFunctionIndex, spawn.SourceInstructionIndex),
            Math.Max(0, spawn.SourceInstructionIndex - 1));
    }

    public void ShowPlaybackInstruction(int functionIndex, int instructionIndex)
    {
        if (script is null) return;
        var function = script.Functions.FirstOrDefault(value => value.Index == functionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value => value.Index == instructionIndex);
        if (function is null || instruction is null) return;

        suppressInstructionSelected = true;
        suppressFunctionSelected = true;
        try
        {
            if (selectedFunctionIndex != functionIndex)
            {
                PopulateScenes(functionIndex);
            }
            SelectInstruction(function, instruction);
            // Playback drives the view here, and only here: the reader keeps
            // control of the canvas the rest of the time.
            blocks.FollowInstruction(instruction.Index);
        }
        finally
        {
            suppressInstructionSelected = false;
            suppressFunctionSelected = false;
        }
    }

    /// <summary>Where the playback stands, shown next to its controls.</summary>
    public void ShowPlaybackPosition(string text)
    {
        playbackPosition.Text = text;
        playFunctionButton.Enabled = text.Length == 0;
    }

    /// <summary>How fast the host should run the scene, as a multiplier.</summary>
    public float PlaybackSpeed => playbackSpeed.SelectedItem is string label
        && float.TryParse(
            label.TrimEnd('x'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 1f;

    public void SetRuntimeEntities(IReadOnlyList<ScriptEntityChoice> entities)
    {
        entitiesList.BeginUpdate();
        try
        {
            entitiesList.DataSource = entities.OrderBy(value => value.EntityId).ToArray();
            entitiesList.DisplayMember = nameof(ScriptEntityChoice.Label);
        }
        finally
        {
            entitiesList.EndUpdate();
        }
    }

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
        var editMenu = new ToolStripMenuItem("Edit");
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Undo", null, (_, _) => Undo())
        {
            ShortcutKeys = Keys.Control | Keys.Z,
        });
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Redo", null, (_, _) => Redo())
        {
            ShortcutKeys = Keys.Control | Keys.Y,
        });
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(new ToolStripMenuItem(
            "Copy selected blocks", null, (_, _) => CopySelectedInstructions())
        {
            ShortcutKeys = Keys.Control | Keys.C,
        });
        editMenu.DropDownItems.Add(new ToolStripMenuItem(
            "Paste after selected block", null, (_, _) => PasteInstructionsAfterSelection())
        {
            ShortcutKeys = Keys.Control | Keys.V,
        });
        menu.Items.Add(editMenu);

        editorTools.Items.Add(new ToolStripLabel("Scene:"));
        editorTools.Items.Add(functionSelector);
        editorTools.Items.Add(new ToolStripSeparator());
        editorTools.Items.Add(addInstructionButton);
        editorTools.Items.Add(addAfterButton);
        editorTools.Items.Add(moveUpButton);
        editorTools.Items.Add(moveDownButton);
        editorTools.Items.Add(copyInstructionsButton);
        editorTools.Items.Add(pasteInstructionsButton);
        editorTools.Items.Add(placeFieldMonsterButton);
        editorTools.Items.Add(new ToolStripSeparator());
        editorTools.Items.Add(playFunctionButton);
        editorTools.Items.Add(stopPlaybackButton);
        playbackSpeed.Items.AddRange(new object[] { "0.5x", "1x", "2x", "4x", "8x" });
        playbackSpeed.SelectedItem = "1x";
        editorTools.Items.Add(playbackSpeed);
        editorTools.Items.Add(skipWaitButton);
        editorTools.Items.Add(playbackPosition);
        editorTools.Items.Add(new ToolStripSeparator());
        editorTools.Items.Add(new ToolStripLabel("Find:"));
        editorTools.Items.Add(blockSearch);
        editorTools.Items.Add(findNextButton);
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
        copyInstructionsButton.Click += (_, _) => CopySelectedInstructions();
        pasteInstructionsButton.Click += (_, _) => PasteInstructionsAfterSelection();
        placeFieldMonsterButton.Click += (_, _) => BeginFieldMonsterPlacement();
        functionSelector.SelectedIndexChanged += (_, _) =>
        {
            if (syncingFunctionSelector) return;
            if (functionSelector.SelectedIndex < 0
                || functionSelector.SelectedIndex >= scenesList.Items.Count)
            {
                return;
            }
            scenesList.SelectedIndex = functionSelector.SelectedIndex;
        };
        blockSearch.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter) return;
            FindNextBlock();
            eventArgs.SuppressKeyPress = true;
        };
        playFunctionButton.Click += (_, _) =>
        {
            if (GetSelectedFunction() is { } function) PlayFunctionRequested?.Invoke(function);
        };
        stopPlaybackButton.Click += (_, _) => StopPlaybackRequested?.Invoke();
        skipWaitButton.Click += (_, _) => SkipToNextCommandRequested?.Invoke();
        findNextButton.Click += (_, _) => FindNextBlock();
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
        var scenesTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(2),
        };
        newSceneButton.Click += (_, _) => CreateScene();
        deleteSceneButton.Click += (_, _) => DeleteScene();
        scenesTools.Controls.Add(newSceneButton);
        scenesTools.Controls.Add(deleteSceneButton);
        scenesGroup.Controls.Add(scenesList);
        scenesGroup.Controls.Add(scenesTools);
        leftSplit.Panel1.Controls.Add(scenesGroup);
        var lowerNavigator = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
        };
        var tablesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Tables (double-click to edit)" };
        tablesGroup.Controls.Add(tablesTree);
        lowerNavigator.Panel1.Controls.Add(tablesGroup);
        var entitiesGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Entities at selected instruction",
        };
        entitiesGroup.Controls.Add(entitiesList);
        lowerNavigator.Panel2.Controls.Add(entitiesGroup);
        leftSplit.Panel2.Controls.Add(lowerNavigator);
        navigationSplit.Panel1.Controls.Add(leftSplit);

        flagContextGroup.Controls.Add(flagContextList);
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(blocks);
        rightPanel.Controls.Add(flagContextGroup);
        rightPanel.Controls.Add(navigatorButton);
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
            try
            {
                lowerNavigator.SplitterDistance = Math.Max(
                    lowerNavigator.Panel1MinSize, lowerNavigator.Height / 2);
            }
            catch (InvalidOperationException) { }
        };
        blocks.ActivePathChanged += RefreshFlagContext;
        flagContextList.DoubleClick += (_, _) =>
        {
            if (flagContextList.SelectedItem is FlagContextEntry entry) blocks.ToggleBranch(entry.ForkInstruction);
        };
        scenesList.SelectedIndexChanged += (_, _) => ShowSelectedScene();
        tablesTree.AfterSelect += (_, eventArgs) =>
        {
            if (eventArgs.Node?.Tag is DecompiledFunction { Table: { } table } function)
                statusLabel.Text = $"{function.Name}: {table.Kind} — double-click to edit";
        };
        tablesTree.NodeMouseDoubleClick += (_, eventArgs) => ShowTable(eventArgs.Node);
        entitiesList.DoubleClick += (_, _) =>
        {
            if (entitiesList.SelectedItem is ScriptEntityChoice entity)
                EntityActivated?.Invoke(entity.EntityId);
        };
    }

    /// <summary>Asks the host to run the whole scene, on a loop.</summary>
    public event Action<DecompiledFunction>? PlayFunctionRequested;

    /// <summary>Asks the host to stop whatever it is playing.</summary>
    public event Action? StopPlaybackRequested;

    /// <summary>
    /// Asks the host to skip the wait it is sitting in. A scene is mostly
    /// authored pauses that the game fills with dialogue the editor does not
    /// display, so a reader needs to step from command to command.
    /// </summary>
    public event Action? SkipToNextCommandRequested;

    /// <summary>The block the reader has selected, if any.</summary>
    public int? SelectedInstruction => selectedInstructionIndex;

    /// <summary>The scene the reader is looking at, if any.</summary>
    private DecompiledFunction? GetSelectedFunction()
        => scenesList.SelectedIndex >= 0 && scenesList.SelectedIndex < codeFunctions.Count
            ? codeFunctions[scenesList.SelectedIndex]
            : null;

    private void OpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "CS1 scripts (*.dat)|*.dat|All files|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // With no host listening (the editor opened on its own), load it here.
        if (OpenRequested is { } open) open(dialog.FileName);
        else LoadDat(dialog.FileName);
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

        DialogEncoding = ScriptDialogText.ResolveEncoding(datPath);
        voiceTable = null;
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

    /// <summary>Whether the open script has edits that are not on disk.</summary>
    public bool HasUnsavedChanges => document is { IsDirty: true };

    /// <summary>Where the open script would be written, saved before or not.</summary>
    public string? CurrentSavePath => document is null ? null : document.SavedPath ?? document.SourcePath;

    public string? CurrentFileName =>
        CurrentSavePath is { } path ? Path.GetFileName(path) : null;

    string? IProjectDocumentEditor.DocumentPath => CurrentSavePath;

    bool IProjectDocumentEditor.SaveWithoutAsking() => Save(saveAs: false);

    /// <summary>
    /// The game folder of the project this editor belongs to, when it has one. A
    /// script opened from inside it is saved back over itself: the project holds the
    /// pristine copy, so the edit can be undone, and a name the game does not read
    /// would make the edit pointless.
    /// </summary>
    public string? ProjectGameDirectory { get; set; }

    private bool InProjectGameDirectory(string path)
    {
        if (string.IsNullOrEmpty(ProjectGameDirectory)) return false;
        var root = Path.GetFullPath(ProjectGameDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<FishingSpotScriptBinding> FindFishingSpotBindings(string functionName) =>
        script is null
            ? Array.Empty<FishingSpotScriptBinding>()
            : FishingSpotScriptBinding.Read(script, functionName);

    public IReadOnlyList<ShopScriptBinding> FindShopBindings(string functionName) =>
        script is null
            ? Array.Empty<ShopScriptBinding>()
            : ShopScriptBinding.Read(script, functionName);

    public void CreateShopInteraction(
        string functionName,
        int shopId,
        string? entitySetupFunction = null,
        int? entityId = null)
    {
        if (document is null || script is null)
            throw new InvalidOperationException("No script is loaded.");
        ValidateNewFunctionName(functionName);
        if (shopId is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shopId));
        if ((entitySetupFunction is null) != (entityId is null))
        {
            throw new ArgumentException(
                "An optional NPC binding requires both a setup function and an entity ID.");
        }
        var setup = entitySetupFunction is null
            ? null
            : script.Functions.FirstOrDefault(value =>
                value.IsCode
                && value.Name.Equals(entitySetupFunction, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Setup function '{entitySetupFunction}' does not exist.");

        var createdFunction = -1;
        int? insertedBinding = null;
        try
        {
            createdFunction = document.AddCodeFunction(functionName);
            document.InsertInstruction(createdFunction, 0, "Shop_Open");
            var created = document.Snapshot.Functions.First(value =>
                value.Index == createdFunction).Instructions[0];
            var shopArgument = created.Arguments.FirstOrDefault(value =>
                value.Kind == "scalar")
                ?? throw new InvalidDataException("Shop_Open has no scalar shop ID.");
            document.SetInteger(
                createdFunction, created.Index, shopArgument.Index, shopId);

            if (setup is not null && entityId is not null)
            {
                var position = setup.Instructions.Count > 0
                    && setup.Instructions[^1].Name.Equals(
                        "Return", StringComparison.Ordinal)
                        ? setup.Instructions.Count - 1
                        : setup.Instructions.Count;
                document.InsertInstruction(
                    setup.Index, position, "LookPoint_BindEntity");
                insertedBinding = position;
                var binding = document.Snapshot.Functions.First(value =>
                    value.Index == setup.Index).Instructions[position];
                var nameArgument = binding.Arguments.FirstOrDefault(value =>
                    value.Kind == "string")
                    ?? throw new InvalidDataException(
                        "LookPoint_BindEntity has no LookPoint-name operand.");
                var entityArgument = binding.Arguments.FirstOrDefault(value =>
                    value.Kind == "scalar")
                    ?? throw new InvalidDataException(
                        "LookPoint_BindEntity has no entity-ID operand.");
                document.SetString(
                    setup.Index, binding.Index, nameArgument.Index, functionName);
                document.SetInteger(
                    setup.Index, binding.Index, entityArgument.Index, entityId.Value);
            }
        }
        catch
        {
            if (insertedBinding is not null && setup is not null)
            {
                try { document.RemoveInstruction(setup.Index, insertedBinding.Value); }
                catch { /* Preserve the original creation error. */ }
            }
            if (createdFunction >= 0)
            {
                try { document.RemoveFunction(createdFunction); }
                catch { /* Preserve the original creation error. */ }
            }
            throw;
        }

        RefreshDocument(createdFunction, 0);
    }

    public void CreateFishingInteraction(
        string functionName,
        int fishingPointId,
        Vector3 playerPosition,
        float headingDegrees,
        Vector3 waterTarget)
    {
        if (document is null || script is null)
            throw new InvalidOperationException("No script is loaded.");
        ValidateNewFunctionName(functionName);
        var payload = new FishingSpotScriptBinding(
            -1, functionName, -1, -1, fishingPointId,
            playerPosition, headingDegrees, waterTarget).EncodePayload();

        var createdFunction = -1;
        try
        {
            createdFunction = document.AddCodeFunction(functionName);
            document.InsertInstruction(createdFunction, 0, "OP73_1");
            var created = document.Snapshot.Functions.First(value =>
                value.Index == createdFunction).Instructions[0];
            var bytesArgument = created.Arguments.FirstOrDefault(value =>
                value.Kind == "bytes"
                && value.Type == "bytes"
                && value.Raw.Length == FishingSpotScriptBinding.PayloadSize)
                ?? throw new InvalidDataException(
                    "OP73_1 has no established 32-byte fishing payload.");
            document.SetBytes(
                createdFunction, created.Index, bytesArgument.Index, payload);
        }
        catch
        {
            if (createdFunction >= 0)
            {
                try { document.RemoveFunction(createdFunction); }
                catch { /* Preserve the original creation error. */ }
            }
            throw;
        }

        RefreshDocument(createdFunction, 0);
    }

    private void ValidateNewFunctionName(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("A script function name is required.", nameof(functionName));
        if (script!.Functions.Any(value =>
                value.Name.Equals(functionName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Function '{functionName}' already exists.");
        }
    }

    public void UpdateShopBinding(ShopScriptBinding binding, int shopId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (document is null || script is null)
            throw new InvalidOperationException("No script is loaded.");
        var function = script.Functions.FirstOrDefault(value =>
            value.Index == binding.FunctionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value =>
            value.Index == binding.InstructionIndex);
        if (instruction is null || instruction.Opcode != ShopScriptBinding.ShopOpcode)
            throw new InvalidOperationException("The linked OP114 no longer exists.");
        var argument = instruction.Arguments.FirstOrDefault(value =>
            value.Kind == "scalar")
            ?? throw new InvalidDataException("The linked OP114 has no scalar shop ID.");
        document.SetInteger(
            binding.FunctionIndex,
            binding.InstructionIndex,
            argument.Index,
            shopId);
        RefreshDocument(binding.FunctionIndex, binding.InstructionIndex);
    }

    public void UpdateFishingSpotBinding(
        FishingSpotScriptBinding original,
        FishingSpotScriptBinding updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        if (document is null || script is null)
            throw new InvalidOperationException("No script is loaded.");
        if (original.FunctionIndex != updated.FunctionIndex
            || original.InstructionIndex != updated.InstructionIndex
            || original.PayloadArgumentIndex != updated.PayloadArgumentIndex)
        {
            throw new ArgumentException(
                "A fishing payload update cannot change its source instruction.",
                nameof(updated));
        }
        document.SetBytes(
            original.FunctionIndex,
            original.InstructionIndex,
            original.PayloadArgumentIndex,
            updated.EncodePayload());
        RefreshDocument(original.FunctionIndex, original.InstructionIndex);
    }

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
            ScriptSceneStateResolver.VerifyReplaySmoke(script);
            var function = script.Functions[selectedFunctionIndex];
            if (function.Instructions.Where(value => value.Opcode == 5)
                .Any(value => blockByIndex.Contains(value.Index)))
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
        if (string.IsNullOrEmpty(target) && !saveAs && InProjectGameDirectory(document.SourcePath))
        {
            target = document.SourcePath;
        }
        if (string.IsNullOrEmpty(target))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save the script",
                Filter = "CS1 scripts (*.dat)|*.dat|All files|*.*",
                InitialDirectory = Path.GetDirectoryName(document.SourcePath),
                // Its own name: the game loads t1000.dat, not t1000.edited.dat.
                FileName = Path.GetFileName(document.SourcePath),
                DefaultExt = "dat",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            target = dialog.FileName;
        }

        try
        {
            FileSaving?.Invoke(target!);
            document.Save(target);
            FileSaved?.Invoke(target!);
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
        ScriptChanged?.Invoke(script);
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
        functionSelector.ComboBox!.BeginUpdate();
        functionSelector.Items.Clear();
        foreach (var function in codeFunctions)
        {
            functionSelector.Items.Add($"{function.Name}  ({function.Instructions.Count})");
        }
        functionSelector.ComboBox.EndUpdate();
        var listIndex = codeFunctions.FindIndex(function => function.Index == desiredFunction);
        if (listIndex < 0)
            listIndex = codeFunctions.FindIndex(function => function.Instructions.Any(instruction => instruction.Jumps.Count > 0));
        if (listIndex < 0 && codeFunctions.Count > 0) listIndex = 0;
        scenesList.SelectedIndex = listIndex;
        if (listIndex >= 0 && listIndex < functionSelector.Items.Count)
        {
            syncingFunctionSelector = true;
            functionSelector.SelectedIndex = listIndex;
            syncingFunctionSelector = false;
        }
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
        // Showing another scene starts reading it from its entry; refreshing the
        // one already open keeps the reader where they were.
        var isNewScene = selectedFunctionIndex != function.Index;
        selectedFunctionIndex = function.Index;
        selectedInstructionIndex = null;
        if (!suppressFunctionSelected)
            FunctionSelected?.Invoke(function);
        SetRuntimeEntities(Array.Empty<ScriptEntityChoice>());
        SetInstructionToolsEnabled(true);
        ClearInstructionInspector();
        // The canvas draws its blocks, so showing a scene only fills a list and
        // repaints: no control is created, whatever the size of the scene.
        blocks.SuspendLayout();
        try
        {
            blocks.ClearSelection();
            blocks.Controls.Clear();
            blockByIndex.Clear();
            var newBlocks = new List<ScriptFlowBlock>(function.Instructions.Count);
            foreach (var instruction in function.Instructions)
            {
                // OP5 = branch point (pivot node drawn by the canvas).
                // OP3 = unconditional jump: the arrow stands for it, so no block.
                if (instruction.Opcode == 5) continue;
                if (instruction.Opcode == 3 && instruction.Jumps.Any(value =>
                    value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0)) continue;
                if (IsTrailingPadding(function, instruction)) continue;
                blockByIndex.Add(instruction.Index);
                newBlocks.Add(new ScriptFlowBlock(
                    instruction.Index,
                    $"#{instruction.Index}   {instruction.Name}",
                    BuildInstructionSummary(function, instruction),
                    GetInstructionColor(instruction.Name)));
            }
            blocks.SetGraph(newBlocks, function);
            // Framing follows the reader, never the playback: centring on the
            // entry each time a preview stepped into another scene made the view
            // jump back on its own.
            if (isNewScene && !suppressFunctionSelected) blocks.CenterOnEntry();
        }
        finally
        {
            blocks.ResumeLayout();
        }
    }

    /// <summary>One condition of the active path and the branch taken.</summary>
    private sealed record FlagContextEntry(int ForkInstruction, bool TakenTrue, string Condition)
    {
        public override string ToString() =>
            $"#{ForkInstruction,-4} {(TakenTrue ? "TRUE " : "FALSE")} {Condition}";
    }

    /// <summary>
    /// The menu for whatever was right-clicked on the canvas.
    ///
    /// Built for the thing under the pointer rather than shown whole and greyed:
    /// a block offers copy and delete, an arrow offers to put an instruction at the
    /// step it stands for, and an empty function offers its first instruction —
    /// which used to be impossible to add, since every path in went through
    /// selecting a block that was not there.
    /// </summary>
    private void ShowFlowMenu(ScriptFlowPanel.FlowContext context)
    {
        if (script is null || selectedFunctionIndex < 0) return;
        var function = script.Functions[selectedFunctionIndex];
        var menu = new ContextMenuStrip();

        if (context.Instruction is { } instruction)
        {
            var many = context.Selection.Count > 1;
            menu.Items.Add(new ToolStripMenuItem(
                many ? $"Copy {context.Selection.Count} blocks" : "Copy block",
                null,
                (_, _) => CopySelectedInstructions())
            { ShortcutKeyDisplayString = "Ctrl+C" });
            menu.Items.Add(new ToolStripMenuItem(
                "Paste after this",
                null,
                (_, _) => PasteInstructionsAfterSelection())
            {
                ShortcutKeyDisplayString = "Ctrl+V",
                Enabled = document is { InstructionClipboardCount: > 0 },
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(
                "Insert before this…", null, (_, _) => InsertInstruction(instruction)));
            menu.Items.Add(new ToolStripMenuItem(
                "Insert after this…", null, (_, _) => InsertInstruction(instruction + 1)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(
                many ? $"Delete {context.Selection.Count} blocks" : "Delete block",
                null,
                (_, _) => RemoveSelectedInstructions()));
        }
        else if (context.EdgeFrom is { } from)
        {
            // An arrow is the step between two instructions, so "here" is after the
            // one it leaves — whether it stands for a jump or for plain succession.
            var to = context.EdgeTo;
            var where = to is { } target && target > from ? target : from + 1;
            menu.Items.Add(new ToolStripMenuItem(
                to is { } named
                    ? $"Insert an instruction here (between {from} and {named})…"
                    : "Insert an instruction here…",
                null,
                (_, _) => InsertInstruction(Math.Max(0, where))));
        }
        else if (function.Instructions.Count == 0
            || function.Instructions.All(value => value.Opcode is 0 or 1))
        {
            menu.Items.Add(new ToolStripMenuItem(
                "Add the first instruction…", null, (_, _) => InsertInstruction(position: null)));
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem(
                "Add an instruction at the end…",
                null,
                (_, _) => InsertInstruction(position: null)));
            menu.Items.Add(new ToolStripMenuItem(
                "Paste at the end",
                null,
                (_, _) => PasteInstructionsAfterSelection())
            { Enabled = document is { InstructionClipboardCount: > 0 } });
        }

        // Kept, not disposed on close. A ContextMenuStrip is still being used by
        // WinForms after it raises Closed — the click that dismissed it is still
        // being routed — so disposing it there pulls the object out from under the
        // message that is closing it, and the window goes down. The previous menu is
        // released when the next one replaces it, and the last one with the window.
        flowMenu?.Dispose();
        flowMenu = menu;
        menu.Show(blocks, context.Location);
    }

    private void RefreshFlagContext(IReadOnlyList<ScriptFlowPanel.BranchDecision> decisions)
    {
        flagContextList.BeginUpdate();
        flagContextList.Items.Clear();
        foreach (var decision in decisions)
        {
            // The edge already carries the condition that side is taken under,
            // written out as words: "if flag[256] is set", "if work[5] != 4".
            var condition = decision.Label;
            if (condition.StartsWith("if ", StringComparison.Ordinal)) condition = condition[3..];
            flagContextList.Items.Add(new FlagContextEntry(
                decision.ForkInstruction, decision.TakenTrue,
                condition.Length == 0 ? "condition" : condition));
        }
        flagContextList.EndUpdate();
    }

    private void FindNextBlock()
    {
        if (script is null || selectedFunctionIndex < 0) return;
        var query = blockSearch.Text.Trim();
        if (query.Length == 0)
        {
            statusLabel.Text = "Enter an opcode or operand value to search.";
            blockSearch.Focus();
            return;
        }

        var function = script.Functions[selectedFunctionIndex];
        var matches = function.Instructions
            .Where(instruction => blockByIndex.Contains(instruction.Index))
            .Where(instruction => MatchesSearch(instruction, query))
            .ToArray();
        if (matches.Length == 0)
        {
            statusLabel.Text = $"No block contains \"{query}\".";
            return;
        }

        var currentMatch = selectedInstructionIndex is { } selected
            ? Array.FindIndex(matches, value => value.Index == selected)
            : -1;
        var next = matches[(currentMatch + 1) % matches.Length];
        SelectInstruction(function, next);
        ScrollToBlock(next.Index);
        var matchNumber = Array.FindIndex(matches, value => value.Index == next.Index) + 1;
        statusLabel.Text = $"Match {matchNumber}/{matches.Length}: #{next.Index} {next.Name}";
    }

    private static bool MatchesSearch(DecompiledInstruction instruction, string query)
    {
        if (instruction.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return instruction.Arguments.Any(argument => MatchesSearch(argument, query));
    }

    private static bool MatchesSearch(InstructionArgument argument, string query)
    {
        if (!string.IsNullOrEmpty(argument.Name)
            && argument.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (FormatArg(argument).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (argument.Kind != "scalar") return false;

        var exactValue = argument.Type == "f32"
            ? argument.FloatValue.ToString("R", CultureInfo.InvariantCulture)
            : argument.IntValue.ToString(CultureInfo.InvariantCulture);
        return exactValue.Contains(query, StringComparison.OrdinalIgnoreCase);
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
        // A block with no operands says so by having no second line. Writing "No
        // arguments" under the title spends a line of every RETURN in the scene to
        // report an absence the empty space already reports.
        return values.Count == 0 ? string.Empty : string.Join("   |   ", values);
    }

    /// <summary>Selection raised by the canvas, which knows indices, not models.</summary>
    private void SelectDrawnInstructions(IReadOnlyList<int> indices, int? primaryIndex)
    {
        selectedInstructionIndices.Clear();
        foreach (var selectedIndex in indices) selectedInstructionIndices.Add(selectedIndex);
        if (script is null || selectedFunctionIndex < 0 || primaryIndex is not { } index)
        {
            selectedInstructionIndex = null;
            SetSelectedInstructionToolsEnabled(-1, 0);
            return;
        }
        var function = script.Functions.FirstOrDefault(value => value.Index == selectedFunctionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value => value.Index == index);
        if (function is null || instruction is null) return;
        selectedInstructionIndex = instruction.Index;
        SetSelectedInstructionToolsEnabled(instruction.Index, function.Instructions.Count);
        if (!suppressInstructionSelected)
            InstructionSelected?.Invoke(function, instruction);
    }

    private void ActivateDrawnInstruction(int index)
    {
        if (script is null || selectedFunctionIndex < 0) return;
        var function = script.Functions.FirstOrDefault(value => value.Index == selectedFunctionIndex);
        var instruction = function?.Instructions.FirstOrDefault(value => value.Index == index);
        if (function is null || instruction is null) return;
        OpenInstructionEditor(function, instruction);
    }

    private void SelectInstruction(DecompiledFunction function, DecompiledInstruction instruction)
    {
        selectedInstructionIndices.Clear();
        selectedInstructionIndices.Add(instruction.Index);
        selectedInstructionIndex = instruction.Index;
        blocks.SelectInstruction(instruction.Index);
        SetSelectedInstructionToolsEnabled(instruction.Index, function.Instructions.Count);
        if (!suppressInstructionSelected)
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
        selectedInstructionIndices.Clear();
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

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (!IsTextEntryFocused() && keyData == (Keys.Control | Keys.C))
        {
            CopySelectedInstructions();
            return true;
        }
        if (!IsTextEntryFocused() && keyData == (Keys.Control | Keys.V))
        {
            PasteInstructionsAfterSelection();
            return true;
        }
        // A script opened in a window of its own: Ctrl+S still means the whole
        // project. Only reached when this is a top-level window — inside the main
        // window's panel the form handles the key first.
        if (TopLevel && keyData == (Keys.Control | Keys.S))
        {
            if (!ProjectSave.Everything()) Save(saveAs: false);
            return true;
        }
        if (TopLevel && keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            Save(saveAs: true);
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
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
        var currentFunction = script?.Functions.FirstOrDefault(value =>
            value.Index == function.Index);
        var currentInstruction = currentFunction?.Instructions.FirstOrDefault(value =>
            value.Index == instruction.Index);
        if (currentFunction is null || currentInstruction is null) return;
        function = currentFunction;
        instruction = currentInstruction;
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
        // One palette for every field, applied once over the built dialog.
        //
        // The fields are made in a dozen places and each carried whatever colours it
        // was given, so a text box could end up light-on-white or dark-on-dark
        // depending on which branch had built it. Reading a value is the whole point
        // of the dialog, and a value that cannot be read is worse than one that is
        // not offered.
        StyleFields(fields);
        editor.Controls.Add(fields);
        editor.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(activeInstructionEditor, editor)) activeInstructionEditor = null;
        };
        activeInstructionEditor = editor;
        editor.Show(this);
    }

    /// <summary>Gives every editable field the same readable colours.</summary>
    private static void StyleFields(Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case TextBoxBase or ComboBox or NumericUpDown or ListBox:
                    child.BackColor = Color.FromArgb(24, 24, 28);
                    child.ForeColor = Color.Gainsboro;
                    break;
                case CheckBox or RadioButton or Label:
                    child.ForeColor = Color.Gainsboro;
                    break;
                case DataGridView grid:
                    grid.BackgroundColor = Color.FromArgb(24, 24, 28);
                    grid.DefaultCellStyle.BackColor = Color.FromArgb(24, 24, 28);
                    grid.DefaultCellStyle.ForeColor = Color.Gainsboro;
                    grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(72, 82, 101);
                    grid.DefaultCellStyle.SelectionForeColor = Color.White;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 46, 52);
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
                    grid.EnableHeadersVisualStyles = false;
                    break;
            }
            if (child.HasChildren) StyleFields(child);
        }
    }

    private void SetSelectedInstructionToolsEnabled(int index, int instructionCount)
    {
        var selected = index >= 0 && index < instructionCount;
        addAfterButton.Enabled = selected;
        moveUpButton.Enabled = selectedInstructionIndices.Count == 1 && selected && index > 0;
        moveDownButton.Enabled = selectedInstructionIndices.Count == 1
            && selected && index + 1 < instructionCount;
        copyInstructionsButton.Enabled = selectedInstructionIndices.Count > 0;
        pasteInstructionsButton.Enabled = selected
            && document is { InstructionClipboardCount: > 0 };
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
        if (ScriptSemanticValueConverter.IsPosition(arguments)
            && semanticContext?.BeginSurfacePositionCapture is { } beginCapture)
        {
            var pick = new Button { AutoSize = true, Text = "Pick on map…" };
            pick.Click += (_, _) =>
            {
                beginCapture(position =>
                {
                    if (row.IsDisposed) return;
                    var values = ScriptSemanticValueConverter.WritePosition(position);
                    for (var index = 0; index < fields.Count; index++)
                        fields[index].Text = values[index];
                });
            };
            row.Controls.Add(pick);
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
        var hasInterp = ScriptCameraStateResolver.HasInterpolation(instruction);

        var apply = new Button { AutoSize = true, Text = hasInterp ? "Set Camera Properties (exit preview)" : "Apply current viewport camera" };
        apply.Click += (_, _) =>
        {
            var snapshot = semanticContext!.GetCameraSnapshot();
            var capture = CameraPropertyWriter.Capture(
                instruction, snapshot, ResolveSceneBefore(instruction));
            var byteUpdates = ScriptCameraInstructionCodec.Capture(instruction, snapshot);

            RunEdit(() =>
            {
                foreach (var write in capture.Writes)
                    SetScalar(instruction.Index, write.Argument, write.Value);
                foreach (var update in byteUpdates)
                    document!.SetBytes(selectedFunctionIndex, instruction.Index, update.ArgumentIndex, update.Value);
            }, instruction.Index);

            StopPreview?.Invoke();
        };
        var initialSnapshot = semanticContext!.GetCameraSnapshot();
        var initialCapture = CameraPropertyWriter.Capture(
            instruction, initialSnapshot, ResolveSceneBefore(instruction));
        var initialByteUpdates = ScriptCameraInstructionCodec.Capture(instruction, initialSnapshot);
        var components = initialCapture.Components
            .Concat(initialByteUpdates.Select(value => value.Component))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        apply.Enabled = initialCapture.CanCapture || initialByteUpdates.Count > 0;

        panel.Controls.Add(apply);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(6, 7, 0, 0),
            Text = components.Count > 0
                ? $"Copies: {string.Join(", ", components)}"
                : initialCapture.UnavailableComponents.Count > 0
                    ? $"Unavailable until defined earlier: {string.Join(", ", initialCapture.UnavailableComponents)}"
                    : "No camera property is mapped for this instruction",
        });
        return panel;
    }

    /// <summary>Événement déclenché quand l'utilisateur clique sur Set Camera Properties.</summary>
    public event Action? StopPreview;

    private ScriptSceneState? ResolveSceneBefore(DecompiledInstruction instruction)
    {
        if (script is null || selectedFunctionIndex < 0) return null;
        var function = script.Functions.FirstOrDefault(value => value.Index == selectedFunctionIndex);
        return function is null
            ? null
            : ScriptSceneStateResolver.ResolveBefore(script, function, instruction.Index);
    }

    private bool HasCameraCapture(DecompiledInstruction instruction, ScriptCameraSnapshot snapshot)
    {
        if (instruction.Opcode != 45) return false;
        var capture = CameraPropertyWriter.Capture(
            instruction, snapshot, ResolveSceneBefore(instruction));
        return capture.CanCapture
            || capture.UnavailableComponents.Count > 0
            || ScriptCameraInstructionCodec.Capture(instruction, snapshot).Count > 0;
    }

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

    /// <summary>
    /// Dialogue editor. One operand can hold several spoken lines, so each gets
    /// its own voice clip and its own text: the voice id is what t_voice.tbl
    /// resolves to an audio file, and the text keeps its control codes as {XX}
    /// escapes (0x01 breaks a line, 0x02 closes a page, 0x00 terminates). Leave
    /// the escapes alone and the operand comes back byte for byte.
    /// </summary>
    private Control BuildDialogEditor(
        DecompiledFunction function, DecompiledInstruction instruction, InstructionArgument argument)
    {
        var encoding = ScriptDialogText.ResolveEncoding(document?.SourcePath);
        var lines = ScriptDialogText.Split(argument.Raw, encoding);
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(8, 6, 8, 10),
            Padding = new Padding(8),
            BackColor = Color.FromArgb(41, 54, 68),
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Text = $"Dialogue ({argument.Raw.Length} bytes, {lines.Count} line(s))"
                + " — {XX} = control byte, kept as is",
        });

        var voiceFields = new List<TextBox>();
        var textFields = new List<TextBox>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            header.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(0, 6, 4, 0),
                Text = $"Line {index + 1} — voice",
            });
            var voice = new TextBox
            {
                Width = 90,
                Text = line.VoiceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            };
            var clip = new Label
            {
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(6, 6, 0, 0),
                Text = DescribeVoice(line.VoiceId),
            };
            voice.TextChanged += (_, _) => clip.Text = DescribeVoice(
                int.TryParse(voice.Text, out var parsed) ? parsed : null);
            var play = new Button { AutoSize = true, Text = "Play", Margin = new Padding(6, 0, 0, 0) };
            play.Click += (_, _) => PlayVoice(
                int.TryParse(voice.Text, out var parsed) ? parsed : null);
            header.Controls.Add(voice);
            header.Controls.Add(play);
            header.Controls.Add(clip);
            var field = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Width = 460,
                Height = Math.Clamp(lines.Count, 1, 3) > 1 ? 92 : 140,
                Font = DialogFont,
                // A multiline text box only breaks a line on CRLF; the decoder
                // speaks in single new lines.
                Text = line.Text.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            };
            voiceFields.Add(voice);
            textFields.Add(field);
            panel.Controls.Add(header);
            panel.Controls.Add(field);
        }

        var status = new Label { AutoSize = true, ForeColor = Color.Gainsboro, Text = string.Empty };
        var apply = new Button { AutoSize = true, Text = "Apply dialogue" };
        apply.Click += (_, _) =>
        {
            var edited = new List<ScriptDialogLine>(lines.Count);
            for (var index = 0; index < lines.Count; index++)
            {
                int? voiceId = null;
                var typed = voiceFields[index].Text.Trim();
                if (typed.Length != 0)
                {
                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        || parsed < 0)
                    {
                        status.Text = $"Line {index + 1}: '{typed}' is not a voice id.";
                        return;
                    }
                    voiceId = parsed;
                }
                edited.Add(lines[index] with { VoiceId = voiceId, Text = textFields[index].Text });
            }
            byte[] encoded;
            try
            {
                encoded = ScriptDialogText.Join(edited, encoding);
            }
            catch (EncoderFallbackException exception)
            {
                status.Text = $"Unsupported character for this script's encoding: {exception.Message}";
                return;
            }
            RunEdit(
                () => document!.SetBytes(function.Index, instruction.Index, argument.Index, encoded),
                instruction.Index);
        };
        panel.Controls.Add(apply);
        panel.Controls.Add(status);
        return panel;
    }

    private string DescribeVoice(int? voiceId)
    {
        if (voiceId is not { } id) return "no voice";
        var file = VoiceTable.FindFile(id);
        return file is null ? $"id {id} (not in t_voice.tbl)" : $"{file}";
    }

    private void PlayVoice(int? voiceId)
    {
        if (voiceId is not { } id || GameDataPath is not { } gameDataPath) return;
        var path = VoiceTable.FindAudioPath(gameDataPath, id);
        if (path is null)
        {
            MessageBox.Show(
                this,
                VoiceTable.FindFile(id) is { } missing
                    ? $"'{missing}' was not found under data/voice."
                    : $"Voice id {id} is not declared in t_voice.tbl.",
                "No audio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                // The shipped clips are plain WAV: play them in place.
                var player = new System.Media.SoundPlayer(path);
                player.Play();
                return;
            }
            // Anything else is compressed audio the editor does not decode; hand
            // it to whatever the system plays it with.
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true },
            };
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or FormatException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot play", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Game data directory the open script belongs to.</summary>
    private string? GameDataPath
    {
        get
        {
            if (document is null) return null;
            var directory = new DirectoryInfo(Path.GetDirectoryName(document.SourcePath)!);
            while (directory is not null)
            {
                if (directory.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                    return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }
    }

    private ScriptVoiceTable VoiceTable =>
        voiceTable ??= ScriptVoiceTable.Load(GameDataPath, document?.SourcePath);

    private ScriptVoiceTable? voiceTable;

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

        if (argument.Kind == "scalar" && argument.Type != "ptr32"
            && (argument.Sem == "entity"
                || argument.Sem?.StartsWith("entity:", StringComparison.OrdinalIgnoreCase) == true))
            return BuildEntityReferenceEditor(instruction, argument);

        if (argument.Kind == "scalar" && argument.Type != "ptr32")
            return BuildScalarEditor(instruction, new[] { argument });

        if (argument.Kind == "dialog")
            return BuildDialogEditor(function, instruction, argument);

        var row = CreateArgumentRow(argument);
        if (argument.Kind == "string")
        {
            if (argument.Sem == "entity_animation_function")
                return BuildEntityAnimationFunctionEditor(function, instruction, argument);
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
                if (selectedFunction.FunctionIndex != jump.TargetFunctionIndex) return;
                // Where this jump actually goes, selected. An unresolved jump has no
                // instruction to point at, so it selects the entry that says so
                // rather than falling to the top of the list and reading as though
                // the jump were already aimed somewhere.
                target.SelectedItem = choices.FirstOrDefault(value =>
                        value.InstructionIndex == jump.TargetInstructionIndex)
                    ?? choices.FirstOrDefault(value => value.InstructionIndex == int.MinValue)
                    ?? choices[0];
            }
            targetFunction.SelectedIndexChanged += (_, _) => PopulateTargets();

            // The function this jump names, or a plain statement that it names none.
            // Quietly falling back to the current function showed "func_0" and an
            // empty target beside a jump that had a perfectly good one, and gave no
            // way to tell that apart from a jump genuinely aimed at func_0.
            var named = functionChoices.FirstOrDefault(value =>
                value.FunctionIndex == jump.TargetFunctionIndex);
            if (named is null)
            {
                row.Controls.Add(new Label
                {
                    AutoSize = true,
                    ForeColor = Color.Goldenrod,
                    Padding = new Padding(0, 6, 6, 0),
                    Text = $"target function {jump.TargetFunctionIndex} is not in this file:",
                });
                named = functionChoices.First(value => value.FunctionIndex == function.Index);
            }
            // Assigning the selection raises the handler, which fills the targets.
            // Calling it again afterwards rebuilt the list and lost what it had just
            // chosen whenever the two disagreed.
            targetFunction.SelectedItem = named;
            if (targetFunction.SelectedItem is not FunctionChoice) PopulateTargets();
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

    private Control BuildEntityAnimationFunctionEditor(
        DecompiledFunction function,
        DecompiledInstruction instruction,
        InstructionArgument argument)
    {
        var row = CreateArgumentRow(argument);
        var entityId = instruction.Arguments.FirstOrDefault(value =>
            value.Kind == "scalar"
            && (value.Sem == "entity"
                || value.Sem?.StartsWith("entity:", StringComparison.OrdinalIgnoreCase) == true))
            ?.IntValue;
        var current = DecodeText(argument.Raw);
        var choices = entityId is { } id
            ? semanticContext?.GetEntityAnimationFunctions(id) ?? Array.Empty<string>()
            : Array.Empty<string>();
        var selector = new ComboBox
        {
            Width = 320,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        selector.Items.AddRange(choices.Cast<object>().ToArray());
        var currentChoiceIndex = choices
            .Select((value, index) => (value, index))
            .Where(value => value.value.Equals(current, StringComparison.Ordinal))
            .Select(value => value.index)
            .DefaultIfEmpty(-1)
            .First();
        selector.SelectedIndex = currentChoiceIndex;
        if (currentChoiceIndex < 0)
            selector.Text = current;
        var apply = new Button { AutoSize = true, Text = "Apply ANI function" };
        apply.Click += (_, _) => RunEdit(
            () => document!.SetString(
                function.Index, instruction.Index, argument.Index, selector.Text),
            instruction.Index);
        row.Controls.Add(selector);
        row.Controls.Add(apply);
        row.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(4, 5, 0, 0),
            Text = choices.Count > 0
                ? $"{choices.Count} functions from the entity ANI script"
                : "ANI script unresolved; arbitrary names remain allowed",
        });
        return row;
    }

    private Control BuildEntityReferenceEditor(
        DecompiledInstruction instruction,
        InstructionArgument argument)
    {
        var row = CreateArgumentRow(argument);
        var currentId = argument.IntValue;
        var choices = semanticContext?.GetEntities()
            .OrderBy(value => value.EntityId)
            .ToList() ?? new List<ScriptEntityChoice>();
        if (choices.All(value => value.EntityId != currentId))
            choices.Insert(0, new ScriptEntityChoice(
                currentId, string.Empty, ScriptEntityReferences.DisplayName(currentId)));

        var selector = new ComboBox
        {
            Width = 330,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(ScriptEntityChoice.Label),
        };
        selector.Items.AddRange(choices.Cast<object>().ToArray());
        selector.SelectedIndex = choices.FindIndex(value => value.EntityId == currentId);
        var apply = new Button { AutoSize = true, Text = "Apply entity" };
        apply.Click += (_, _) =>
        {
            if (selector.SelectedItem is not ScriptEntityChoice selected) return;
            RunEdit(() => document!.SetInteger(
                selectedFunctionIndex, instruction.Index, argument.Index, selected.EntityId),
                instruction.Index);
        };
        row.Controls.Add(selector);
        row.Controls.Add(apply);
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
        // The readable way in, beside the raw one. The grid stays: it can express
        // anything the stack can, which the builder cannot yet, and taking it away
        // before the builder covers those cases would leave those conditions with no
        // way to be edited at all.
        var build = new Button { AutoSize = true, Text = "Condition builder…" };
        build.Click += (_, _) =>
        {
            using var builder = new ExpressionBuilderForm(argument.Expression);
            if (builder.ShowDialog(this) != DialogResult.OK
                || builder.Result is not { } written)
            {
                return;
            }
            RunEdit(
                () => document!.ReplaceExpression(
                    function.Index,
                    instruction.Index,
                    argument.Index,
                    written
                        .Select(value => new ScriptExpressionToken(value.SubOp, value.Value))
                        .ToArray()),
                instruction.Index);
        };
        tools.Controls.Add(moveUp);
        tools.Controls.Add(moveDown);
        tools.Controls.Add(apply);
        tools.Controls.Add(build);
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

    /// <summary>
    /// Takes back the last edit and shows the script as it was.
    ///
    /// The state is restored whole, so what comes back is not an approximation of
    /// the previous script but the previous script. Everything downstream — the
    /// scene list, the canvas, the inspector — is rebuilt from it, as it is after
    /// any other edit.
    /// </summary>
    private void Undo()
    {
        if (document is null) return;
        try
        {
            if (!document.Undo())
            {
                statusLabel.Text = "Nothing to undo.";
                return;
            }
            activeInstructionEditor?.Close();
            RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
            statusLabel.Text = "Undone.";
        }
        catch (InvalidOperationException failure)
        {
            statusLabel.Text = "Undo failed: " + failure.Message;
        }
    }

    private void Redo()
    {
        if (document is null) return;
        try
        {
            if (!document.Redo())
            {
                statusLabel.Text = "Nothing to redo.";
                return;
            }
            activeInstructionEditor?.Close();
            RefreshDocument(selectedFunctionIndex, selectedInstructionIndex);
            statusLabel.Text = "Redone.";
        }
        catch (InvalidOperationException failure)
        {
            statusLabel.Text = "Redo failed: " + failure.Message;
        }
    }

    private bool RunEdit(Action edit, int? selectedInstruction)
    {
        // Where the script stands before the edit, so it can be taken back. Every
        // edit in this window goes through here, which is what makes ten steps of
        // undo a property of the editor rather than of the few actions someone
        // remembered to wire it into.
        document?.Checkpoint();

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

    /// <summary>
    /// Creates an empty scenario function. The game binds an event to a function
    /// by NAME: an OPS entry box (or look point) carrying the same name runs it,
    /// so the name typed here is what the map trigger must repeat.
    /// </summary>
    private void CreateScene()
    {
        if (document is null || script is null) return;
        var name = PromptForSceneName();
        if (name is null) return;
        if (script.Functions.Any(value => value.Name.Equals(name, StringComparison.Ordinal)))
        {
            MessageBox.Show(
                this, $"'{name}' already exists in this script.", "Cannot create scene",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var index = document.AddCodeFunction(name);
            RefreshDocument(index, 0);
            statusLabel.Text = $"Created scene '{name}'. Add a map trigger with the same name"
                + " (OPS creation panel → \"Event trigger\") to run it when the player walks in.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot create scene", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteScene()
    {
        if (document is null || script is null || selectedFunctionIndex < 0) return;
        var function = script.Functions.FirstOrDefault(value => value.Index == selectedFunctionIndex);
        if (function is null) return;
        var result = MessageBox.Show(
            this,
            $"Delete scene '{function.Name}' and its {function.Instructions.Count} instructions?"
            + "\n\nBranches from other scenes into this one are not rewritten.",
            "Delete scene", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        try
        {
            document.RemoveFunction(function.Index);
            RefreshDocument(Math.Max(0, function.Index - 1), null);
            statusLabel.Text = $"Deleted scene '{function.Name}'.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot delete scene", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string? PromptForSceneName()
    {
        using var dialog = new Form
        {
            Text = "New scene",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 120),
        };
        var label = new Label
        {
            AutoSize = true,
            Left = 12,
            Top = 14,
            Text = "Function name (an OPS trigger of the same name will run it):",
        };
        var input = new TextBox { Left = 12, Top = 40, Width = 396, Text = "EV_NEW00" };
        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Left = 252, Top = 74, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 74, Width = 75 };
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        var name = input.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    private string? PromptForFunctionName(
        string title,
        string prompt,
        string suggestedName)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(440, 120),
        };
        var label = new Label
        {
            AutoSize = true,
            Left = 12,
            Top = 14,
            Text = prompt,
        };
        var input = new TextBox
        {
            Left = 12,
            Top = 40,
            Width = 416,
            Text = suggestedName,
        };
        var ok = new Button
        {
            Text = "Create",
            DialogResult = DialogResult.OK,
            Left = 272,
            Top = 74,
            Width = 75,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 353,
            Top = 74,
            Width = 75,
        };
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        var name = input.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    private T? ChooseItem<T>(
        string title,
        string prompt,
        IReadOnlyList<T> choices)
        where T : class
    {
        if (choices.Count == 0)
        {
            MessageBox.Show(
                this,
                "No compatible item exists in the current script.",
                title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }
        if (choices.Count == 1) return choices[0];
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 125),
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = prompt,
            Padding = new Padding(10, 7, 0, 0),
        };
        var list = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        list.Items.AddRange(choices.Cast<object>().ToArray());
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(buttons);
        dialog.Controls.Add(list);
        dialog.Controls.Add(label);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        return dialog.ShowDialog(this) == DialogResult.OK
            ? list.SelectedItem as T
            : null;
    }

    private IReadOnlyList<FieldMonsterTableChoice> EncounterTables()
    {
        if (script is null) return Array.Empty<FieldMonsterTableChoice>();
        return script.Functions
            .Where(value => value.Table is not null
                && CreateMonstersTableReader.TryRead(value.Table, out _))
            .Select(value =>
            {
                CreateMonstersTableReader.TryRead(value.Table!, out var table);
                return new FieldMonsterTableChoice(value.Index, value.Name, table!);
            })
            .ToArray();
    }

    /// <summary>
    /// Puts a new instruction in, asking which one at the moment it is asked for.
    ///
    /// The kind used to come from a drop-down in the toolbar, which meant choosing
    /// it long before deciding where it went and scrolling a few hundred names to
    /// do so. It is a searchable, grouped list now, and it opens where the
    /// instruction is going rather than above the whole canvas.
    /// </summary>
    private void InsertInstruction(int? position)
    {
        if (document is null || selectedFunctionIndex < 0) return;
        var names = ScriptEditorDocument.GetInstructionNames(instructionDefinitionsPath);
        if (names.Count == 0)
        {
            statusLabel.Text = "No instruction definitions are loaded, so none can be added.";
            return;
        }
        using var picker = new InstructionPickerForm(
            names,
            lastInsertedInstruction,
            ScriptInstructionPresets.Load(instructionDefinitionsPath));
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (picker.ChosenPreset is { } preset)
        {
            InsertPreset(preset, position);
            return;
        }
        if (picker.Chosen is not { } name) return;
        lastInsertedInstruction = name;
        var function = script!.Functions[selectedFunctionIndex];
        // "At the end" means the end of what actually runs: appending after the
        // closing RETURN would produce unreachable code, which the flow view then
        // stacks apart from the scene.
        var insertion = position ?? LastExecutableIndex(function);
        if (!RunEdit(
                () => document.InsertInstruction(selectedFunctionIndex, insertion, name),
                insertion))
        {
            return;
        }
        // Straight into its operands. An instruction is inserted in order to be
        // given values; making that a second, separate action meant every insertion
        // was two gestures and a hunt for the block that had just appeared.
        OpenInsertedInstruction(insertion);
    }

    /// <summary>
    /// Inserts a whole run of instructions, in order, as one edit.
    ///
    /// One edit, so one step of undo: a preset that half-applied and had to be taken
    /// back a command at a time would be worse than typing the commands out.
    /// </summary>
    private void InsertPreset(ScriptPreset preset, int? position)
    {
        if (document is null || script is null || selectedFunctionIndex < 0) return;
        var function = script.Functions[selectedFunctionIndex];
        var insertion = position ?? LastExecutableIndex(function);
        var failed = new List<string>();
        if (!RunEdit(
                () =>
                {
                    var at = insertion;
                    foreach (var step in preset.Steps)
                    {
                        try
                        {
                            document.InsertInstruction(selectedFunctionIndex, at, step.Instruction);
                            at++;
                        }
                        catch (Exception exception) when (exception is ArgumentException
                            or InvalidOperationException)
                        {
                            // A preset naming an instruction this build does not have
                            // inserts the rest and says which one it skipped, rather
                            // than losing the whole run to one stale name.
                            failed.Add(step.Instruction);
                        }
                    }
                },
                insertion))
        {
            return;
        }
        statusLabel.Text = failed.Count == 0
            ? $"Inserted '{preset.Name}' — {preset.Steps.Count} instruction(s)."
            : $"Inserted '{preset.Name}' — {preset.Steps.Count - failed.Count} of"
                + $" {preset.Steps.Count}; this build has no {string.Join(", ", failed)}.";
        OpenInsertedInstruction(insertion);
    }

    /// <summary>Opens the editor on an instruction that has just been put in.</summary>
    private void OpenInsertedInstruction(int insertion)
    {
        if (script is null || selectedFunctionIndex < 0) return;
        var written = script.Functions[selectedFunctionIndex];
        if (insertion < 0 || insertion >= written.Instructions.Count) return;
        BeginInvoke(() => OpenInstructionEditor(written, written.Instructions[insertion]));
    }

    /// <summary>
    /// Index of the function's closing RETURN, i.e. where a new instruction must
    /// go to stay part of the executed flow.
    /// </summary>
    private static int LastExecutableIndex(DecompiledFunction function)
    {
        for (var index = function.Instructions.Count - 1; index >= 0; index--)
            if (function.Instructions[index].Opcode == 1) return index;
        return function.Instructions.Count;
    }

    /// <summary>
    /// A function is padded to its alignment with OP0 bytes after the closing
    /// RETURN. They are not instructions and must not appear as blocks.
    /// </summary>
    private static bool IsTrailingPadding(DecompiledFunction function, DecompiledInstruction instruction)
        => instruction.Opcode == 0 && instruction.Index > LastExecutableIndex(function);

    private void MoveInstruction(int from, int to)
    {
        if (to < 0) return;
        RunEdit(() => document!.MoveInstruction(selectedFunctionIndex, from, to), to);
    }

    private void CopySelectedInstructions()
    {
        if (document is null || selectedFunctionIndex < 0
            || selectedInstructionIndices.Count == 0)
        {
            return;
        }
        try
        {
            document.CopyInstructions(selectedFunctionIndex, selectedInstructionIndices);
            pasteInstructionsButton.Enabled = selectedInstructionIndex is not null;
            statusLabel.Text = $"Copied {selectedInstructionIndices.Count} instruction block(s).";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot copy instructions",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PasteInstructionsAfterSelection()
    {
        if (document is null || script is null || selectedFunctionIndex < 0
            || selectedInstructionIndex is not { } selected)
        {
            return;
        }
        var insertion = selected + 1;
        try
        {
            // Pasting is the one edit that does not go through RunEdit — it has its
            // own selection handling afterwards — so it has to record where the
            // script stood itself. Without this it was the single action in the
            // window that could not be taken back.
            document.Checkpoint();
            var count = document.PasteInstructions(selectedFunctionIndex, insertion);
            RefreshDocument(selectedFunctionIndex, selectedInstruction: null);
            var pasted = Enumerable.Range(insertion, count).ToArray();
            selectedInstructionIndices.Clear();
            foreach (var index in pasted) selectedInstructionIndices.Add(index);
            selectedInstructionIndex = pasted[^1];
            blocks.SelectInstructions(pasted, selectedInstructionIndex);
            SetSelectedInstructionToolsEnabled(
                selectedInstructionIndex.Value,
                script!.Functions[selectedFunctionIndex].Instructions.Count);
            var function = script.Functions[selectedFunctionIndex];
            var instruction = function.Instructions[selectedInstructionIndex.Value];
            if (!suppressInstructionSelected)
                InstructionSelected?.Invoke(function, instruction);
            blocks.ScrollInstructionIntoView(selectedInstructionIndex.Value);
            statusLabel.Text = $"Pasted {count} instruction block(s) after #{selected}.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot paste instructions",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveSelectedInstructions()
    {
        if (document is null || script is null || selectedFunctionIndex < 0
            || selectedInstructionIndices.Count == 0)
        {
            return;
        }
        var selected = selectedInstructionIndices.ToArray();
        var description = selected.Length == 1
            ? $"Delete #{selected[0]} {script.Functions[selectedFunctionIndex].Instructions[selected[0]].Name}?"
            : $"Delete the {selected.Length} selected instruction blocks?";
        var result = MessageBox.Show(this, description,
            "Delete instructions", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        RunEdit(
            () =>
            {
                foreach (var index in selected.OrderByDescending(value => value))
                    document.RemoveInstruction(selectedFunctionIndex, index);
            },
            Math.Max(0, selected.Min() - 1));
    }

    private void ShowTable(TreeNode node)
    {
        if (document is null || node.Tag is not DecompiledFunction { Table: not null } function) return;
        using var editor = new TableEditorForm(
            document,
            function.Index,
            monsterChoices,
            battleMapAssets: battleMapAssets,
            createBattleMapAsset: createBattleMapAsset);
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
        statusLabel.Text = $"Table: {function.Name} — {table.Kind}" +
            (table.IsStale ? "  (stale/malformed)" : string.Empty);
        if (CreateMonstersTableReader.TryRead(table, out var monsters) && monsters is not null)
        {
            statusLabel.Text += $" — map {monsters.MapAsset}, {monsters.Encounters.Count} encounters";
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
            monsterGrid.Columns.Add("name", "Monster name");
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
                        MonsterName(encounter.MonsterAssets[slot]),
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

    private string MonsterName(string assetId)
        => monsterChoices.FirstOrDefault(value =>
                value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? (string.IsNullOrEmpty(assetId) ? "(empty slot)" : "Unknown");

    private void ScrollToBlock(int index)
    {
        if (!blockByIndex.Contains(index)) return;
        blocks.SelectInstruction(index);
        blocks.ScrollInstructionIntoView(index);
    }

    private void SetInstructionToolsEnabled(bool enabled)
    {
        addInstructionButton.Enabled = enabled;
        placeFieldMonsterButton.Enabled = enabled
            && script is not null
            && ScriptMonsterSpawnReader.Read(script).Count > 0
            && semanticContext?.BeginSurfacePositionCapture is not null;
        if (!enabled) SetSelectedInstructionToolsEnabled(-1, 0);
    }

    private void BeginFieldMonsterPlacement()
    {
        if (document is null || script is null) return;
        var table = ChooseItem(
            "Instantiate encounter",
            "Battle table:",
            EncounterTables());
        if (table is null) return;
        var encounter = ChooseItem(
            "Instantiate encounter",
            "Encounter:",
            table.Table.Encounters.Select(value =>
                new EncounterChoice(value.Index, value, MonsterName)).ToArray());
        if (encounter is null) return;
        BeginEncounterPlacement(table, encounter.Encounter);
    }

    private void BeginEncounterPlacement(
        FieldMonsterTableChoice table,
        CreateMonstersEncounter encounter)
    {
        if (document is null || script is null
            || semanticContext?.BeginSurfacePositionCapture is not { } beginCapture)
        {
            return;
        }
        var existingSpawns = ScriptMonsterSpawnReader.Read(script);
        var preferredAssets = encounter.MonsterAssets
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var assets = preferredAssets
            .Select(ResolveMonsterChoice)
            .Concat(monsterChoices)
            .DistinctBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (assets.Length == 0)
        {
            MessageBox.Show(
                this,
                "No monster model is available. Add a monster to the encounter "
                + "or load t_mons before instantiating it.",
                "Cannot instantiate encounter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        var targetFunctions = script.Functions
            .Where(value => value.IsCode)
            .Select(value => new EncounterTargetChoice(
                value.Index,
                $"{value.Name} ({value.Instructions.Count} instructions)"))
            .ToArray();
        if (targetFunctions.Length == 0)
        {
            MessageBox.Show(
                this,
                "The script contains no code function that can receive Entity_Spawn.",
                "Cannot instantiate encounter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (encounter.Id is < byte.MinValue or > byte.MaxValue)
        {
            MessageBox.Show(
                this,
                $"Encounter ID {encounter.Id} cannot be encoded in OP19's u8 operand.",
                "Cannot instantiate encounter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var nextEntityId = existingSpawns.Count == 0
            ? 2000
            : Math.Clamp(existingSpawns.Max(value => value.EntityId) + 1, short.MinValue, short.MaxValue);
        var defaults = FieldMonsterSpawnParameters.CreateDefault(
            nextEntityId,
            assets[0].AssetId,
            table.FunctionIndex,
            encounter.Id);
        using var dialog = new Form
        {
            Text = $"Instantiate encounter {encounter.Id}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(650, 590),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var generalTab = new TabPage("Encounter instance");
        var advancedTab = new TabPage("Advanced OP19 fields");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 8,
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var targetFunctionList = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        targetFunctionList.Items.AddRange(targetFunctions.Cast<object>().ToArray());
        var assetList = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(MonsterTableChoice.Label),
        };
        assetList.Items.AddRange(assets.Cast<object>().ToArray());
        var entityId = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Minimum = short.MinValue,
            Maximum = short.MaxValue,
            Value = defaults.EntityId,
        };
        var heading = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Minimum = -3600,
            Maximum = 3600,
            DecimalPlaces = 2,
            Increment = 5,
            Value = (decimal)defaults.HeadingDegrees,
        };
        var flags = IntegerEditor(defaults.Flags);
        var entityType = IntegerEditor(defaults.EntityType, byte.MinValue, byte.MaxValue);
        var scale = FloatEditor(defaults.Scale);
        var collisionHeight = FloatEditor(defaults.CollisionHeight);
        var collisionRadius = FloatEditor(defaults.CollisionRadius);
        AddRow(layout, 0, "Target function", targetFunctionList);
        AddRow(layout, 1, "Monster (t_mons)", assetList);
        AddRow(layout, 2, "Entity ID", entityId);
        AddRow(layout, 3, "Heading (degrees)", heading);
        AddRow(layout, 4, "Entity type", entityType);
        AddRow(layout, 5, "Flags", flags);
        AddRow(layout, 6, "Scale", scale);
        AddRow(
            layout,
            7,
            "Collision",
            Pair(collisionHeight, collisionRadius, "Height", "Radius"));

        var advanced = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 8,
            AutoScroll = true,
        };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var modelAsset = new TextBox { Dock = DockStyle.Fill, Text = defaults.ModelAsset };
        var displayName = new TextBox { Dock = DockStyle.Fill, Text = defaults.DisplayName };
        var scriptFile = new TextBox { Dock = DockStyle.Fill, Text = defaults.ScriptFile };
        var initFunction = new TextBox { Dock = DockStyle.Fill, Text = defaults.InitFunction };
        var unknown1 = IntegerEditor(defaults.UnknownParameter1);
        var unknown2 = IntegerEditor(defaults.UnknownParameter2);
        var unknown3 = IntegerEditor(defaults.UnknownParameter3, short.MinValue, short.MaxValue);
        AddRow(advanced, 0, "Model asset (argument 1)", modelAsset);
        AddRow(advanced, 1, "Display name (argument 2)", displayName);
        AddRow(advanced, 2, "Script file (argument 13)", scriptFile);
        AddRow(advanced, 3, "Init function (argument 14)", initFunction);
        AddRow(advanced, 4, "Battle table function", ReadOnlyValue(
            $"#{table.FunctionIndex} {table.Name}"));
        AddRow(advanced, 5, "Encounter ID", ReadOnlyValue(encounter.Id.ToString()));
        AddRow(
            advanced,
            6,
            "Unknown raw s32",
            Pair(unknown1, unknown2, "Argument 17", "Argument 18"));
        AddRow(advanced, 7, "Unknown raw s16 (argument 19)", unknown3);
        generalTab.Controls.Add(layout);
        advancedTab.Controls.Add(advanced);
        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(advancedTab);
        var ok = new Button
        {
            Text = "Choose position on map",
            AutoSize = true,
            DialogResult = DialogResult.OK,
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(tabs);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (targetFunctionList.Items.Count == 0 || assetList.Items.Count == 0)
        {
            MessageBox.Show(
                this,
                "The encounter placement choices could not be populated.",
                "Cannot instantiate encounter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        var preferredTarget = Array.FindIndex(targetFunctions, value =>
            value.FunctionIndex == selectedFunctionIndex);
        targetFunctionList.SelectedIndex = preferredTarget >= 0 ? preferredTarget : 0;
        assetList.SelectedIndex = 0;
        if (dialog.ShowDialog(this) != DialogResult.OK
            || targetFunctionList.SelectedItem is not EncounterTargetChoice target
            || assetList.SelectedItem is not MonsterTableChoice selectedModel
            || string.IsNullOrWhiteSpace(selectedModel.AssetId))
        {
            return;
        }

        var parameters = new FieldMonsterSpawnParameters(
            decimal.ToInt32(entityId.Value),
            modelAsset.Text,
            displayName.Text,
            selectedModel.AssetId,
            decimal.ToInt32(entityType.Value),
            decimal.ToInt32(flags.Value),
            Vector3.Zero,
            (float)heading.Value,
            (float)scale.Value,
            (float)collisionHeight.Value,
            (float)collisionRadius.Value,
            scriptFile.Text,
            initFunction.Text,
            table.FunctionIndex,
            encounter.Id,
            decimal.ToInt32(unknown1.Value),
            decimal.ToInt32(unknown2.Value),
            decimal.ToInt32(unknown3.Value));
        var request = new FieldMonsterPlacementRequest(
            parameters,
            target.FunctionIndex);
        beginCapture(position => InsertFieldMonster(request, position));

        static void AddRow(TableLayoutPanel owner, int row, string label, Control control)
        {
            owner.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            owner.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, row);
            owner.Controls.Add(control, 1, row);
        }

        static NumericUpDown IntegerEditor(
            int value,
            int minimum = int.MinValue,
            int maximum = int.MaxValue)
            => new()
            {
                Dock = DockStyle.Left,
                Width = 190,
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
            };

        static NumericUpDown FloatEditor(float value)
            => new()
            {
                Dock = DockStyle.Left,
                Width = 190,
                Minimum = -1000000,
                Maximum = 1000000,
                DecimalPlaces = 4,
                Increment = 0.1m,
                Value = (decimal)value,
            };

        static Control Pair(
            Control first,
            Control second,
            string firstLabel,
            string secondLabel)
        {
            first.Dock = DockStyle.Fill;
            second.Dock = DockStyle.Fill;
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ColumnCount = 4,
                RowCount = 1,
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = firstLabel,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 6, 0),
            }, 0, 0);
            panel.Controls.Add(first, 1, 0);
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = secondLabel,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 8, 6, 0),
            }, 2, 0);
            panel.Controls.Add(second, 3, 0);
            return panel;
        }

        static Control ReadOnlyValue(string value)
            => new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Text = value,
            };

        MonsterTableChoice ResolveMonsterChoice(string assetId)
            => monsterChoices.FirstOrDefault(choice =>
                    choice.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase))
                ?? new MonsterTableChoice(
                    assetId,
                    $"Unknown monster ({assetId})",
                    string.Empty);
    }

    private void InsertFieldMonster(FieldMonsterPlacementRequest request, Vector3 position)
    {
        if (document is null || script is null) return;
        var targetFunctionIndex = request.TargetFunctionIndex;
        var targetFunction = script.Functions.FirstOrDefault(value =>
            value.Index == targetFunctionIndex);
        if (targetFunction is null)
            throw new InvalidOperationException("The selected target function is no longer available.");
        var parameters = request.Parameters with { Position = position };
        var insertion = LastExecutableIndex(targetFunction);
        selectedFunctionIndex = targetFunctionIndex;
        RunEdit(() =>
        {
            document.InsertInstruction(targetFunctionIndex, insertion, "Entity_Spawn");
            document.SetInteger(targetFunctionIndex, insertion, 0, parameters.EntityId);
            document.SetString(targetFunctionIndex, insertion, 1, parameters.ModelAsset);
            document.SetString(targetFunctionIndex, insertion, 2, parameters.DisplayName);
            document.SetString(targetFunctionIndex, insertion, 3, parameters.MonsterAsset);
            document.SetInteger(targetFunctionIndex, insertion, 4, parameters.EntityType);
            document.SetInteger(targetFunctionIndex, insertion, 5, parameters.Flags);
            document.SetFloat(targetFunctionIndex, insertion, 6, parameters.Position.X);
            document.SetFloat(targetFunctionIndex, insertion, 7, parameters.Position.Y);
            document.SetFloat(targetFunctionIndex, insertion, 8, parameters.Position.Z);
            document.SetFloat(targetFunctionIndex, insertion, 9, parameters.HeadingDegrees);
            document.SetFloat(targetFunctionIndex, insertion, 10, parameters.Scale);
            document.SetFloat(targetFunctionIndex, insertion, 11, parameters.CollisionHeight);
            document.SetFloat(targetFunctionIndex, insertion, 12, parameters.CollisionRadius);
            document.SetString(targetFunctionIndex, insertion, 13, parameters.ScriptFile);
            document.SetString(targetFunctionIndex, insertion, 14, parameters.InitFunction);
            document.SetInteger(
                targetFunctionIndex, insertion, 15, parameters.BattleFunctionIndex);
            document.SetInteger(
                targetFunctionIndex, insertion, 16, parameters.EncounterIndex);
            document.SetInteger(
                targetFunctionIndex, insertion, 17, parameters.UnknownParameter1);
            document.SetInteger(
                targetFunctionIndex, insertion, 18, parameters.UnknownParameter2);
            document.SetInteger(
                targetFunctionIndex, insertion, 19, parameters.UnknownParameter3);
        }, insertion);
    }

    private sealed record FieldMonsterTableChoice(
        int FunctionIndex,
        string Name,
        CreateMonstersTable Table)
    {
        public override string ToString() => $"#{FunctionIndex} {Name} — {Table.MapAsset}";
    }

    private sealed record EncounterChoice(
        int Index,
        CreateMonstersEncounter Encounter,
        Func<string, string> ResolveMonsterName)
    {
        public override string ToString()
        {
            var monsters = Encounter.MonsterAssets
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => $"{ResolveMonsterName(value)} ({value})");
            return $"Encounter {Encounter.Id} — {string.Join(", ", monsters)}";
        }
    }

    private sealed record EncounterTargetChoice(int FunctionIndex, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record FieldMonsterPlacementRequest(
        FieldMonsterSpawnParameters Parameters,
        int TargetFunctionIndex);

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
        return focused is TextBoxBase or ComboBox or DataGridView;
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
        "dialog" => DescribeDialog(argument.Raw),
        _ => argument.Raw.Length == 0 ? "-" : BitConverter.ToString(argument.Raw).Replace('-', ' '),
    };

    private static string DescribeDialog(byte[] raw)
    {
        var preview = ScriptDialogText.Summarize(raw, DialogEncoding);
        return preview.Length == 0 ? $"[dialogue {raw.Length} o]" : $"“{preview}”";
    }

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
        return ScriptEncoding.GetString(raw, 0, length);
    }

    private static Encoding CreateScriptEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
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
