using ED8Editor.Tables;

namespace ED8Editor.Application;

/// <summary>What an attachment edit touched, so the caller can say so.</summary>
public sealed record AttachmentEditResult(
    IReadOnlyList<string> Files,
    int RecordIndex);

/// <summary>
/// Hangs equipment on a character, through the mod project rather than around
/// it.
///
/// Every table this touches is captured before it is written and tracked after,
/// so a mod holds its own copy of each file it changed and the original can be
/// put back. Editing a game file without going through here would leave the
/// project unable to say what it had done.
///
/// The three names an attachment needs come from three different places, and an
/// editor should offer each as a list rather than a box to type in:
/// the character from <c>t_name</c>, the item from <c>t_item</c> — the
/// attachment's item id is that table's <c>id</c> — and the point from the
/// character's own model, which states the sixteen it offers as PLocator
/// objects.
/// </summary>
public sealed class AttachmentEditor
{
    private const string AttachTable = "text/dat/t_attach.tbl";
    private const string AttachTableUs = "text/dat_us/t_attach.tbl";

    private readonly ModProject project;

    public AttachmentEditor(ModProject project)
        => this.project = project ?? throw new ArgumentNullException(nameof(project));

    /// <summary>Where the attach table lives, English variant included.</summary>
    public IReadOnlyList<string> TablePaths()
    {
        var paths = new List<string>();
        foreach (var relative in new[] { AttachTable, AttachTableUs })
        {
            var path = Path.Combine(project.GameDirectory, "data", relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>Every attachment the game states, for a character or for all.</summary>
    public IReadOnlyList<Cs1Attachment> Read(int? character = null)
    {
        var path = TablePaths().FirstOrDefault();
        if (path is null) return Array.Empty<Cs1Attachment>();
        var table = Cs1AttachTable.Read(path);
        return character is null
            ? table.Attachments
            : table.Attachments.Where(value => value.Character == character).ToArray();
    }

    /// <summary>
    /// Writes <paramref name="attachment"/> into every copy of the table the
    /// game ships, capturing each original first and tracking each save.
    ///
    /// Both the Japanese and English tables are written: leaving one behind
    /// gives a mod that works in one language and not the other, which is the
    /// kind of failure nobody thinks to look for.
    /// </summary>
    public AttachmentEditResult Set(Cs1Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var paths = TablePaths();
        if (paths.Count == 0)
        {
            throw new InvalidOperationException(
                "The game folder has no t_attach.tbl to edit.");
        }

        var written = new List<string>();
        var index = -1;
        foreach (var path in paths)
        {
            project.CaptureOriginal(path);
            var table = Cs1AttachTable.Read(path);
            index = table.Set(attachment);
            table.Write();
            project.TrackSave(path);
            written.Add(path);
        }
        return new AttachmentEditResult(written, index);
    }

    /// <summary>Takes an attachment off, in every copy of the table.</summary>
    public AttachmentEditResult Remove(int character, int slot, int itemId)
    {
        var written = new List<string>();
        var index = -1;
        foreach (var path in TablePaths())
        {
            var table = Cs1AttachTable.Read(path);
            var found = table.Attachments.FirstOrDefault(value =>
                value.Character == character && value.Slot == slot && value.ItemId == itemId);
            if (found is null) continue;
            project.CaptureOriginal(path);
            table.Remove(found.Index);
            table.Write();
            project.TrackSave(path);
            written.Add(path);
            index = found.Index;
        }
        return new AttachmentEditResult(written, index);
    }
}
