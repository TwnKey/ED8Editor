using ED8Editor.Tables;

namespace ED8Editor.Viewer;

internal sealed record ShopCreationOptions(
    string FunctionName,
    int ShopId,
    string Title,
    int TemplateShopId,
    string? EntitySetupFunction,
    int? EntityId);

internal sealed class ShopCreationForm : Form
{
    private readonly TextBox functionName = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown shopId = Integer();
    private readonly TextBox title = new() { Dock = DockStyle.Fill };
    private readonly ComboBox template = Choice();
    private readonly CheckBox bindNpc = new()
    {
        AutoSize = true,
        Text = "Bind the LookPoint to an NPC",
    };
    private readonly ComboBox setupFunction = Choice();
    private readonly NumericUpDown entityId = Integer();

    public ShopCreationForm(
        string suggestedFunctionName,
        int suggestedShopId,
        IReadOnlyList<Cs1ShopTitle> templates,
        IReadOnlyList<string> setupFunctions,
        IReadOnlyList<ScriptEntityChoice> entities)
    {
        if (templates.Count == 0)
            throw new InvalidDataException("t_shop.tbl contains no ShopTitle template.");
        Text = "Create shop interaction";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 390);
        MinimumSize = new Size(560, 360);
        MinimizeBox = false;

        functionName.Text = suggestedFunctionName;
        shopId.Value = Clamp(suggestedShopId, shopId);
        title.Text = "New shop";
        template.DisplayMember = nameof(Cs1ShopTitle.Label);
        template.DataSource = templates.ToArray();
        setupFunction.Items.AddRange(setupFunctions.Cast<object>().ToArray());
        if (setupFunction.Items.Count > 0) setupFunction.SelectedIndex = 0;

        var entityChoices = entities
            .Select(value => new EntityChoice(value.EntityId, value.Label))
            .ToArray();
        var entityList = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        entityList.Items.AddRange(entityChoices.Cast<object>().ToArray());
        entityList.TextChanged += (_, _) =>
        {
            if (entityList.SelectedItem is EntityChoice choice)
                entityId.Value = Clamp(choice.Id, entityId);
            else if (int.TryParse(entityList.Text, out var parsed))
                entityId.Value = Clamp(parsed, entityId);
        };
        if (entityChoices.Length > 0)
        {
            entityList.SelectedIndex = 0;
            entityId.Value = Clamp(entityChoices[0].Id, entityId);
        }

        var npcPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        npcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        npcPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        npcPanel.Controls.Add(entityList, 0, 0);
        npcPanel.Controls.Add(entityId, 1, 0);

        var layout = CreateLayout();
        AddRow(layout, "Script function", functionName);
        AddRow(layout, "New ShopTitle ID", shopId);
        AddRow(layout, "Displayed title", title);
        AddRow(layout, "Shop settings preset", template);
        AddRow(layout, "Optional NPC link", bindNpc);
        AddRow(layout, "Setup function", setupFunction);
        AddRow(layout, "NPC / entity ID", npcPanel);
        var note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "The preset copies the engine settings that are not editable yet"
                + " from an existing shop. The new shop inventory starts empty.",
        };
        var noteRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(note, 0, noteRow);
        layout.SetColumnSpan(note, 2);

        void RefreshNpc()
        {
            setupFunction.Enabled = bindNpc.Checked;
            entityList.Enabled = bindNpc.Checked;
            entityId.Enabled = bindNpc.Checked;
        }
        bindNpc.CheckedChanged += (_, _) => RefreshNpc();
        RefreshNpc();

        var buttons = Buttons(out var ok, out var cancel);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public ShopCreationOptions ReadResult()
    {
        var selectedTemplate = template.SelectedItem as Cs1ShopTitle
            ?? throw new InvalidOperationException("Select a ShopTitle template.");
        var name = functionName.Text.Trim();
        if (name.Length == 0) throw new ArgumentException("A script function is required.");
        if (title.Text.Trim().Length == 0) throw new ArgumentException("A title is required.");
        if (bindNpc.Checked && setupFunction.SelectedItem is not string)
            throw new ArgumentException(
                "Select the function in which LookPoint_BindEntity must be inserted.");
        return new ShopCreationOptions(
            name,
            decimal.ToInt32(shopId.Value),
            title.Text.Trim(),
            selectedTemplate.Id,
            bindNpc.Checked ? (string)setupFunction.SelectedItem! : null,
            bindNpc.Checked ? decimal.ToInt32(entityId.Value) : null);
    }

    private sealed record EntityChoice(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    internal static NumericUpDown Integer() => new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = 0,
        Minimum = short.MinValue,
        Maximum = short.MaxValue,
    };

    internal static ComboBox Choice() => new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    internal static TableLayoutPanel CreateLayout() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Padding = new Padding(12),
        ColumnCount = 2,
        RowCount = 0,
        ColumnStyles =
        {
            new ColumnStyle(SizeType.Absolute, 175),
            new ColumnStyle(SizeType.Percent, 100),
        },
    };

    internal static void AddRow(
        TableLayoutPanel layout,
        string label,
        Control control,
        int height = 38)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    internal static FlowLayoutPanel Buttons(out Button ok, out Button cancel)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        ok = new Button
        {
            AutoSize = true,
            Text = "Create and place",
            DialogResult = DialogResult.OK,
        };
        cancel = new Button
        {
            AutoSize = true,
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
        };
        panel.Controls.Add(ok);
        panel.Controls.Add(cancel);
        return panel;
    }

    internal static decimal Clamp(int value, NumericUpDown control) =>
        Math.Clamp((decimal)value, control.Minimum, control.Maximum);
}

internal sealed record FishingCreationOptions(
    string FunctionName,
    int FishingPointId,
    int AvailabilitySourcePointId,
    IReadOnlyList<int> AvailableFishIds,
    float Radius,
    float HeadingDegrees);

internal sealed class FishingCreationForm : Form
{
    private readonly TextBox functionName = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown pointId = ShopCreationForm.Integer();
    private readonly ComboBox template = ShopCreationForm.Choice();
    private readonly CheckedListBox availableFish = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
    };
    private readonly NumericUpDown radius = Number(0m, 100000m, 1.5m);
    private readonly NumericUpDown heading = Number(-3600m, 3600m, 0m);

    public FishingCreationForm(
        string suggestedFunctionName,
        int suggestedPointId,
        IReadOnlyList<Cs1FishingPoint> templates,
        IReadOnlyList<Cs1FishChoice> fishChoices)
    {
        if (templates.Count == 0)
            throw new InvalidDataException(
                "t_fish.tbl contains no existing fishing configuration.");
        Text = "Create fishing spot";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(640, 480);
        MinimizeBox = false;

        functionName.Text = suggestedFunctionName;
        pointId.Value = ShopCreationForm.Clamp(suggestedPointId, pointId);
        template.DisplayMember = nameof(Cs1FishingPoint.Label);
        template.DataSource = templates.ToArray();
        availableFish.Items.AddRange(fishChoices.Cast<object>().ToArray());
        template.SelectedIndexChanged += (_, _) => LoadPresetFish();

        var layout = ShopCreationForm.CreateLayout();
        ShopCreationForm.AddRow(layout, "Interaction script name", functionName);
        ShopCreationForm.AddRow(layout, "New fishing spot ID", pointId);
        ShopCreationForm.AddRow(layout, "Base fishing behavior", template);
        ShopCreationForm.AddRow(layout, "Available fish", availableFish, 150);
        ShopCreationForm.AddRow(layout, "Interaction radius (map units)", radius);
        ShopCreationForm.AddRow(layout, "Player facing direction (degrees)", heading);
        var note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Fishing spot ID: unique identifier stored in t_fish.tbl.\r\n"
                + "Base fishing behavior: preserves four engine parameters whose exact"
                + " meanings are not established yet. Available fish are independently"
                + " editable below and their names come from t_notefish.tbl.\r\n"
                + "Interaction radius: how close the player must stand to activate the spot.\r\n"
                + "Facing direction: the player's rotation while fishing.\r\n"
                + "After placement, double-click the spot to adjust the exact player position"
                + " and the water/cast target.",
        };
        var noteRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(note, 0, noteRow);
        layout.SetColumnSpan(note, 2);

        var buttons = ShopCreationForm.Buttons(out var ok, out var cancel);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        LoadPresetFish();
    }

    public FishingCreationOptions ReadResult()
    {
        var selectedTemplate = template.SelectedItem as Cs1FishingPoint
            ?? throw new InvalidOperationException("Select an available-fish configuration.");
        var name = functionName.Text.Trim();
        if (name.Length == 0) throw new ArgumentException("A script function is required.");
        var selectedFish = availableFish.CheckedItems
            .Cast<Cs1FishChoice>()
            .Select(value => value.Id)
            .ToArray();
        if (selectedFish.Length > 13)
            throw new ArgumentException("Select at most 13 fish species.");
        return new FishingCreationOptions(
            name,
            decimal.ToInt32(pointId.Value),
            selectedTemplate.Id,
            selectedFish,
            (float)radius.Value,
            (float)heading.Value);
    }

    private void LoadPresetFish()
    {
        if (template.SelectedItem is not Cs1FishingPoint point) return;
        var selected = point.FishIds.ToHashSet();
        for (var index = 0; index < availableFish.Items.Count; index++)
        {
            availableFish.SetItemChecked(
                index,
                availableFish.Items[index] is Cs1FishChoice fish
                    && selected.Contains(fish.Id));
        }
    }

    private static NumericUpDown Number(
        decimal minimum,
        decimal maximum,
        decimal value) => new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = 3,
        Minimum = minimum,
        Maximum = maximum,
        Increment = 0.1m,
        Value = value,
    };
}
