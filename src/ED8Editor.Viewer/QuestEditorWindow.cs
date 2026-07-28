using System.Globalization;
using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// Focused editor for the verified relationship in t_quest.tbl:
/// QSTitle.unknown_short is the quest ID and groups every QSText.id stage.
/// Unknown flags remain untouched and visible instead of being assigned guessed
/// gameplay meanings.
/// </summary>
internal sealed class QuestEditorWindow : Form
{
    private readonly string sourcePath;
    private readonly Cs1TableDocument document;
    private readonly Cs1TableRecordCodec codec;
    private readonly IReadOnlyList<QuestRecord> quests;
    private readonly Action<string, bool> onSaving;
    private readonly string? scriptPath;
    private readonly Action openScriptEditor;
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
    private string? savedPath;
    private QuestRecord? currentQuest;
    private QuestStage? currentStage;

    public QuestEditorWindow(
        string path,
        string? scriptPath,
        Action openScriptEditor,
        Action<string, bool> onSaving)
    {
        sourcePath = Path.GetFullPath(path);
        this.scriptPath = scriptPath;
        this.openScriptEditor =
            openScriptEditor ?? throw new ArgumentNullException(nameof(openScriptEditor));
        this.onSaving = onSaving ?? throw new ArgumentNullException(nameof(onSaving));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var locale = Path.GetFileName(Path.GetDirectoryName(sourcePath));
        var encoding = string.Equals(locale, "dat", StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false, true);
        codec = new Cs1TableRecordCodec(textEncoding: encoding);
        document = Cs1TableDocument.Read(sourcePath);
        quests = BuildQuestRecords(document, codec);
        Text = "Quest editor — t_quest.tbl";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(850, 560);
        BuildUi();
        PopulateQuests();
    }

    private void BuildUi()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        file.DropDownItems.Add(new ToolStripMenuItem("Save", null, (_, _) => Save(false))
        {
            ShortcutKeys = Keys.Control | Keys.S,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("Save As…", null, (_, _) => Save(true)));
        menu.Items.Add(file);
        MainMenuStrip = menu;

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

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
        };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(editor);
        var questDataTab = new TabPage("Quest data") { Padding = new Padding(4) };
        questDataTab.Controls.Add(split);
        var integrationTab = new TabPage("Quest integration") { Padding = new Padding(8) };
        integrationTab.Controls.Add(BuildIntegrationPanel());
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(questDataTab);
        tabs.TabPages.Add(integrationTab);
        Controls.Add(tabs);
        Controls.Add(menu);
        WinFormsLayout.SetInitialSplitterDistance(split, 390);
        WinFormsLayout.SetInitialSplitterDistance(left, 380);
        questList.SelectedIndexChanged += (_, _) => SelectQuest();
        stageList.SelectedIndexChanged += (_, _) => SelectStage();
        apply.Click += (_, _) => TryApplyCurrent();
    }

    private Control BuildIntegrationPanel()
    {
        var components = new ListView
        {
            Dock = DockStyle.Top,
            Height = 245,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        components.Columns.Add("Component", 190);
        components.Columns.Add("Current knowledge", 500);
        components.Columns.Add("Status", 170);
        Add("t_quest / QSTitle", "Quest ID, title, requester and preserved unknown flags.", "Editable");
        Add("t_quest / QSText", "Journal stages grouped by the verified quest ID.", "Editable");
        Add("t_quest / QSRank + QSChapter", "Global rank/chapter lookup data; exact quest lifecycle role not established.", "Decoded");
        Add("t_navi / NaviTextData", "Objective/navigation text and opaque transition bytes.", "Decoded; link unresolved");
        Add("Scenario script", "Start, progress and completion logic must reuse the existing script graph.", "Open current script");
        Add("Quest state transitions", "Opcodes/flags that publish journal stages still require corpus verification.", "Research required");
        Add("Rewards and inventory", "Likely executed by scenario instructions; not owned by QSTitle/QSText.", "Research required");
        Add("Map markers / destinations", "Potential script, OPS and place-table references; no relation is assumed.", "Research required");
        Add("Dialogue / cutscenes", "Calls and branches remain authored in the existing script editor.", "Reuse script graph");

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
        var path = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "t_navi.tbl");
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

    private void ApplyCurrent()
    {
        if (currentQuest is null) return;
        var changed = false;
        changed |= SetIfChanged(currentQuest.TitleFields, "title", title.Text);
        changed |= SetIfChanged(currentQuest.TitleFields, "persons", persons.Text);
        if (currentStage is not null)
        {
            changed |= SetIfChanged(currentStage.Fields, "text", stageText.Text);
        }
        if (!changed) return;
        questList.Refresh();
        stageList.Refresh();
        Text = "Quest editor — t_quest.tbl *";
    }

    private void Save(bool saveAs)
    {
        if (!TryApplyCurrent()) return;
        var target = saveAs ? null : savedPath;
        if (string.IsNullOrEmpty(target))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save quest table",
                Filter = "Cold Steel tables (*.tbl)|*.tbl|All files|*.*",
                InitialDirectory = Path.GetDirectoryName(sourcePath),
                FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}.edited.tbl",
                DefaultExt = "tbl",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
            Text = $"Quest editor — {Path.GetFileName(target)}";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or InvalidDataException
            or FormatException or OverflowException)
        {
            MessageBox.Show(this, exception.Message, "Cannot save quest table",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        // The codec values are immutable records; this list is always the mutable
        // List created below.
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
