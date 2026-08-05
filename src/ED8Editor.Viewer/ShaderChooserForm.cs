using ED8Editor.Application;
using ED8Editor.Shaders.Compilation;

namespace ED8Editor.Viewer;

/// <summary>What a material was pointed at, and with what values.</summary>
/// <param name="Cluster">
/// The compiled effect, whichever it came from. A game variant travels with its
/// file just as an author's does: a package whose material names a shader it does
/// not carry is bound to whatever else happens to be loaded, which is not a mod
/// anyone can ship.
/// </param>
/// <param name="Custom">Whether the author wrote it, rather than the game.</param>
internal sealed record ShaderChoice(
    string AssetName,
    byte[] Cluster,
    IReadOnlyList<ShaderParameter> Parameters,
    IReadOnlyDictionary<string, string> Values,
    bool Custom);

/// <summary>
/// Picks the shader a material draws with — one of the game's, or one written by
/// the author and compiled here.
///
/// The same window serves every editor that has materials, which is all of them:
/// a map's surfaces, a piece of equipment, a character, an enemy. Nothing in it
/// knows which caller it is answering.
///
/// Custom HLSL is compiled on the spot rather than pointed at as a file someone
/// forged earlier. That is the difference between an editor a creator can express
/// themselves in and one that only re-arranges what already exists — and it is
/// why the compile errors are shown here rather than in a console the author
/// never sees.
/// </summary>
internal sealed class ShaderChooserForm : Form
{
    private readonly string gameDirectory;

    private readonly TextBox filter = new()
    {
        Dock = DockStyle.Top,
        PlaceholderText = "Filtrer : hash, ou un commutateur (ALPHA_TESTING, DOUBLE_SIDED…)",
    };

    private readonly ListBox variants = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox switches = new()
    {
        Dock = DockStyle.Bottom,
        Height = 76,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };

    private readonly TextBox hlslPath = new() { Dock = DockStyle.Top, ReadOnly = true };
    private readonly TextBox hlslReport = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly DataGridView values = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly TabControl sources = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };

    private IReadOnlyList<ShaderVariant> all = Array.Empty<ShaderVariant>();
    private IReadOnlyList<ShaderVariant> shown = Array.Empty<ShaderVariant>();
    private IReadOnlyList<ShaderParameter> parameters = Array.Empty<ShaderParameter>();
    private byte[]? chosenCluster;
    private string chosenName = string.Empty;
    private bool chosenIsCustom;

    /// <summary>What the author settled on, or null if they backed out.</summary>
    public ShaderChoice? Choice { get; private set; }

    public ShaderChooserForm(string gameDirectory, string materialName)
    {
        this.gameDirectory = gameDirectory;

        Text = $"Shader pour « {materialName} »";
        Width = 1040;
        Height = 660;
        StartPosition = FormStartPosition.CenterParent;

        values.Columns.Add("name", "Paramètre");
        values.Columns.Add("kind", "Type");
        var value = new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Valeur" };
        values.Columns.Add(value);
        values.Columns["name"]!.ReadOnly = true;
        values.Columns["kind"]!.ReadOnly = true;

        // The game's own variants.
        var gameTab = new TabPage("Variantes du jeu") { Padding = new Padding(6) };
        var gamePanel = new Panel { Dock = DockStyle.Fill };
        gamePanel.Controls.Add(variants);
        gamePanel.Controls.Add(filter);
        gamePanel.Controls.Add(switches);
        gameTab.Controls.Add(gamePanel);
        sources.TabPages.Add(gameTab);

        // The author's own.
        var mineTab = new TabPage("Mon HLSL") { Padding = new Padding(6) };
        var choose = new Button { Text = "Choisir un fichier…", AutoSize = true, Dock = DockStyle.Top };
        var compile = new Button { Text = "Compiler", AutoSize = true, Dock = DockStyle.Top };
        choose.Click += (_, _) => ChooseHlsl();
        compile.Click += (_, _) => Compile();
        var minePanel = new Panel { Dock = DockStyle.Fill };
        minePanel.Controls.Add(hlslReport);
        minePanel.Controls.Add(compile);
        minePanel.Controls.Add(choose);
        minePanel.Controls.Add(hlslPath);
        mineTab.Controls.Add(minePanel);
        sources.TabPages.Add(mineTab);

        var ok = new Button { Text = "Utiliser ce shader", AutoSize = true };
        var cancel = new Button { Text = "Annuler", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { ok, cancel });

        var valuesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Paramètres réglables" };
        valuesGroup.Controls.Add(values);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 520 };
        split.Panel1.Controls.Add(sources);
        split.Panel2.Controls.Add(valuesGroup);

        Controls.Add(split);
        Controls.Add(tools);
        Controls.Add(status);
        CancelButton = cancel;

        filter.TextChanged += (_, _) => ApplyFilter();
        variants.SelectedIndexChanged += (_, _) => ShowVariant();
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        status.Text = "Lecture des variantes du jeu…";
        var directory = gameDirectory;
        all = await Task.Run(() => ShaderVariantCatalog.Load(directory));
        if (IsDisposed) return;
        ApplyFilter();
        status.Text = $"{all.Count} variantes. Les commutateurs d'une variante se lisent"
            + " quand vous la sélectionnez.";
    }

    private void ApplyFilter()
    {
        var wanted = filter.Text.Trim();
        shown = wanted.Length == 0
            ? all
            : all.Where(one =>
                one.AssetName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || one.Switches.Any(value =>
                    value.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        variants.BeginUpdate();
        variants.Items.Clear();
        foreach (var one in shown.Take(2000)) variants.Items.Add(one.AssetName);
        variants.EndUpdate();
    }

    private void ShowVariant()
    {
        if (variants.SelectedIndex < 0 || variants.SelectedIndex >= shown.Count) return;
        var variant = shown[variants.SelectedIndex];
        Cursor = Cursors.WaitCursor;
        try
        {
            var declared = ShaderVariantCatalog.Switches(variant);
            switches.Text = declared.Count == 0
                ? "Cette variante ne déclare aucun commutateur."
                : string.Join(Environment.NewLine, declared);
            chosenCluster = ShaderVariantCatalog.Cluster(variant);
            chosenName = variant.AssetName;
            chosenIsCustom = false;
            Fill(ShaderVariantCatalog.Parameters(variant));
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ChooseHlsl()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Votre shader",
            Filter = "HLSL (*.hlsl;*.fx;*.txt)|*.hlsl;*.fx;*.txt|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        hlslPath.Text = dialog.FileName;
        hlslReport.Text = "Compilez pour voir ce que le shader déclare.";
    }

    /// <summary>
    /// Compiles what the author wrote, here and now. A failure is shown as the
    /// compiler stated it — a shader that does not build is a mistake to fix, not
    /// an error to hide behind a message of our own.
    /// </summary>
    private void Compile()
    {
        if (hlslPath.Text.Length == 0)
        {
            MessageBox.Show(
                this, "Choisissez d'abord un fichier.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            var source = File.ReadAllText(hlslPath.Text);
            var name = "ed8.fx#" + Path.GetFileNameWithoutExtension(hlslPath.Text).ToUpperInvariant();
            chosenCluster = new PhyreEffectClusterBuilder()
                .BuildFromSources(source, source, name, name + ".phyre");
            chosenName = name;
            chosenIsCustom = true;

            var declared = ED8Editor.Phyre.Authoring.PhyreEffectParameters.Read(chosenCluster);
            Fill(declared.Values
                .Select(one => new ShaderParameter(
                    one.Name, one.Semantic, one.DataType,
                    ShaderVariantCatalog.Components(one.DataType),
                    one.Semantic is 64 or 65 or 66 or 67 or 68 or 71))
                .OrderByDescending(one => one.Settable)
                .ThenBy(one => one.Name, StringComparer.Ordinal)
                .ToArray());

            hlslReport.Text =
                $"Compilé : {compiled.Length} octets, {declared.Count} paramètre(s) déclaré(s)."
                + Environment.NewLine
                + "Il sera écrit sous le nom " + name + "." + Environment.NewLine
                + string.Join(Environment.NewLine, declared.Values
                    .Select(one => $"  {one.Name,-30} type {one.Semantic,3}  donnee {one.DataType,3}"));
            status.Text = $"{name} compilé.";
        }
        catch (Exception failure)
        {
            chosenCluster = null;
            chosenName = string.Empty;
            hlslReport.Text = failure.Message;
            status.Text = "La compilation a échoué.";
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Shows what the chosen shader takes. Only what a material fills is offered:
    /// the rest is the engine's to supply, and a value typed for one of those
    /// would be overwritten before it was ever read.
    /// </summary>
    private void Fill(IReadOnlyList<ShaderParameter> declared)
    {
        parameters = declared;
        values.Rows.Clear();
        foreach (var one in declared.Where(value => value.Settable))
        {
            var row = values.Rows[values.Rows.Add(one.Name, one.Kind, Default(one))];
            row.Tag = one;
        }
        var engine = declared.Count(one => !one.Settable);
        status.Text = $"{values.Rows.Count} paramètre(s) réglable(s)"
            + (engine == 0 ? "." : $", {engine} alimenté(s) par le moteur.");
    }

    /// <summary>
    /// What a parameter starts at. Zero for a value and nothing for a texture:
    /// neither is a guess at what the author wants, which is the point — an
    /// invented default is a value nobody chose and everybody inherits.
    /// </summary>
    private static string Default(ShaderParameter parameter) => parameter.DataType switch
    {
        52 => string.Empty,
        49 => "1 0 0 0  0 1 0 0  0 0 1 0  0 0 0 1",
        _ => string.Join(" ", Enumerable.Repeat("0", Math.Max(1, parameter.Components))),
    };

    private void Accept()
    {
        if (chosenCluster is null || chosenName.Length == 0)
        {
            MessageBox.Show(
                this, "Choisissez une variante, ou compilez votre HLSL.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in values.Rows)
        {
            if (row.Tag is not ShaderParameter parameter) continue;
            chosen[parameter.Name] = row.Cells["value"].Value?.ToString() ?? string.Empty;
        }
        Choice = new ShaderChoice(chosenName, chosenCluster, parameters, chosen, chosenIsCustom);
        DialogResult = DialogResult.OK;
        Close();
    }
}
