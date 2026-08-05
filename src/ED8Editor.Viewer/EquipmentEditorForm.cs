using System.Numerics;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Core;
using ED8Editor.Models;
using ED8Editor.Packages;
using ED8Editor.Phyre.Authoring;
using ED8Editor.Rendering;
using ED8Editor.Scene;

namespace ED8Editor.Viewer;

/// <summary>
/// Every piece of equipment the game models, and what each is made of.
///
/// The character studio covers who a character is — their model, their animations,
/// their table rows. It does not cover what they carry: equipment lives in its own
/// packages, is referenced by the attach table, and is edited by replacing the files
/// of that package. That is what this window is for.
///
/// Replacing an entry is the whole of it, deliberately. A package holds its model,
/// its textures and its shaders as separate files, so putting a new model or a forged
/// shader in place is a swap — and a swap keeps every other entry, their order, and
/// the archive's own magic word, which repacking a folder does not.
/// </summary>
internal sealed class EquipmentEditorForm : Form
{
    private readonly string gameDirectory;
    private readonly string gameDataPath;
    private readonly ModProject? project;
    private readonly D3D11GraphicsDevice graphics;
    private readonly EditorProjectLoader loader;

    private readonly Panel previewHost = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(18, 20, 25),
    };

    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 33 };
    private readonly Dictionary<string, CpuModel> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, D3D11ModelResources> resources =
        new(StringComparer.OrdinalIgnoreCase);

    private D3D11Viewport? preview;
    private CpuModel? currentModel;
    private D3D11ModelResources? currentResources;
    private float yawDegrees = 30f;
    private float pitchDegrees = 12f;
    private float distance = 3f;
    private bool orbiting;
    private Point previousMouse;

    private readonly TextBox filter = new()
    {
        Dock = DockStyle.Top,
        PlaceholderText = "Filter: asset name, attach point, character…",
    };

    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox details = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly ListBox files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox materialList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
    };

    private readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true };

    /// <summary>
    /// What the author pointed each material at, by equipment and then by material.
    ///
    /// Held here rather than written on the spot: a shader assignment only means
    /// something once the package is written, and writing the package is one action
    /// that carries the model, the effects and every material at once.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, ShaderAssignment>> assignments =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<(string Material, string Shader, IReadOnlyList<string> Textures)>
        materials = Array.Empty<(string, string, IReadOnlyList<string>)>();

    private IReadOnlyList<EquipmentEntry> all = Array.Empty<EquipmentEntry>();
    private IReadOnlyList<EquipmentEntry> shown = Array.Empty<EquipmentEntry>();

    public EquipmentEditorForm(
        string gameDirectory,
        string gameDataPath,
        ModProject? project,
        D3D11GraphicsDevice graphics,
        EditorProjectLoader loader)
    {
        this.gameDirectory = gameDirectory;
        this.gameDataPath = gameDataPath;
        this.project = project;
        this.graphics = graphics;
        this.loader = loader;

        Text = "Equipment";
        Width = 1100;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        var replaceFile = new Button { AutoSize = true, Text = "Replace this file…" };
        var replaceModel = new Button { AutoSize = true, Text = "Replace the model…" };
        var shader = new Button { AutoSize = true, Text = "Material shader…" };
        shader.Click += (_, _) => ChooseShader();
        var applyShaders = new Button { AutoSize = true, Text = "Write the shaders" };
        applyShaders.Click += (_, _) => WriteShaders();
        var importModel = new Button { AutoSize = true, Text = "Import a 3D model…" };
        var add = new Button { AutoSize = true, Text = "Add equipment…" };
        var extract = new Button { AutoSize = true, Text = "Extract…" };
        var reload = new Button { AutoSize = true, Text = "Reload" };
        importModel.Click += (_, _) => ImportModel();
        add.Click += (_, _) => AddEquipment();
        replaceFile.Click += (_, _) => Replace(files.SelectedItem as string);
        replaceModel.Click += (_, _) => Replace(Selected() is { } one
            ? EquipmentCatalog.Contents(one.PackagePath).Model
            : null);
        extract.Click += (_, _) => Extract(files.SelectedItem as string);
        reload.Click += (_, _) => Reload();

        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[]
        {
            importModel, shader, applyShaders, add, replaceModel, replaceFile,
            extract, reload,
        });

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(list);
        left.Controls.Add(filter);

        var filesGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Package files" };
        filesGroup.Controls.Add(files);
        var detailsGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Materials and uses" };
        var detailsSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 150,
        };
        var materialsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Materials — double-click to choose the shader",
        };
        materialsGroup.Controls.Add(materialList);
        detailsSplit.Panel1.Controls.Add(materialsGroup);
        detailsSplit.Panel2.Controls.Add(details);
        detailsGroup.Controls.Add(detailsSplit);
        materialList.DoubleClick += (_, _) => ChooseShader();

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
        };
        right.Panel1.Controls.Add(filesGroup);
        right.Panel2.Controls.Add(detailsGroup);

        var previewGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Preview" };
        previewGroup.Controls.Add(previewHost);

        var middle = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 460,
        };
        middle.Panel1.Controls.Add(previewGroup);
        middle.Panel2.Controls.Add(right);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 340 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(middle);

        Controls.Add(split);
        Controls.Add(tools);
        Controls.Add(status);

        filter.TextChanged += (_, _) => ApplyFilter();
        list.SelectedIndexChanged += (_, _) => ShowSelected();

        previewHost.MouseWheel += (_, eventArgs) =>
            distance = Math.Clamp(distance * (eventArgs.Delta > 0 ? 0.85f : 1.18f), 0.05f, 500f);
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
        renderTimer.Tick += (_, _) => RenderPreview();
        renderTimer.Start();
        FormClosed += (_, _) =>
        {
            renderTimer.Stop();
            preview?.Dispose();
        };

        Reload();
    }

    private EquipmentEntry? Selected()
        => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count
            ? shown[list.SelectedIndex]
            : null;

    private void Reload()
    {
        all = EquipmentCatalog.Load(gameDirectory);
        ApplyFilter();
        status.Text = all.Count == 0
            ? $"No C_EQU package found in {EquipmentCatalog.AssetDirectory(gameDirectory)}."
            : $"{all.Count} pieces of equipment, {all.Count(one => one.Uses.Count != 0)} worn by"
                + " at least one character.";
    }

    private void ApplyFilter()
    {
        var wanted = filter.Text.Trim();
        shown = wanted.Length == 0
            ? all
            : all.Where(one => one.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var one in shown) list.Items.Add(one.Label);
        list.EndUpdate();
        if (shown.Count != 0) list.SelectedIndex = 0;
        else ShowSelected();
    }

    private void ShowSelected()
    {
        files.Items.Clear();
        if (Selected() is not { } one)
        {
            details.Text = string.Empty;
            materials = Array.Empty<(string, string, IReadOnlyList<string>)>();
            ShowMaterials(null);
            return;
        }

        LoadPreview(one);

        var contents = EquipmentCatalog.Contents(one.PackagePath);
        foreach (var name in new[] { contents.Model }
                     .Concat(contents.Shaders)
                     .Concat(contents.Textures)
                     .Where(name => name.Length != 0))
        {
            files.Items.Add(name);
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"{one.Asset}   {one.Size / 1024} Ko");
        text.AppendLine(one.PackagePath);
        text.AppendLine();

        if (one.Uses.Count == 0)
        {
            text.AppendLine("No character wears it in t_attach.");
        }
        else
        {
            text.AppendLine($"Worn by ({one.Uses.Count}):");
            foreach (var use in one.Uses)
            {
                text.AppendLine($"   personnage {use.Wearer,-7} {use.Kind,-10}"
                    + $" objet {use.ItemId,-6} sur {use.AttachPoint}");
            }
        }
        text.AppendLine();

        materials = EquipmentCatalog.Materials(one.PackagePath);
        ShowMaterials(one);
        if (materials.Count == 0)
        {
            text.AppendLine("Materials cannot be read from this package.");
        }
        else
        {
            text.AppendLine($"Materials ({materials.Count}):");
            foreach (var (material, shader, textures) in materials)
            {
                text.AppendLine($"   {material}");
                text.AppendLine($"      shader   {Path.GetFileName(shader)}");
                foreach (var texture in textures) text.AppendLine($"      texture  {texture}");
            }
        }
        details.Text = text.ToString();
    }

    /// <summary>
    /// Draws the selected equipment. The same path the character studio uses: the
    /// asset loader turns an asset id into a model, and the viewport draws it.
    /// </summary>
    private void RenderPreview()
    {
        if (previewHost.ClientSize.Width <= 0 || previewHost.ClientSize.Height <= 0) return;
        preview ??= new D3D11Viewport(
            graphics, previewHost.Handle,
            previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.Resize(previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.SetClearColor(new Vector4(0.09f, 0.10f, 0.13f, 1f));
        preview.SetDebugLines(Ground());
        preview.SetDebugTriangles(Array.Empty<D3D11DebugTriangle>());
        preview.SetEffectQuads(Array.Empty<D3D11EffectQuad>());

        var instances = currentResources is null
            ? Array.Empty<D3D11SceneInstance>()
            : new[]
            {
                new D3D11SceneInstance(
                    1, currentResources, Matrix4x4.Identity, false,
                    MaterialDiffuse: Vector4.One),
            };
        preview.Render(instances, Camera(), verticalSync: false);
    }

    private ViewportCamera Camera()
    {
        var centre = currentModel is null ? Vector3.Zero : Extent(currentModel).Center;
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
                Math.Max(0.001f, distance / 1000f),
                Math.Max(100f, distance * 20f)));
    }

    private static SceneBoundsResult Extent(CpuModel model)
        => new SceneBoundsCalculator().Calculate(new[]
        {
            new SceneModelInstance(1, model.AssetId, model.AssetId, model, Matrix4x4.Identity),
        });

    /// <summary>A metre grid, so the size of what is shown can be read off it.</summary>
    private static IReadOnlyList<D3D11DebugLine> Ground()
    {
        var lines = new List<D3D11DebugLine>();
        var colour = new Vector4(0.25f, 0.28f, 0.34f, 1f);
        for (var at = -3; at <= 3; at++)
        {
            lines.Add(new D3D11DebugLine(new Vector3(-3, 0, at), new Vector3(3, 0, at), colour));
            lines.Add(new D3D11DebugLine(new Vector3(at, 0, -3), new Vector3(at, 0, 3), colour));
        }
        return lines;
    }

    /// <summary>Loads the selected equipment into the preview, or clears it.</summary>
    private void LoadPreview(EquipmentEntry? entry)
    {
        currentModel = null;
        currentResources = null;
        if (entry is null) return;
        try
        {
            if (!models.TryGetValue(entry.Asset, out var model))
            {
                var load = loader.LoadAsset(entry.Asset, gameDataPath);
                if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null) return;
                model = load.Model;
                models[entry.Asset] = model;
            }
            if (!resources.TryGetValue(entry.Asset, out var gpu))
            {
                gpu = new D3D11ModelUploader(graphics.Device).Upload(model);
                resources[entry.Asset] = gpu;
            }
            currentModel = model;
            currentResources = gpu;
            distance = Math.Max(Extent(model).Radius * 2.8f, 0.2f);
        }
        catch (Exception failure)
        {
            status.Text = $"Preview impossible : {failure.Message}";
        }
    }

    /// <summary>Forgets what was cached for an asset, after its package changed.</summary>
    private void Forget(string asset)
    {
        models.Remove(asset);
        if (resources.Remove(asset, out var gpu)) gpu.Dispose();
    }

    /// <summary>
    /// Puts an authored model in the place of the equipment's own geometry.
    ///
    /// Only the geometry changes: the package keeps its shader, its parameter block
    /// and its textures, and the model is written under the asset path the package
    /// already states, so everything that referred to it still does. That none of the
    /// game's equipment is skinned is what makes this possible — each is a rigid model
    /// hung from a locator.
    /// </summary>
    private void ImportModel()
    {
        if (Selected() is not { } one) return;
        if (project is null)
        {
            MessageBox.Show(
                this,
                "Writing a model goes through a mod project, so the original is"
                    + " kept and the change can be undone. Open one.",
                "Import a model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (EquipmentCatalog.AssetPaths(one.PackagePath) is not { } paths)
        {
            MessageBox.Show(
                this,
                $"{one.Asset} does not state the path its model answers to,"
                    + " so none can be written in its place.",
                "Import a model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"Model to write into {one.Asset}",
            CheckFileExists = true,
            Filter = "3D models (*.glb;*.gltf;*.fbx;*.dae;*.obj)|*.glb;*.gltf;*.fbx;*.dae;*.obj"
                + "|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var imported = ImportedModelAdapter.Convert(
                new AssimpModelImporter().Import(dialog.FileName));
            MapModelPackage.WriteProp(
                project,
                one.Asset,
                imported.Model,
                say: line => status.Text = line,
                assetFolder: paths.Folder,
                assetName: paths.Name,
                shaderAssignments: assignments.TryGetValue(one.Asset, out var chosen)
                    ? chosen
                    : null);
            Forget(one.Asset);
            Reload();
            status.Text = $"{Path.GetFileName(dialog.FileName)} written into {one.Asset}"
                + $" sous {paths.Folder}/{paths.Name}.dae.";
        }
        catch (Exception failure)
        {
            MessageBox.Show(
                this, failure.Message, "Import a model",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Adds a piece of equipment by copying one that exists, under a new asset id.
    ///
    /// A package copied whole is one the game can already load: its shader, its
    /// material and its asset paths are consistent from the first moment. Importing a
    /// model into it then changes the one thing meant to change. Writing a package
    /// from nothing would instead start from something that has never loaded, and
    /// leave no way to tell a bad model from a bad package.
    /// </summary>
    private void AddEquipment()
    {
        if (Selected() is not { } from) return;
        if (project is null)
        {
            MessageBox.Show(
                this,
                "Adding equipment goes through a mod project, which tracks the file it creates.",
                "Add equipment",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var chosen = Prompt(
            "Name of the new equipment",
            $"Copied from {from.Asset}. The game reads the package name, so keep the"
                + " shape of its own.",
            NextFreeName());
        if (chosen is null) return;
        chosen = chosen.Trim().ToUpperInvariant();
        if (chosen.Length == 0) return;

        var target = Path.Combine(EquipmentCatalog.AssetDirectory(gameDirectory), chosen + ".pkg");
        if (File.Exists(target))
        {
            MessageBox.Show(
                this, $"{chosen} already exists.", "Add equipment",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            File.Copy(from.PackagePath, target);
            project.TrackSave(target);
            Reload();
            var at = shown.ToList().FindIndex(one =>
                one.Asset.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) list.SelectedIndex = at;
            status.Text = $"{chosen} created from {from.Asset}. Its model still answers under"
                + " the copied package's path: import one, then name it in t_attach.";
        }
        catch (Exception failure)
        {
            MessageBox.Show(
                this, failure.Message, "Add equipment",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>The first equipment name the game does not already use.</summary>
    private string NextFreeName()
    {
        var taken = all.Select(one => one.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 900; number < 1000; number++)
        {
            var candidate = $"C_EQU{number}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return "C_EQU900";
    }

    /// <summary>A one-line question, which WinForms does not offer on its own.</summary>
    private static string? Prompt(string title, string message, string initial)
    {
        using var window = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 130),
        };
        var label = new Label { Left = 12, Top = 12, Width = 436, Height = 40, Text = message };
        var box = new TextBox { Left = 12, Top = 60, Width = 436, Text = initial };
        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK, Left = 292, Top = 92, Width = 75,
        };
        var cancel = new Button
        {
            Text = "Annuler", DialogResult = DialogResult.Cancel, Left = 373, Top = 92, Width = 75,
        };
        window.Controls.AddRange(new Control[] { label, box, ok, cancel });
        window.AcceptButton = ok;
        window.CancelButton = cancel;
        return window.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    /// <summary>
    /// Points the selected material at a shader — one of the game's, or one
    /// compiled from the author's own HLSL a moment ago.
    ///
    /// The choice is remembered, not written: what a material draws with only
    /// becomes real when the package is written, and the package is written in one
    /// go — the model, every effect it needs and every material's constant block
    /// together — so there is no state in which half of it has landed.
    /// </summary>
    private void ChooseShader()
    {
        if (Selected() is not { } one) return;
        if (materialList.SelectedIndex < 0 || materialList.SelectedIndex >= materials.Count)
        {
            MessageBox.Show(
                this, "Choose a material from the list first.", "Shader",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var material = materials[materialList.SelectedIndex].Material;
        using var chooser = new ShaderChooserForm(gameDirectory, material);
        if (chooser.ShowDialog(this) != DialogResult.OK || chooser.Choice is not { } choice) return;

        if (!assignments.TryGetValue(one.Asset, out var forAsset))
        {
            forAsset = new Dictionary<string, ShaderAssignment>(StringComparer.Ordinal);
            assignments[one.Asset] = forAsset;
        }
        forAsset[material] = AuthoredShaderBinding.For(
            material, choice.AssetName, choice.Cluster, choice.Values, choice.Custom);

        ShowMaterials(one);
        status.Text = $"{material} → {choice.AssetName}."
            + " Write the shaders, or import a model: either writes the package"
            + " with every assignment at once.";
    }

    /// <summary>
    /// Writes the package with the shaders the author assigned.
    ///
    /// Two ways, and the gentler one first. A model names its shader once, in a
    /// string of a fixed length, so a shader whose interface matches can be put in
    /// its place without the model being touched at all — nothing moves, and the
    /// geometry the game ships is the geometry it keeps. When the shader wants a
    /// different material block that is not possible, and the model is written
    /// again from its own geometry instead, which has no such restriction and is
    /// asked for rather than done quietly.
    /// </summary>
    private void WriteShaders()
    {
        if (Selected() is not { } one) return;
        if (!assignments.TryGetValue(one.Asset, out var forAsset) || forAsset.Count == 0)
        {
            MessageBox.Show(
                this, "No material has been given a shader.", "Write the shaders",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (project is null)
        {
            MessageBox.Show(
                this,
                "Writing a package goes through a mod project, so the original is"
                    + " kept and the change can be undone. Open one.",
                "Write the shaders",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            var archive = new PkgArchiveReader().Read(one.PackagePath);
            var modelEntry = archive.Entries.FirstOrDefault(entry =>
                entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"{one.Asset} holds no model.");
            var cluster = archive.ReadEntry(modelEntry).ToArray();

            var refused = new List<string>();
            var alsoChanged = new List<string>();
            foreach (var (material, assignment) in forAsset)
            {
                var plan = PhyreEffectRebind.Plan(
                    cluster, material, assignment.ShaderAsset, assignment.Cluster);
                if (plan.Problems.Count != 0)
                {
                    refused.Add($"{material} → {Path.GetFileName(assignment.ShaderAsset)} : "
                        + string.Join(" ", plan.Problems));
                    continue;
                }
                cluster = PhyreEffectRebind.Repoint(
                    cluster, material, assignment.ShaderAsset, assignment.Cluster);
                alsoChanged.AddRange(plan.SharedWith);
            }

            if (refused.Count != 0)
            {
                var rewrite = MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, refused)
                        + Environment.NewLine + Environment.NewLine
                        + "The model can be written again in full from its own"
                        + " geometry, which lifts that restriction. Do that?",
                    "Write the shaders",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (rewrite == DialogResult.Yes)
                {
                    RewriteWithShaders(one, forAsset);
                    return;
                }
                if (alsoChanged.Count == 0 && refused.Count == forAsset.Count) return;
            }

            var entries = AuthoredShaderPackage.With(
                one.PackagePath,
                forAsset.Values
                    .Select(value => (value.EntryName, value.Cluster))
                    .DistinctBy(value => value.EntryName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                replaceModel: _ => cluster);

            project.CaptureOriginal(one.PackagePath);
            new PkgArchiveWriter().Write(one.PackagePath, archive.Magic, entries.ToArray());
            project.TrackSave(one.PackagePath);
            Forget(one.Asset);
            Reload();
            status.Text = $"{one.Asset}: shader changed without rewriting the model"
                + (alsoChanged.Count == 0
                    ? "."
                    : $" — {string.Join(", ", alsoChanged.Distinct())} shared the same"
                        + " shader and followed it.");
        }
        catch (Exception failure)
        {
            MessageBox.Show(
                this, failure.Message, "Write the shaders",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Writes the whole package again — its geometry read back out and written by
    /// the same writer an import goes through, with every material on the shader it
    /// was assigned.
    /// </summary>
    private void RewriteWithShaders(
        EquipmentEntry one,
        IReadOnlyDictionary<string, ShaderAssignment> forAsset)
    {
        if (EquipmentCatalog.AssetPaths(one.PackagePath) is not { } paths)
        {
            MessageBox.Show(
                this,
                $"{one.Asset} does not state the path its model answers to.",
                "Write the shaders",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var load = loader.LoadAsset(one.Asset, gameDataPath);
        if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null)
        {
            throw new InvalidOperationException(
                $"{one.Asset} does not load, so its geometry cannot be"
                    + " written again with another shader.");
        }
        var source = LoadedModelSource.From(load.Model, one.Asset, out var problems);
        if (source is null)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        MapModelPackage.WriteProp(
            project!,
            one.Asset,
            source,
            say: line => status.Text = line,
            assetFolder: paths.Folder,
            assetName: paths.Name,
            shaderAssignments: forAsset);
        Forget(one.Asset);
        Reload();
        status.Text = $"{one.Asset} written again: {forAsset.Count} material(s) on their shader.";
    }

    /// <summary>What each material draws with: what the package says, or what the
    /// author has chosen for it since.</summary>
    private void ShowMaterials(EquipmentEntry? entry)
    {
        materialList.BeginUpdate();
        materialList.Items.Clear();
        var keep = materialList.SelectedIndex;
        if (entry is not null)
        {
            assignments.TryGetValue(entry.Asset, out var forAsset);
            foreach (var (material, shader, _) in materials)
            {
                var assigned = forAsset is not null && forAsset.TryGetValue(material, out var one)
                    ? one.Label
                    : Path.GetFileName(shader);
                materialList.Items.Add($"{material}   →   {assigned}");
            }
        }
        materialList.EndUpdate();
        if (keep >= 0 && keep < materialList.Items.Count) materialList.SelectedIndex = keep;
        else if (materialList.Items.Count != 0) materialList.SelectedIndex = 0;
    }

    /// <summary>Writes one entry of the package out, so it can be worked on.</summary>
    private void Extract(string? entryName)
    {
        if (Selected() is not { } one || string.IsNullOrEmpty(entryName)) return;
        using var dialog = new SaveFileDialog
        {
            Title = $"Extract {entryName}",
            FileName = entryName,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var package = new PkgArchiveReader().Read(one.PackagePath);
        var entry = package.Entries.First(value =>
            value.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        File.WriteAllBytes(dialog.FileName, package.ReadEntry(entry).ToArray());
        status.Text = $"{entryName} written to {dialog.FileName}.";
    }

    /// <summary>
    /// Swaps one entry of the package for a file on disk, keeping every other entry,
    /// their order and the archive's magic word.
    ///
    /// This is how a custom shader gets in: forge it under the name the material
    /// already binds, and put it here. The model goes the same way.
    /// </summary>
    private void Replace(string? entryName)
    {
        if (Selected() is not { } one) return;
        if (string.IsNullOrEmpty(entryName))
        {
            MessageBox.Show(
                this,
                "Choose a file from the package first.",
                "Remplacer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var extension = Path.GetExtension(entryName);
        using var dialog = new OpenFileDialog
        {
            Title = $"Replace {entryName} of {one.Asset}",
            CheckFileExists = true,
            Filter = extension.Length > 1
                ? $"Fichiers {extension.TrimStart('.')} (*{extension})|*{extension}"
                    + "|All files (*.*)|*.*"
                : "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var replacement = File.ReadAllBytes(dialog.FileName);
            var package = new PkgArchiveReader().Read(one.PackagePath);
            var swapped = 0;
            var rebuilt = package.Entries
                .Select(entry =>
                {
                    if (!entry.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (entry.Name, Data: package.ReadEntry(entry).ToArray());
                    }
                    swapped++;
                    return (entry.Name, Data: replacement);
                })
                .ToArray();
            if (swapped == 0) return;

            // Through the project when there is one, so the game's own version is kept
            // and the change can be undone like any other.
            project?.CaptureOriginal(one.PackagePath);
            new PkgArchiveWriter().Write(one.PackagePath, package.Magic, rebuilt);
            project?.TrackSave(one.PackagePath);

            Reload();
            status.Text = $"{entryName} replaced in {one.Asset}"
                + $" ({replacement.Length / 1024} Ko)"
                + (project is null ? " — outside a project, nothing is tracked." : ".");
        }
        catch (Exception failure)
        {
            MessageBox.Show(
                this,
                failure.Message,
                "Remplacer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
