using System.Globalization;
using System.Text;
using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// Quest table editor plus an exact index of script-side quest mutations.
/// Table fields with unresolved meanings remain visible and round-tripped.
/// </summary>
internal sealed class QuestEditorWindow : Form, IProjectDocumentEditor
{
    private readonly string sourcePath;
    private readonly Cs1TableDocument document;
    private readonly Cs1TableRecordCodec codec;
    private readonly IReadOnlyList<QuestRecord> quests;
    private readonly List<QuestScriptMutation> scriptMutations;
    private readonly CancellationTokenSource corpusIndexCancellation = new();
    private readonly Action<string, bool> onSaving;
    private readonly string? scriptPath;
    private readonly Action openScriptEditor;
    private readonly Action<QuestScriptMutation> navigateToScriptMutation;
    private readonly ListBox questList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox stageList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox title = new() { Dock = DockStyle.Top };
    private readonly TextBox persons = new() { Dock = DockStyle.Top };
    private readonly TextBox stageText = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        AcceptsReturn = true,
        AcceptsTab = true,
    };
    private readonly Label metadata = new() { Dock = DockStyle.Top, Height = 54, AutoEllipsis = true };
    private readonly ListView lifecycleList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        ShowItemToolTips = true,
    };
    private readonly Label lifecycleSummary = new()
    {
        Dock = DockStyle.Top,
        Height = 54,
        AutoEllipsis = true,
    };
    private string? savedPath;

    /// <summary>Whether an applied edit is waiting to be written.</summary>
    private bool applied;
    private QuestRecord? currentQuest;
    private QuestStage? currentStage;

    public QuestEditorWindow(
        string path,
        string? scriptPath,
        IReadOnlyList<QuestScriptSource> scriptSources,
        Action openScriptEditor,
        Action<QuestScriptMutation> navigateToScriptMutation,
        Action<string, bool> onSaving)
    {
        sourcePath = Path.GetFullPath(path);
        this.scriptPath = scriptPath;
        this.openScriptEditor = openScriptEditor
            ?? throw new ArgumentNullException(nameof(openScriptEditor));
        this.navigateToScriptMutation = navigateToScriptMutation
            ?? throw new ArgumentNullException(nameof(navigateToScriptMutation));
        this.onSaving = onSaving ?? throw new ArgumentNullException(nameof(onSaving));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var locale = Path.GetFileName(Path.GetDirectoryName(sourcePath));
        var encoding = string.Equals(locale, "dat", StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false, true);
        codec = new Cs1TableRecordCodec(textEncoding: encoding);
        document = Cs1TableDocument.Read(sourcePath);
        quests = BuildQuestRecords(document, codec);
        var analyzer = new QuestScriptAnalyzer();
        scriptMutations = (scriptSources ?? Array.Empty<QuestScriptSource>())
            .SelectMany(value => analyzer.Analyze(value.Path, value.Script))
            .ToList();
        Text = "Quest editor — t_quest.tbl";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(850, 560);
        BuildUi();
        PopulateQuests();
    }

    public async Task IndexScriptCorpusAsync(
        string directory,
        string? instructionDefinitionsPath)
    {
        lifecycleSummary.Text = $"Indexing quest references in {directory}…";
        QuestScriptCorpusIndex index;
        try
        {
            index = await Task.Run(
                () => new QuestScriptCorpusIndexer().AnalyzeDirectory(
                    directory,
                    instructionDefinitionsPath,
                    corpusIndexCancellation.Token),
                corpusIndexCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (IsDisposed || corpusIndexCancellation.IsCancellationRequested) return;
        var existing = scriptMutations
            .Select(MutationKey)
            .ToHashSet();
        foreach (var mutation in index.Mutations)
        {
            if (existing.Add(MutationKey(mutation))) scriptMutations.Add(mutation);
        }
        if (currentQuest is { } selected) PopulateLifecycle(selected);
        if (index.UnreadableScripts.Count > 0)
        {
            lifecycleSummary.Text += $" {index.UnreadableScripts.Count} unreadable script(s) were skipped.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            corpusIndexCancellation.Cancel();
            corpusIndexCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    private static (string Path, int Function, int Instruction) MutationKey(
        QuestScriptMutation mutation) =>
        (mutation.ScriptPath.ToUpperInvariant(), mutation.FunctionIndex, mutation.InstructionIndex);

    private void BuildUi()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        // Everything, not just this table: the focus should not decide how much of
        // the author's work is written. If the main window is gone, this one alone.
        file.DropDownItems.Add(new ToolStripMenuItem(
            "Save everything unsaved",
            null,
            (_, _) => { if (!ProjectSave.Everything()) Save(false); })
        {
            ShortcutKeys = Keys.Control | Keys.S,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("Save As…", null, (_, _) => Save(true)));
        menu.Items.Add(file);
        MainMenuStrip = menu;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var questDataTab = new TabPage("Quest data") { Padding = new Padding(4) };
        questDataTab.Controls.Add(BuildQuestDataPanel());
        var lifecycleTab = new TabPage("Script lifecycle") { Padding = new Padding(8) };
        lifecycleTab.Controls.Add(BuildLifecyclePanel());
        var integrationTab = new TabPage("Architecture") { Padding = new Padding(8) };
        integrationTab.Controls.Add(BuildIntegrationPanel());
        tabs.TabPages.Add(questDataTab);
        tabs.TabPages.Add(lifecycleTab);
        tabs.TabPages.Add(integrationTab);
        Controls.Add(tabs);
        Controls.Add(menu);
    }

    private Control BuildQuestDataPanel()
    {
        var questGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Quests (QSTitle)" };
        questGroup.Controls.Add(questList);
        var stageGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Journal stages (QSText)" };
        stageGroup.Controls.Add(stageList);
        var left = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        left.Panel1.Controls.Add(questGroup);
        left.Panel2.Controls.Add(stageGroup);

        var apply = new Button { Text = "Apply current fields", Dock = DockStyle.Bottom, Height = 34 };
        var editor = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        editor.Controls.Add(stageText);
        editor.Controls.Add(new Label { Text = "Journal text", Dock = DockStyle.Top, Height = 24 });
        editor.Controls.Add(metadata);
        editor.Controls.Add(persons);
        editor.Controls.Add(new Label { Text = "Requester / persons", Dock = DockStyle.Top, Height = 24 });
        editor.Controls.Add(title);
        editor.Controls.Add(new Label { Text = "Quest title", Dock = DockStyle.Top, Height = 24 });
        editor.Controls.Add(apply);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(editor);
        WinFormsLayout.SetInitialSplitterDistance(split, 390);
        WinFormsLayout.SetInitialSplitterDistance(left, 380);
        questList.SelectedIndexChanged += (_, _) => SelectQuest();
        stageList.SelectedIndexChanged += (_, _) => SelectStage();
        apply.Click += (_, _) => TryApplyCurrent();
        return split;
    }

    private Control BuildLifecyclePanel()
    {
        lifecycleList.Columns.Add("Location", 340);
        lifecycleList.Columns.Add("Operation", 180);
        lifecycleList.Columns.Add("Value", 160);
        lifecycleList.Columns.Add("Evidence", 400);
        lifecycleList.DoubleClick += (_, _) =>
        {
            NavigateToSelectedMutation();
        };
        var goTo = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Text = "Go to selected script block",
        };
        goTo.Click += (_, _) => NavigateToSelectedMutation();
        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Text = "Double-click a reference to open the script graph. Validation conditions, rewards, "
                + "dialogue, encounters and NPC interaction remain graph instructions. This index anchors "
                + "them to verified quest-state operations instead of duplicating the script editor.",
        };
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(lifecycleList);
        panel.Controls.Add(note);
        panel.Controls.Add(goTo);
        panel.Controls.Add(lifecycleSummary);
        return panel;
    }

    private void NavigateToSelectedMutation()
    {
        if (lifecycleList.SelectedItems.Count == 0
            || lifecycleList.SelectedItems[0].Tag is not QuestScriptMutation mutation)
        {
            return;
        }
        navigateToScriptMutation(mutation);
    }

    private Control BuildIntegrationPanel()
    {
        var components = new ListView
        {
            Dock = DockStyle.Top,
            Height = 285,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        components.Columns.Add("Component", 190);
        components.Columns.Add("Current knowledge", 620);
        components.Columns.Add("Status", 190);
        Add("t_quest / QSTitle", "Quest ID, title, requester and preserved unknown flags.", "Editable");
        Add("t_quest / QSText", "Journal stages grouped by the verified quest ID.", "Editable");
        Add("OP103 selector 1", "Publishes a QSText journal-stage index for a quest ID.", "Verified in corpus");
        Add("OP103 selector 3", "Lifecycle flags: value 4 activates/accepts; value 8 completes.", "Verified in corpus");
        Add("OP103 selectors 2/4/5/6", "Indexed without assigning an unverified gameplay meaning.", "Unresolved");
        Add("Validation conditions", "Branches, expressions and flags leading to OP103; owned by the script graph.", "Graph-owned");
        Add("Rewards / inventory", "Separate scenario operations near completion; exact opcode semantics still require verification.", "Research required");
        Add("Quest giver", "Dialogue and interaction function that reaches the activation mutation.", "Graph-owned");
        Add("Encounters", "CreateMonsters plus scenario functions can be linked to the quest flow through graph references.", "Partially implemented");
        Add("t_navi / NaviTextData", "Objective/navigation text and opaque transition bytes.", "Decoded; link unresolved");
        Add("QSRank / QSChapter / QSBook / QSMons", "Decoded records retained as separate sources until their exact relationships are established.", "Research required");

        var openScript = new Button
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = scriptPath is null
                ? "No scenario script is associated with this table"
                : $"Open current script graph — {Path.GetFileName(scriptPath)}",
            Enabled = scriptPath is not null,
        };
        openScript.Click += (_, _) => openScriptEditor();

        var naviGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        naviGrid.Columns.Add("id", "Navi ID");
        naviGrid.Columns.Add("text", "Navigation text");
        naviGrid.Columns.Add("opaque", "Preserved transition bytes");
        PopulateNavigationRows(naviGrid);
        var naviGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "t_navi.tbl — decoded reference data (quest relationship not inferred)",
        };
        naviGroup.Controls.Add(naviGrid);
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(naviGroup);
        panel.Controls.Add(openScript);
        panel.Controls.Add(components);
        return panel;

        void Add(string component, string knowledge, string status)
        {
            var item = new ListViewItem(component);
            item.SubItems.Add(knowledge);
            item.SubItems.Add(status);
            components.Items.Add(item);
        }
    }

    private void PopulateNavigationRows(DataGridView grid)
    {
        var path = Path.Combine(Path.GetDirectoryName(sourcePath)!, "t_navi.tbl");
        if (!File.Exists(path)) return;
        var navi = Cs1TableDocument.Read(path);
        foreach (var entry in navi.Entries.Where(value =>
                     value.Category.Equals("NaviTextData", StringComparison.Ordinal)))
        {
            var values = codec.Decode(entry);
            if (values is null) continue;
            grid.Rows.Add(
                Value(values, "unknown_short"),
                Value(values, "text"),
                Value(values, "unknown_data_2"));
        }
    }

    private void PopulateQuests()
    {
        questList.DataSource = quests;
        questList.DisplayMember = nameof(QuestRecord.Label);
        if (quests.Count > 0) questList.SelectedIndex = 0;
    }

    private void SelectQuest()
    {
        ApplyCurrent();
        if (questList.SelectedItem is not QuestRecord quest) return;
        currentQuest = quest;
        currentStage = null;
        title.Text = Value(quest.TitleFields, "title");
        persons.Text = Value(quest.TitleFields, "persons");
        stageList.DataSource = quest.Stages;
        stageList.DisplayMember = nameof(QuestStage.Label);
        metadata.Text = $"Quest ID {quest.Id}; title flags: "
            + $"byte={Value(quest.TitleFields, "unknown_byte")}, "
            + $"data={Value(quest.TitleFields, "unknown_data")}. "
            + "These fields are preserved without an inferred name.";
        PopulateLifecycle(quest);
        if (quest.Stages.Count > 0) stageList.SelectedIndex = 0;
        else stageText.Clear();
    }

    private void SelectStage()
    {
        ApplyCurrent();
        if (stageList.SelectedItem is not QuestStage stage) return;
        currentStage = stage;
        stageText.Text = Value(stage.Fields, "text");
        metadata.Text = $"Quest ID {stage.QuestId}; QSText state code "
            + $"{Value(stage.Fields, "unknown_byte_1")}; trailing byte "
            + $"{Value(stage.Fields, "unknown_byte_2")}. Unknown codes are preserved.";
    }

    private void PopulateLifecycle(QuestRecord quest)
    {
        lifecycleList.BeginUpdate();
        lifecycleList.Items.Clear();
        var mutations = scriptMutations
            .Where(value => value.QuestId == quest.Id)
            .OrderBy(value => value.ScriptPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.FunctionIndex)
            .ThenBy(value => value.InstructionIndex)
            .ToArray();
        foreach (var mutation in mutations)
        {
            var (operation, value, evidence) = Describe(mutation);
            var item = new ListViewItem(mutation.Location)
            {
                Tag = mutation,
                ToolTipText = mutation.ScriptPath,
            };
            item.SubItems.Add(operation);
            item.SubItems.Add(value);
            item.SubItems.Add(evidence);
            lifecycleList.Items.Add(item);
        }
        lifecycleSummary.Text = mutations.Length == 0
            ? $"Quest {quest.Id}: no OP103 reference was found in the scripts currently indexed."
            : $"Quest {quest.Id}: {mutations.Length} exact OP103 reference(s) in "
                + $"{mutations.Select(value => value.ScriptPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} script(s).";
        lifecycleList.EndUpdate();
    }

    private static (string Operation, string Value, string Evidence) Describe(
        QuestScriptMutation mutation)
    {
        if (mutation.Kind == QuestMutationKind.JournalStage)
            return ("Set journal stage",
                mutation.Value?.ToString(CultureInfo.InvariantCulture) ?? "—",
                "OP103 selector 1; value is the zero-based QSText stage index.");
        if (mutation.Kind == QuestMutationKind.LifecycleFlags)
        {
            var label = mutation.Value switch
            {
                4 => "4 — active/accepted",
                8 => "8 — completed",
                2 => "2 — exact lifecycle name unresolved",
                { } value => value.ToString(CultureInfo.InvariantCulture),
                _ => "—",
            };
            return ("Set lifecycle flags", label,
                "OP103 selector 3. Values 4 and 8 are verified from start/completion flows.");
        }
        return ($"OP103 selector {mutation.Selector}",
            mutation.Value?.ToString(CultureInfo.InvariantCulture) ?? "—",
            "Indexed without assigning an unverified gameplay meaning.");
    }

    private void ApplyCurrent()
    {
        if (currentQuest is null) return;
        var changed = false;
        changed |= SetIfChanged(currentQuest.TitleFields, "title", title.Text);
        changed |= SetIfChanged(currentQuest.TitleFields, "persons", persons.Text);
        if (currentStage is not null)
            changed |= SetIfChanged(currentStage.Fields, "text", stageText.Text);
        if (!changed) return;
        applied = true;
        questList.Refresh();
        stageList.Refresh();
        ShowTitle();
    }

    /// <summary>Whether the table has edits that are not on disk.</summary>
    public bool HasUnsavedChanges => applied || HasPendingFieldEdits();

    /// <summary>Where the table would be written.</summary>
    public string? DocumentPath => savedPath ?? sourcePath;

    public bool SaveWithoutAsking() => Save(saveAs: false);

    /// <summary>
    /// Whether what is typed in the fields differs from the quest it belongs to.
    ///
    /// Compared on demand rather than watched: the same boxes are filled by the
    /// editor itself whenever another quest is picked, so a change event would call
    /// that an edit.
    /// </summary>
    private bool HasPendingFieldEdits()
    {
        if (currentQuest is null) return false;
        if (!Value(currentQuest.TitleFields, "title").Equals(title.Text, StringComparison.Ordinal))
            return true;
        if (!Value(currentQuest.TitleFields, "persons").Equals(persons.Text, StringComparison.Ordinal))
            return true;
        return currentStage is not null
            && !Value(currentStage.Fields, "text").Equals(stageText.Text, StringComparison.Ordinal);
    }

    private void ShowTitle()
        => Text = "Quest editor — t_quest.tbl" + (HasUnsavedChanges ? " *" : string.Empty);

    private bool Save(bool saveAs)
    {
        if (!TryApplyCurrent()) return false;
        // Over the file it was read from: the project took a pristine copy through
        // onSaving, and "t_quest.edited.tbl" is a name the game does not read.
        var target = saveAs ? null : savedPath ?? sourcePath;
        if (string.IsNullOrEmpty(target))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save quest table",
                Filter = "Cold Steel tables (*.tbl)|*.tbl|All files|*.*",
                InitialDirectory = Path.GetDirectoryName(sourcePath),
                FileName = Path.GetFileName(sourcePath),
                DefaultExt = "tbl",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            target = dialog.FileName;
        }
        try
        {
            foreach (var quest in quests)
            {
                quest.TitleEntry.Data = codec.Encode("QSTitle", quest.TitleFields);
                foreach (var stage in quest.Stages)
                    stage.Entry.Data = codec.Encode("QSText", stage.Fields);
            }
            onSaving(target!, true);
            document.Write(target!);
            onSaving(target!, false);
            savedPath = target;
            applied = false;
            Text = $"Quest editor — {Path.GetFileName(target)}";
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or InvalidDataException
            or FormatException or OverflowException)
        {
            MessageBox.Show(this, exception.Message, "Cannot save quest table",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool TryApplyCurrent()
    {
        try
        {
            ApplyCurrent();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or FormatException or OverflowException or EncoderFallbackException)
        {
            MessageBox.Show(this, exception.Message, "Cannot apply quest fields",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static IReadOnlyList<QuestRecord> BuildQuestRecords(
        Cs1TableDocument document,
        Cs1TableRecordCodec codec)
    {
        var stages = document.Entries
            .Where(value => value.Category.Equals("QSText", StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Fields: codec.Decode(entry)!.ToList()))
            .GroupBy(value => int.Parse(Value(value.Fields, "id"), CultureInfo.InvariantCulture))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<QuestStage>)group.Select((value, index) =>
                    new QuestStage(group.Key, index, value.Entry, value.Fields)).ToArray());
        return document.Entries
            .Where(value => value.Category.Equals("QSTitle", StringComparison.Ordinal))
            .Select(entry =>
            {
                var fields = codec.Decode(entry)!.ToList();
                var id = int.Parse(Value(fields, "unknown_short"), CultureInfo.InvariantCulture);
                return new QuestRecord(
                    id,
                    entry,
                    fields,
                    stages.GetValueOrDefault(id) ?? Array.Empty<QuestStage>());
            })
            .ToArray();
    }

    private static string Value(IReadOnlyList<Cs1TableFieldValue> values, string name)
        => values.First(value => value.Field.Name.Equals(name, StringComparison.Ordinal)).Value;

    private static void Set(IReadOnlyList<Cs1TableFieldValue> values, string name, string value)
    {
        var index = values.ToList().FindIndex(field =>
            field.Field.Name.Equals(name, StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException($"Field '{name}' is absent.");
        ((List<Cs1TableFieldValue>)values)[index] = values[index] with { Value = value };
    }

    private static bool SetIfChanged(
        IReadOnlyList<Cs1TableFieldValue> values,
        string name,
        string value)
    {
        if (Value(values, name).Equals(value, StringComparison.Ordinal)) return false;
        Set(values, name, value);
        return true;
    }

    private sealed record QuestRecord(
        int Id,
        Cs1TableEntry TitleEntry,
        List<Cs1TableFieldValue> TitleFields,
        IReadOnlyList<QuestStage> Stages)
    {
        public string Label => $"{Id}: {Value(TitleFields, "title")} ({Stages.Count} stages)";
    }

    private sealed record QuestStage(
        int QuestId,
        int Index,
        Cs1TableEntry Entry,
        List<Cs1TableFieldValue> Fields)
    {
        public string Label
        {
            get
            {
                var text = Value(Fields, "text").Replace("\r", " ").Replace("\n", " ");
                if (text.Length > 70) text = text[..70] + "…";
                return $"{Index}: code {Value(Fields, "unknown_byte_1")} — {text}";
            }
        }
    }
}
