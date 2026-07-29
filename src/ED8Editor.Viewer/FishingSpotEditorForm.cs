using System.Globalization;
using System.Numerics;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed record FishingSpotEditResult(
    string FunctionName,
    Vector3 InteractionPosition,
    float Radius,
    FishingSpotScriptBinding? OriginalBinding,
    FishingSpotScriptBinding? UpdatedBinding);

internal sealed class FishingSpotEditorForm : Form
{
    private readonly TextBox functionName = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown interactionX = Number();
    private readonly NumericUpDown interactionY = Number();
    private readonly NumericUpDown interactionZ = Number();
    private readonly NumericUpDown radius = Number(minimum: 0m);
    private readonly ComboBox variants = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly NumericUpDown fishingPointId = Number(decimalPlaces: 0, minimum: 0m, maximum: int.MaxValue);
    private readonly NumericUpDown playerX = Number();
    private readonly NumericUpDown playerY = Number();
    private readonly NumericUpDown playerZ = Number();
    private readonly NumericUpDown heading = Number(minimum: -3600m, maximum: 3600m);
    private readonly NumericUpDown targetX = Number();
    private readonly NumericUpDown targetY = Number();
    private readonly NumericUpDown targetZ = Number();
    private readonly Label bindingStatus = new() { AutoSize = true };
    private readonly IReadOnlyList<FishingSpotScriptBinding> bindings;
    private bool refreshing;

    public FishingSpotEditorForm(
        string name,
        Vector3 interactionPosition,
        float interactionRadius,
        Vector3? opsWaterTarget,
        IReadOnlyList<FishingSpotScriptBinding> bindings)
    {
        this.bindings = bindings;
        Text = $"Fishing spot — {name}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(650, 540);
        ClientSize = new Size(720, 610);
        MinimizeBox = false;

        functionName.Text = name;
        functionName.ReadOnly = true;
        SetVector(interactionPosition, interactionX, interactionY, interactionZ);
        radius.Value = Clamp(interactionRadius, radius);
        variants.DisplayMember = nameof(FishingSpotScriptBinding.Label);
        variants.DataSource = bindings.ToArray();
        variants.SelectedIndexChanged += (_, _) => LoadSelectedBinding();

        var useInteraction = new Button { AutoSize = true, Text = "Copy interaction position" };
        useInteraction.Click += (_, _) =>
            SetVector(ReadVector(interactionX, interactionY, interactionZ), playerX, playerY, playerZ);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, "Script function", functionName);
        AddRow(layout, "Interaction position", VectorPanel(interactionX, interactionY, interactionZ));
        AddRow(layout, "Interaction radius", radius);
        AddRow(layout, "Script variant", variants);
        AddRow(layout, "fish_pnt ID", fishingPointId);
        AddRow(layout, "Player position", WithButton(VectorPanel(playerX, playerY, playerZ), useInteraction));
        AddRow(layout, "Facing yaw (degrees)", heading);
        AddRow(layout, "Water / cast target", VectorPanel(targetX, targetY, targetZ));
        AddRow(layout, "Binding", bindingStatus);

        if (bindings.Count > 0)
        {
            variants.SelectedIndex = 0;
            LoadSelectedBinding();
        }
        else
        {
            bindingStatus.Text = "No OP73 selector-1 payload was found in this function.";
            SetScriptEditorsEnabled(false);
            if (opsWaterTarget is { } target)
                SetVector(target, targetX, targetY, targetZ);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var ok = new Button { AutoSize = true, Text = "Apply", DialogResult = DialogResult.OK };
        var cancel = new Button { AutoSize = true, Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public FishingSpotEditResult ReadResult()
    {
        var original = variants.SelectedItem as FishingSpotScriptBinding;
        FishingSpotScriptBinding? updated = null;
        if (original is not null)
        {
            updated = original with
            {
                FishingPointId = decimal.ToInt32(fishingPointId.Value),
                PlayerPosition = ReadVector(playerX, playerY, playerZ),
                HeadingDegrees = (float)heading.Value,
                WaterTarget = ReadVector(targetX, targetY, targetZ),
            };
            _ = updated.EncodePayload();
        }
        return new FishingSpotEditResult(
            functionName.Text.Trim(),
            ReadVector(interactionX, interactionY, interactionZ),
            (float)radius.Value,
            original,
            updated);
    }

    private void LoadSelectedBinding()
    {
        if (refreshing || variants.SelectedItem is not FishingSpotScriptBinding binding) return;
        refreshing = true;
        try
        {
            fishingPointId.Value = Clamp(binding.FishingPointId, fishingPointId);
            SetVector(binding.PlayerPosition, playerX, playerY, playerZ);
            heading.Value = Clamp(binding.HeadingDegrees, heading);
            SetVector(binding.WaterTarget, targetX, targetY, targetZ);
            bindingStatus.Text =
                $"Function #{binding.FunctionIndex}, instruction #{binding.InstructionIndex}, 32-byte payload.";
            SetScriptEditorsEnabled(true);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SetScriptEditorsEnabled(bool enabled)
    {
        variants.Enabled = enabled;
        fishingPointId.Enabled = enabled;
        playerX.Enabled = enabled;
        playerY.Enabled = enabled;
        playerZ.Enabled = enabled;
        heading.Enabled = enabled;
        targetX.Enabled = enabled;
        targetY.Enabled = enabled;
        targetZ.Enabled = enabled;
    }

    private static NumericUpDown Number(
        int decimalPlaces = 3,
        decimal minimum = -1000000m,
        decimal maximum = 1000000m) => new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = decimalPlaces,
        Minimum = minimum,
        Maximum = maximum,
        Increment = decimalPlaces == 0 ? 1m : 0.1m,
    };

    private static Control VectorPanel(params NumericUpDown[] values)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = values.Length };
        for (var index = 0; index < values.Length; index++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / values.Length));
            panel.Controls.Add(values[index], index, 0);
        }
        return panel;
    }

    private static Control WithButton(Control editor, Control button)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(editor, 0, 0);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

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

    private static void SetVector(
        Vector3 value,
        NumericUpDown x,
        NumericUpDown y,
        NumericUpDown z)
    {
        x.Value = Clamp(value.X, x);
        y.Value = Clamp(value.Y, y);
        z.Value = Clamp(value.Z, z);
    }

    private static Vector3 ReadVector(NumericUpDown x, NumericUpDown y, NumericUpDown z) =>
        new((float)x.Value, (float)y.Value, (float)z.Value);

    private static decimal Clamp(float value, NumericUpDown control) =>
        Clamp((decimal)value, control);

    private static decimal Clamp(int value, NumericUpDown control) =>
        Clamp((decimal)value, control);

    private static decimal Clamp(decimal value, NumericUpDown control) =>
        Math.Clamp(value, control.Minimum, control.Maximum);
}
