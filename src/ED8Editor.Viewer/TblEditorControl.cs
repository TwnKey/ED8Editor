using System.Globalization;
using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

public sealed class TblEditorControl : UserControl
{
    private readonly string textRoot;
    private readonly ComboBox localeList = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly TextBox filter = new() { PlaceholderText = "Filter .tbl files…", Dock = DockStyle.Top };
    private readonly ListBox files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox categoryList = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox entries = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox categoryField = new() { Width = 150 };
    private readonly TextBox payloadField = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        AcceptsReturn = true,
        AcceptsTab = true,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    private readonly DataGridView fieldsGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly TabControl entryEditorTabs = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, AutoEllipsis = true };
    private readonly Button saveButton = new() { Text = "Save", AutoSize = true, Enabled = false };
    private readonly Button saveAsButton = new() { Text = "Save As…", AutoSize = true, Enabled = false };
    private readonly Button applyButton = new() { Text = "Apply entry", AutoSize = true, Enabled = false };
    private readonly Button duplicateButton = new() { Text = "Duplicate", AutoSize = true, Enabled = false };
    private readonly Button deleteButton = new() { Text = "Delete", AutoSize = true, Enabled = false };
    private Cs1TableDocument? document;
    private bool dirty;
    private bool refreshing;
    private readonly Cs1TableRecordCodec recordCodec = new();
    private IReadOnlyList<Cs1TableFieldValue>? decodedFields;

    public TblEditorControl(string gameDataPath, string? scriptPath)
    {
        textRoot = Path.Combine(gameDataPath, "text");
        BuildUi();
        // tbled's CS1 schema target is explicitly the XSeed PC English variant.
        localeList.Items.AddRange(Directory.Exists(textRoot)
            ? Directory.GetDirectories(textRoot, "dat_us").Select(Path.GetFileName).Cast<object>().ToArray()
            : Array.Empty<object>());
        var preferred = PathContainsSegment(scriptPath, "dat_us") ? "dat_us" : "dat";
        localeList.SelectedItem = localeList.Items.Cast<string>().FirstOrDefault(value =>
            value.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (localeList.SelectedIndex < 0 && localeList.Items.Count > 0) localeList.SelectedIndex = 0;
        RefreshFiles();
    }

    public event EventHandler? CatalogChanged;

    public string? CurrentDirectory => localeList.SelectedItem is string locale
        ? Path.Combine(textRoot, locale)
        : null;

    public void SaveCurrent(bool saveAs = false) => Save(saveAs);

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 35, WrapContents = false, Padding = new Padding(3) };
        tools.Controls.Add(new Label { Text = "Language:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        tools.Controls.Add(localeList);
        tools.Controls.Add(saveButton);
        tools.Controls.Add(saveAsButton);

        var filePanel = new Panel { Dock = DockStyle.Fill };
        filePanel.Controls.Add(files);
        filePanel.Controls.Add(filter);
        var fileGroup = new GroupBox { Dock = DockStyle.Fill, Text = "TBL files" };
        fileGroup.Controls.Add(filePanel);

        var entryPanel = new Panel { Dock = DockStyle.Fill };
        entryPanel.Controls.Add(entries);
        entryPanel.Controls.Add(categoryList);
        var entryGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Entries by category" };
        entryGroup.Controls.Add(entryPanel);

        var navigation = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 250 };
        navigation.Panel1.Controls.Add(fileGroup);
        navigation.Panel2.Controls.Add(entryGroup);

        var entryTools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false, Padding = new Padding(3) };
        entryTools.Controls.Add(new Label { Text = "Category:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        entryTools.Controls.Add(categoryField);
        entryTools.Controls.Add(applyButton);
        entryTools.Controls.Add(duplicateButton);
        entryTools.Controls.Add(deleteButton);
        fieldsGrid.Columns.Add("Field", "Field");
        fieldsGrid.Columns.Add("Type", "Type");
        fieldsGrid.Columns.Add("Value", "Value");
        fieldsGrid.Columns[0].ReadOnly = true;
        fieldsGrid.Columns[1].ReadOnly = true;
        fieldsGrid.Columns[0].FillWeight = 32;
        fieldsGrid.Columns[1].FillWeight = 18;
        fieldsGrid.Columns[2].FillWeight = 50;
        var fieldsTab = new TabPage("Typed fields");
        fieldsTab.Controls.Add(fieldsGrid);
        var rawTab = new TabPage("Raw payload");
        rawTab.Controls.Add(payloadField);
        entryEditorTabs.TabPages.Add(fieldsTab);
        entryEditorTabs.TabPages.Add(rawTab);
        var payloadGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Selected entry" };
        payloadGroup.Controls.Add(entryEditorTabs);
        payloadGroup.Controls.Add(entryTools);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 150, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(navigation);
        split.Panel2.Controls.Add(payloadGroup);
        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(tools);

        localeList.SelectedIndexChanged += (_, _) => { if (!refreshing) SwitchDirectory(); };
        filter.TextChanged += (_, _) => RefreshFiles();
        files.SelectedIndexChanged += (_, _) => { if (!refreshing) OpenSelectedFile(); };
        categoryList.SelectedIndexChanged += (_, _) => RefreshEntries();
        entries.SelectedIndexChanged += (_, _) => ShowSelectedEntry();
        applyButton.Click += (_, _) => ApplyEntry();
        duplicateButton.Click += (_, _) => DuplicateEntry();
        deleteButton.Click += (_, _) => DeleteEntry();
        saveButton.Click += (_, _) => Save(saveAs: false);
        saveAsButton.Click += (_, _) => Save(saveAs: true);
    }

    private void SwitchDirectory()
    {
        if (!ConfirmDiscard()) return;
        document = null;
        dirty = false;
        RefreshFiles();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshFiles()
    {
        var selected = files.SelectedItem?.ToString();
        var directory = CurrentDirectory;
        var query = filter.Text.Trim();
        var paths = directory is not null && Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.tbl").Where(path => string.IsNullOrEmpty(query)
                || Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        refreshing = true;
        files.Items.Clear();
        files.Items.AddRange(paths.Select(Path.GetFileName).Cast<object>().ToArray());
        if (selected is not null) files.SelectedItem = selected;
        refreshing = false;
        status.Text = directory is null ? "No game text directory found." : $"{paths.Length} TBL files — {directory}";
    }

    private void OpenSelectedFile()
    {
        if (files.SelectedItem is not string name || CurrentDirectory is not { } directory) return;
        if (!ConfirmDiscard()) return;
        try
        {
            document = Cs1TableDocument.Read(Path.Combine(directory, name));
            dirty = false;
            saveButton.Enabled = saveAsButton.Enabled = true;
            RefreshCategories();
            status.Text = $"{name}: {document.Entries.Count} entries";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or DecoderFallbackException)
        {
            MessageBox.Show(exception.Message, "Cannot open TBL", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshCategories()
    {
        var selected = categoryList.SelectedItem?.ToString();
        categoryList.Items.Clear();
        if (document is null) return;
        categoryList.Items.Add("All categories");
        categoryList.Items.AddRange(document.Entries.Select(value => value.Category).Distinct(StringComparer.Ordinal).Order().Cast<object>().ToArray());
        categoryList.SelectedItem = selected is not null && categoryList.Items.Contains(selected) ? selected : "All categories";
    }

    private void RefreshEntries()
    {
        entries.Items.Clear();
        if (document is null) return;
        var category = categoryList.SelectedItem?.ToString();
        var visible = document.Entries.Select((entry, index) => new EntryChoice(index, entry))
            .Where(value => category == "All categories" || value.Entry.Category == category).ToArray();
        entries.Items.AddRange(visible.Cast<object>().ToArray());
        if (entries.Items.Count > 0) entries.SelectedIndex = 0;
    }

    private void ShowSelectedEntry()
    {
        var enabled = entries.SelectedItem is EntryChoice;
        applyButton.Enabled = duplicateButton.Enabled = deleteButton.Enabled = enabled;
        if (entries.SelectedItem is not EntryChoice choice) return;
        categoryField.Text = choice.Entry.Category;
        payloadField.Text = Convert.ToHexString(choice.Entry.Data);
        fieldsGrid.Rows.Clear();
        decodedFields = null;
        try
        {
            decodedFields = recordCodec.Decode(choice.Entry);
            if (decodedFields is null)
            {
                entryEditorTabs.SelectedIndex = 1;
                entryEditorTabs.TabPages[0].Text = "Typed fields (no schema)";
                return;
            }
            entryEditorTabs.TabPages[0].Text = "Typed fields";
            foreach (var value in decodedFields)
                fieldsGrid.Rows.Add(value.Field.Name, DescribeType(value.Field), value.Value);
            entryEditorTabs.SelectedIndex = 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or DecoderFallbackException)
        {
            entryEditorTabs.TabPages[0].Text = "Typed fields (schema mismatch)";
            entryEditorTabs.SelectedIndex = 1;
            status.Text = exception.Message;
        }
    }

    private void ApplyEntry()
    {
        if (entries.SelectedItem is not EntryChoice choice) return;
        try
        {
            var category = categoryField.Text.Trim();
            if (string.IsNullOrEmpty(category) || category.IndexOf('\0') >= 0)
                throw new ArgumentException("A valid category is required.");
            var hex = new string(payloadField.Text.Where(value => !char.IsWhiteSpace(value)).ToArray());
            byte[] data;
            if (decodedFields is not null && category == choice.Entry.Category && entryEditorTabs.SelectedIndex == 0)
            {
                var values = decodedFields.Select((value, index) => value with
                {
                    Value = fieldsGrid.Rows[index].Cells[2].Value?.ToString() ?? string.Empty,
                }).ToArray();
                data = recordCodec.Encode(category, values);
            }
            else
            {
                data = Convert.FromHexString(hex);
            }
            choice.Entry.Category = category;
            choice.Entry.Data = data;
            SetDirty();
            RefreshCategories();
            status.Text = $"Entry #{choice.DocumentIndex} updated (not saved).";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            MessageBox.Show(exception.Message, "Invalid TBL entry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DuplicateEntry()
    {
        if (document is null || entries.SelectedItem is not EntryChoice choice) return;
        document.Entries.Insert(choice.DocumentIndex + 1,
            new Cs1TableEntry(choice.Entry.Category, choice.Entry.Data.ToArray()));
        SetDirty();
        RefreshCategories();
    }

    private void DeleteEntry()
    {
        if (document is null || entries.SelectedItem is not EntryChoice choice) return;
        document.Entries.RemoveAt(choice.DocumentIndex);
        SetDirty();
        RefreshCategories();
    }

    private void Save(bool saveAs)
    {
        if (document is null) return;
        var path = document.SourcePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Cold Steel tables (*.tbl)|*.tbl|All files (*.*)|*.*",
                InitialDirectory = CurrentDirectory,
                FileName = Path.GetFileName(path),
                AddExtension = true,
                DefaultExt = "tbl",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }
        try
        {
            document.Write(path!);
            dirty = false;
            status.Text = $"Saved {path}";
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Cannot save TBL", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetDirty()
    {
        dirty = true;
        status.Text = "Modified — save to write the TBL.";
    }

    private bool ConfirmDiscard() => !dirty || MessageBox.Show(this,
        "Discard unsaved TBL changes?", "Unsaved TBL", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning) == DialogResult.Yes;

    private static bool PathContainsSegment(string? path, string segment) => path is not null
        && Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => value.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static string DescribeType(Cs1TableAtomicField field) => field.Type == "bytes"
        ? $"bytes[{field.Size}]"
        : field.Type;

    private sealed record EntryChoice(int DocumentIndex, Cs1TableEntry Entry)
    {
        public override string ToString() => $"#{DocumentIndex} — {Entry.Category} ({Entry.Data.Length} bytes)";
    }
}
