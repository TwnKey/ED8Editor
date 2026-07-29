using ED8Editor.Models;

namespace ED8Editor.Viewer;

internal sealed class ModelImportSourceDialog : Form
{
    private readonly ComboBox candidates = new()
    {
        Dock = DockStyle.Top,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(ModelImportCandidate.DisplayName),
    };

    private ModelImportSourceDialog(
        string packageRoot,
        IReadOnlyList<ModelImportCandidate> choices)
    {
        Text = "Choose model source";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 145);
        var explanation = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(10),
            Text = $"The package '{Path.GetFileName(packageRoot)}' contains several model files. "
                + "Choose the source explicitly; the importer will not guess.",
        };
        candidates.Items.AddRange(choices.Cast<object>().ToArray());
        candidates.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(5),
        };
        var accept = new Button { Text = "Import", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);
        Controls.Add(candidates);
        Controls.Add(explanation);
        AcceptButton = accept;
        CancelButton = cancel;
    }

    public static ModelImportCandidate? Choose(
        IWin32Window owner,
        string packageRoot,
        IReadOnlyList<ModelImportCandidate> choices)
    {
        if (choices.Count == 0) return null;
        using var dialog = new ModelImportSourceDialog(packageRoot, choices);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.candidates.SelectedItem as ModelImportCandidate
            : null;
    }
}
