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

    /// <summary>The instruction chosen, or null if the author backed out.</summary>
    public string? Chosen { get; private set; }

    public InstructionPickerForm(IReadOnlyList<string> names, string? initial = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        all = names;

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

        Controls.Add(split);
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
        if (list.SelectedItem is not string name) return;
        Chosen = name;
        DialogResult = DialogResult.OK;
        Close();
    }
}
