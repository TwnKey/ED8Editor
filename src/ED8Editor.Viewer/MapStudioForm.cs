using ED8Editor.Application;
using ED8Editor.Models;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Viewer;

/// <summary>
/// Creates a map.
///
/// The game keeps a map as three files in three folders in two languages, plus a
/// model package — and nothing ties them together but the name. That is what this
/// hides; a map needs a model, so being asked for one is not worth a heading of
/// its own.
///
/// Getting into the new map is not this window's business. A way between two maps
/// is an OPS element like any other: the scene view already places one, with the
/// destination map and entry as ordinary fields. Building a second path for it
/// here would only give the two a chance to disagree.
///
/// Everything is written through the mod project, so a test can be undone.
/// </summary>
internal sealed class MapStudioForm : Form
{
    private readonly ModProject project;
    private readonly MapAuthoring maps;

    private readonly TextBox mapName = new() { Text = "z9100", Width = 160 };
    private readonly TextBox placeName = new() { Text = "New Area", Width = 260 };
    private readonly ComboBox placeKind = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260,
    };
    private readonly TextBox modelPath = new() { Width = 340, ReadOnly = true };
    private readonly Button browse = new() { Text = "Choose…", AutoSize = true };
    private readonly ComboBox skybox = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
    private readonly Button create = new() { Text = "Create the map", AutoSize = true };

    // A diagnostic, kept visible rather than hidden in code: a map that will not draw
    // is either failing on its shader or on its model, and the only way to tell them
    // apart is to change one of the two. The model stays authored from nothing.
    private readonly CheckBox useShippedShader = new()
    {
        Text = "Bind a compiled shader from a shipped package instead (diagnostic)",
        AutoSize = true,
    };

    private readonly TextBox shaderPath = new() { Width = 340, ReadOnly = true, Enabled = false };
    private readonly Button browseShader = new() { Text = "Choose…", AutoSize = true, Enabled = false };

    private readonly TextBox report = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    public MapStudioForm(ModProject project)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        maps = new MapAuthoring(project);

        Text = "Create a map";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        // The skies the game ships, most-used first. It is a free field as well as
        // a list: the set is what these maps happen to use, not a closed one.
        skybox.Items.AddRange(new object[]
        {
            "O_S00SKY00", "O_S00SKY01", "O_S00SKY02", "O_S00SKY03", "O_S00SKY05",
            "O_S00SKY10", "O_S00SKY11", "O_S00SKY12", string.Empty,
        });
        skybox.SelectedIndex = 0;
        placeKind.Items.AddRange(new object[]
        {
            new PlaceKindChoice("Dungeon / other", 6),
            new PlaceKindChoice("Town / building", 3),
            new PlaceKindChoice("Road / field", 4),
            new PlaceKindChoice("Capital district", 1),
        });
        placeKind.SelectedIndex = 0;

        browse.Click += (_, _) => ChooseModel();
        browseShader.Click += (_, _) => ChooseShader();
        create.Click += (_, _) => CreateMap();
        useShippedShader.CheckedChanged += (_, _) =>
        {
            shaderPath.Enabled = useShippedShader.Checked;
            browseShader.Enabled = useShippedShader.Checked;
        };

        Controls.Add(Build());
        Say("Nothing is copied from another map. The settings, scene script, model,"
            + " minimal material and shader are all authored by ED8Editor.");
        Say(string.Empty);
        Say("To get into the map, place an entry transition in the scene view of the map"
            + " you want to leave, and set its destination to this one.");
    }

    private Control Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var rows = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        AddRow(rows, "Map ID", mapName);
        AddRow(rows, "Display name", placeName);
        AddRow(rows, "Place category", placeKind);
        AddRow(rows, "Model", Side(modelPath, browse));
        AddRow(rows, "Skybox", skybox);
        AddRow(rows, string.Empty, useShippedShader);
        AddRow(rows, "Shader from", Side(shaderPath, browseShader));
        AddRow(rows, string.Empty, create);
        root.Controls.Add(rows);
        root.Controls.Add(report);
        return root;
    }

    private static void AddRow(TableLayoutPanel rows, string label, Control editor)
    {
        rows.RowCount++;
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 8, 0),
        }, 0, rows.RowCount - 1);
        rows.Controls.Add(editor, 1, rows.RowCount - 1);
    }

    /// <summary>
    /// Which shipped package to take a compiled shader from, for the diagnostic. A
    /// map package is the sensible choice — it carries the shader the game uses to
    /// draw map geometry, which is what an imported map is.
    /// </summary>
    private void ChooseShader()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a shipped package to take the compiled shader from",
            Filter = "Game packages (*.pkg)|*.pkg|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(project.GameDirectory, "data", "asset", "D3D11"),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) shaderPath.Text = dialog.FileName;
    }

    private static Control Side(Control left, Control right)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        panel.Controls.Add(left);
        panel.Controls.Add(right);
        return panel;
    }

    private void ChooseModel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the model the map is made of",
            Filter = "3D models (*.glb;*.gltf;*.fbx;*.dae;*.obj)|*.glb;*.gltf;*.fbx;*.dae;*.obj"
                + "|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) modelPath.Text = dialog.FileName;
    }

    private void CreateMap()
    {
        var name = mapName.Text.Trim();
        if (name.Length == 0) { Say("The map needs a name."); return; }
        var displayName = placeName.Text.Trim();
        if (displayName.Length == 0) { Say("The map needs a display name."); return; }
        if (modelPath.Text.Length == 0) { Say("Pick the model."); return; }
        if (useShippedShader.Checked && shaderPath.Text.Length == 0)
        {
            Say("Pick the package to take the compiled shader from, or clear the box.");
            return;
        }
        if (maps.Maps().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Say($"A map called '{name}' already exists. Pick another name, or revert the project.");
            return;
        }

        try
        {
            Say(string.Empty);
            Say($"Reading {Path.GetFileName(modelPath.Text)}…");
            var scene = new AssimpModelImporter().Import(modelPath.Text);
            Say($"  {scene.Meshes.Count} meshes, {scene.Meshes.Sum(m => m.Vertices.Count)} vertices,"
                + $" {scene.Meshes.Sum(m => m.Indices.Length) / 3} triangles,"
                + $" up {scene.CoordinateSystem.UpAxis}, unit {scene.CoordinateSystem.UnitScaleMeters}");

            var converted = ImportedModelAdapter.Convert(scene);
            foreach (var note in converted.Notes) Say("  " + note);
            if (converted.FlippedTriangles != 0)
            {
                Say($"  {converted.FlippedTriangles} triangles turned to agree with their normals");
            }
            var problems = converted.Model.Problems();
            if (problems.Count != 0)
            {
                Say("This model cannot be written: " + string.Join("; ", problems.Take(5)));
                return;
            }

            var written = MapModelPackage.WriteMinimal(
                project, name, converted.Model, Say,
                useShippedShader.Checked ? shaderPath.Text : null);
            var made = maps.CreateEmptyMap(
                name,
                MapSettings.Default with { Skybox = skybox.Text.Trim() },
                model: converted.Model);
            var placeFiles = new PlaceTableAuthoring(project).Upsert(
                name,
                displayName,
                ((PlaceKindChoice)placeKind.SelectedItem!).Value);
            Say(string.Empty);
            Say($"'{name}' created — {made.Files.Count + placeFiles.Count + 1} files, all tracked:");
            foreach (var file in made.Files.Append(written).Concat(placeFiles))
            {
                Say("  " + Path.GetRelativePath(project.GameDirectory, file));
            }
            Say(string.Empty);
            Say("No textures are bound yet, so expect it plain. Its collision is the geometry"
                + " you gave it.");
            Say("The sky is an object in the map, so changing it later is editing that"
                + " object's asset in the scene view — the same as any other prop.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or NotSupportedException or InvalidPhyreException)
        {
            Say("The map was not created: " + exception.Message);
        }
    }

    private void Say(string line)
    {
        report.AppendText(line + Environment.NewLine);
        report.SelectionStart = report.TextLength;
        report.ScrollToCaret();
    }

    private sealed record PlaceKindChoice(string Label, short Value)
    {
        public override string ToString() => Label;
    }
}
