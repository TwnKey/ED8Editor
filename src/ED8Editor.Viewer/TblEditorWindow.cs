namespace ED8Editor.Viewer;

/// <summary>
/// The table editor in a window of its own, so the main window keeps its panels
/// for the scene and its script.
/// </summary>
internal sealed class TblEditorWindow : Form, IProjectDocumentEditor
{
    private readonly TblEditorControl editor;

    public bool HasUnsavedChanges => editor.HasUnsavedChanges;

    public string? DocumentPath => editor.DocumentPath;

    public bool SaveWithoutAsking() => editor.SaveCurrent();

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

    /// <summary>
    /// Ctrl+S saves the whole project, not just this table: which window has the
    /// focus should not decide how much of the author's work reaches the disk.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == (Keys.Control | Keys.S))
        {
            if (!ProjectSave.Everything()) editor.SaveCurrent();
            return true;
        }
        if (key == (Keys.Control | Keys.Shift | Keys.S))
        {
            editor.SaveCurrent(saveAs: true);
            return true;
        }
        return base.ProcessCmdKey(ref message, key);
    }
}
