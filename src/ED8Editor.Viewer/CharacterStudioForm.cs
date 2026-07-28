using System.Numerics;
using ED8Editor.Application;
using ED8Editor.Core;
using ED8Editor.Rendering;
using ED8Editor.Scene;

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
    private readonly IReadOnlyList<CharacterAuthoringEntry> catalog;
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
    private readonly Button copyAnimationProgram = new()
    {
        Text = "Copy selected ANI program…",
        AutoSize = true,
        Enabled = false,
    };
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

    public CharacterStudioForm(
        string gameDataPath,
        EditorProjectLoader loader,
        D3D11GraphicsDevice graphics,
        ScriptAnimationLibrary animationLibrary,
        CharacterAuthoringKind kind,
        Action<string, bool> onSaving)
    {
        this.gameDataPath = gameDataPath ?? throw new ArgumentNullException(nameof(gameDataPath));
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        this.animationLibrary = animationLibrary ?? throw new ArgumentNullException(nameof(animationLibrary));
        this.kind = kind;
        this.onSaving = onSaving ?? throw new ArgumentNullException(nameof(onSaving));
        catalog = kind == CharacterAuthoringKind.Character
            ? CharacterAuthoringCatalog.LoadCharacters(animationLibrary)
            : CharacterAuthoringCatalog.LoadEnemies(gameDataPath);

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
        copyAnimationProgram.Click += (_, _) => CopyAnimationProgram();
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
        var rigTab = new TabPage("Rig compatibility") { Padding = new Padding(4) };
        rigTab.Controls.Add(rigReport);
        inspectorTabs.TabPages.Add(rigTab);
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
        authoring.Controls.Add(new Button { Text = "Import skinned model… (writer unavailable)", AutoSize = true, Enabled = false });
        authoring.Controls.Add(copyAnimationProgram);
        authoring.Controls.Add(new Button { Text = "Create blank ANI… (format contract incomplete)", AutoSize = true, Enabled = false });
        authoring.Controls.Add(new Button { Text = "Write .dae.phyre… (writer unavailable)", AutoSize = true, Enabled = false });
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
            SplitterDistance = 470,
            Panel1MinSize = 330,
            Panel2MinSize = 400,
        };
        split.Panel1.Controls.Add(inspectorTabs);
        split.Panel2.Controls.Add(previewPanel);
        Controls.Add(split);
        Controls.Add(tools);
    }

    private async Task LoadSelectedAsync()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry) return;
        var generation = ++loadGeneration;
        status.Text = $"Loading {entry.ModelAssetId}…";
        currentClip = null;
        copyAnimationProgram.Enabled =
            animationLibrary.FindAnimationScriptPath(entry.AnimationScript) is not null;
        var model = await GetModelAsync(entry.ModelAssetId);
        if (generation != loadGeneration || IsDisposed) return;
        if (model is null)
        {
            currentModel = null;
            currentResources = null;
            modelTree.Nodes.Clear();
            status.Text = $"Could not load {entry.ModelAssetId}.";
            return;
        }
        currentModel = model;
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
        status.Text = $"{entry.ModelAssetId}: {model.Meshes.Count} meshes, "
            + $"{model.Materials.Count} materials, {model.Skeleton?.Joints.Count ?? 0} joints.";
    }

    private void CopyAnimationProgram()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry) return;
        var source = animationLibrary.FindAnimationScriptPath(entry.AnimationScript);
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
        models.Add(assetId, load.Model);
        return load.Model;
    }

    private async Task LoadAnimationAsync()
    {
        if (entries.SelectedItem is not CharacterAuthoringEntry entry
            || currentModel?.Skeleton is null
            || string.IsNullOrWhiteSpace(animationName.Text))
        {
            return;
        }
        status.Text = $"Loading clip {entry.ModelAssetId}:{animationName.Text.Trim()}…";
        var load = await Task.Run(() =>
            loader.LoadAnimationAsset(entry.ModelAssetId, animationName.Text.Trim(), gameDataPath));
        if (load.Status != AssetAnimationLoadStatus.Loaded || load.Clip is null)
        {
            currentClip = null;
            status.Text = load.Error ?? "The animation clip could not be loaded.";
            return;
        }
        try
        {
            _ = new CpuSkeletonPoseEvaluator().Evaluate(currentModel.Skeleton, load.Clip, load.Clip.StartTime);
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
        var reference = await GetModelAsync(referenceEntry.ModelAssetId);
        if (reference is null) return;
        var profile = new Cs1CharacterRigProfile(referenceEntry.ModelAssetId, reference);
        var result = profile.Compare(currentModel);
        rigReport.Text =
            $"Reference: {profile.ReferenceAssetId} ({profile.ReferenceNodeCount} named nodes)\r\n"
            + $"Candidate: {currentModel.AssetId}\r\n"
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
            binding.Nodes.Add($"Display/script: {entry.DisplayName} / {entry.AnimationScript}");
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
        preview.SetClearColor(new Vector4(0.07f, 0.08f, 0.11f, 1f));
        preview.SetDebugLines(BuildGround());
        preview.SetDebugTriangles(Array.Empty<D3D11DebugTriangle>());
        preview.SetEffectQuads(Array.Empty<D3D11EffectQuad>());
        IReadOnlyList<Matrix4x4>? skin = null;
        if (currentModel?.Skeleton is { } skeleton)
        {
            var clip = currentClip;
            var time = clip is null ? 0f : AnimationTime(clip);
            try
            {
                skin = new CpuSkeletonPoseEvaluator().Evaluate(skeleton, clip, time).SkinMatrices;
            }
            catch (InvalidDataException)
            {
                skin = new CpuSkeletonPoseEvaluator().Evaluate(skeleton, null, 0f).SkinMatrices;
            }
        }
        var instances = currentResources is null
            ? Array.Empty<D3D11SceneInstance>()
            : new[]
            {
                new D3D11SceneInstance(
                    1, currentResources, Matrix4x4.Identity, false, SkinMatrices: skin),
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            renderTimer.Stop();
            renderTimer.Dispose();
            preview?.Dispose();
            foreach (var resource in resources.Values) resource.Dispose();
        }
        base.Dispose(disposing);
    }
}
