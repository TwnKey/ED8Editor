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

    public QuestEditorWindow(string path, Action<string, bool> onSaving)
    {
        sourcePath = Path.GetFullPath(path);
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
            SplitterDistance = 380,
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
            SplitterDistance = 390,
            Panel1MinSize = 260,
            Panel2MinSize = 420,
        };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(editor);
        Controls.Add(split);
        Controls.Add(menu);
        questList.SelectedIndexChanged += (_, _) => SelectQuest();
        stageList.SelectedIndexChanged += (_, _) => SelectStage();
        apply.Click += (_, _) => TryApplyCurrent();
    }

    private void PopulateQuests()
    {
        questList.DataSource = quests;
        questList.DisplayMember = nameof(QuestRecord.Label);
        if (quests.Count > 0) questList.SelectedIndex = 0;
    }

    private void SelectQuest()
    {
        if (questList.SelectedItem is not QuestRecord quest) return;
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
        if (stageList.SelectedItem is not QuestStage stage) return;
        stageText.Text = Value(stage.Fields, "text");
        metadata.Text = $"Quest ID {stage.QuestId}; QSText state code "
            + $"{Value(stage.Fields, "unknown_byte_1")}; trailing byte "
            + $"{Value(stage.Fields, "unknown_byte_2")}. Unknown codes are preserved.";
    }

    private void ApplyCurrent()
    {
        if (questList.SelectedItem is not QuestRecord quest) return;
        Set(quest.TitleFields, "title", title.Text);
        Set(quest.TitleFields, "persons", persons.Text);
        quest.TitleEntry.Data = codec.Encode("QSTitle", quest.TitleFields);
        if (stageList.SelectedItem is QuestStage stage)
        {
            Set(stage.Fields, "text", stageText.Text);
            stage.Entry.Data = codec.Encode("QSText", stage.Fields);
        }
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
