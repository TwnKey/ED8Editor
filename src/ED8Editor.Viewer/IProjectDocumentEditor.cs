namespace ED8Editor.Viewer;

/// <summary>
/// A window holding edits to one file of the project.
///
/// The game keeps one idea across several files in several formats — a map is an
/// <c>.ops</c>, two scenario scripts and a package — and the editor answers with a
/// window per format. That split is the tool's, not the author's: what they have is
/// a project with unsaved work in it, and one keystroke should write all of it.
///
/// Implementing this is what puts a window's file in that keystroke, in the star
/// beside its name in the project tree, and in the list shown when closing. A window
/// that keeps no pending edits of its own — a modal Apply/Cancel dialog, or one that
/// edits a document another window owns — has nothing to declare here.
/// </summary>
internal interface IProjectDocumentEditor
{
    /// <summary>Whether this window is holding edits that are not on disk.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// Where those edits would be written, whether or not they have been written
    /// before. Null when the window has nothing open.
    /// </summary>
    string? DocumentPath { get; }

    /// <summary>
    /// Writes the edits under the name the game reads, asking nothing. False when
    /// the window could not, having already said why.
    /// </summary>
    bool SaveWithoutAsking();
}

/// <summary>
/// Saving from anywhere in the tool.
///
/// Ctrl+S in a window used to save that window and only that window, so which of
/// them had the focus decided how much of the work was written. It means the same
/// thing everywhere now: write all of it.
/// </summary>
internal static class ProjectSave
{
    /// <summary>
    /// Saves everything the project has open and unsaved. False only when there is
    /// no main window to do it — a save that was attempted and failed has already
    /// said so, and returning false for that would have each window try again and
    /// show the same error a second time.
    /// </summary>
    public static bool Everything()
    {
        foreach (var form in System.Windows.Forms.Application.OpenForms)
        {
            // The main window owns the map and the script panel, neither of which is
            // a window of its own, so it does the saving for everybody.
            if (form is ViewerForm viewer)
            {
                viewer.SaveProject();
                return true;
            }
        }
        return false;
    }
}
