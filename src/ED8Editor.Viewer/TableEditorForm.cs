using System.Globalization;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed class TableEditorForm : Form
{
    private readonly ScriptEditorDocument document;
    private readonly int functionIndex;
    private readonly Label header = new() { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 0, 0, 0) };
    private readonly DataGridView fields = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Button applyButton = new() { Text = "Apply", AutoSize = true };
    private readonly Button closeButton = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

    public TableEditorForm(ScriptEditorDocument document, int functionIndex)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.functionIndex = functionIndex;
        Text = "Table editor";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 420);
        ClientSize = new Size(860, 620);
        ShowInTaskbar = false;

        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "index", HeaderText = "#", FillWeight = 12, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "field", HeaderText = "Field", FillWeight = 35, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "Type", FillWeight = 18, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value", FillWeight = 80 });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(applyButton);
        Controls.Add(fields);
        Controls.Add(buttons);
        Controls.Add(header);
        CancelButton = closeButton;
        applyButton.Click += (_, _) => ApplyChanges();
        LoadTable();
    }

    public event EventHandler? TableChanged;

    private void LoadTable()
    {
        var function = document.Snapshot.Functions.ElementAtOrDefault(functionIndex);
        if (function?.Table is not { } table)
            throw new InvalidOperationException("The selected function is not a parsed table.");
        Text = $"Table editor — {function.Name}";
        header.Text = $"{function.Name} — {table.Kind}" + (table.IsStale ? " (stale/malformed)" : string.Empty);
        applyButton.Enabled = !table.IsStale;
        fields.Rows.Clear();
        foreach (var field in table.Fields)
        {
            var rowIndex = fields.Rows.Add(
                field.Index,
                DescribeField(table, field),
                field.Type,
                FormatValue(field));
            var row = fields.Rows[rowIndex];
            row.Tag = field;
            if (field.Type == "fill" || table.IsStale)
            {
                row.Cells["value"].ReadOnly = true;
                row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
        }
    }

    private void ApplyChanges()
    {
        try
        {
            foreach (DataGridViewRow row in fields.Rows)
            {
                if (row.Tag is not TableField field) continue;
                var text = row.Cells["value"].Value?.ToString() ?? string.Empty;
                switch (field.Type)
                {
                    case "u8":
                        document.SetTableInteger(functionIndex, field.Index,
                            byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "s16":
                        document.SetTableInteger(functionIndex, field.Index,
                            short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "s32":
                        document.SetTableInteger(functionIndex, field.Index,
                            int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "f32":
                        document.SetTableFloat(functionIndex, field.Index,
                            float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                        break;
                    case "string":
                        document.SetTableText(functionIndex, field.Index, text);
                        break;
                    case "bytes":
                        var bytes = ParseHex(text);
                        if (bytes.Length != field.Raw.Length)
                            throw new FormatException(
                                $"Field {field.Index} must remain exactly {field.Raw.Length} bytes long.");
                        document.SetTableBytes(functionIndex, field.Index, bytes);
                        break;
                }
            }
            LoadTable();
            TableChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
            or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Invalid table value",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FormatValue(TableField field) => field.Type switch
    {
        "string" => field.Text ?? string.Empty,
        "f32" => field.FloatValue.ToString("G9", CultureInfo.InvariantCulture),
        "bytes" or "fill" => Convert.ToHexString(field.Raw),
        _ => field.IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static byte[] ParseHex(string text)
    {
        var compact = new string(text.Where(value => !char.IsWhiteSpace(value) && value != '-').ToArray());
        if (compact.Length % 2 != 0) throw new FormatException("Hexadecimal byte fields require an even number of digits.");
        return Convert.FromHexString(compact);
    }

    private static string DescribeField(DecompiledTable table, TableField field)
    {
        if (table.Kind != "CreateMonsters") return $"Field {field.Index}";
        if (field.Index == 0) return "Map asset";
        if (field.Index == 1) return "Map asset padding";
        if (field.Index == 2) return "Header value 0";
        if (field.Index is >= 3 and <= 8) return $"Header value {field.Index - 2}";
        if (!CreateMonstersTableReader.TryRead(table, out var parsed) || parsed is null)
            return $"Field {field.Index}";
        foreach (var encounter in parsed.Encounters)
        {
            var local = encounter.SourceFields.ToList().FindIndex(value => value.Index == field.Index);
            if (local < 0) continue;
            if (local == 0) return $"Encounter {encounter.Index}: ID";
            if (local is >= 1 and <= 16)
                return local % 2 == 1
                    ? $"Encounter {encounter.Index}: monster {(local - 1) / 2}"
                    : $"Encounter {encounter.Index}: monster {(local - 2) / 2} padding";
            if (local is >= 17 and <= 24)
                return $"Encounter {encounter.Index}: weight {local - 17}";
            return $"Encounter {encounter.Index}: auxiliary data";
        }
        return "Trailer data";
    }
}
