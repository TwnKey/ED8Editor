using ED8Editor.Application;
using ED8Editor.Core;

namespace ED8Editor.Viewer;

/// <summary>
/// Puts the animations an imported file brought with it into the clip slots the
/// character already declares.
///
/// The slots are the point. A character's ANI logic calls its clips by slot name
/// — <c>WAIT</c>, <c>RUN</c>, <c>BTL_WAIT</c> — so an animation written into a
/// slot plays wherever that slot plays, with no script touched. What this window
/// asks is therefore only which animation goes where, and it answers that itself
/// wherever the names already agree.
/// </summary>
internal sealed class CharacterAnimationImportForm : Form
{
    private readonly IReadOnlyList<CpuAnimationClip> animations;
    private readonly IReadOnlyList<CharacterAnimationPackage.ClipSlot> slots;
    private readonly Func<IReadOnlyList<(CpuAnimationClip Clip, CharacterAnimationPackage.ClipSlot Slot)>, bool> commit;

    private readonly ListView pairs = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        CheckBoxes = true,
    };

    private readonly Label summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 46 };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };

    public CharacterAnimationImportForm(
        IReadOnlyList<CpuAnimationClip> animations,
        IReadOnlyList<CharacterAnimationPackage.ClipSlot> slots,
        string targetAssetId,
        Func<IReadOnlyList<(CpuAnimationClip, CharacterAnimationPackage.ClipSlot)>, bool> commit)
    {
        this.animations = animations;
        this.slots = slots;
        this.commit = commit;

        Text = $"Animations importées → emplacements de {targetAssetId}";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;

        pairs.Columns.Add("Animation importée", 300);
        pairs.Columns.Add("Durée", 80);
        pairs.Columns.Add("Canaux", 70);
        pairs.Columns.Add("Emplacement du jeu", 220);
        pairs.DoubleClick += (_, _) => ChooseSlot();

        var apply = new Button { Text = "Écrire les animations cochées", AutoSize = true };
        var cancel = new Button { Text = "Fermer", AutoSize = true };
        apply.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Close();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { apply, cancel });

        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Double-cliquez une ligne pour changer son emplacement",
        };
        group.Controls.Add(pairs);

        Controls.Add(group);
        Controls.Add(tools);
        Controls.Add(status);
        Controls.Add(summary);

        Populate();
    }

    private void Populate()
    {
        var slotNames = slots.Select(value => value.Slot).ToArray();
        var matched = 0;
        foreach (var animation in animations)
        {
            var guess = CharacterAnimationPackage.GuessSlot(animation.Name, slotNames);
            var item = new ListViewItem(animation.Name) { Checked = guess is not null };
            item.SubItems.Add($"{animation.Duration:0.###} s");
            item.SubItems.Add(animation.Channels.Count.ToString());
            item.SubItems.Add(guess ?? string.Empty);
            if (guess is not null) matched++;
            pairs.Items.Add(item);
        }

        summary.Text =
            $"{animations.Count} animation(s) importée(s), {slots.Count} emplacement(s) déclaré(s)"
            + $" par le personnage, {matched} appariée(s) par leur nom.\r\n"
            + "Écrire dans un emplacement remplace l'animation que le jeu y jouait ;"
            + " aucun script d'animation n'est modifié.";
    }

    private void ChooseSlot()
    {
        if (pairs.SelectedItems.Count == 0) return;
        var item = pairs.SelectedItems[0];

        using var window = new Form
        {
            Text = $"Emplacement pour « {item.Text} »",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 420),
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        list.Items.Add("(aucun — ne pas écrire cette animation)");
        foreach (var slot in slots) list.Items.Add(slot.Slot);
        list.SelectedIndex = Math.Max(0, list.Items.IndexOf(item.SubItems[3].Text));
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        window.Controls.Add(list);
        window.Controls.Add(ok);
        window.AcceptButton = ok;
        if (window.ShowDialog(this) != DialogResult.OK) return;

        var chosen = list.SelectedIndex <= 0 ? string.Empty : (string)list.SelectedItem!;
        item.SubItems[3].Text = chosen;
        item.Checked = chosen.Length != 0;
    }

    private void Apply()
    {
        var chosen = new List<(CpuAnimationClip, CharacterAnimationPackage.ClipSlot)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < pairs.Items.Count; index++)
        {
            var item = pairs.Items[index];
            if (!item.Checked) continue;
            var slotName = item.SubItems[3].Text;
            if (slotName.Length == 0) continue;
            if (!taken.Add(slotName))
            {
                MessageBox.Show(
                    this,
                    $"Deux animations visent l'emplacement « {slotName} ». Le jeu n'en jouerait"
                        + " qu'une : choisissez-en une seule.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            var slot = slots.First(value =>
                value.Slot.Equals(slotName, StringComparison.OrdinalIgnoreCase));
            chosen.Add((animations[index], slot));
        }

        if (chosen.Count == 0)
        {
            MessageBox.Show(
                this, "Cochez au moins une animation et donnez-lui un emplacement.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            if (commit(chosen)) Close();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
