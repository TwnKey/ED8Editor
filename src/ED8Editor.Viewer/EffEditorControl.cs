using System.Globalization;
using ED8Editor.Core;

namespace ED8Editor.Viewer;

/// <summary>What the viewport should show while an effect is being edited.</summary>
public sealed record EffPreviewRequest(EffFile? Effect, float Seconds);

/// <summary>
/// The effect editor: the .eff files the game ships, the segments each one
/// spawns, and the keyframe tracks that move them. Everything is edited on the
/// parsed file and written back through the same writer that round-trips the
/// whole corpus, so a save only changes what was actually edited.
/// </summary>
public sealed class EffEditorControl : UserControl
{
    private readonly string gameDataPath;
    private readonly string effectRoot;
    private readonly TextBox filter = new() { PlaceholderText = "Filter effects…", Dock = DockStyle.Top };
    private readonly ListBox files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TreeView segments = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ComboBox trackList = new()
    {
        Dock = DockStyle.Top,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly DataGridView keyframes = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly DataGridView fields = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly TabControl segmentTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage tracksTab = new("Tracks");
    private readonly TabPage fieldsTab = new("Segment");
    private readonly TrackBar time = new()
    {
        Dock = DockStyle.Fill,
        Minimum = 0,
        Maximum = 300,
        TickFrequency = 20,
        SmallChange = 1,
        LargeChange = 10,
    };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, AutoEllipsis = true };
    private readonly Button saveButton = new() { Text = "Save", AutoSize = true, Enabled = false };
    private readonly Button saveAsButton = new() { Text = "Save As…", AutoSize = true, Enabled = false };
    private readonly Button importTextureButton = new()
    {
        Text = "Import texture…",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button newEffectButton = new() { Text = "New effect", AutoSize = true };
    private readonly Button newFromButton = new() { Text = "New from…", AutoSize = true };
    private readonly Button addBlankSegmentButton = new()
    {
        Text = "Add blank segment",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button addSegmentButton = new()
    {
        Text = "Add segment",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button removeSegmentButton = new()
    {
        Text = "Remove segment",
        AutoSize = true,
        Enabled = false,
    };
    private readonly ComboBox parentList = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
        Enabled = false,
    };
    private readonly CheckBox preview = new() { Text = "Preview", AutoSize = true, Checked = true };
    private readonly Label timeLabel = new() { AutoSize = true, Text = "0.00 s" };

    private EffFile? effect;
    private string? effectPath;
    private bool dirty;
    private bool refreshing;

    /// <summary>Raised before and after a save, so the mod project can track it.</summary>
    public event EventHandler<EffSaveEventArgs>? Saving;

    /// <summary>Raised once an imported texture package has been written.</summary>
    public event EventHandler<EffSaveEventArgs>? TextureImported;

    /// <summary>Raised whenever the viewport should show a different frame.</summary>
    public event EventHandler<EffPreviewRequest>? PreviewChanged;

    public EffEditorControl(string gameDataPath)
    {
        this.gameDataPath = gameDataPath;
        effectRoot = Path.Combine(gameDataPath, "effects");
        BuildUi();
        RefreshFiles();
    }

    /// <summary>The seconds of playback the preview slider is asking for.</summary>
    public float PreviewSeconds => time.Value / 20f;

    public bool SaveCurrent(bool saveAs = false) => Save(saveAs);

    /// <summary>Whether the open effect has edits that are not on disk.</summary>
    public bool HasUnsavedChanges => dirty && effect is not null;

    /// <summary>Where the open effect would be written.</summary>
    public string? DocumentPath => string.IsNullOrEmpty(effectPath) ? null : effectPath;

    /// <summary>Stops the preview, for when the window is closed.</summary>
    public void StopPreview() => PreviewChanged?.Invoke(this, new EffPreviewRequest(null, 0f));

    /// <summary>Raised when the reader picks another segment.</summary>
    public event EventHandler? SegmentSelected;

    /// <summary>The segment being edited, for the window's texture view.</summary>
    public EffSegment? CurrentSegment => SelectedSegment;

    /// <summary>
    /// Sets the piece of its texture the selected segment draws. The crop is
    /// stored as plain texture coordinates, so a rectangle dragged over the
    /// texture writes straight into the segment.
    /// </summary>
    public void SetCrop(float left, float top, float right, float bottom)
    {
        if (SelectedSegment is not { } segment) return;
        segment.Data04[0] = left;
        segment.Data04[1] = top;
        segment.Data04[2] = right;
        segment.Data04[3] = bottom;
        SetDirty();
        RefreshFields();
        RaisePreview();
    }

    private void BuildUi()
    {
        var browser = new Panel { Dock = DockStyle.Fill };
        browser.Controls.Add(files);
        browser.Controls.Add(filter);

        var editor = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        editor.Panel1.Controls.Add(segments);
        segmentTabs.TabPages.Add(tracksTab);
        segmentTabs.TabPages.Add(fieldsTab);
        var tracks = new Panel { Dock = DockStyle.Fill };
        tracks.Controls.Add(keyframes);
        tracks.Controls.Add(trackList);
        tracksTab.Controls.Add(tracks);
        fieldsTab.Controls.Add(fields);
        editor.Panel2.Controls.Add(segmentTabs);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        split.Panel1.Controls.Add(browser);
        split.Panel2.Controls.Add(editor);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        bar.Controls.Add(newEffectButton);
        bar.Controls.Add(newFromButton);
        bar.Controls.Add(saveButton);
        bar.Controls.Add(saveAsButton);
        bar.Controls.Add(importTextureButton);
        bar.Controls.Add(addSegmentButton);
        bar.Controls.Add(addBlankSegmentButton);
        bar.Controls.Add(removeSegmentButton);
        bar.Controls.Add(new Label { Text = "Spawned by:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        bar.Controls.Add(parentList);
        bar.Controls.Add(preview);
        bar.Controls.Add(timeLabel);

        var timeRow = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        timeRow.Controls.Add(time);

        Controls.Add(split);
        Controls.Add(timeRow);
        Controls.Add(bar);
        Controls.Add(status);
        Dock = DockStyle.Fill;
        WinFormsLayout.SetInitialSplitterDistance(editor, 240);
        WinFormsLayout.SetInitialSplitterDistance(split, 220);

        filter.TextChanged += (_, _) => RefreshFiles();
        files.SelectedIndexChanged += (_, _) => OpenSelectedFile();
        segments.AfterSelect += (_, _) => RefreshSegment();
        trackList.SelectedIndexChanged += (_, _) => RefreshKeyframes();
        keyframes.CellEndEdit += (_, eventArgs) => ApplyKeyframeEdit(eventArgs.RowIndex, eventArgs.ColumnIndex);
        fields.CellEndEdit += (_, eventArgs) => ApplyFieldEdit(eventArgs.RowIndex, eventArgs.ColumnIndex);
        newEffectButton.Click += (_, _) => NewEffect();
        newFromButton.Click += (_, _) => NewEffectFromTemplate();
        addSegmentButton.Click += (_, _) => AddSegment(blank: false);
        addBlankSegmentButton.Click += (_, _) => AddSegment(blank: true);
        removeSegmentButton.Click += (_, _) => RemoveSegment();
        parentList.SelectedIndexChanged += (_, _) => ApplyParentChange();
        importTextureButton.Click += (_, _) => ImportTexture();
        saveButton.Click += (_, _) => Save(saveAs: false);
        saveAsButton.Click += (_, _) => Save(saveAs: true);
        preview.CheckedChanged += (_, _) => RaisePreview();
        time.ValueChanged += (_, _) =>
        {
            timeLabel.Text = $"{PreviewSeconds:0.00} s";
            RaisePreview();
        };

        keyframes.Columns.AddRange(
            TextColumn("Time"), TextColumn("Mode"),
            TextColumn("X"), TextColumn("Y"), TextColumn("Z"), TextColumn("W"),
            TextColumn("X2"), TextColumn("Y2"), TextColumn("Z2"), TextColumn("W2"));
        fields.Columns.AddRange(TextColumn("Field", readOnly: true), TextColumn("Value"));
    }

    private static DataGridViewTextBoxColumn TextColumn(string name, bool readOnly = false)
        => new() { HeaderText = name, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };

    private void RefreshFiles()
    {
        files.BeginUpdate();
        files.Items.Clear();
        if (Directory.Exists(effectRoot))
        {
            var pattern = filter.Text.Trim();
            foreach (var path in Directory.EnumerateFiles(effectRoot, "*.eff", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(effectRoot, path).Replace('\\', '/');
                if (pattern.Length > 0
                    && relative.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                files.Items.Add(relative);
            }
        }
        files.EndUpdate();
        status.Text = files.Items.Count == 0
            ? $"No effect found under {effectRoot}."
            : $"{files.Items.Count} effects.";
    }

    private void OpenSelectedFile()
    {
        if (files.SelectedItem is not string relative) return;
        if (dirty && MessageBox.Show(
                "The current effect has unsaved changes. Discard them?",
                "Effect editor",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        var path = Path.Combine(effectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            effect = EffFileReader.Read(path);
            effectPath = path;
            dirty = false;
            saveButton.Enabled = true;
            saveAsButton.Enabled = true;
            importTextureButton.Enabled = true;
            addSegmentButton.Enabled = true;
            addBlankSegmentButton.Enabled = true;
            status.Text = $"{relative} — version {EffGameVersion.Describe(effect.Version)},"
                + $" {effect.Segments.Count} segments, {effect.Textures.Count} textures";
            RefreshSegmentTree();
            RaisePreview();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            effect = null;
            effectPath = null;
            saveButton.Enabled = false;
            saveAsButton.Enabled = false;
            segments.Nodes.Clear();
            status.Text = $"Cannot read {relative}: {exception.Message}";
        }
    }

    /// <summary>
    /// The segments, as the spawn descriptors chain them: a segment nobody fires
    /// is a root the engine starts on its own.
    /// </summary>
    private void RefreshSegmentTree()
    {
        segments.BeginUpdate();
        segments.Nodes.Clear();
        if (effect is not null)
        {
            var spawned = new HashSet<int>();
            foreach (var segment in effect.Segments)
            {
                foreach (var spawn in EffSimulation.ReadSpawns(segment, effect.Segments.Count))
                {
                    spawned.Add(spawn.SegmentIndex);
                }
            }
            for (var index = 0; index < effect.Segments.Count; index++)
            {
                if (spawned.Contains(index)) continue;
                segments.Nodes.Add(BuildSegmentNode(index, new HashSet<int>()));
            }
            // A cycle in the spawn chain would otherwise hide a segment entirely.
            for (var index = 0; index < effect.Segments.Count; index++)
            {
                if (!spawned.Contains(index) || FindNode(segments.Nodes, index) is not null) continue;
                segments.Nodes.Add(BuildSegmentNode(index, new HashSet<int>()));
            }
        }
        segments.ExpandAll();
        segments.EndUpdate();
        if (segments.Nodes.Count > 0) segments.SelectedNode = segments.Nodes[0];
        RefreshSegment();
    }

    private TreeNode BuildSegmentNode(int index, ISet<int> visiting)
    {
        var segment = effect!.Segments[index];
        var label = segment.Name.Length > 0 ? segment.Name : $"segment {index}";
        var node = new TreeNode($"#{index} {label}") { Tag = index };
        if (!visiting.Add(index)) return node;
        foreach (var spawn in EffSimulation.ReadSpawns(segment, effect.Segments.Count))
        {
            if (spawn.SegmentIndex == index) continue;
            node.Nodes.Add(BuildSegmentNode(spawn.SegmentIndex, visiting));
        }
        visiting.Remove(index);
        return node;
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, int index)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is int value && value == index) return node;
            if (FindNode(node.Nodes, index) is { } found) return found;
        }
        return null;
    }

    private EffSegment? SelectedSegment
        => effect is not null && segments.SelectedNode?.Tag is int index && index < effect.Segments.Count
            ? effect.Segments[index]
            : null;

    private void RefreshSegment()
    {
        refreshing = true;
        trackList.Items.Clear();
        if (SelectedSegment is not null)
        {
            trackList.Items.AddRange(new object[]
            {
                "Position", "Rotation", "Scale", "Rotation 2", "Colour multiply", "Colour add", "Spawns",
            });
            trackList.SelectedIndex = 0;
        }
        refreshing = false;
        RefreshKeyframes();
        RefreshFields();
        RefreshParents(segments.SelectedNode?.Tag as int?);
        SegmentSelected?.Invoke(this, EventArgs.Empty);
    }

    private List<EffKeyframe>? SelectedTrack => SelectedSegment is not { } segment
        ? null
        : trackList.SelectedIndex switch
        {
            0 => segment.Position,
            1 => segment.Rotation,
            2 => segment.Scale,
            3 => segment.Rotation2,
            4 => segment.ColorMultiply,
            5 => segment.ColorAdd,
            6 => segment.Children,
            _ => null,
        };

    private void RefreshKeyframes()
    {
        refreshing = true;
        keyframes.Rows.Clear();
        if (SelectedTrack is { } track)
        {
            foreach (var keyframe in track)
            {
                keyframes.Rows.Add(
                    Format(keyframe.Time),
                    $"0x{keyframe.Flags:X4}",
                    Format(keyframe.Floats[0]), Format(keyframe.Floats[1]),
                    Format(keyframe.Floats[2]), Format(keyframe.Floats[3]),
                    Format(keyframe.Floats[4]), Format(keyframe.Floats[5]),
                    Format(keyframe.Floats[6]), Format(keyframe.Floats[7]));
            }
        }
        refreshing = false;
    }

    private void RefreshFields()
    {
        refreshing = true;
        fields.Rows.Clear();
        if (SelectedSegment is { } segment)
        {
            fields.Rows.Add("Name", segment.Name);
            fields.Rows.Add("Texture", segment.TextureName);
            fields.Rows.Add("Model", segment.ModelName);
            fields.Rows.Add("Lifetime (s)", Format(segment.Data04[4]));
            fields.Rows.Add("Gravity", Format(segment.Data04[10]));
            fields.Rows.Add("Launch speed low", Format(segment.Data04[8]));
            fields.Rows.Add("Launch speed high", Format(segment.Data04[9]));
            for (var index = 0; index < segment.Data02.Length; index++)
            {
                fields.Rows.Add($"Flags {index:X2}", $"0x{segment.Data02[index]:X8}");
            }
        }
        refreshing = false;
    }

    private static string Format(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes one edited cell back into the keyframe. Only that cell is
    /// rewritten: rebuilding the whole grid from inside the grid's own
    /// end-of-edit notification re-enters it — which is what a click on another
    /// cell used to crash on.
    /// </summary>
    private void ApplyKeyframeEdit(int row, int column)
    {
        if (refreshing || row < 0 || column < 0) return;
        if (SelectedTrack is not { } track || row >= track.Count) return;
        var keyframe = track[row];
        var cell = keyframes.Rows[row].Cells[column];
        var text = cell.Value?.ToString() ?? string.Empty;
        var applied = column switch
        {
            0 => TrySetFloat(text, value => keyframe.Time = value),
            1 => TrySetFlags(text, value => keyframe.Flags = value),
            _ => TrySetFloat(text, value => keyframe.Floats[FloatSlot(column)] = value),
        };
        refreshing = true;
        try
        {
            // Show the value as it is now stored, which also puts a rejected
            // edit back to what the keyframe still holds.
            cell.Value = column switch
            {
                0 => Format(keyframe.Time),
                1 => $"0x{keyframe.Flags:X4}",
                _ => Format(keyframe.Floats[FloatSlot(column)]),
            };
        }
        finally
        {
            refreshing = false;
        }
        if (!applied)
        {
            status.Text = "That value is not a number.";
            return;
        }
        SetDirty();
        RaisePreview();
    }

    /// <summary>Columns 2..5 are the value, 6..9 the bound a random keyframe rolls to.</summary>
    private static int FloatSlot(int column) => column - 2;

    private void ApplyFieldEdit(int row, int column)
    {
        if (refreshing || column != 1 || row < 0) return;
        if (SelectedSegment is not { } segment) return;
        var text = fields.Rows[row].Cells[1].Value?.ToString() ?? string.Empty;
        var applied = row switch
        {
            0 => SetName(segment, text),
            1 => SetTextureName(segment, text),
            2 => SetModelName(segment, text),
            3 => TrySetFloat(text, value => segment.Data04[4] = value),
            4 => TrySetFloat(text, value => segment.Data04[10] = value),
            5 => TrySetFloat(text, value => segment.Data04[8] = value),
            6 => TrySetFloat(text, value => segment.Data04[9] = value),
            _ => TrySetUInt32(text, value => segment.Data02[row - 7] = value),
        };
        refreshing = true;
        try
        {
            fields.Rows[row].Cells[1].Value = row switch
            {
                0 => segment.Name,
                1 => segment.TextureName,
                2 => segment.ModelName,
                3 => Format(segment.Data04[4]),
                4 => Format(segment.Data04[10]),
                5 => Format(segment.Data04[8]),
                6 => Format(segment.Data04[9]),
                _ => $"0x{segment.Data02[row - 7]:X8}",
            };
        }
        finally
        {
            refreshing = false;
        }
        if (!applied)
        {
            status.Text = "That value is not a number.";
            return;
        }
        SetDirty();
        // Renaming a segment changes what the tree shows; rebuilding it from
        // inside the grid's notification would re-enter the grid, so it waits
        // until the grid has finished with the edit.
        if (row == 0) BeginInvoke(RefreshSegmentTree);
        RaisePreview();
    }

    private static bool SetName(EffSegment segment, string text)
    {
        segment.Name = text;
        return true;
    }

    private static bool SetTextureName(EffSegment segment, string text)
    {
        segment.TextureName = text;
        return true;
    }

    private static bool SetModelName(EffSegment segment, string text)
    {
        segment.ModelName = text;
        return true;
    }

    private static bool TrySetFloat(string text, Action<float> apply)
    {
        if (!float.TryParse(
                text.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return false;
        }
        apply(value);
        return true;
    }

    private static bool TrySetFlags(string text, Action<ushort> apply)
    {
        var trimmed = text.Trim();
        var hexadecimal = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ushort.TryParse(
                hexadecimal ? trimmed[2..] : trimmed,
                hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return false;
        }
        apply(value);
        return true;
    }

    private static bool TrySetUInt32(string text, Action<uint> apply)
    {
        var trimmed = text.Trim();
        var hexadecimal = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(
                hexadecimal ? trimmed[2..] : trimmed,
                hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return false;
        }
        apply(value);
        return true;
    }

    private void SetDirty()
    {
        dirty = true;
        status.Text = "Modified — save to write the effect.";
    }

    private void RaisePreview()
        => PreviewChanged?.Invoke(
            this,
            new EffPreviewRequest(preview.Checked ? effect : null, PreviewSeconds));

    /// <summary>
    /// Starts an effect from nothing: one segment, written from what the format
    /// says — drawn, facing the camera, a second long, its own size, white. The
    /// fields this project has not reversed stay zero.
    /// </summary>
    private void NewEffect()
    {
        if (PromptForName("new_effect", "Name of the new effect") is not { } name) return;
        effect = EffAuthoring.CreateEffect(name);
        effectPath = null;
        dirty = true;
        saveButton.Enabled = true;
        saveAsButton.Enabled = true;
        importTextureButton.Enabled = true;
        addSegmentButton.Enabled = true;
        addBlankSegmentButton.Enabled = true;
        RefreshSegmentTree();
        RaisePreview();
        status.Text = "New effect — give its segment a texture, then Save As.";
    }

    /// <summary>
    /// Starts a new effect from one the game ships. A file built out of nothing
    /// would hold segments full of zeroes, which draw nothing and tell the
    /// author nothing; starting from a real effect and saving under another name
    /// is how a new one is actually made.
    /// </summary>
    private void NewEffectFromTemplate()
    {
        if (files.SelectedItem is not string relative)
        {
            status.Text = "Pick the effect to start from in the list first.";
            return;
        }
        OpenSelectedFile();
        if (effect is null) return;
        // It is a new file until it is saved: the path is dropped so Save asks
        // where it goes instead of writing over the effect it came from.
        effectPath = null;
        SetDirty();
        status.Text = $"New effect started from {relative} — use Save As to name it.";
    }

    /// <summary>
    /// Adds a segment: a copy of the selected one, fired by it. A copy rather
    /// than an empty segment because the format has no notion of a default —
    /// a segment of zeroes has no shape, no texture and no lifetime.
    /// </summary>
    private void AddSegment(bool blank)
    {
        if (effect is null) return;
        var selected = segments.SelectedNode?.Tag as int?;
        var source = selected ?? 0;
        if (!blank && effect.Segments.Count == 0) return;
        var suggestion = blank || effect.Segments.Count == 0
            ? "segment"
            : $"{effect.Segments[source].Name} copy";
        var name = PromptForName(suggestion, "Name of the new segment");
        if (name is null) return;
        try
        {
            var added = blank
                ? EffAuthoring.AddNewSegment(effect, effect.Version, selected, name)
                : EffAuthoring.AddSegment(effect, source, selected, name);
            SetDirty();
            RefreshSegmentTree();
            SelectSegment(added);
            status.Text = selected is { } under
                ? $"Added segment #{added} under #{under}."
                : $"Added segment #{added} as a root.";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot add the segment",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveSegment()
    {
        if (effect is null || segments.SelectedNode?.Tag is not int index) return;
        var doomed = EffAuthoring.Descendants(effect, index).Count;
        if (MessageBox.Show(
                this,
                doomed > 1
                    ? $"Remove segment #{index} and the {doomed - 1} it spawns?"
                    : $"Remove segment #{index}?",
                "Effect editor",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        EffAuthoring.RemoveSegment(effect, index);
        SetDirty();
        RefreshSegmentTree();
        status.Text = $"Removed {doomed} segment(s).";
    }

    /// <summary>Moves the selected segment under the parent the list names.</summary>
    private void ApplyParentChange()
    {
        if (refreshing || effect is null) return;
        if (segments.SelectedNode?.Tag is not int index) return;
        if (parentList.SelectedItem is not ParentChoice choice) return;
        var current = CurrentParent(index);
        if (choice.Index == current) return;
        try
        {
            EffAuthoring.Reparent(effect, index, choice.Index);
            SetDirty();
            RefreshSegmentTree();
            SelectSegment(index);
            RaisePreview();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot move the segment",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshParents(index);
        }
    }

    private int? CurrentParent(int index)
    {
        if (effect is null) return null;
        for (var parent = 0; parent < effect.Segments.Count; parent++)
        {
            foreach (var descriptor in effect.Segments[parent].Children)
            {
                if (EffAuthoring.TargetOf(descriptor) == index) return parent;
            }
        }
        return null;
    }

    private void RefreshParents(int? index)
    {
        refreshing = true;
        parentList.Items.Clear();
        if (effect is not null && index is { } selected)
        {
            var forbidden = EffAuthoring.Descendants(effect, selected);
            parentList.Items.Add(new ParentChoice(null, "(nothing — a root)"));
            for (var candidate = 0; candidate < effect.Segments.Count; candidate++)
            {
                if (forbidden.Contains(candidate)) continue;
                parentList.Items.Add(new ParentChoice(
                    candidate, $"#{candidate} {effect.Segments[candidate].Name}"));
            }
            var current = CurrentParent(selected);
            parentList.SelectedItem = parentList.Items.Cast<ParentChoice>()
                .FirstOrDefault(value => value.Index == current);
        }
        parentList.Enabled = parentList.Items.Count > 0;
        removeSegmentButton.Enabled = index is not null;
        refreshing = false;
    }

    private void SelectSegment(int index)
    {
        if (FindNode(segments.Nodes, index) is { } node) segments.SelectedNode = node;
    }

    private sealed record ParentChoice(int? Index, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Brings an image in as a texture package and gives it to the selected
    /// segment. The name also joins the effect's own texture list: the game
    /// preloads that list, and a segment whose texture is not in it has nothing
    /// to draw with.
    /// </summary>
    private void ImportTexture()
    {
        if (effect is null || SelectedSegment is not { } segment)
        {
            status.Text = "Select the segment the texture is for first.";
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the image to bring in",
            Filter = "Images (*.png;*.bmp;*.tga;*.jpg)|*.png;*.bmp;*.tga;*.jpg|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var (name, format) = PromptForTexture(EffTextureImport.SuggestName(gameDataPath));
        if (name is null || format is null) return;

        try
        {
            var packagePath = Path.Combine(
                gameDataPath, "asset", "D3D11", $"{name}.pkg");
            TextureImported?.Invoke(this, new EffSaveEventArgs(packagePath, beforeWrite: true));
            var imported = EffTextureImport.Import(gameDataPath, dialog.FileName, name, format);
            TextureImported?.Invoke(this, new EffSaveEventArgs(imported.PackagePath, beforeWrite: false));

            segment.TextureName = imported.AssetName;
            if (!effect.Textures.Any(value =>
                    value.Equals(imported.AssetName, StringComparison.OrdinalIgnoreCase)))
            {
                effect.Textures.Add(imported.AssetName);
            }
            SetDirty();
            RefreshFields();
            RaisePreview();
            status.Text =
                $"Imported {imported.Width}x{imported.Height} as {imported.AssetName}"
                + " — save the effect to keep it on this segment.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot import the texture",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Asks what the texture package is called and which format it is written
    /// in. A compressed one is a quarter the size and is what the game itself
    /// ships; an uncompressed one keeps every pixel exactly.
    /// </summary>
    private (string? Name, string? Format) PromptForTexture(string suggestion)
    {
        using var prompt = new Form
        {
            Text = "Import a texture",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(440, 150),
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var formats = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
        };
        formats.Items.AddRange(EffTextureImport.Formats.Cast<object>().ToArray());
        formats.SelectedIndex = 0;
        var input = new TextBox { Text = suggestion, Dock = DockStyle.Top };
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = "The game resolves an effect texture by this name (I_EFTEX###)."
                + " DXT5 keeps the alpha and is four times smaller; ARGB8 is exact.",
        };
        var accept = new Button { Text = "Import", DialogResult = DialogResult.OK, Dock = DockStyle.Right };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        prompt.Controls.Add(buttons);
        prompt.Controls.Add(formats);
        prompt.Controls.Add(hint);
        prompt.Controls.Add(input);
        prompt.AcceptButton = accept;
        prompt.CancelButton = cancel;
        return prompt.ShowDialog(this) == DialogResult.OK && input.Text.Trim().Length > 0
            ? (input.Text.Trim(), (string)formats.SelectedItem!)
            : (null, null);
    }

    /// <summary>Asks for a name, for whatever is being created.</summary>
    private string? PromptForName(string suggestion, string title = "Name of the texture package")
    {
        using var prompt = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 108),
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var input = new TextBox { Text = suggestion, Dock = DockStyle.Top, Margin = new Padding(8) };
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = title.Contains("texture", StringComparison.OrdinalIgnoreCase)
                ? "The game resolves an effect texture by this name (I_EFTEX###)."
                : "Segment names are stored in the file's own encoding (cp932).",
        };
        var accept = new Button { Text = "Import", DialogResult = DialogResult.OK, Dock = DockStyle.Right };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        prompt.Controls.Add(buttons);
        prompt.Controls.Add(hint);
        prompt.Controls.Add(input);
        prompt.AcceptButton = accept;
        prompt.CancelButton = cancel;
        return prompt.ShowDialog(this) == DialogResult.OK && input.Text.Trim().Length > 0
            ? input.Text.Trim()
            : null;
    }

    private bool Save(bool saveAs)
    {
        if (effect is null) return false;
        var path = effectPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Cold Steel effects (*.eff)|*.eff|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(path) ?? effectRoot,
                FileName = Path.GetFileName(path) ?? "effect.eff",
                AddExtension = true,
                DefaultExt = "eff",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }
        try
        {
            Saving?.Invoke(this, new EffSaveEventArgs(path!, beforeWrite: true));
            EffFileWriter.Write(effect, path!);
            Saving?.Invoke(this, new EffSaveEventArgs(path!, beforeWrite: false));
            effectPath = path;
            dirty = false;
            status.Text = $"Saved {path}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message, "Cannot save the effect", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

/// <summary>A save the mod project should record, before and after the write.</summary>
public sealed class EffSaveEventArgs : EventArgs
{
    public EffSaveEventArgs(string path, bool beforeWrite)
    {
        Path = path;
        BeforeWrite = beforeWrite;
    }

    public string Path { get; }

    public bool BeforeWrite { get; }
}
