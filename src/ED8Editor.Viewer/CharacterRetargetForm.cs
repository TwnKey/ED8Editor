using ED8Editor.Application;
using ED8Editor.Core;
using ED8Editor.Models;

namespace ED8Editor.Viewer;

/// <summary>
/// Fits an imported model onto a character the game already has, so it rides
/// every animation that character already owns.
///
/// The window exists to make one decision visible and everything else
/// automatic. The decision is which of the target's material segments the
/// imported geometry replaces — nothing can guess that, since it is what the
/// author means the mesh to be. Everything else is derived and shown rather
/// than asked: whether the import brought its own weights, which of its bones
/// answer to which of the game's, and how much of that the guess could not
/// settle on its own.
/// </summary>
internal sealed class CharacterRetargetForm : Form
{
    private readonly ImportedModelScene scene;
    private readonly CpuModel target;
    private readonly string targetAssetId;
    private readonly Func<int, IReadOnlyDictionary<string, string>?, bool> commit;

    private readonly ListBox segments = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListView bones = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        LabelEdit = true,
    };

    private readonly Label summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 58 };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };
    private readonly Dictionary<string, string> overrides = new(StringComparer.Ordinal);

    public CharacterRetargetForm(
        ImportedModelScene scene,
        CpuModel target,
        string targetAssetId,
        Func<int, IReadOnlyDictionary<string, string>?, bool> commit)
    {
        this.scene = scene;
        this.target = target;
        this.targetAssetId = targetAssetId;
        this.commit = commit;

        Text = $"Poser {Path.GetFileName(scene.SourcePath)} sur {targetAssetId}";
        Width = 940;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;

        var apply = new Button { Text = "Écrire dans le personnage", AutoSize = true };
        var cancel = new Button { Text = "Annuler", AutoSize = true };
        apply.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Close();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { apply, cancel });

        bones.Columns.Add("Os importé", 260);
        bones.Columns.Add("Os du jeu", 200);
        bones.Columns.Add("Origine", 110);
        bones.AfterLabelEdit += (_, eventArgs) => eventArgs.CancelEdit = true;
        bones.DoubleClick += (_, _) => EditSelectedBone();

        var segmentsGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Segment à remplacer" };
        segmentsGroup.Controls.Add(segments);
        var bonesGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Correspondance des os — double-cliquez pour corriger",
        };
        bonesGroup.Controls.Add(bones);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300 };
        split.Panel1.Controls.Add(segmentsGroup);
        split.Panel2.Controls.Add(bonesGroup);

        Controls.Add(split);
        Controls.Add(tools);
        Controls.Add(status);
        Controls.Add(summary);

        Populate();
    }

    private void Populate()
    {
        var labels = CharacterRetargetPackage.SegmentLabels(target);
        for (var index = 0; index < labels.Count; index++)
        {
            segments.Items.Add($"{index}: {labels[index]}");
        }
        if (segments.Items.Count != 0) segments.SelectedIndex = 0;

        if (!scene.IsSkinned)
        {
            summary.Text =
                $"L'import n'a pas de poids : il sera mis à la taille de {targetAssetId}"
                + " puis lié aux os les plus proches, deux par sommet.\r\n"
                + "Aucune correspondance d'os n'est nécessaire — le squelette du jeu"
                + " est utilisé tel quel, donc toutes ses animations marchent.";
            bones.Enabled = false;
            return;
        }

        var targetNames = target.Skeleton?.Joints.Select(joint => joint.Name).ToArray()
            ?? Array.Empty<string>();
        var sourceNames = scene.Nodes.Select(node => node.Name).ToArray();
        var guessed = Cs1RigNameMapper.AutoMap(sourceNames, targetNames);

        foreach (var one in guessed)
        {
            var item = new ListViewItem(one.SourceName);
            item.SubItems.Add(one.TargetName ?? string.Empty);
            item.SubItems.Add(one.TargetName is null ? "à décider" : one.ByAlias ? "par synonyme" : "exact");
            if (one.TargetName is null) item.ForeColor = Color.Firebrick;
            bones.Items.Add(item);
        }

        var matched = guessed.Count(one => one.TargetName is not null);
        summary.Text =
            $"L'import a son propre squelette : {guessed.Count} os, dont {matched} reconnus"
            + $" parmi les {targetNames.Length} de {targetAssetId}.\r\n"
            + "Les os non reconnus ne recevront aucune animation — un os laissé vide reste"
            + " immobile, ce qui vaut mieux qu'une correspondance devinée de travers.";
    }

    private void EditSelectedBone()
    {
        if (bones.SelectedItems.Count == 0 || target.Skeleton is null) return;
        var item = bones.SelectedItems[0];
        var chosen = ChooseTarget(item.Text, item.SubItems[1].Text);
        if (chosen is null) return;
        item.SubItems[1].Text = chosen;
        item.SubItems[2].Text = chosen.Length == 0 ? "écarté" : "choisi";
        item.ForeColor = chosen.Length == 0 ? Color.Gray : SystemColors.WindowText;
        if (chosen.Length == 0) overrides.Remove(item.Text);
        else overrides[item.Text] = chosen;
    }

    /// <summary>A game bone picked from the target's own list, or none at all.</summary>
    private string? ChooseTarget(string sourceName, string current)
    {
        using var window = new Form
        {
            Text = $"Os du jeu pour « {sourceName} »",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 420),
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        list.Items.Add("(aucun — cet os n'est pas animé)");
        foreach (var joint in target.Skeleton!.Joints)
        {
            if (joint.Name.Length != 0) list.Items.Add(joint.Name);
        }
        list.SelectedIndex = Math.Max(0, list.Items.IndexOf(current));
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        window.Controls.Add(list);
        window.Controls.Add(ok);
        window.AcceptButton = ok;
        return window.ShowDialog(this) == DialogResult.OK
            ? list.SelectedIndex <= 0 ? string.Empty : (string)list.SelectedItem!
            : null;
    }

    private void Apply()
    {
        if (segments.SelectedIndex < 0)
        {
            MessageBox.Show(
                this, "Choisissez le segment à remplacer.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyDictionary<string, string>? mapping = null;
        if (scene.IsSkinned)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ListViewItem item in bones.Items)
            {
                var chosen = item.SubItems[1].Text;
                if (chosen.Length != 0) table[item.Text] = chosen;
            }
            if (table.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Aucun os de l'import ne correspond à un os du jeu : le résultat ne"
                        + " suivrait aucune animation. Corrigez au moins les os principaux.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            mapping = table;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            if (commit(segments.SelectedIndex, mapping)) Close();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
