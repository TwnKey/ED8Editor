namespace ED8Editor.Viewer;

/// <summary>
/// The table editor in a window of its own, so the main window keeps its panels
/// for the scene and its script.
/// </summary>
internal sealed class TblEditorWindow : Form
{
    private readonly TblEditorControl editor;

    public TblEditorWindow(string gameDataPath, string? scriptPath, EventHandler onCatalogChanged)
    {
        Text = "Table editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 720);
        MinimumSize = new Size(840, 520);
        editor = new TblEditorControl(gameDataPath, scriptPath);
        editor.CatalogChanged += onCatalogChanged;
        Controls.Add(editor);
    }

    /// <summary>Saves whatever table is open, for the window's own shortcut.</summary>
    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == (Keys.Control | Keys.S))
        {
            editor.SaveCurrent();
            return true;
        }
        return base.ProcessCmdKey(ref message, key);
    }
}
