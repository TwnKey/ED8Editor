using ED8Editor.Application;

namespace ED8Editor.Viewer;

/// <summary>
/// What a new mod is, asked once, at the moment it is created.
///
/// A project used to be a filename and nothing else. A mod has an author and a
/// purpose, and they belong with it rather than in a readme beside it — they are
/// what someone else reads when the mod reaches them.
///
/// It also asks the one question that decides where every later edit is written:
/// whether to work in the game's loose-loading <c>dev</c> folder. Files there are
/// picked up without the game being restarted, and they are usually not this mod's
/// alone — several mods' files end up in it — so being asked whether to adopt what
/// is already there is the difference between editing them and quietly ignoring them.
/// </summary>
internal sealed class NewModProjectForm : Form
{
    private readonly TextBox name = new() { Width = 380 };
    private readonly TextBox author = new() { Width = 380 };
    private readonly TextBox description = new()
    {
        Width = 380,
        Height = 90,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
    };

    private readonly CheckBox adopt = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Take the files already in dev/ into this project",
    };

    private readonly Label devNote = new()
    {
        AutoSize = false,
        Width = 470,
        Height = 46,
        ForeColor = Color.Gainsboro,
    };

    /// <summary>What the author called the mod.</summary>
    public string ModName => name.Text.Trim();
    public string ModAuthor => author.Text.Trim();
    public string ModDescription => description.Text.Trim();

    /// <summary>Whether to bring the loose-loading folder's files in.</summary>
    public bool AdoptDevelopmentFiles => adopt.Checked && adopt.Enabled;

    public NewModProjectForm(string gameDirectory, string suggestedName)
    {
        Text = "New mod project";
        Width = 560;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        name.Text = suggestedName;

        var development = Path.Combine(gameDirectory, "dev");
        var exists = Directory.Exists(development);
        adopt.Enabled = exists;
        adopt.Checked = exists;
        devNote.Text = exists
            ? $"Found {development}.\r\nEdits will be written there, where the game picks"
                + " them up without being restarted."
            : $"No dev folder at {development}.\r\nEdits will be written into the game"
                + " folder itself. Create dev/ and reopen the project to work loosely.";

        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        void Row(string label, Control editor)
        {
            rows.RowCount++;
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(
                new Label
                {
                    Text = label,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Padding = new Padding(0, 6, 8, 0),
                },
                0,
                rows.RowCount - 1);
            rows.Controls.Add(editor, 1, rows.RowCount - 1);
        }

        Row("Name", name);
        Row("Author", author);
        Row("Description", description);
        Row(string.Empty, adopt);
        Row(string.Empty, devNote);

        var ok = new Button { Text = "Create", AutoSize = true };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        ok.Click += (_, _) =>
        {
            if (ModName.Length == 0)
            {
                MessageBox.Show(
                    this, "The mod needs a name.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { ok, cancel });

        Controls.Add(rows);
        Controls.Add(tools);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
