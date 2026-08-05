using ED8Editor.Application;

namespace ED8Editor.Viewer;

/// <summary>
/// Picks the instruction to insert.
///
/// It used to be a drop-down in the toolbar: a list of every opcode the game has,
/// permanently open above the canvas, offering no way to search it and no hint of
/// what any entry does. Choosing from it was a scroll through hundreds of names,
/// and it took a line of toolbar whether or not anything was being added.
///
/// Asked for at the moment of adding instead, it can be a real list — searchable,
/// grouped by what the instructions are for, and wide enough to read.
///
/// The groups are drawn from the names themselves rather than from a table kept
/// beside them: a table would have to be maintained against every opcode the game
/// has, and would be wrong the moment one is renamed. What a name starts with is
/// what the game itself uses to file it.
/// </summary>
internal sealed class InstructionPickerForm : Form
{
    private readonly ListBox list = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        Font = new Font("Consolas", 9.5f),
    };

    private readonly TextBox search = new()
    {
        Dock = DockStyle.Top,
        PlaceholderText = "Search by name…",
    };

    private readonly CheckedListBox categories = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
    };

    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };

    private readonly IReadOnlyList<string> all;
    private IReadOnlyList<string> shown = Array.Empty<string>();

    private readonly ListBox presets = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
    };

    private readonly IReadOnlyList<ScriptPreset> available;

    /// <summary>The instruction chosen, or null if the author backed out.</summary>
    public string? Chosen { get; private set; }

    /// <summary>
    /// The run of instructions chosen, when a preset was picked instead of a single
    /// instruction. The two are exclusive: one of them is null.
    /// </summary>
    public ScriptPreset? ChosenPreset { get; private set; }

    public InstructionPickerForm(
        IReadOnlyList<string> names,
        string? initial = null,
        IReadOnlyList<ScriptPreset>? presets = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        all = names;
        available = presets ?? Array.Empty<ScriptPreset>();

        Text = "Add an instruction";
        Width = 820;
        Height = 620;
        StartPosition = FormStartPosition.CenterParent;

        foreach (var category in Categories(all)) categories.Items.Add(category, true);

        var ok = new Button { Text = "Insert", AutoSize = true };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        ok.Click += (_, _) => Accept();
        list.DoubleClick += (_, _) => Accept();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { ok, cancel });

        var categoriesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Categories" };
        categoriesGroup.Controls.Add(categories);
        var listGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Instructions" };
        listGroup.Controls.Add(list);
        listGroup.Controls.Add(search);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(categoriesGroup);
        split.Panel2.Controls.Add(listGroup);
        WinFormsLayout.SetInitialSplitterDistance(split, 230);

        // Runs of instructions that go together, when there are any. Inserting a
        // camera move is three commands; asking for them one at a time is where the
        // tedium is.
        var pages = new TabControl { Dock = DockStyle.Fill };
        var singleTab = new TabPage("One instruction") { Padding = new Padding(6) };
        singleTab.Controls.Add(split);
        pages.TabPages.Add(singleTab);
        if (available.Count != 0)
        {
            var presetTab = new TabPage($"Presets ({available.Count})")
            {
                Padding = new Padding(6),
            };
            presetTab.Controls.Add(this.presets);
            pages.TabPages.Add(presetTab);
            foreach (var preset in available
                         .OrderBy(value => value.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                this.presets.Items.Add(preset);
            }
            this.presets.DisplayMember = string.Empty;
            this.presets.Format += (_, eventArgs) =>
            {
                if (eventArgs.ListItem is not ScriptPreset one) return;
                eventArgs.Value = $"{one.Category,-12} {one.Name}"
                    + $"   ({one.Steps.Count} instruction(s))"
                    + (string.IsNullOrWhiteSpace(one.Description)
                        ? string.Empty
                        : "   — " + one.Description);
            };
            this.presets.DoubleClick += (_, _) => Accept();
            if (this.presets.Items.Count != 0) this.presets.SelectedIndex = 0;
        }

        Controls.Add(pages);
        Controls.Add(tools);
        Controls.Add(status);
        AcceptButton = ok;
        CancelButton = cancel;

        search.TextChanged += (_, _) => ApplyFilter();
        categories.ItemCheck += (_, _) => BeginInvoke(ApplyFilter);
        ApplyFilter();
        if (initial is not null && list.Items.Contains(initial)) list.SelectedItem = initial;
        else if (list.Items.Count != 0) list.SelectedIndex = 0;
    }

    /// <summary>
    /// What the instructions are for, taken from what they are called.
    ///
    /// A name like <c>CAM_SET_POS</c> files itself under CAM; one with no underscore
    /// files itself under its own name. This is the game's own grouping, so it needs
    /// no maintenance and cannot disagree with the list it groups.
    /// </summary>
    private static IReadOnlyList<string> Categories(IEnumerable<string> names)
        => names
            .Select(CategoryOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string CategoryOf(string name)
    {
        var cut = name.IndexOf('_');
        return cut > 0 ? name[..cut] : name;
    }

    private void ApplyFilter()
    {
        var wanted = search.Text.Trim();
        var checkedCategories = categories.CheckedItems.Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Nothing ticked means nothing has been narrowed down yet, which reads as
        // "all of them" — an empty list would only look broken.
        var byCategory = checkedCategories.Count == 0
            ? all
            : all.Where(name => checkedCategories.Contains(CategoryOf(name)));
        shown = (wanted.Length == 0
                ? byCategory
                : byCategory.Where(name =>
                    name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var keep = list.SelectedItem as string;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var name in shown) list.Items.Add(name);
        list.EndUpdate();
        if (keep is not null && list.Items.Contains(keep)) list.SelectedItem = keep;
        else if (list.Items.Count != 0) list.SelectedIndex = 0;
        status.Text = $"{shown.Count} of {all.Count} instructions.";
    }

    private void Accept()
    {
        // Whichever page is in front decides what is being inserted, so the two can
        // never both be answered.
        if (presets.Visible && presets.SelectedItem is ScriptPreset preset)
        {
            ChosenPreset = preset;
            Chosen = null;
        }
        else if (list.SelectedItem is string name)
        {
            Chosen = name;
            ChosenPreset = null;
        }
        else
        {
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
