using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

internal sealed record ShopEditResult(
    ShopScriptBinding OriginalBinding,
    int ShopId,
    string Title,
    IReadOnlyList<Cs1ShopItemValue> Items);

internal sealed class ShopEditorForm : Form
{
    private readonly ComboBox bindings = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly ComboBox shops = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly TextBox title = new() { Dock = DockStyle.Fill };
    private readonly DataGridView inventory = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
    };
    private readonly Label status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 34,
        AutoEllipsis = true,
    };
    private readonly Cs1ShopTable table;
    private readonly IReadOnlyList<ShopItemChoice> itemChoices;
    private bool refreshing;

    public ShopEditorForm(
        string pointName,
        IReadOnlyList<ShopScriptBinding> shopBindings,
        Cs1ShopTable table,
        IReadOnlyList<Cs1TableChoice> itemChoices)
    {
        this.table = table;
        var codec = new Cs1TableRecordCodec();
        var resolvedItems = itemChoices
            .Where(value => value.Value is >= ushort.MinValue and <= ushort.MaxValue)
            .GroupBy(value => value.Value)
            .Select(group =>
            {
                var choice = group.First();
                var name = codec.Decode(choice.Entry)?
                    .FirstOrDefault(value => value.Field.Name == "name")?.Value
                    ?? choice.Label;
                return new ShopItemChoice(choice.Value, name);
            })
            .ToList();
        var knownIds = resolvedItems.Select(value => value.Id).ToHashSet();
        foreach (var unresolvedId in table.Titles
                     .SelectMany(value => table.Items(value.Id))
                     .Select(value => (int)value.ItemId)
                     .Distinct()
                     .Where(value => !knownIds.Contains(value)))
        {
            resolvedItems.Add(new ShopItemChoice(
                unresolvedId,
                $"Unresolved item #{unresolvedId}"));
        }
        this.itemChoices = resolvedItems
            .OrderBy(value => value.Id)
            .ToArray();
        Text = $"Shop — {pointName}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 650);
        MinimumSize = new Size(680, 480);
        MinimizeBox = false;

        bindings.DisplayMember = nameof(ShopScriptBinding.Label);
        bindings.DataSource = shopBindings.ToArray();
        shops.DisplayMember = nameof(Cs1ShopTitle.Label);
        shops.ValueMember = nameof(Cs1ShopTitle.Id);
        shops.DataSource = table.Titles.ToArray();

        inventory.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Item",
            HeaderText = "Item",
            DisplayMember = nameof(ShopItemChoice.Label),
            ValueMember = nameof(ShopItemChoice.Id),
            DataSource = this.itemChoices.ToArray(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
            FillWeight = 80,
        });
        inventory.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "UnknownValue",
            HeaderText = "Unknown u16",
            FillWeight = 20,
        });

        var add = new Button { AutoSize = true, Text = "Add item" };
        var remove = new Button { AutoSize = true, Text = "Remove selected" };
        add.Click += (_, _) => AddItem();
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in inventory.SelectedRows
                         .Cast<DataGridViewRow>()
                         .OrderByDescending(value => value.Index))
                inventory.Rows.RemoveAt(row.Index);
        };
        var inventoryTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            WrapContents = false,
        };
        inventoryTools.Controls.Add(add);
        inventoryTools.Controls.Add(remove);
        inventoryTools.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(12, 7, 0, 0),
            Text = "The third ShopItem word is preserved as an explicitly unknown value.",
        });

        var inventoryPanel = new Panel { Dock = DockStyle.Fill };
        inventoryPanel.Controls.Add(inventory);
        inventoryPanel.Controls.Add(inventoryTools);
        var inventoryGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Inventory (ShopItem)",
        };
        inventoryGroup.Controls.Add(inventoryPanel);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 126,
            Padding = new Padding(10),
            ColumnCount = 2,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(header, "Script binding", bindings);
        AddRow(header, "Shop ID / title", shops);
        AddRow(header, "Displayed title", title);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var apply = new Button { AutoSize = true, Text = "Apply", DialogResult = DialogResult.OK };
        var cancel = new Button { AutoSize = true, Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);

        Controls.Add(inventoryGroup);
        Controls.Add(header);
        Controls.Add(status);
        Controls.Add(buttons);
        AcceptButton = apply;
        CancelButton = cancel;

        bindings.SelectedIndexChanged += (_, _) => SelectBinding();
        shops.SelectedIndexChanged += (_, _) => LoadShop();
        if (shopBindings.Count > 0)
        {
            bindings.SelectedIndex = 0;
            SelectBinding();
        }
        else
        {
            SetEditorsEnabled(false);
            status.Text =
                "No OP114 was found by following selector-11 CALL_EXT instructions from this function.";
        }
    }

    public ShopEditResult ReadResult()
    {
        var binding = bindings.SelectedItem as ShopScriptBinding
            ?? throw new InvalidOperationException("No shop binding is selected.");
        var shop = shops.SelectedItem as Cs1ShopTitle
            ?? throw new InvalidOperationException("No ShopTitle is selected.");
        var values = new List<Cs1ShopItemValue>(inventory.Rows.Count);
        foreach (DataGridViewRow row in inventory.Rows)
        {
            var itemId = ParseUInt16(row.Cells[0].Value, "Item");
            var unknown = ParseUInt16(row.Cells[1].Value, "Unknown ShopItem value");
            values.Add(new Cs1ShopItemValue(itemId, unknown));
        }
        return new ShopEditResult(binding, shop.Id, title.Text, values);
    }

    private void SelectBinding()
    {
        if (refreshing || bindings.SelectedItem is not ShopScriptBinding binding) return;
        refreshing = true;
        try
        {
            var index = table.Titles.ToList().FindIndex(value => value.Id == binding.ShopId);
            shops.SelectedIndex = index;
        }
        finally
        {
            refreshing = false;
        }
        LoadShop();
        if (shops.SelectedIndex < 0)
        {
            SetEditorsEnabled(false);
            status.Text = $"OP114 references missing ShopTitle {binding.ShopId}.";
        }
    }

    private void LoadShop()
    {
        if (shops.SelectedItem is not Cs1ShopTitle shop) return;
        title.Text = shop.Name;
        inventory.Rows.Clear();
        foreach (var item in table.Items(shop.Id))
            inventory.Rows.Add((int)item.ItemId, item.UnknownValue);
        SetEditorsEnabled(true);
        status.Text =
            $"Shop {shop.Id}: {inventory.Rows.Count} item(s) · {System.IO.Path.GetFileName(table.Path)}";
    }

    private void AddItem()
    {
        if (itemChoices.Count == 0)
        {
            status.Text = "No item entry could be loaded from t_item.tbl.";
            return;
        }
        var row = inventory.Rows.Add(itemChoices[0].Id, 0);
        inventory.CurrentCell = inventory.Rows[row].Cells[0];
        inventory.BeginEdit(selectAll: false);
    }

    private void SetEditorsEnabled(bool enabled)
    {
        shops.Enabled = enabled;
        title.Enabled = enabled;
        inventory.Enabled = enabled;
    }

    private static ushort ParseUInt16(object? value, string field) =>
        ushort.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : throw new ArgumentException($"{field} must be between 0 and 65535.");

    private static void AddRow(TableLayoutPanel layout, string label, Control editor)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            Padding = new Padding(0, 7, 8, 0),
        }, 0, row);
        layout.Controls.Add(editor, 1, row);
    }

    private sealed record ShopItemChoice(int Id, string Name)
    {
        public string Label => $"{Name} (ID {Id})";
    }
}
