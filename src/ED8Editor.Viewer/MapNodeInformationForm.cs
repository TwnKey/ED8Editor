using System.Globalization;
using ED8Editor.Application;

namespace ED8Editor.Viewer;

/// <summary>
/// Edits the parameter each collision surface of a map carries — the value the game
/// reads to decide what the ground is made of, and from there which footstep effect
/// to play.
///
/// The choices offered are the values the shipped maps actually give a collision
/// node, each shown with the maps that give it. That is deliberate: the executable
/// holds four effect files in a table of five slots, but which parameter selects
/// which of them is not established — writing 8, the value r0510 states for its own
/// surface, produced no snow in game. Naming the choices "snow" or "gravel" would be
/// inventing a mapping; naming the maps that use them is a fact the author can judge.
/// </summary>
internal sealed class MapNodeInformationForm : Form
{
    private readonly ListView nodes = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
    };

    private readonly ComboBox parameter = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    private readonly Label explanation = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        Text = "Le paramètre dit de quoi la surface est faite. ed8.exe le transforme en"
            + " l'un de foot01, foot09, foot10 ou foot12 sous data/effects/system —"
            + " quatre fichiers dans une table de cinq fentes, compilée dans"
            + " l'exécutable, donc non extensible. Quelle valeur choisit quel effet"
            + " n'est pas établi : les cartes citées sont ce que le jeu fait de chaque"
            + " valeur, pas une signification.",
    };

    private readonly MapNodeInformation information;
    private readonly IReadOnlyList<MapNodeParameterUse> choices;
    private readonly string path;
    private bool refreshing;

    public MapNodeInformationForm(
        string mapName,
        string nodeInformationPath,
        MapNodeInformation information,
        IReadOnlyList<MapNodeParameterUse> choices)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(choices);
        this.information = information;
        this.choices = choices;
        path = nodeInformationPath;

        Text = $"Surfaces de collision — {mapName}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 420);
        Size = new Size(760, 520);

        nodes.Columns.Add("Surface", 120);
        nodes.Columns.Add("param0", 80);
        nodes.Columns.Add("Cartes du jeu qui emploient cette valeur", 480);
        nodes.SelectedIndexChanged += (_, _) => ShowSelected();

        // Every value a shipped map gives a collision node, and a plain number for
        // anything the author wants that no shipped map uses.
        var offered = choices.Select(value => value.Value).ToHashSet();
        for (var value = 0; value < 16; value++)
        {
            if (!offered.Contains(value)) offered.Add(value);
        }
        foreach (var value in offered.OrderBy(value => value))
        {
            parameter.Items.Add(new Choice(value, Users(value)));
        }
        parameter.SelectedIndexChanged += (_, _) => Apply();

        var save = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, AutoSize = true };
        var close = new Button { Text = "Fermer", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => Save();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);

        var chooser = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chooser.Controls.Add(new Label { Text = "param0", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        chooser.Controls.Add(parameter, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(nodes, 0, 0);
        layout.Controls.Add(chooser, 0, 1);
        layout.Controls.Add(explanation, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = close;

        Fill();
    }

    private string Users(int value)
    {
        var found = choices.FirstOrDefault(one => one.Value == value);
        if (found is null || found.Maps.Count == 0) return "aucune carte du jeu";
        var shown = string.Join(", ", found.Maps.Take(6));
        return found.Maps.Count > 6
            ? $"{shown} … et {found.Maps.Count - 6} autres"
            : shown;
    }

    private void Fill()
    {
        refreshing = true;
        nodes.Items.Clear();
        foreach (var node in information.ChosenNodes)
        {
            var item = new ListViewItem(node);
            item.SubItems.Add(information[node].ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(Users(information[node]));
            item.Tag = node;
            nodes.Items.Add(item);
        }
        refreshing = false;
        if (nodes.Items.Count != 0) nodes.Items[0].Selected = true;
    }

    private void ShowSelected()
    {
        if (refreshing || nodes.SelectedItems.Count == 0) return;
        refreshing = true;
        var value = information[(string)nodes.SelectedItems[0].Tag!];
        for (var at = 0; at < parameter.Items.Count; at++)
        {
            if (((Choice)parameter.Items[at]!).Value != value) continue;
            parameter.SelectedIndex = at;
            break;
        }
        refreshing = false;
    }

    private void Apply()
    {
        if (refreshing || nodes.SelectedItems.Count == 0 || parameter.SelectedItem is null) return;
        var node = (string)nodes.SelectedItems[0].Tag!;
        var value = ((Choice)parameter.SelectedItem).Value;
        information[node] = value;
        nodes.SelectedItems[0].SubItems[1].Text = value.ToString(CultureInfo.InvariantCulture);
        nodes.SelectedItems[0].SubItems[2].Text = Users(value);
    }

    private void Save()
    {
        try
        {
            information.Save(path);
        }
        catch (IOException failure)
        {
            MessageBox.Show(this, failure.Message, "Enregistrement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }

    private sealed record Choice(int Value, string Maps)
    {
        public override string ToString() => $"{Value}  —  {Maps}";
    }
}
