namespace ED8Editor.Viewer;

internal sealed record OpsAttributeEditorDescriptor(
    string Name,
    string Value,
    bool ReadOnly,
    IReadOnlyList<OpsAttributeChoice> Choices);

internal sealed record OpsAttributeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Large modal editor for one OPS element. Attribute semantics stay supplied by
/// the caller so new decoded element kinds can add proper selectors without
/// coupling the form to game-data discovery.
/// </summary>
internal sealed class OpsElementEditorForm : Form
{
    private readonly IReadOnlyList<(OpsAttributeEditorDescriptor Descriptor, Control Editor)> editors;

    public OpsElementEditorForm(
        string title,
        IReadOnlyList<OpsAttributeEditorDescriptor> descriptors)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 420);
        ClientSize = new Size(680, 620);
        MinimizeBox = false;
        MaximizeBox = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = descriptors.Count,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var built = new List<(OpsAttributeEditorDescriptor, Control)>();
        for (var row = 0; row < descriptors.Count; row++)
        {
            var descriptor = descriptors[row];
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                Text = descriptor.Name,
                Padding = new Padding(0, 7, 8, 0),
            }, 0, row);

            Control editor;
            if (descriptor.Choices.Count > 0)
            {
                var choices = descriptor.Choices.ToList();
                if (choices.All(value => !value.Value.Equals(
                        descriptor.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    choices.Insert(0, new OpsAttributeChoice(
                        descriptor.Value,
                        string.IsNullOrEmpty(descriptor.Value) ? "(none)" : descriptor.Value));
                }
                var combo = new ComboBox
                {
                    Dock = DockStyle.Top,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    Enabled = !descriptor.ReadOnly,
                    DisplayMember = nameof(OpsAttributeChoice.Label),
                    ValueMember = nameof(OpsAttributeChoice.Value),
                    DataSource = choices,
                };
                combo.SelectedItem = choices.First(value =>
                    value.Value.Equals(descriptor.Value, StringComparison.OrdinalIgnoreCase));
                editor = combo;
            }
            else
            {
                editor = new TextBox
                {
                    Dock = DockStyle.Top,
                    Text = descriptor.Value,
                    ReadOnly = descriptor.ReadOnly,
                };
            }
            layout.Controls.Add(editor, 1, row);
            built.Add((descriptor, editor));
        }
        editors = built;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var ok = new Button { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public IReadOnlyDictionary<string, string> ReadAttributes()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (descriptor, editor) in editors)
        {
            var value = editor switch
            {
                ComboBox combo when combo.SelectedItem is OpsAttributeChoice choice => choice.Value,
                ComboBox combo => combo.Text.Trim(),
                TextBox text => text.Text,
                _ => string.Empty,
            };
            result.Add(descriptor.Name, value);
        }
        return result;
    }
}
