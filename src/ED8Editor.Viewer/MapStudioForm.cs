using System.Numerics;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Core;
using ED8Editor.Models;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;
using ED8Editor.Rendering;
using ED8Editor.Scene;

namespace ED8Editor.Viewer;

/// <summary>
/// Makes a map, and lets it be made again.
///
/// The game keeps a map as three files in three folders in two languages, plus a
/// model package — and nothing ties them together but the name. That is what this
/// hides; a map needs a model, so being asked for one is not worth a heading of
/// its own.
///
/// Nothing is copied from another map. The model, its collision, its materials and
/// the shader each material draws with are all chosen here and written by this
/// editor. Borrowing a shipped map's shader was once offered as a diagnostic and is
/// gone: a creator picks a shader, from the game's catalogue or from their own HLSL,
/// and picks it per material, because a map does not have one.
///
/// What the author chose is written down beside the mod project, so opening the map
/// again brings back the form as it was rather than an empty one.
///
/// Getting into the new map is not this window's business. A way between two maps
/// is an OPS element like any other: the scene view already places one, with the
/// destination map and entry as ordinary fields.
/// </summary>
internal sealed class MapStudioForm : Form
{
    private readonly ModProject project;
    private readonly MapAuthoring maps;
    private readonly string gameDataPath;
    private readonly EditorProjectLoader loader;
    private readonly D3D11GraphicsDevice graphics;

    private readonly TextBox mapName = new() { Text = "z9100", Width = 160 };
    private readonly TextBox placeName = new() { Text = "New Area", Width = 260 };
    private readonly ComboBox placeKind = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260,
    };

    private readonly TextBox modelPath = new() { Width = 300, ReadOnly = true };
    private readonly Button browse = new() { Text = "Choose…", AutoSize = true };
    private readonly ComboBox skybox = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
    private readonly CheckBox showSkybox = new()
    {
        Text = "Show the sky in the preview",
        AutoSize = true,
        Checked = true,
    };

    private readonly Button create = new() { Text = "Create the map", AutoSize = true };

    private readonly ListBox meshList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox collisionKind = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 210,
    };

    private readonly NumericUpDown collisionIndex = new()
    {
        Minimum = 0,
        Maximum = 99,
        Width = 55,
    };

    private readonly Button assignCollision = new() { Text = "Assign", AutoSize = true };
    private readonly Button clearCollision = new() { Text = "Clear", AutoSize = true };

    private readonly ListBox materialList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button chooseShader = new() { Text = "Material shader…", AutoSize = true };

    private readonly Panel previewHost = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(18, 20, 25),
    };

    private readonly TextBox report = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 33 };

    private PhyreModelSource? model;
    private CpuModel? preview;
    private D3D11ModelResources? previewResources;
    private D3D11ModelResources? skyResources;
    private string skyLoaded = string.Empty;
    private D3D11Viewport? viewport;

    /// <summary>Which node each mesh becomes collision on, by mesh index.</summary>
    private readonly Dictionary<int, string> collisionNodes = new();

    /// <summary>What each material draws with, by material name.</summary>
    private readonly Dictionary<string, ShaderAssignment> shaders = new(StringComparer.Ordinal);

    /// <summary>Where each assigned shader came from, so it can be written down.</summary>
    private readonly Dictionary<string, string?> shaderSources = new(StringComparer.Ordinal);

    private IReadOnlyList<string> materialNames = Array.Empty<string>();
    private float yawDegrees = 35f;
    private float pitchDegrees = 20f;
    private float distance = 40f;
    private Vector3 centre;
    private bool orbiting;
    private Point previousMouse;
    private readonly string? reopening;

    public MapStudioForm(
        ModProject project,
        string gameDataPath,
        EditorProjectLoader loader,
        D3D11GraphicsDevice graphics,
        string? openMap = null)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.gameDataPath = gameDataPath ?? throw new ArgumentNullException(nameof(gameDataPath));
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        maps = new MapAuthoring(project);
        reopening = openMap;

        Text = "Map editor";
        Width = 1280;
        Height = 800;
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

        // The three prefixes the game's own collision nodes use. What distinguishes
        // them is not established here: 832 nodes are named CA, 3733 CK and 3764 CS
        // across the shipped maps, and no reading of those files has told us what
        // the letter selects. So the choice is offered with the counts and without
        // an invented meaning — naming one "walkable" because it usually is would
        // be a guess wearing a label.
        collisionKind.Items.AddRange(new object[]
        {
            new CollisionKindChoice("CK — the most common (3733 nodes)", "CK"),
            new CollisionKindChoice("CS — almost as common (3764)", "CS"),
            new CollisionKindChoice("CA — the rarest (832)", "CA"),
        });
        collisionKind.SelectedIndex = 0;

        browse.Click += (_, _) => ChooseModel();
        create.Click += (_, _) => CreateMap();
        assignCollision.Click += (_, _) => AssignCollision();
        clearCollision.Click += (_, _) => ClearCollision();
        chooseShader.Click += (_, _) => ChooseMaterialShader();
        materialList.DoubleClick += (_, _) => ChooseMaterialShader();
        meshList.SelectedIndexChanged += (_, _) => { };
        skybox.TextChanged += (_, _) => { skyLoaded = string.Empty; };

        previewHost.MouseWheel += (_, eventArgs) =>
            distance = Math.Clamp(distance * (eventArgs.Delta > 0 ? 0.85f : 1.18f), 0.05f, 5000f);
        previewHost.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            orbiting = true;
            previousMouse = eventArgs.Location;
            previewHost.Capture = true;
        };
        previewHost.MouseMove += (_, eventArgs) =>
        {
            if (!orbiting) return;
            yawDegrees += eventArgs.X - previousMouse.X;
            pitchDegrees = Math.Clamp(pitchDegrees + eventArgs.Y - previousMouse.Y, -89f, 89f);
            previousMouse = eventArgs.Location;
        };
        previewHost.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            orbiting = false;
            previewHost.Capture = false;
        };

        Controls.Add(Build());
        renderTimer.Tick += (_, _) => Render();
        renderTimer.Start();
        FormClosed += (_, _) =>
        {
            renderTimer.Stop();
            viewport?.Dispose();
            previewResources?.Dispose();
            skyResources?.Dispose();
        };

        if (reopening is not null)
        {
            create.Text = "Apply the changes";
            Reopen(reopening);
        }
        else
        {
            Say("Nothing is copied from another map. The settings, the scene script, the"
                + " model, the materials and the shaders are all written by ED8Editor.");
            Say(string.Empty);
            Say("To get into the map, place an entry transition in the scene view of the"
                + " map you want to leave, and set its destination to this one.");
        }
    }

    private Control Build()
    {
        var rows = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        AddRow(rows, "Map ID", mapName);
        AddRow(rows, "Display name", placeName);
        AddRow(rows, "Place category", placeKind);
        AddRow(rows, "Model", Side(modelPath, browse));
        AddRow(rows, "Skybox", Side(skybox, showSkybox));
        AddRow(rows, string.Empty, create);

        var collisionTools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        collisionTools.Controls.Add(collisionKind);
        collisionTools.Controls.Add(collisionIndex);
        collisionTools.Controls.Add(assignCollision);
        collisionTools.Controls.Add(clearCollision);
        var meshGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Meshes — select to see one, assign it a collision node",
        };
        var meshPanel = new Panel { Dock = DockStyle.Fill };
        meshPanel.Controls.Add(meshList);
        meshPanel.Controls.Add(collisionTools);
        meshGroup.Controls.Add(meshPanel);

        var materialGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Materials — double-click to choose the shader",
        };
        var materialPanel = new Panel { Dock = DockStyle.Fill };
        var materialTools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        materialTools.Controls.Add(chooseShader);
        materialPanel.Controls.Add(materialList);
        materialPanel.Controls.Add(materialTools);
        materialGroup.Controls.Add(materialPanel);

        var lists = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        lists.Panel1.Controls.Add(meshGroup);
        lists.Panel2.Controls.Add(materialGroup);

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        left.Controls.Add(lists);
        left.Controls.Add(rows);

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        var previewGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Preview" };
        previewGroup.Controls.Add(previewHost);
        right.Panel1.Controls.Add(previewGroup);
        right.Panel2.Controls.Add(report);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        WinFormsLayout.SetInitialSplitterDistance(split, 520);
        WinFormsLayout.SetInitialSplitterDistance(right, 430);
        WinFormsLayout.SetInitialSplitterDistance(lists, 320);
        return split;
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
            Title = "The model the map is made of",
            Filter = "Models 3D (*.glb;*.gltf;*.fbx;*.dae;*.obj)|*.glb;*.gltf;*.fbx;*.dae;*.obj"
                + "|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        LoadModel(dialog.FileName);
    }

    /// <summary>
    /// Reads the model and shows what it is made of.
    ///
    /// The same conversion the writer will use, not a second one for looking at:
    /// what the list names is what gets written, and a mesh that cannot be written
    /// is a mesh that is said so here rather than at the end.
    /// </summary>
    private void LoadModel(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            Say(string.Empty);
            Say($"Reading {Path.GetFileName(path)}…");
            var scene = new AssimpModelImporter().Import(path);
            Say($"  {scene.Meshes.Count} meshes, {scene.Meshes.Sum(m => m.Vertices.Count)} vertices,"
                + $" {scene.Meshes.Sum(m => m.Indices.Length) / 3} triangles,"
                + $" up {scene.CoordinateSystem.UpAxis},"
                + $" unit {scene.CoordinateSystem.UnitScaleMeters}");

            var converted = ImportedModelAdapter.Convert(scene);
            foreach (var note in converted.Notes) Say("  " + note);
            if (converted.FlippedTriangles != 0)
            {
                Say($"  {converted.FlippedTriangles} triangles turned to agree with their normals");
            }

            modelPath.Text = path;
            model = converted.Model;
            collisionNodes.Clear();
            for (var index = 0; index < model.Meshes.Count; index++)
            {
                // What the file already said. A mesh sitting under a node called CK00
                // in the source is a mesh whose author already made this choice.
                if (model.Meshes[index].CollisionNode is { } node) collisionNodes[index] = node;
            }

            preview = ImportedModelCpuAdapter.Convert(scene).Model;
            previewResources?.Dispose();
            previewResources = new D3D11ModelUploader(graphics.Device).Upload(preview);
            var bounds = Extent(preview);
            centre = bounds.Center;
            distance = Math.Max(bounds.Radius * 2.4f, 1f);

            ShowMeshes();
            ShowMaterials();
            var problems = model.Problems();
            if (problems.Count != 0)
            {
                Say("This model cannot be written: " + string.Join(" ; ", problems.Take(5)));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or NotSupportedException or InvalidPhyreException)
        {
            Say("Model non lu : " + exception.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ShowMeshes()
    {
        var keep = meshList.SelectedIndex;
        meshList.BeginUpdate();
        meshList.Items.Clear();
        if (model is not null)
        {
            for (var index = 0; index < model.Meshes.Count; index++)
            {
                var mesh = model.Meshes[index];
                var node = collisionNodes.TryGetValue(index, out var named) ? named : null;
                meshList.Items.Add(
                    $"{index,3}  {mesh.MaterialName,-28} {mesh.Indices.Length / 3,7} tri"
                    + (node is null ? string.Empty : $"     collision {node}"));
            }
        }
        meshList.EndUpdate();
        if (keep >= 0 && keep < meshList.Items.Count) meshList.SelectedIndex = keep;
    }

    private void ShowMaterials()
    {
        materialNames = model is null
            ? Array.Empty<string>()
            : model.Meshes
                .Select(mesh => mesh.MaterialName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        var keep = materialList.SelectedIndex;
        materialList.BeginUpdate();
        materialList.Items.Clear();
        foreach (var name in materialNames)
        {
            materialList.Items.Add(shaders.TryGetValue(name, out var chosen)
                ? $"{name}   →   {chosen.Label}"
                : $"{name}   →   (no shader chosen)");
        }
        materialList.EndUpdate();
        if (keep >= 0 && keep < materialList.Items.Count) materialList.SelectedIndex = keep;
        else if (materialList.Items.Count != 0) materialList.SelectedIndex = 0;
    }

    /// <summary>
    /// Makes the selected mesh a collision surface on a node of the chosen name.
    ///
    /// The game gives each surface its own node and its own rigid body — r0510
    /// carries five, named CK00, CS00, CA00, CA01, CS01 — so a name is what the
    /// physics aims at, and two surfaces sharing one would be one surface.
    /// </summary>
    private void AssignCollision()
    {
        if (model is null || meshList.SelectedIndex < 0) return;
        var kind = ((CollisionKindChoice)collisionKind.SelectedItem!).Prefix;
        var node = $"{kind}{(int)collisionIndex.Value:00}";
        var taken = collisionNodes
            .Where(pair => pair.Key != meshList.SelectedIndex
                && string.Equals(pair.Value, node, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        if (taken.Length != 0)
        {
            var share = MessageBox.Show(
                this,
                $"Node {node} already carries mesh {string.Join(", ", taken)}."
                    + " The game gives each surface its own node and its own rigid"
                    + " body; two meshes on one node are one surface."
                    + Environment.NewLine + Environment.NewLine
                    + "Put them together anyway?",
                "Collision node",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (share != DialogResult.Yes) return;
        }
        collisionNodes[meshList.SelectedIndex] = node;
        ShowMeshes();
        Say($"mesh {meshList.SelectedIndex} →   collision {node}");
    }

    private void ClearCollision()
    {
        if (meshList.SelectedIndex < 0) return;
        if (collisionNodes.Remove(meshList.SelectedIndex)) ShowMeshes();
    }

    /// <summary>
    /// Points the selected material at a shader. Every material of the map gets its
    /// own: a map does not have a shader, it has one per surface — a shipped one
    /// binds fourteen — and giving them all the same is what made every surface of
    /// an authored map look alike.
    /// </summary>
    private void ChooseMaterialShader()
    {
        if (materialList.SelectedIndex < 0 || materialList.SelectedIndex >= materialNames.Count)
        {
            MessageBox.Show(
                this, "Choose a material first.", "Shader",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var material = materialNames[materialList.SelectedIndex];
        using var chooser = new ShaderChooserForm(project.GameDirectory, material);
        if (chooser.ShowDialog(this) != DialogResult.OK || chooser.Choice is not { } choice) return;

        shaders[material] = AuthoredShaderBinding.For(
            material, choice.AssetName, choice.Cluster, choice.Values, choice.Custom);
        shaderSources[material] = choice.SourcePath;
        ShowMaterials();
        Say($"{material} → {choice.AssetName}"
            + (choice.Custom ? " (yours)" : string.Empty));
    }

    /// <summary>The model with the collision the author assigned, and nothing else changed.</summary>
    private PhyreModelSource? Assigned()
    {
        if (model is null) return null;
        var meshes = new List<PhyreMeshSource>(model.Meshes.Count);
        for (var index = 0; index < model.Meshes.Count; index++)
        {
            var mesh = model.Meshes[index];
            meshes.Add(collisionNodes.TryGetValue(index, out var node)
                ? mesh with { IsCollision = true, CollisionNode = node }
                : mesh with { IsCollision = false, CollisionNode = null });
        }
        return model with { Meshes = meshes };
    }

    private void CreateMap()
    {
        var name = mapName.Text.Trim();
        if (name.Length == 0) { Say("The map needs an ID."); return; }
        var displayName = placeName.Text.Trim();
        if (displayName.Length == 0) { Say("The map needs a display name."); return; }
        if (Assigned() is not { } authored) { Say("Pick the model."); return; }

        var replacing = reopening is not null
            && string.Equals(reopening, name, StringComparison.OrdinalIgnoreCase);
        if (!replacing && maps.Maps().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Say($"A map called '{name}' already exists. Pick another name, or"
                + " revert the project.");
            return;
        }

        var unassigned = materialNames
            .Where(value => !shaders.ContainsKey(value))
            .ToArray();
        if (unassigned.Length != 0)
        {
            var carryOn = MessageBox.Show(
                this,
                $"{unassigned.Length} material(s) have no shader:"
                    + Environment.NewLine + string.Join(", ", unassigned.Take(12))
                    + Environment.NewLine + Environment.NewLine
                    + "They will take the one ED8Editor compiles itself. Carry on?",
                "Create the map",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (carryOn != DialogResult.Yes) return;
        }

        try
        {
            Say(string.Empty);
            var problems = authored.Problems();
            if (problems.Count != 0)
            {
                Say("This model cannot be written: " + string.Join(" ; ", problems.Take(5)));
                return;
            }

            var written = MapModelPackage.WriteMinimal(
                project, name, authored, Say,
                shaderAssignments: shaders.Count == 0 ? null : shaders);

            // The scene is written once. Re-applying rewrites the model, its
            // collision and its materials — and leaves the scene alone, because by
            // then the author has been placing things in it, and rewriting it would
            // throw those away to restore a file they have already moved past.
            var sceneFiles = replacing
                ? Array.Empty<string>()
                : maps.CreateEmptyMap(
                    name,
                    MapSettings.Default with { Skybox = skybox.Text.Trim() },
                    model: authored).Files.ToArray();
            var placeFiles = new PlaceTableAuthoring(project).Upsert(
                name,
                displayName,
                ((PlaceKindChoice)placeKind.SelectedItem!).Value);

            Record(name, displayName).Save(project);

            Say(string.Empty);
            Say($"'{name}' written — {sceneFiles.Length + placeFiles.Count + 1} files, all tracked:");
            foreach (var file in sceneFiles.Append(written).Concat(placeFiles))
            {
                Say("  " + Path.GetRelativePath(project.GameDirectory, file));
            }
            Say(string.Empty);
            if (replacing)
            {
                Say("The scene was not written again: what you placed in it stays."
                    + " The sky is an object in the map, so it is changed in the scene"
                    + " view like any other.");
            }
            else
            {
                Say("The settings are kept: opening this map again reopens this"
                    + " form as it is.");
                Say("The sky is an object in the map: changing it later means"
                    + " editing that object in the scene view.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or NotSupportedException or InvalidPhyreException)
        {
            Say("The map was not written: " + exception.Message);
        }
    }

    private MapAuthoringRecord Record(string name, string displayName) => new(
        name,
        displayName,
        ((PlaceKindChoice)placeKind.SelectedItem!).Value,
        skybox.Text.Trim(),
        modelPath.Text,
        collisionNodes.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value),
        shaders.ToDictionary(
            pair => pair.Key,
            pair => new MapShaderRecord(
                pair.Value.ShaderAsset[("shaders/".Length)..],
                shaderSources.TryGetValue(pair.Key, out var source) ? source : null,
                pair.Value.Values),
            StringComparer.Ordinal));

    /// <summary>
    /// Brings a map that was authored here back into the form.
    ///
    /// The shaders are found again rather than stored: one of the game's is read
    /// from its package, and one of the author's is compiled from the file it was
    /// compiled from before — so what reopens is the shader as it is now, which is
    /// what the author would change it for.
    /// </summary>
    private void Reopen(string name)
    {
        var record = MapAuthoringRecord.Load(project, name);
        if (record is null)
        {
            Say($"'{name}' was not created by this editor, so there are no settings"
                + " to reopen. The fields are empty.");
            mapName.Text = name;
            return;
        }

        mapName.Text = record.MapName;
        placeName.Text = record.DisplayName;
        foreach (var item in placeKind.Items)
        {
            if (item is PlaceKindChoice choice && choice.Value == record.PlaceKind)
            {
                placeKind.SelectedItem = item;
            }
        }
        skybox.Text = record.Skybox;

        if (record.ModelPath.Length != 0 && File.Exists(record.ModelPath))
        {
            LoadModel(record.ModelPath);
        }
        else if (record.ModelPath.Length != 0)
        {
            Say($"The model {record.ModelPath} is gone. Choose it again: the rest"
                + " of the settings has been reopened.");
        }

        collisionNodes.Clear();
        foreach (var (key, node) in record.CollisionNodes)
        {
            if (int.TryParse(key, out var index)) collisionNodes[index] = node;
        }
        ShowMeshes();

        foreach (var (material, chosen) in record.MaterialShaders)
        {
            if (Recover(chosen) is not { } assignment)
            {
                Say($"{material}'s shader ({chosen.AssetName}) could not be"
                    + " found again; choose it again.");
                continue;
            }
            shaders[material] = assignment with { Material = material };
            shaderSources[material] = chosen.HlslPath;
        }
        ShowMaterials();
        Say($"'{record.MapName}' reopened: {collisionNodes.Count} collision node(s),"
            + $" {shaders.Count} material(s) with a shader.");
    }

    /// <summary>The shader a record names, found again or compiled again.</summary>
    private ShaderAssignment? Recover(MapShaderRecord record)
    {
        try
        {
            if (record.HlslPath is { } source && File.Exists(source))
            {
                var text = File.ReadAllText(source);
                var cluster = new ED8Editor.Shaders.Compilation.PhyreEffectClusterBuilder()
                    .BuildFromSources(text, text, record.AssetName, record.AssetName + ".phyre");
                return AuthoredShaderBinding.For(
                    string.Empty, record.AssetName, cluster, record.Values, custom: true);
            }
            var variant = ShaderVariantCatalog.Load(project.GameDirectory)
                .FirstOrDefault(value => string.Equals(
                    value.AssetName, record.AssetName, StringComparison.OrdinalIgnoreCase));
            if (variant is null) return null;
            return AuthoredShaderBinding.For(
                string.Empty,
                record.AssetName,
                ShaderVariantCatalog.Cluster(variant),
                record.Values,
                custom: false);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            return null;
        }
    }

    private void Render()
    {
        if (previewHost.ClientSize.Width <= 0 || previewHost.ClientSize.Height <= 0) return;
        viewport ??= new D3D11Viewport(
            graphics, previewHost.Handle,
            previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        viewport.Resize(previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        viewport.SetClearColor(new Vector4(0.09f, 0.10f, 0.13f, 1f));
        viewport.SetEffectQuads(Array.Empty<D3D11EffectQuad>());
        viewport.SetDebugLines(Highlight(out var triangles));
        viewport.SetDebugTriangles(triangles);

        var instances = new List<D3D11SceneInstance>();
        if (showSkybox.Checked && Sky() is { } sky)
        {
            instances.Add(new D3D11SceneInstance(
                2, sky, Matrix4x4.CreateTranslation(centre), false,
                MaterialDiffuse: Vector4.One));
        }
        if (previewResources is not null)
        {
            instances.Add(new D3D11SceneInstance(
                1, previewResources, Matrix4x4.Identity, false,
                MaterialDiffuse: Vector4.One));
        }
        viewport.Render(instances, Camera(), verticalSync: false);
    }

    /// <summary>
    /// The selected mesh, drawn over the model so it can be told apart.
    ///
    /// Filled when it is small enough to draw that way and a box around it when it
    /// is not: a map's mesh can be a hundred thousand triangles, and drawing those
    /// one at a time as debug geometry would stop the window rather than highlight
    /// anything.
    /// </summary>
    private IReadOnlyList<D3D11DebugLine> Highlight(
        out IReadOnlyList<D3D11DebugTriangle> triangles)
    {
        triangles = Array.Empty<D3D11DebugTriangle>();
        var lines = new List<D3D11DebugLine>();
        if (model is null || meshList.SelectedIndex < 0
            || meshList.SelectedIndex >= model.Meshes.Count)
        {
            return lines;
        }

        var mesh = model.Meshes[meshList.SelectedIndex];
        var collision = collisionNodes.ContainsKey(meshList.SelectedIndex);
        var colour = collision
            ? new Vector4(1f, 0.45f, 0.2f, 0.55f)
            : new Vector4(0.3f, 0.85f, 1f, 0.5f);

        var least = new Vector3(float.MaxValue);
        var most = new Vector3(float.MinValue);
        foreach (var vertex in mesh.Vertices)
        {
            least = Vector3.Min(least, vertex.Position);
            most = Vector3.Max(most, vertex.Position);
        }
        if (mesh.Vertices.Count == 0) return lines;
        foreach (var (from, to) in BoxEdges(least, most))
        {
            lines.Add(new D3D11DebugLine(from, to, colour with { W = 1f }, 2f));
        }

        const int mostTriangles = 40000;
        if (mesh.Indices.Length / 3 <= mostTriangles)
        {
            var filled = new List<D3D11DebugTriangle>(mesh.Indices.Length / 3);
            for (var at = 0; at + 2 < mesh.Indices.Length; at += 3)
            {
                filled.Add(new D3D11DebugTriangle(
                    mesh.Vertices[mesh.Indices[at]].Position,
                    mesh.Vertices[mesh.Indices[at + 1]].Position,
                    mesh.Vertices[mesh.Indices[at + 2]].Position,
                    colour));
            }
            triangles = filled;
        }
        return lines;
    }

    private static IEnumerable<(Vector3 From, Vector3 To)> BoxEdges(Vector3 least, Vector3 most)
    {
        var corners = new Vector3[8];
        for (var at = 0; at < 8; at++)
        {
            corners[at] = new Vector3(
                (at & 1) == 0 ? least.X : most.X,
                (at & 2) == 0 ? least.Y : most.Y,
                (at & 4) == 0 ? least.Z : most.Z);
        }
        var pairs = new[]
        {
            (0, 1), (2, 3), (4, 5), (6, 7),
            (0, 2), (1, 3), (4, 6), (5, 7),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (a, b) in pairs) yield return (corners[a], corners[b]);
    }

    /// <summary>The sky the map will carry, drawn as the map will carry it.</summary>
    private D3D11ModelResources? Sky()
    {
        var wanted = skybox.Text.Trim();
        if (wanted.Length == 0) return null;
        if (string.Equals(wanted, skyLoaded, StringComparison.OrdinalIgnoreCase))
        {
            return skyResources;
        }
        skyLoaded = wanted;
        skyResources?.Dispose();
        skyResources = null;
        try
        {
            var load = loader.LoadAsset(wanted, gameDataPath);
            if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null)
            {
                Say($"Skybox {wanted}: not found, the preview goes without it.");
                return null;
            }
            skyResources = new D3D11ModelUploader(graphics.Device).Upload(load.Model);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            Say($"Skybox {wanted} : {exception.Message}");
        }
        return skyResources;
    }

    private ViewportCamera Camera()
    {
        var yaw = yawDegrees * MathF.PI / 180f;
        var pitch = pitchDegrees * MathF.PI / 180f;
        var direction = new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
        return new ViewportCamera(
            Matrix4x4.CreateLookAt(centre - direction * distance, centre, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                previewHost.ClientSize.Width / (float)Math.Max(1, previewHost.ClientSize.Height),
                Math.Max(0.01f, distance / 2000f),
                Math.Max(500f, distance * 40f)));
    }

    private static SceneBoundsResult Extent(CpuModel model)
        => new SceneBoundsCalculator().Calculate(new[]
        {
            new SceneModelInstance(1, model.AssetId, model.AssetId, model, Matrix4x4.Identity),
        });

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

    private sealed record CollisionKindChoice(string Label, string Prefix)
    {
        public override string ToString() => Label;
    }
}
