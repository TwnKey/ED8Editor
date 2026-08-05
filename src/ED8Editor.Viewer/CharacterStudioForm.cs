using System.Numerics;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Core;
using ED8Editor.Decompiler;
using ED8Editor.Models;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;
using ED8Editor.Rendering;
using ED8Editor.Scene;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ED8Editor.Viewer;

/// <summary>
/// Shared character/enemy authoring workbench. The first milestone deliberately
/// reads game-native assets only: model, skinning, clips and table bindings are
/// inspectable against an explicit reference rig. Phyre export remains disabled
/// until a verified writer exists, so this window cannot emit plausible-looking
/// but invalid game files.
/// </summary>
internal sealed class CharacterStudioForm : Form
{
    private readonly string gameDataPath;
    private readonly EditorProjectLoader loader;
    private readonly D3D11GraphicsDevice graphics;
    private readonly ScriptAnimationLibrary animationLibrary;
    private readonly CharacterAuthoringKind kind;
    private readonly Action<string, bool> onSaving;
    private readonly ModProject? modProject;
    private readonly string? instructionDefinitionsPath;
    private readonly IReadOnlyList<CharacterAuthoringEntry> catalog;
    private readonly IReadOnlyDictionary<int, EnemyBattleProfile> enemyProfiles;
    private readonly ComboBox entries = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 390,
    };
    private readonly ComboBox referenceEntries = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300,
    };
    private readonly Panel previewHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 25) };
    private readonly TreeView modelTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly DataGridView enemyFields = CreateReadWriteGrid("Field", "Value");
    private readonly DataGridView enemyActions = CreateReadOnlyGrid(
        ("id", "Action ID"), ("animation", "ANI function"),
        ("label", "Display label"), ("raw", "Native parameters"));
    private readonly DataGridView enemyRules = CreateReadOnlyGrid(
        ("action", "Action"), ("condition", "When code"),
        ("chance", "Chance %"), ("target", "Target code"),
        ("threshold", "Threshold"), ("parameterA", "Parameter A"),
        ("parameterB", "Parameter B"), ("raw", "Additional parameters"));
    private readonly TreeView enemySupplemental = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListBox enemyDiagnostics = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button saveEnemyProfile = new() { Text = "Save t_mons row", AutoSize = true };
    private readonly Button openEnemyAi = new() { Text = "Open AI graph…", AutoSize = true };
    private readonly Button openEnemyAni = new() { Text = "Open ANI graph…", AutoSize = true };
    private readonly ListBox enemyUses = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button scanEnemyUses = new() { Text = "Scan scenario references", AutoSize = true };
    private readonly TextBox rigReport = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 9f),
    };
    private readonly TextBox animationName = new() { Width = 170, PlaceholderText = "Exact clip name" };
    private readonly Button loadAnimation = new() { Text = "Load clip", AutoSize = true };
    private readonly CheckBox loopAnimation = new() { Text = "Loop", Checked = true, AutoSize = true };
    private readonly Button importModelFile = new() { Text = "Import model file…", AutoSize = true };
    private readonly Button importModelPackage = new() { Text = "Import model folder…", AutoSize = true };
    private readonly ComboBox importedAnimations = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 330,
        Enabled = false,
        DisplayMember = nameof(ImportedAnimationChoice.DisplayName),
    };
    private readonly TextBox importReport = new()
    {
        Width = 420,
        Height = 150,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly Button copyAnimationProgram = new()
    {
        Text = "Copy selected ANI program…",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button fitImportedModel = new()
    {
        Text = "Fit imported model onto this character…",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button importAnimations = new()
    {
        Text = "Put imported animations into this character's slots…",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button createFromSelected = new()
    {
        Text = "Create a new one from the selected…",
        AutoSize = true,
    };
    private readonly ListBox materialList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
    };

    private readonly Button chooseShader = new()
    {
        Text = "Material shader…",
        AutoSize = true,
    };

    private readonly Button writeShaders = new()
    {
        Text = "Write the shaders",
        AutoSize = true,
    };

    /// <summary>
    /// What the author pointed each of this asset's materials at. Kept until the
    /// package is written, so every material lands in one write rather than one
    /// each.
    /// </summary>
    private readonly Dictionary<string, ShaderAssignment> shaderAssignments =
        new(StringComparer.Ordinal);

    private IReadOnlyList<string> materialNames = Array.Empty<string>();

    private ImportedModelScene? importedScene;
    private IReadOnlyList<CpuAnimationClip> importedClips = Array.Empty<CpuAnimationClip>();
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, AutoEllipsis = true };
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };
    private readonly Dictionary<string, CpuModel> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, D3D11ModelResources> resources = new(StringComparer.OrdinalIgnoreCase);
    private D3D11Viewport? preview;
    private CpuModel? currentModel;
    private D3D11ModelResources? currentResources;
    private CpuAnimationClip? currentClip;
    private DateTime animationStarted;
    private float yawDegrees = 25f;
    private float pitchDegrees = -12f;
    private float distance = 4f;
    private Point previousMouse;
    private bool orbiting;
    private int loadGeneration;
    private int rigComparisonGeneration;
    private int animationLoadGeneration;
    private bool disposed;

    public CharacterStudioForm(
        string gameDataPath,
        EditorProjectLoader loader,
        D3D11GraphicsDevice graphics,
        ScriptAnimationLibrary animationLibrary,
        CharacterAuthoringKind kind,
        Action<string, bool> onSaving,
        string? instructionDefinitionsPath = null,
        ModProject? modProject = null)
    {
        this.gameDataPath = gameDataPath ?? throw new ArgumentNullException(nameof(gameDataPath));
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        this.animationLibrary = animationLibrary ?? throw new ArgumentNullException(nameof(animationLibrary));
        this.kind = kind;
        this.onSaving = onSaving ?? throw new ArgumentNullException(nameof(onSaving));
        this.modProject = modProject;
        this.instructionDefinitionsPath = instructionDefinitionsPath;
        var profiles = kind == CharacterAuthoringKind.Enemy
            ? EnemyBattleCatalog.LoadProfiles(gameDataPath)
            : Array.Empty<EnemyBattleProfile>();
        enemyProfiles = profiles.ToDictionary(value => value.DocumentIndex);
        catalog = kind == CharacterAuthoringKind.Character
            ? CharacterAuthoringCatalog.LoadCharacters(animationLibrary)
            : profiles.Select(profile => new CharacterAuthoringEntry(
                CharacterAuthoringKind.Enemy,
                profile.DocumentIndex,
                profile.DisplayName,
                profile.ModelAssetId,
                profile.AnimationScriptName,
                profile.AiScriptName,
                string.Empty,
                $"t_mons.tbl / status row {profile.DocumentIndex}")).ToArray();

        Text = kind == CharacterAuthoringKind.Character ? "Character studio" : "Enemy studio";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1300, 800);
        MinimumSize = new Size(940, 600);
        BuildUi();
        renderTimer.Tick += (_, _) => RenderPreview();
        Shown += (_, _) =>
        {
            renderTimer.Start();
            if (entries.Items.Count > 0) entries.SelectedIndex = 0;
        };
        entries.SelectedIndexChanged += async (_, _) => await LoadSelectedAsync();
        referenceEntries.SelectedIndexChanged += async (_, _) => await UpdateRigComparisonAsync();
        loadAnimation.Click += async (_, _) => await LoadAnimationAsync();
        importModelFile.Click += async (_, _) => await ImportModelFileAsync();
        importModelPackage.Click += async (_, _) => await ImportModelPackageAsync();
        importedAnimations.SelectedIndexChanged += (_, _) => SelectImportedAnimation();
        copyAnimationProgram.Click += (_, _) => CopyAnimationProgram();
        fitImportedModel.Click += (_, _) => FitImportedModel();
        importAnimations.Click += (_, _) => ImportAnimations();
        createFromSelected.Click += (_, _) => CreateFromSelected();
        saveEnemyProfile.Click += (_, _) => SaveEnemyProfile();
        openEnemyAi.Click += (_, _) => OpenSelectedEnemyScript(ai: true);
        openEnemyAni.Click += (_, _) => OpenSelectedEnemyScript(ai: false);
        scanEnemyUses.Click += async (_, _) => await ScanEnemyUsesAsync();
        enemyUses.DoubleClick += (_, _) => OpenSelectedEnemyUse();
        previewHost.MouseWheel += (_, eventArgs) =>
            distance = Math.Clamp(distance * (eventArgs.Delta > 0 ? 0.85f : 1.18f), 0.15f, 500f);
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
    }

    private void BuildUi()
    {
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        tools.Items.Add(new ToolStripLabel(kind == CharacterAuthoringKind.Character ? "Character:" : "Enemy:"));
        tools.Items.Add(new ToolStripControlHost(entries));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(new ToolStripLabel("Reference rig:"));
        tools.Items.Add(new ToolStripControlHost(referenceEntries));

        entries.DisplayMember = nameof(CharacterAuthoringEntry.Label);
        entries.Items.AddRange(catalog.Cast<object>().ToArray());
        referenceEntries.DisplayMember = nameof(CharacterAuthoringEntry.Label);
        referenceEntries.Items.AddRange(catalog.Cast<object>().ToArray());

        var inspectorTabs = new TabControl { Dock = DockStyle.Fill };
        var modelTab = new TabPage("Native model") { Padding = new Padding(4) };
        modelTab.Controls.Add(modelTree);
        inspectorTabs.TabPages.Add(modelTab);
        var materialsTab = new TabPage("Materials") { Padding = new Padding(4) };
        var materialsTools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        materialsTools.Controls.Add(chooseShader);
        materialsTools.Controls.Add(writeShaders);
        var materialsPanel = new Panel { Dock = DockStyle.Fill };
        materialsPanel.Controls.Add(materialList);
        materialsPanel.Controls.Add(materialsTools);
        materialsTab.Controls.Add(materialsPanel);
        inspectorTabs.TabPages.Add(materialsTab);
        materialList.DoubleClick += (_, _) => ChooseMaterialShader();
        chooseShader.Click += (_, _) => ChooseMaterialShader();
        writeShaders.Click += (_, _) => WriteMaterialShaders();

        var rigTab = new TabPage("Rig compatibility") { Padding = new Padding(4) };
        rigTab.Controls.Add(rigReport);
        inspectorTabs.TabPages.Add(rigTab);
        if (kind == CharacterAuthoringKind.Enemy)
            AddEnemyTabs(inspectorTabs);
        var authoringTab = new TabPage("Authoring") { Padding = new Padding(12) };
        var authoring = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        authoring.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Text = "Planned output: model + textures, t_name/t_mons bindings, t_attach equipment, "
                + "ANI program and imported-clip assignments. Export is locked until the Phyre "
                + "model writer and its shader/material contracts are verified.",
        });
        authoring.Controls.Add(new TextBox { Width = 360, PlaceholderText = "Future target asset ID" });
        authoring.Controls.Add(importModelFile);
        authoring.Controls.Add(importModelPackage);
        authoring.Controls.Add(new Label { Text = "Imported animation:", AutoSize = true });
        authoring.Controls.Add(importedAnimations);
        authoring.Controls.Add(importReport);
        authoring.Controls.Add(copyAnimationProgram);
        authoring.Controls.Add(new Button { Text = "Create blank ANI… (format contract incomplete)", AutoSize = true, Enabled = false });
        authoring.Controls.Add(fitImportedModel);
        authoring.Controls.Add(importAnimations);
        authoring.Controls.Add(createFromSelected);
        authoringTab.Controls.Add(authoring);
        inspectorTabs.TabPages.Add(authoringTab);

        var animationBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(5),
            WrapContents = false,
        };
        animationBar.Controls.Add(new Label { Text = "Animation:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        animationBar.Controls.Add(animationName);
        animationBar.Controls.Add(loadAnimation);
        animationBar.Controls.Add(loopAnimation);
        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(previewHost);
        previewPanel.Controls.Add(animationBar);
        previewPanel.Controls.Add(status);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
        };
        split.Panel1.Controls.Add(inspectorTabs);
        split.Panel2.Controls.Add(previewPanel);
        Controls.Add(split);
        Controls.Add(tools);
        WinFormsLayout.SetInitialSplitterDistance(split, 470);
    }

    private async Task ImportModelFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a 3D model",
            Filter = ModelImportCatalog.FileDialogFilter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportModelAsync(dialog.FileName, Path.GetDirectoryName(dialog.FileName)!);
    }

    private async Task ImportModelPackageAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder containing a model and its textures",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var candidates = ModelImportCatalog.Find(dialog.SelectedPath);
        if (candidates.Count == 0)
        {
            MessageBox.Show(
                this,
                "The folder contains no supported model file.",
                "Import model package",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        var selected = candidates.Count == 1
            ? candidates[0]
            : ModelImportSourceDialog.Choose(this, dialog.SelectedPath, candidates);
        if (selected is null) return;
        await ImportModelAsync(selected.Path, dialog.SelectedPath);
    }

    private async Task ImportModelAsync(string modelPath, string packageRoot)
    {
        importModelFile.Enabled = false;
        importModelPackage.Enabled = false;
        status.Text = $"Importing {Path.GetFileName(modelPath)}…";
        try
        {
            var result = await Task.Run(() =>
            {
                var scene = new ModelImportService().Import(modelPath, packageRoot);
                var previewBundle = ImportedModelCpuAdapter.Convert(scene, DecodePreviewTexture);
                return (Scene: scene, Preview: previewBundle);
            });
            if (IsDisposed) return;

            var resourceKey = $"import:{Path.GetFullPath(modelPath)}";
            if (resources.Remove(resourceKey, out var previousResource))
                previousResource.Dispose();
            var gpu = new D3D11ModelUploader(graphics.Device).Upload(result.Preview.Model);
            resources.Add(resourceKey, gpu);
            currentResources = gpu;
            currentModel = result.Preview.Model;
            currentClip = null;
            distance = SuggestedDistance(result.Preview.Model);
            PopulateImportedModelTree(result.Scene, result.Preview.Model);

            importedAnimations.BeginUpdate();
            try
            {
                importedAnimations.Items.Clear();
                importedAnimations.Items.Add(new ImportedAnimationChoice("Bind pose", null));
                importedAnimations.Items.AddRange(result.Preview.Animations
                    .Select(clip => new ImportedAnimationChoice(
                        $"{clip.Name} — {clip.Duration:0.###} s",
                        clip))
                    .Cast<object>()
                    .ToArray());
                importedAnimations.Enabled = true;
                importedAnimations.SelectedIndex = 0;
            }
            finally
            {
                importedAnimations.EndUpdate();
            }
            importReport.Text = BuildImportReport(result.Scene, result.Preview);
            importedScene = result.Scene;
            importedClips = result.Preview.Animations;
            fitImportedModel.Enabled = true;
            importAnimations.Enabled = importedClips.Count != 0;
            await UpdateRigComparisonAsync();
            status.Text =
                $"Imported {Path.GetFileName(modelPath)}: {result.Scene.Meshes.Count} meshes, "
                + $"{result.Scene.Animations.Count} animations, {result.Scene.Textures.Count} source textures.";
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Cannot import model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            status.Text = "Model import failed.";
        }
        finally
        {
            if (!IsDisposed)
            {
                importModelFile.Enabled = true;
                importModelPackage.Enabled = true;
            }
        }
    }

    private void SelectImportedAnimation()
    {
        if (importedAnimations.SelectedItem is not ImportedAnimationChoice choice) return;
        currentClip = choice.Clip;
        animationStarted = DateTime.UtcNow;
        status.Text = choice.Clip is { } clip
            ? $"{clip.Name}: {clip.Duration:0.###} s, {clip.Channels.Count} channels."
            : "Bind pose: imported mesh and skeleton, with no animation applied.";
    }

    private void PopulateImportedModelTree(ImportedModelScene scene, CpuModel model)
    {
        modelTree.BeginUpdate();
        try
        {
            modelTree.Nodes.Clear();
            var source = modelTree.Nodes.Add($"Imported source: {Path.GetFileName(scene.SourcePath)}");
            source.Nodes.Add(
                $"Imported geometry units: {scene.CoordinateSystem.UnitScaleMeters:G6} metre");
            source.Nodes.Add(
                $"Source-declared units: {scene.CoordinateSystem.SourceUnitScaleMeters:G6} metre");
            source.Nodes.Add($"Basis: {(scene.CoordinateSystem.RightHanded ? "right-handed" : "left-handed")}, "
                + $"{scene.CoordinateSystem.UpAxis}-up");
            var meshes = modelTree.Nodes.Add($"Meshes ({scene.Meshes.Count})");
            foreach (var mesh in scene.Meshes)
                meshes.Nodes.Add(
                    $"{mesh.Name} — {mesh.Vertices.Count} vertices, {mesh.Indices.Length / 3} triangles"
                    + (mesh.Skin is null ? string.Empty : $", {mesh.Skin.InverseBindMatrices.Count} bones"));
            var materials = modelTree.Nodes.Add($"Materials ({scene.Materials.Count})");
            foreach (var material in scene.Materials)
                materials.Nodes.Add($"{material.Name} — {material.TextureBindings.Count} texture bindings");
            var textures = modelTree.Nodes.Add($"Source textures ({scene.Textures.Count})");
            foreach (var texture in scene.Textures)
                textures.Nodes.Add($"{texture.Name} — {texture.MediaType}, {texture.EncodedData.Length:N0} bytes");
            var animations = modelTree.Nodes.Add($"Animations ({scene.Animations.Count})");
            foreach (var animation in scene.Animations)
                animations.Nodes.Add(
                    $"{animation.Name} — {animation.DurationSeconds:0.###} s, {animation.Channels.Count} channels");
            var skeleton = modelTree.Nodes.Add($"Scene nodes ({model.Skeleton?.Joints.Count ?? 0})");
            if (model.Skeleton is not null)
                foreach (var joint in model.Skeleton.Joints)
                    skeleton.Nodes.Add(joint.Name);
            source.Expand();
            meshes.Expand();
            animations.Expand();
        }
        finally
        {
            modelTree.EndUpdate();
        }
    }

    private static string BuildImportReport(
        ImportedModelScene scene,
        ImportedCpuModelBundle previewBundle)
    {
        var lines = new List<string>
        {
            $"Source: {scene.SourcePath}",
            $"Nodes: {scene.Nodes.Count}",
            $"Meshes: {scene.Meshes.Count}",
            $"Vertices: {scene.Meshes.Sum(value => value.Vertices.Count):N0}",
            $"Triangles: {scene.Meshes.Sum(value => value.Indices.Length / 3):N0}",
            $"Materials: {scene.Materials.Count}",
            $"Textures preserved: {scene.Textures.Count}",
            $"Animations: {scene.Animations.Count}",
            $"Skinned meshes: {scene.Meshes.Count(value => value.Skin is not null)}",
            string.Empty,
            "Diagnostics:",
        };
        lines.AddRange(previewBundle.Diagnostics.Select(value =>
            $"{value.Severity}: [{value.Code}] {value.Message}"));
        if (previewBundle.Diagnostics.Count == 0) lines.Add("None");
        return string.Join(Environment.NewLine, lines);
    }

    private static CpuTexture? DecodePreviewTexture(ImportedTexture texture)
    {
        using var stream = new MemoryStream(texture.EncodedData, writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(bitmap.Width * 4);
            var data = new byte[checked(rowBytes * bitmap.Height)];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(
                    locked.Scan0 + row * locked.Stride,
                    data,
                    row * rowBytes,
                    rowBytes);
            }
            return new CpuTexture(
                texture.Name,
                bitmap.Width,
                bitmap.Height,
                1,
                "ARGB8",
                data);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry) return;
        var generation = ++loadGeneration;
        ++animationLoadGeneration;
        status.Text = $"Loading {entry.ModelAssetId}…";
        currentClip = null;
        copyAnimationProgram.Enabled = FindAnimationScriptPath(entry) is not null;
        var battleDataTask = kind == CharacterAuthoringKind.Enemy
            ? LoadEnemyBattleDataAsync(entry, generation)
            : Task.CompletedTask;
        var model = await GetModelAsync(entry.ModelAssetId);
        if (generation != loadGeneration || IsDisposed) return;
        if (model is null)
        {
            currentModel = null;
            currentResources = null;
            modelTree.Nodes.Clear();
            status.Text = $"Could not load {entry.ModelAssetId}.";
            await battleDataTask;
            return;
        }
        currentModel = model;
        shaderAssignments.Clear();
        ShowMaterials(entry);
        if (!resources.TryGetValue(entry.ModelAssetId, out var gpu))
        {
            gpu = new D3D11ModelUploader(graphics.Device).Upload(model);
            resources.Add(entry.ModelAssetId, gpu);
        }
        currentResources = gpu;
        distance = SuggestedDistance(model);
        PopulateModelTree(entry, model);
        if (referenceEntries.SelectedIndex < 0)
            referenceEntries.SelectedItem = entry;
        await UpdateRigComparisonAsync();
        await battleDataTask;
        status.Text = $"{entry.ModelAssetId}: {model.Meshes.Count} meshes, "
            + $"{model.Materials.Count} materials, {model.Skeleton?.Joints.Count ?? 0} joints.";
    }

    /// <summary>
    /// The model's materials, and what each draws with. Read from the cluster
    /// itself, which is what names them: a material is what a shader is bound to,
    /// so a list built from anything else would name things the rebind cannot find.
    /// </summary>
    private void ShowMaterials(CharacterAuthoringEntry? entry)
    {
        materialNames = Array.Empty<string>();
        materialList.Items.Clear();
        if (entry is null) return;
        try
        {
            if (CharacterRetargetPackage.ResolveModel(
                    new GameAssetResolverFactory(),
                    new PkgArchiveReader(),
                    new AssetManifestReader(),
                    gameDataPath,
                    entry.ModelAssetId) is not { } resolved)
            {
                return;
            }
            var bound = PhyreMaterialTableReader.ReadAll(resolved.Cluster);
            materialNames = bound.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            foreach (var name in materialNames)
            {
                var shader = shaderAssignments.TryGetValue(name, out var chosen)
                    ? chosen.Label
                    : Path.GetFileName(bound[name].ShaderAsset);
                materialList.Items.Add($"{name}   →   {shader}");
            }
            if (materialList.Items.Count != 0) materialList.SelectedIndex = 0;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            materialList.Items.Add("Materials cannot be read: " + exception.Message);
        }
    }

    /// <summary>
    /// Points the selected material at a shader — one of the game's, or one the
    /// author compiled here. The same window every other editor uses.
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
        using var chooser = new ShaderChooserForm(
            Path.GetDirectoryName(gameDataPath)!, material);
        if (chooser.ShowDialog(this) != DialogResult.OK || chooser.Choice is not { } choice) return;

        shaderAssignments[material] = AuthoredShaderBinding.For(
            material, choice.AssetName, choice.Cluster, choice.Values, choice.Custom);
        ShowMaterials(entries.SelectedItem as CharacterAuthoringEntry);
        status.Text = $"{material} → {choice.AssetName}. Write the shaders to apply it.";
    }

    /// <summary>
    /// Puts the chosen shaders into the asset's own package.
    ///
    /// A character's model is not rewritten here: its skinning, its segments and
    /// its clips are what the rest of this window is careful not to disturb. So the
    /// shader is changed the one way that leaves the model untouched — the name it
    /// is bound by, replaced in place — and a shader that wants a different
    /// material block is refused with its reason rather than forced.
    /// </summary>
    private void WriteMaterialShaders()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry) return;
        if (shaderAssignments.Count == 0)
        {
            MessageBox.Show(
                this, "No material has been given a shader.", "Write the shaders",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            if (CharacterRetargetPackage.ResolveModel(
                    new GameAssetResolverFactory(),
                    new PkgArchiveReader(),
                    new AssetManifestReader(),
                    gameDataPath,
                    entry.ModelAssetId) is not { } resolved)
            {
                throw new InvalidOperationException(
                    $"{entry.ModelAssetId} does not say where its model is.");
            }

            var cluster = resolved.Cluster;
            var refused = new List<string>();
            var alsoChanged = new List<string>();
            foreach (var (material, assignment) in shaderAssignments)
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

            if (refused.Count == shaderAssignments.Count)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine + Environment.NewLine, refused)
                        + Environment.NewLine + Environment.NewLine
                        + "Nothing was written. A shader with a different interface needs"
                        + " the model written again, which a skinned character does not"
                        + " support here.",
                    "Write the shaders",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var written = AuthoredShaderPackage.With(
                resolved.PackagePath,
                shaderAssignments.Values
                    .Select(value => (value.EntryName, value.Cluster))
                    .DistinctBy(value => value.EntryName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                replaceModel: name => name.Equals(
                    resolved.EntryName, StringComparison.OrdinalIgnoreCase)
                        ? cluster
                        : throw new InvalidOperationException(
                            $"{resolved.PackagePath} holds a second model, {name}."));

            var archive = new PkgArchiveReader().Read(resolved.PackagePath);
            onSaving(resolved.PackagePath, true);
            new PkgArchiveWriter().Write(
                resolved.PackagePath, archive.Magic, written.ToArray());
            onSaving(resolved.PackagePath, false);

            models.Remove(entry.ModelAssetId);
            if (resources.Remove(entry.ModelAssetId, out var stale)) stale.Dispose();
            shaderAssignments.Clear();
            ShowMaterials(entry);
            status.Text = $"{entry.ModelAssetId}: shader(s) changed"
                + (refused.Count == 0 ? string.Empty : $", {refused.Count} refused")
                + (alsoChanged.Count == 0
                    ? "."
                    : $" — {string.Join(", ", alsoChanged.Distinct())} shared the"
                        + " same shader and followed it.");
            if (refused.Count != 0)
            {
                MessageBox.Show(
                    this, string.Join(Environment.NewLine + Environment.NewLine, refused),
                    "Shaders refused", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or InvalidPhyreException or ArgumentException)
        {
            MessageBox.Show(
                this, exception.Message, "Write the shaders",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Puts the imported model onto the selected character, so it rides that
    /// character's own animations.
    ///
    /// Nothing about the character's skeleton, shaders, materials or clips
    /// changes — only the geometry of the one segment chosen in the dialog.
    /// That is what makes this safe to try: whatever the import turns out to
    /// be, the file it goes into is still the file the game already loads.
    /// </summary>
    private void FitImportedModel()
    {
        if (importedScene is not { } scene)
        {
            MessageBox.Show(
                this, "Import a model first.", "Fit imported model",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (entries.SelectedItem is not CharacterAuthoringEntry entry)
        {
            MessageBox.Show(
                this, "Choose the character to dress.", "Fit imported model",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        CharacterRetargetPackage.ResolvedModel? resolved;
        CpuModel? targetModel;
        try
        {
            resolved = CharacterRetargetPackage.ResolveModel(
                new GameAssetResolverFactory(),
                new PkgArchiveReader(),
                new AssetManifestReader(),
                gameDataPath,
                entry.ModelAssetId);
            targetModel = resolved is null
                ? null
                : new PhyreD3D11ModelReader().Read(entry.ModelAssetId, resolved.Cluster);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            MessageBox.Show(
                this, exception.Message, "Fit imported model",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (resolved is null || targetModel is null)
        {
            MessageBox.Show(
                this,
                $"{entry.ModelAssetId}'s package could not be resolved.",
                "Fit imported model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        if (targetModel.Skeleton is not { Joints.Count: > 0 })
        {
            MessageBox.Show(
                this,
                $"{entry.ModelAssetId} has no skeleton, so there is no animation"
                    + " for a model to be hung from.",
                "Fit imported model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new CharacterRetargetForm(
            scene, targetModel, entry.ModelAssetId,
            (segmentIndex, mapping) => Commit(scene, resolved, targetModel, segmentIndex, mapping));
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Stands a new character or enemy up from the selected one.
    ///
    /// Every package that belongs to it travels — the model and each companion —
    /// and each one is renamed to answer to the new asset. That is what makes the
    /// same action right for both kinds: a character keeps its clips in a
    /// <c>_DF1</c> package and an enemy in its own, and neither is a rule this
    /// has to know, because every companion follows the asset's name.
    /// </summary>
    private void CreateFromSelected()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry)
        {
            MessageBox.Show(
                this, "Choose the one to take as a base first.",
                "Create", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (modProject is null)
        {
            MessageBox.Show(
                this,
                "Creating goes through a mod project, which tracks the files it makes and can"
                    + " remove them. Open one.",
                "Create",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var companions = CharacterCreation.Companions(
            modProject.GameDirectory, entry.ModelAssetId);
        var chosen = Prompt(
            "Name of the new asset",
            $"Copied from {entry.ModelAssetId} and its {companions.Count} package(s)."
                + " Keep the shape of the game's own names.",
            Suggested(entry.ModelAssetId));
        if (chosen is null) return;
        chosen = chosen.Trim().ToUpperInvariant();
        if (chosen.Length == 0) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var made = CharacterCreation.Create(
                modProject, entry.ModelAssetId, chosen, line => status.Text = line);
            MessageBox.Show(
                this,
                $"{made.AssetId} created: {made.Written.Count} package(s),"
                + $" {made.Symbols} symbol(s) renamed.\r\n\r\n"
                + "It already loads, with its source's model and animations. What"
                + " remains for the game to use it is naming it in its tables —"
                + " t_mons for an enemy, t_name for a character — which this"
                + " window does not do yet.",
                "Create",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            status.Text = $"{made.AssetId}: {string.Join(", ", made.Written)}";
        }
        catch (Exception exception) when (exception is IOException
            or FileNotFoundException or ArgumentException or InvalidDataException)
        {
            MessageBox.Show(
                this, exception.Message, "Create",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>A name of the same shape, in the range mods can take over.</summary>
    private string Suggested(string assetId)
    {
        var digits = new string(assetId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length == 0) return assetId + "_NEW";
        var stem = assetId[..^digits.Length];
        for (var number = 900; number < 1000; number++)
        {
            var candidate = stem + number.ToString(new string('0', digits.Length));
            if (CharacterCreation.Companions(
                    modProject!.GameDirectory, candidate).Count == 0)
            {
                return candidate;
            }
        }
        return stem + "900";
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
            ClientSize = new Size(470, 140),
        };
        var label = new Label { Left = 12, Top = 12, Width = 446, Height = 46, Text = message };
        var box = new TextBox { Left = 12, Top = 66, Width = 446, Text = initial };
        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK, Left = 302, Top = 100, Width = 75,
        };
        var cancel = new Button
        {
            Text = "Annuler", DialogResult = DialogResult.Cancel, Left = 383, Top = 100, Width = 75,
        };
        window.Controls.AddRange(new Control[] { label, box, ok, cancel });
        window.AcceptButton = ok;
        window.CancelButton = cancel;
        return window.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    /// <summary>
    /// Writes the animations the import brought into the character's own clip
    /// slots, so the game plays them without a script being touched.
    ///
    /// The bones are renamed on the way in, by the same guess the model fit uses —
    /// an animation targeting <c>mixamorig:LeftForeArm</c> has to drive the bone
    /// the game calls <c>LeftForeArm</c>, or it drives nothing.
    /// </summary>
    private void ImportAnimations()
    {
        if (importedScene is not { } scene || importedClips.Count == 0)
        {
            MessageBox.Show(
                this, "Import a model that carries animations first.",
                "Imported animations", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (entries.SelectedItem is not CharacterAuthoringEntry entry)
        {
            MessageBox.Show(
                this, "Choose the character to animate.",
                "Imported animations", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<CharacterAnimationPackage.ClipSlot> slots;
        CpuModel? target;
        try
        {
            slots = CharacterAnimationPackage.Slots(
                new GameAssetResolverFactory(), new PkgArchiveReader(), new AssetManifestReader(),
                gameDataPath, entry.ModelAssetId);
            target = LoadedModel(entry.ModelAssetId);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(
                this, exception.Message, "Imported animations",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (slots.Count == 0)
        {
            MessageBox.Show(
                this,
                $"{entry.ModelAssetId} declares no animation slot, so there is"
                    + " nothing the game would know how to play.",
                "Imported animations",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Rig bone to game bone, guessed the same way the model fit guesses it.
        var gameBones = target?.Skeleton?.Joints.Select(joint => joint.Name).ToArray()
            ?? Array.Empty<string>();
        var sourceBones = scene.Nodes.Select(node => node.Name).ToArray();
        var mapping = Cs1RigNameMapper.AutoMap(sourceBones, gameBones)
            .Where(value => value.TargetName is not null)
            .ToDictionary(value => value.SourceName, value => value.TargetName!, StringComparer.Ordinal);

        using var dialog = new CharacterAnimationImportForm(
            importedClips, slots, entry.ModelAssetId,
            chosen => CommitAnimations(chosen, mapping, entry.ModelAssetId));
        dialog.ShowDialog(this);
    }

    /// <summary>The character's own model, loaded synchronously for its skeleton.</summary>
    private CpuModel? LoadedModel(string assetId)
    {
        if (models.TryGetValue(assetId, out var cached)) return cached;
        var load = loader.LoadAsset(assetId, gameDataPath);
        if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null) return null;
        models[assetId] = load.Model;
        return load.Model;
    }

    private bool CommitAnimations(
        IReadOnlyList<(CpuAnimationClip Clip, CharacterAnimationPackage.ClipSlot Slot)> chosen,
        IReadOnlyDictionary<string, string> mapping,
        string assetId)
    {
        try
        {
            var written = 0;
            var dropped = 0;
            foreach (var (clip, slot) in chosen)
            {
                var retargeted = CharacterAnimationPackage.Retarget(clip, mapping);
                if (retargeted.Channels.Count == 0)
                {
                    dropped++;
                    continue;
                }
                CharacterAnimationPackage.Write(
                    onSaving, new PkgArchiveReader(), slot, retargeted);
                written++;
            }
            status.Text = dropped == 0
                ? $"{written} animation(s) written into {assetId}."
                : $"{written} animation(s) written into {assetId}; {dropped} drove no bone"
                    + " this skeleton has and were left out.";
            return written != 0;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(
                this, exception.Message, "Imported animations",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>Fits and writes, reporting exactly what the fit cost.</summary>
    private bool Commit(
        ImportedModelScene scene,
        CharacterRetargetPackage.ResolvedModel resolved,
        CpuModel targetModel,
        int segmentIndex,
        IReadOnlyDictionary<string, string>? mapping)
    {
        try
        {
            var fit = CharacterRetargetPackage.Fit(scene, targetModel, segmentIndex, mapping);
            CharacterRetargetPackage.WriteSegment(onSaving, resolved, fit.Full);
            models.Remove(targetModel.AssetId);
            if (resources.Remove(targetModel.AssetId, out var stale)) stale.Dispose();
            status.Text =
                $"Segment {segmentIndex} of {targetModel.AssetId} replaced —"
                + $" {(fit.UsedForeignSkin ? "weights re-addressed from the import's own rig" : "bound to the nearest bones")}"
                + $", {fit.Walked} vertex/vertices carried to an ancestor bone"
                + $", at most {fit.DroppedWeight:0.###} of a vertex's weight dropped.";
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(
                this, exception.Message, "Fit imported model",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void CopyAnimationProgram()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry) return;
        var source = FindAnimationScriptPath(entry);
        if (source is null)
        {
            MessageBox.Show(
                this,
                $"The table-declared ANI program '{entry.AnimationScript}' was not found.",
                "Copy ANI program",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Copy the selected ANI program",
            Filter = "Cold Steel scripts (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(source),
            FileName = Path.GetFileName(source),
            DefaultExt = "dat",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (Path.GetFullPath(dialog.FileName).Equals(
                Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Choose a destination different from the source ANI program.",
                "Copy ANI program",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        try
        {
            onSaving(dialog.FileName, true);
            File.Copy(source, dialog.FileName, overwrite: true);
            onSaving(dialog.FileName, false);
            status.Text =
                $"Copied {Path.GetFileName(source)} to {dialog.FileName}.";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Cannot copy ANI program",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task<CpuModel?> GetModelAsync(string assetId)
    {
        if (models.TryGetValue(assetId, out var cached)) return cached;
        var load = await Task.Run(() => loader.LoadAsset(assetId, gameDataPath));
        if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null) return null;
        if (models.TryAdd(assetId, load.Model)) return load.Model;
        return models[assetId];
    }

    private string? FindAnimationScriptPath(CharacterAuthoringEntry entry)
        => entry.Kind == CharacterAuthoringKind.Enemy
            ? EnemyBattleCatalog.ResolveAniScript(gameDataPath, entry.AnimationScript)
            : animationLibrary.FindAnimationScriptPath(entry.AnimationScript);

    private async Task LoadAnimationAsync()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry
            || currentModel?.Skeleton is null
            || string.IsNullOrWhiteSpace(animationName.Text))
        {
            return;
        }
        var generation = ++animationLoadGeneration;
        var model = currentModel;
        status.Text = $"Loading clip {entry.ModelAssetId}:{animationName.Text.Trim()}…";
        var load = await Task.Run(() =>
            loader.LoadAnimationAsset(entry.ModelAssetId, animationName.Text.Trim(), gameDataPath));
        if (generation != animationLoadGeneration
            || IsDisposed
            || !ReferenceEquals(model, currentModel))
        {
            return;
        }
        if (load.Status != AssetAnimationLoadStatus.Loaded || load.Clip is null)
        {
            currentClip = null;
            status.Text = load.Error ?? "The animation clip could not be loaded.";
            return;
        }
        try
        {
            _ = new CpuSkeletonPoseEvaluator().Evaluate(model.Skeleton!, load.Clip, load.Clip.StartTime);
        }
        catch (InvalidDataException exception)
        {
            currentClip = null;
            status.Text = $"Clip/skeleton mismatch: {exception.Message}";
            return;
        }
        currentClip = load.Clip;
        animationStarted = DateTime.UtcNow;
        status.Text = $"{load.Clip.Name}: {load.Clip.Duration:0.###} s, {load.Clip.Channels.Count} channels.";
    }

    private async Task UpdateRigComparisonAsync()
    {
        if (currentModel is null
            || referenceEntries.SelectedItem is not CharacterAuthoringEntry referenceEntry)
        {
            rigReport.Text = "Select a model and an explicit CS1 reference rig.";
            return;
        }
        var generation = ++rigComparisonGeneration;
        var candidate = currentModel;
        var reference = await GetModelAsync(referenceEntry.ModelAssetId);
        if (reference is null
            || generation != rigComparisonGeneration
            || IsDisposed
            || !ReferenceEquals(candidate, currentModel)
            || !ReferenceEquals(referenceEntry, referenceEntries.SelectedItem))
        {
            return;
        }
        var profile = new Cs1CharacterRigProfile(referenceEntry.ModelAssetId, reference);
        var result = profile.Compare(candidate);
        rigReport.Text =
            $"Reference: {profile.ReferenceAssetId} ({profile.ReferenceNodeCount} named nodes)\r\n"
            + $"Candidate: {candidate.AssetId}\r\n"
            + $"Shared: {result.SharedNodes}\r\n"
            + $"Missing reference nodes: {result.MissingReferenceNodes.Count}\r\n"
            + string.Join("\r\n", result.MissingReferenceNodes.Select(value => "  - " + value))
            + "\r\n\r\n"
            + $"Additional candidate nodes: {result.AdditionalNodes.Count}\r\n"
            + string.Join("\r\n", result.AdditionalNodes.Select(value => "  + " + value));
    }

    private void PopulateModelTree(CharacterAuthoringEntry entry, CpuModel model)
    {
        modelTree.BeginUpdate();
        try
        {
            modelTree.Nodes.Clear();
            var binding = modelTree.Nodes.Add("Game bindings");
            binding.Nodes.Add($"Source: {entry.SourceTable}");
            binding.Nodes.Add($"Display: {entry.DisplayName}");
            binding.Nodes.Add($"ANI program: {entry.AnimationScript}");
            if (!string.IsNullOrWhiteSpace(entry.BattleAiScript))
                binding.Nodes.Add($"Battle AI: al{entry.BattleAiScript}.dat");
            binding.Nodes.Add($"Facial asset: {entry.FacialAssetId}");
            var meshes = modelTree.Nodes.Add($"Meshes ({model.Meshes.Count})");
            foreach (var mesh in model.Meshes) meshes.Nodes.Add($"{mesh.Name} — {mesh.Primitives.Count} primitives");
            var materials = modelTree.Nodes.Add($"Materials ({model.Materials.Count})");
            foreach (var material in model.Materials) materials.Nodes.Add(material.Name);
            var textures = modelTree.Nodes.Add($"Textures ({model.Textures.Count})");
            foreach (var texture in model.Textures) textures.Nodes.Add($"{texture.Name} — {texture.Width}x{texture.Height}");
            var skeleton = modelTree.Nodes.Add($"Skeleton ({model.Skeleton?.Joints.Count ?? 0})");
            if (model.Skeleton is not null)
                foreach (var joint in model.Skeleton.Joints) skeleton.Nodes.Add(joint.Name);
            binding.Expand();
            meshes.Expand();
        }
        finally
        {
            modelTree.EndUpdate();
        }
    }

    private void RenderPreview()
    {
        if (previewHost.ClientSize.Width <= 0 || previewHost.ClientSize.Height <= 0) return;
        preview ??= new D3D11Viewport(
            graphics, previewHost.Handle, previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.Resize(previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.SetClearColor(new Vector4(0.92f, 0.93f, 0.95f, 1f));
        preview.SetDebugLines(BuildGround());
        preview.SetDebugTriangles(Array.Empty<D3D11DebugTriangle>());
        preview.SetEffectQuads(Array.Empty<D3D11EffectQuad>());
        IReadOnlyList<Matrix4x4>? skin = null;
        IReadOnlyList<Matrix4x4>? sceneNodeTransforms = null;
        var clip = currentClip;
        var time = clip is null ? 0f : AnimationTime(clip);
        if (currentModel?.Skeleton is { } skeleton)
        {
            try
            {
                skin = new CpuSkeletonPoseEvaluator().Evaluate(skeleton, clip, time).SkinMatrices;
            }
            catch (InvalidDataException)
            {
                skin = new CpuSkeletonPoseEvaluator().Evaluate(skeleton, null, 0f).SkinMatrices;
            }
        }
        if (clip is not null && currentModel?.SceneNodes is { Count: > 0 } sceneNodes)
        {
            try
            {
                sceneNodeTransforms = new CpuSceneAnimationEvaluator()
                    .Evaluate(sceneNodes, clip, time).WorldTransforms;
            }
            catch (InvalidDataException)
            {
                sceneNodeTransforms = null;
            }
        }
        var instances = currentResources is null
            ? Array.Empty<D3D11SceneInstance>()
            : new[]
            {
                new D3D11SceneInstance(
                    1, currentResources, Matrix4x4.Identity, false,
                    MaterialDiffuse: Vector4.One,
                    SkinMatrices: skin,
                    SceneNodeTransforms: sceneNodeTransforms),
            };
        preview.Render(instances, CreateCamera(), verticalSync: false);
    }

    private float AnimationTime(CpuAnimationClip clip)
    {
        if (clip.Duration <= 0f) return clip.StartTime;
        var elapsed = (float)(DateTime.UtcNow - animationStarted).TotalSeconds;
        return loopAnimation.Checked
            ? clip.StartTime + elapsed % clip.Duration
            : Math.Min(clip.EndTime, clip.StartTime + elapsed);
    }

    private ViewportCamera CreateCamera()
    {
        var center = currentModel is null ? Vector3.Zero : ModelBounds(currentModel).Center;
        var yaw = yawDegrees * MathF.PI / 180f;
        var pitch = pitchDegrees * MathF.PI / 180f;
        var direction = new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
        var position = center - direction * distance;
        return new ViewportCamera(
            Matrix4x4.CreateLookAt(position, center, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                previewHost.ClientSize.Width / (float)Math.Max(1, previewHost.ClientSize.Height),
                Math.Max(0.001f, distance / 1000f),
                Math.Max(100f, distance * 20f)));
    }

    private static float SuggestedDistance(CpuModel model)
        => Math.Max(ModelBounds(model).Radius * 2.8f, 1f);

    private static SceneBoundsResult ModelBounds(CpuModel model)
        => new SceneBoundsCalculator().Calculate(new[]
        {
            new SceneModelInstance(1, model.AssetId, model.AssetId, model, Matrix4x4.Identity),
        });

    private static IReadOnlyList<D3D11DebugLine> BuildGround()
    {
        var lines = new List<D3D11DebugLine>();
        var color = new Vector4(0.25f, 0.28f, 0.34f, 1f);
        for (var i = -5; i <= 5; i++)
        {
            lines.Add(new D3D11DebugLine(new Vector3(-5, 0, i), new Vector3(5, 0, i), color));
            lines.Add(new D3D11DebugLine(new Vector3(i, 0, -5), new Vector3(i, 0, 5), color));
        }
        return lines;
    }

    private void AddEnemyTabs(TabControl tabs)
    {
        var identity = new TabPage("Enemy data") { Padding = new Padding(4) };
        var identityButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        identityButtons.Controls.Add(saveEnemyProfile);
        identity.Controls.Add(enemyFields);
        identity.Controls.Add(identityButtons);
        tabs.TabPages.Add(identity);

        var actions = new TabPage("Actions") { Padding = new Padding(4) };
        actions.Controls.Add(enemyActions);
        tabs.TabPages.Add(actions);

        var rules = new TabPage("AI rules") { Padding = new Padding(4) };
        rules.Controls.Add(enemyRules);
        tabs.TabPages.Add(rules);

        var supplemental = new TabPage("Summons / parts") { Padding = new Padding(4) };
        supplemental.Controls.Add(enemySupplemental);
        tabs.TabPages.Add(supplemental);

        var resourcesTab = new TabPage("Battle resources") { Padding = new Padding(8) };
        var resourcesPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            WrapContents = false,
        };
        resourcesPanel.Controls.Add(openEnemyAi);
        resourcesPanel.Controls.Add(openEnemyAni);
        resourcesTab.Controls.Add(enemyDiagnostics);
        resourcesTab.Controls.Add(resourcesPanel);
        tabs.TabPages.Add(resourcesTab);

        var uses = new TabPage("Used by encounters") { Padding = new Padding(4) };
        var usesTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(4),
        };
        usesTools.Controls.Add(scanEnemyUses);
        uses.Controls.Add(enemyUses);
        uses.Controls.Add(usesTools);
        tabs.TabPages.Add(uses);
    }

    private async Task LoadEnemyBattleDataAsync(
        CharacterAuthoringEntry entry,
        int generation)
    {
        if (entry.TableId is not { } tableIndex
            || !enemyProfiles.TryGetValue(tableIndex, out var profile))
        {
            return;
        }
        enemyFields.Rows.Clear();
        foreach (var field in profile.Fields)
        {
            var row = enemyFields.Rows.Add(field.Field.Name, field.Value);
            enemyFields.Rows[row].Tag = field.Field.Name;
        }
        enemyDiagnostics.Items.Clear();
        enemyDiagnostics.Items.Add("Loading battle resources…");
        var analysis = await Task.Run(() =>
            EnemyBattleCatalog.Analyze(
                gameDataPath, profile, instructionDefinitionsPath));
        if (generation != loadGeneration || IsDisposed) return;

        openEnemyAi.Enabled = analysis.AiScriptPath is not null;
        openEnemyAi.Tag = analysis.AiScriptPath;
        openEnemyAni.Enabled = analysis.AnimationScriptPath is not null;
        openEnemyAni.Tag = analysis.AnimationScriptPath;

        enemyActions.Rows.Clear();
        foreach (var action in analysis.Actions)
        {
            enemyActions.Rows.Add(
                action.ActionId,
                action.AnimationFunction,
                action.DisplayLabel,
                Convert.ToHexString(action.Parameters));
        }

        enemyRules.Rows.Clear();
        foreach (var rule in analysis.Rules)
        {
            enemyRules.Rows.Add(
                FormatAction(rule.ActionId),
                $"0x{rule.ConditionSelector:X2}",
                rule.Probability,
                $"0x{rule.TargetSelector:X2}",
                rule.Threshold,
                rule.ParameterA,
                rule.ParameterB,
                Convert.ToHexString(rule.AdditionalParameters));
        }

        enemySupplemental.BeginUpdate();
        try
        {
            enemySupplemental.Nodes.Clear();
            foreach (var table in analysis.SupplementalTables)
            {
                var tableNode = enemySupplemental.Nodes.Add(
                    $"{table.Kind} ({table.Rows.Count})");
                foreach (var row in table.Rows)
                {
                    var rowNode = tableNode.Nodes.Add(
                        row.TryGetValue("Index", out var index)
                            ? $"Entry {index}"
                            : "Entry");
                    foreach (var value in row.Where(value => value.Key != "Index"))
                        rowNode.Nodes.Add($"{value.Key}: {value.Value}");
                }
                tableNode.Expand();
            }
        }
        finally
        {
            enemySupplemental.EndUpdate();
        }

        enemyDiagnostics.Items.Clear();
        enemyDiagnostics.Items.Add(
            analysis.AiScriptPath is null
                ? "AI: missing"
                : $"AI: {analysis.AiScriptPath}");
        enemyDiagnostics.Items.Add(
            analysis.AnimationScriptPath is null
                ? "ANI: missing"
                : $"ANI: {analysis.AnimationScriptPath}");
        foreach (var diagnostic in analysis.Diagnostics)
            enemyDiagnostics.Items.Add(diagnostic);
        enemyUses.Items.Clear();
        enemyUses.Items.Add("Use “Scan scenario references” to index all scena DAT files.");
    }

    private void SaveEnemyProfile()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry
            {
                TableId: { } tableIndex,
            }
            || !enemyProfiles.TryGetValue(tableIndex, out var profile))
        {
            return;
        }
        try
        {
            var values = enemyFields.Rows
                .Cast<DataGridViewRow>()
                .Where(row => row.Tag is string)
                .ToDictionary(
                    row => (string)row.Tag!,
                    row => row.Cells[1].Value?.ToString() ?? string.Empty,
                    StringComparer.Ordinal);
            EnemyBattleCatalog.SaveProfile(profile, values, onSaving);
            status.Text = $"Saved {Path.GetFileName(profile.TablePath)} row {profile.DocumentIndex}.";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or FormatException
            or OverflowException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot save enemy data",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenSelectedEnemyScript(bool ai)
    {
        var button = ai ? openEnemyAi : openEnemyAni;
        if (button.Tag is not string path || !File.Exists(path)) return;
        var editor = new ScriptEditorForm(
            instructionDefinitionsPath: instructionDefinitionsPath,
            monsterChoices: MonsterTableCatalog.Load(gameDataPath));
        editor.FileSaving += target => onSaving(target, true);
        editor.FileSaved += target => onSaving(target, false);
        editor.FormClosed += (_, _) => editor.Dispose();
        editor.LoadDat(path);
        editor.Show(this);
    }

    private async Task ScanEnemyUsesAsync()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry
            {
                BattleAiScript: { Length: > 0 } assetId,
            })
        {
            return;
        }
        var generation = loadGeneration;
        scanEnemyUses.Enabled = false;
        enemyUses.Items.Clear();
        enemyUses.Items.Add("Scanning scena scripts…");
        try
        {
            var references = await Task.Run(() =>
                EnemyEncounterReferenceCatalog.Find(
                    gameDataPath, assetId, instructionDefinitionsPath));
            if (generation != loadGeneration || IsDisposed) return;
            enemyUses.Items.Clear();
            if (references.Count == 0)
                enemyUses.Items.Add("No CreateMonsters reference found.");
            else
                enemyUses.Items.AddRange(references.Cast<object>().ToArray());
            enemyUses.DisplayMember = nameof(EnemyEncounterReference.Label);
        }
        finally
        {
            if (!IsDisposed) scanEnemyUses.Enabled = true;
        }
    }

    private void OpenSelectedEnemyUse()
    {
        if (enemyUses.SelectedItem is not EnemyEncounterReference reference) return;
        var editor = new ScriptEditorForm(
            instructionDefinitionsPath: instructionDefinitionsPath,
            monsterChoices: MonsterTableCatalog.Load(gameDataPath));
        editor.FileSaving += target => onSaving(target, true);
        editor.FileSaved += target => onSaving(target, false);
        editor.FormClosed += (_, _) => editor.Dispose();
        editor.LoadDat(reference.ScriptPath);
        editor.Show(this);
    }

    private static string FormatAction(int actionId)
        => actionId < 1000
            ? $"{actionId} (shared)"
            : actionId.ToString();

    private static DataGridView CreateReadWriteGrid(string first, string second)
    {
        var grid = CreateGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = first,
            ReadOnly = true,
            FillWeight = 48,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = second,
            FillWeight = 72,
        });
        return grid;
    }

    private static DataGridView CreateReadOnlyGrid(
        params (string Name, string Header)[] columns)
    {
        var grid = CreateGrid();
        foreach (var column in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = column.Name,
                HeaderText = column.Header,
                ReadOnly = true,
            });
        }
        return grid;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
    };

    private sealed record ImportedAnimationChoice(
        string DisplayName,
        CpuAnimationClip? Clip);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            renderTimer.Stop();
            renderTimer.Dispose();
            var viewport = preview;
            preview = null;
            viewport?.Dispose();
            foreach (var resource in resources.Values) resource.Dispose();
            resources.Clear();
        }
        base.Dispose(disposing);
    }
}
