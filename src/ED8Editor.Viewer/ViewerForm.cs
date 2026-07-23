using System.Diagnostics;
using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Decompiler;
using ED8Editor.Ops;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Rendering;
using ED8Editor.Scene;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

public sealed class ViewerForm : Form
{
    private readonly EditorSession session;
    private readonly EditorProjectLoader projectLoader;
    private readonly EditorSettingsStore settingsStore;
    private readonly EditorSceneDocument document;
    private readonly bool smokeTest;
    private readonly string baseTitle;
    private readonly SceneElementPicker elementPicker = new();
    private readonly SceneRaycaster surfaceRaycaster = new();
    private readonly EditorOrbitCamera cameraNavigation = new();
    private readonly EditorCameraDollySmoother cameraDollySmoother = new();
    private readonly SceneTranslationGizmo translationGizmo = new();
    private readonly SceneRotationGizmo rotationGizmo = new();
    private readonly SceneCameraGizmo cameraGizmo = new();
    private readonly OpsWriter opsWriter = new();
    private readonly HashSet<Keys> pressedKeys = new();
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource effectMetadataCancellation = new();
    private readonly List<D3D11ModelResources> uploadedModels = new();
    private readonly Dictionary<string, CpuModel> loadedModelsByAsset = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CpuAnimationClip> loadedCharacterAnimations = new(StringComparer.OrdinalIgnoreCase);
    private readonly CpuSkeletonPoseEvaluator poseEvaluator = new();
    private readonly CpuSceneAnimationEvaluator sceneAnimationEvaluator = new();
    private readonly Panel viewportHost = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly ToolStrip gizmoToolStrip = new()
    {
        Dock = DockStyle.Top,
        GripStyle = ToolStripGripStyle.Hidden,
        RenderMode = ToolStripRenderMode.System,
    };
    private readonly ToolStripButton translateToolButton = new("Move (1)") { ToolTipText = "Translate the selected object" };
    private readonly ToolStripButton rotateToolButton = new("Rotate (2)") { ToolTipText = "Rotate the selected object" };
    private readonly ToolStripButton scaleToolButton = new("Scale (3)") { ToolTipText = "Scale the selected object" };
    private readonly MenuStrip mainMenu = new();
    private readonly TrackBar cameraFovSlider = new()
    {
        Minimum = 20,
        Maximum = 120,
        Value = 60,
        TickFrequency = 10,
        SmallChange = 1,
        LargeChange = 5,
        Width = 150,
        Height = 28,
        AutoSize = false,
    };
    private readonly ToolStripLabel cameraFovLabel = new("FOV: 60°");
    private readonly ToolStripLabel cameraPositionLabel = new("Pos:");
    private readonly TextBox cameraPosX = new() { Text = "0", Width = 55 };
    private readonly TextBox cameraPosY = new() { Text = "0", Width = 55 };
    private readonly TextBox cameraPosZ = new() { Text = "0", Width = 55 };
    private readonly ToolStripLabel cameraAngleLabel = new("Angles (rad):");
    private readonly TextBox cameraYaw = new() { Text = "0", Width = 55 };
    private readonly TextBox cameraPitch = new() { Text = "0", Width = 55 };
    private readonly TextBox cameraRoll = new() { Text = "0", Width = 55 };
    private bool suppressCameraTextUpdate;
    private readonly Panel scenePanel = new() { Dock = DockStyle.Left, Width = 340, Padding = new Padding(8) };
    private readonly GroupBox sceneOutlinerGroup = new()
    {
        Dock = DockStyle.Fill,
        Text = "Map objects (grouped by category)",
    };
    private readonly TreeView sceneOutliner = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
    };
    private readonly Panel assetPanel = new() { Dock = DockStyle.Right, Width = 420, MinimumSize = new Size(280, 0) };
    private readonly Splitter assetPanelSplitter = new()
    {
        Dock = DockStyle.Right,
        Width = 6,
        MinSize = 280,
        MinExtra = 320,
        BackColor = SystemColors.ControlDark,
    };
    private readonly TabControl rightPanelTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage assetsTab = new("Assets / OPS");
    private readonly TabPage scriptsTab = new("Scripts");
    private readonly TabPage tblTab = new("Tbl");
    private readonly Panel assetControlsPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly TextBox assetSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search PKG assets..." };
    private readonly CheckBox snapCheckBox = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        Text = "Snap: 0.25 units / 15 degrees / 0.1 scale",
    };
    private readonly CheckBox showIndicatorsCheckBox = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        Text = "Show map indicators / triggers",
        Checked = true,
    };
    private readonly ComboBox keyboardLayoutList = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly ComboBox environmentVariantList = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly Label effectMetadataStatus = new()
    {
        Dock = DockStyle.Top,
        Height = 24,
        Text = "Phyre effects: pending",
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly ListBox assetList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button addAssetButton = new() { Dock = DockStyle.Bottom, Height = 34, Text = "Add selected asset" };
    private readonly Button duplicateButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Duplicate selected element" };
    private readonly Button deleteButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Delete selected element" };
    private readonly GroupBox propertyGroup = new() { Dock = DockStyle.Bottom, Height = 300, Text = "Selected OPS attributes" };
    private readonly DataGridView propertyGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Button applyPropertiesButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Apply attributes" };
    private readonly GroupBox opsCreationGroup = new() { Dock = DockStyle.Bottom, Height = 170, Text = "Create OPS element" };
    private readonly ComboBox opsProfileList = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label opsProfileEvidence = new() { Dock = DockStyle.Top, Height = 34, AutoEllipsis = true };
    private readonly FlowLayoutPanel opsInputPanel = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
    };
    private readonly Button addOpsElementButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Place OPS element" };
    private readonly SceneSnapSettings snapSettings = new(0.25f, MathF.PI / 12f, 0.1f);
    private readonly SceneOutlinerBuilder outlinerBuilder = new();
    private readonly Dictionary<string, TextBox> opsInputFields = new(StringComparer.Ordinal);
    private IReadOnlyList<AssetCatalogEntry> assetCatalog = Array.Empty<AssetCatalogEntry>();
    private D3D11GraphicsDevice? graphics;
    private D3D11Viewport? viewport;
    private IReadOnlyList<D3D11SceneInstance> instances = Array.Empty<D3D11SceneInstance>();
    private IReadOnlyList<SceneModelInstance> sceneInstances = Array.Empty<SceneModelInstance>();
    private IReadOnlyList<SceneModelInstance> scriptMonsterInstances = Array.Empty<SceneModelInstance>();
    private MapScene? currentMap;
    private float sceneRadius = 10f;
    private float overlayMarkerSize = 0.3f;
    private Point previousMouse;
    private Point leftMouseDown;
    private bool pendingLeftClick;
    private bool lookCursorLocked;
    private bool rightLookMoved;
    private Point lookCursorRestoreScreen;
    private CameraDragMode cameraDrag;
    private long previousFrameTicks;
    private SceneElementSelection? selection;
    private GizmoDragState? gizmoDrag;
    private GizmoMode gizmoMode = GizmoMode.Translate;
    private string? savedOpsPath;
    private PlacementState? placement;
    private SceneCameraHandle cameraHandle = SceneCameraHandle.Eye;
    private IReadOnlyList<SceneElementSelection> outlinerSelections = Array.Empty<SceneElementSelection>();
    private bool refreshingOutliner;
    private EditorKeyboardLayout keyboardLayout;
    private SceneEnvironmentVariant environmentVariant = SceneEnvironmentVariant.Daylight;
    private bool refreshingEnvironmentVariant;
    private string? instructionDefinitionsPath;
    private float CameraVerticalFieldOfView => cameraFovSlider.Value * MathF.PI / 180f;

    public ViewerForm(
        EditorSession session,
        bool smokeTest,
        EditorProjectLoader? projectLoader = null,
        EditorSettingsStore? settingsStore = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.settingsStore = settingsStore ?? new EditorSettingsStore();
        var userSettings = this.settingsStore.Load();
        keyboardLayout = userSettings.KeyboardLayout;
        instructionDefinitionsPath = !string.IsNullOrWhiteSpace(userSettings.InstructionDefinitionsPath)
            && File.Exists(userSettings.InstructionDefinitionsPath)
                ? Path.GetFullPath(userSettings.InstructionDefinitionsPath)
                : null;
        this.projectLoader = projectLoader ?? new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        document = new EditorSceneDocument(session);
        document.Changed += (_, _) => RefreshSceneFromDocument();
        document.PreviewChanged += (_, _) => RefreshScenePreviewFromDocument();
        this.smokeTest = smokeTest;
        baseTitle = $"ED8Editor — {session.Script.Header.Identifier} — 1: move, 2: rotate, 3: scale, Ctrl+click: select through, Ctrl+Z/Y: undo/redo";
        Text = baseTitle;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(640, 360);
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        assetControlsPanel.Controls.Add(assetList);
        assetControlsPanel.Controls.Add(snapCheckBox);
        assetControlsPanel.Controls.Add(showIndicatorsCheckBox);
        assetControlsPanel.Controls.Add(effectMetadataStatus);
        assetControlsPanel.Controls.Add(environmentVariantList);
        assetControlsPanel.Controls.Add(keyboardLayoutList);
        assetControlsPanel.Controls.Add(assetSearch);
        propertyGroup.Controls.Add(propertyGrid);
        propertyGroup.Controls.Add(applyPropertiesButton);
        sceneOutlinerGroup.Controls.Add(sceneOutliner);
        scenePanel.Controls.Add(sceneOutlinerGroup);
        scenePanel.Controls.Add(propertyGroup);
        opsCreationGroup.Controls.Add(opsInputPanel);
        opsCreationGroup.Controls.Add(opsProfileEvidence);
        opsCreationGroup.Controls.Add(opsProfileList);
        opsCreationGroup.Controls.Add(addOpsElementButton);
        assetControlsPanel.Controls.Add(deleteButton);
        assetControlsPanel.Controls.Add(duplicateButton);
        assetControlsPanel.Controls.Add(addAssetButton);
        assetControlsPanel.Controls.Add(opsCreationGroup);
        assetsTab.Controls.Add(assetControlsPanel);
        rightPanelTabs.TabPages.Add(assetsTab);
        rightPanelTabs.TabPages.Add(scriptsTab);
        rightPanelTabs.TabPages.Add(tblTab);
        assetPanel.Controls.Add(rightPanelTabs);
        BuildMainMenu();
        Controls.Add(viewportHost);
        Controls.Add(assetPanelSplitter);
        Controls.Add(assetPanel);
        Controls.Add(scenePanel);
        gizmoToolStrip.Items.AddRange(new ToolStripItem[]
        {
            translateToolButton,
            rotateToolButton,
            scaleToolButton,
            new ToolStripSeparator(),
            cameraFovLabel,
            new ToolStripControlHost(cameraFovSlider) { AutoSize = false, Width = 150 },
            new ToolStripSeparator(),
            cameraPositionLabel,
            new ToolStripControlHost(cameraPosX),
            new ToolStripControlHost(cameraPosY),
            new ToolStripControlHost(cameraPosZ),
            new ToolStripSeparator(),
            cameraAngleLabel,
            new ToolStripControlHost(cameraYaw),
            new ToolStripControlHost(cameraPitch),
            new ToolStripControlHost(cameraRoll),
        });
        Controls.Add(gizmoToolStrip);
        Controls.Add(mainMenu);
        MainMenuStrip = mainMenu;
        mainMenu.BringToFront();
        gizmoToolStrip.BringToFront();
        translateToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Translate);
        rotateToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Rotate);
        scaleToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Scale);
        cameraFovSlider.ValueChanged += (_, _) => cameraFovLabel.Text = $"FOV: {cameraFovSlider.Value}°";

        // Camera position edit handlers (angles in radians)
        EventHandler onCameraEdit = (_, _) =>
        {
            if (suppressCameraTextUpdate) return;
            if (float.TryParse(cameraPosX.Text, out var px) &&
                float.TryParse(cameraPosY.Text, out var py) &&
                float.TryParse(cameraPosZ.Text, out var pz) &&
                float.TryParse(cameraYaw.Text, out var yaw) &&
                float.TryParse(cameraPitch.Text, out var pitch) &&
                float.TryParse(cameraRoll.Text, out var roll))
            {
                var pos = new Vector3(px, py, pz);
                cameraNavigation.SetRoll(roll);
                cameraNavigation.SetView(pos,
                    Vector3.Normalize(new Vector3(
                        MathF.Sin(yaw) * MathF.Cos(pitch),
                        MathF.Sin(pitch),
                        MathF.Cos(yaw) * MathF.Cos(pitch))),
                    cameraNavigation.Distance);
                cameraDollySmoother.Reset();
            }
        };
        cameraPosX.TextChanged += onCameraEdit;
        cameraPosY.TextChanged += onCameraEdit;
        cameraPosZ.TextChanged += onCameraEdit;
        cameraYaw.TextChanged += onCameraEdit;
        cameraPitch.TextChanged += onCameraEdit;
        rightPanelTabs.Selected += (_, eventArgs) =>
        {
            if (eventArgs.TabPage == scriptsTab) OpenScriptEditor();
            else if (eventArgs.TabPage == tblTab) OpenTblEditor();
        };
        viewportHost.Resize += (_, _) => ResizeViewport();
        SetGizmoMode(GizmoMode.Translate);
        assetSearch.TextChanged += (_, _) => FilterAssetCatalog();
        showIndicatorsCheckBox.CheckedChanged += (_, _) =>
        {
            RefreshRenderInstances(uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
            RefreshOverlay();
        };
        addAssetButton.Click += async (_, _) => await AddSelectedAssetAsync();
        duplicateButton.Click += (_, _) => DuplicateSelectedElement();
        deleteButton.Click += (_, _) => DeleteSelectedElement();
        propertyGrid.Columns.Add("Attribute", "Attribute");
        propertyGrid.Columns.Add("Value", "Value");
        applyPropertiesButton.Click += (_, _) => ApplyElementProperties();
        sceneOutliner.AfterSelect += (_, _) => SelectFromOutliner();
        sceneOutliner.NodeMouseDoubleClick += (_, eventArgs) => FocusOutlinerNode(eventArgs.Node);
        opsProfileList.DisplayMember = nameof(OpsSpatialCreationProfile.DisplayName);
        opsProfileList.DataSource = OpsSpatialCreationCatalog.Profiles.ToArray();
        opsProfileList.SelectedIndexChanged += (_, _) => RefreshOpsCreationInputs();
        addOpsElementButton.Click += (_, _) => BeginOpsPlacement();
        keyboardLayoutList.Items.AddRange(new object[]
        {
            "Navigation: AZERTY (ZQSD)",
            "Navigation: QWERTY (WASD)",
        });
        keyboardLayoutList.SelectedIndex = keyboardLayout == EditorKeyboardLayout.Azerty ? 0 : 1;
        keyboardLayoutList.SelectedIndexChanged += (_, _) => ChangeKeyboardLayout();
        environmentVariantList.Items.AddRange(new object[]
        {
            "Environment: Daylight",
            "Environment: Evening",
            "Environment: Night",
            "Environment: Morning",
            "Environment: Rain",
        });
        environmentVariantList.SelectedIndex = 0;
        environmentVariantList.SelectedIndexChanged += (_, _) => ChangeEnvironmentVariant();
        RefreshOpsCreationInputs();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        renderTimer.Tick += (_, _) => RenderFrame();
        KeyDown += (_, eventArgs) =>
        {
            if (assetPanel.Visible && rightPanelTabs.SelectedTab == tblTab
                && eventArgs.Control && eventArgs.KeyCode == Keys.S)
            {
                tblEditor?.SaveCurrent(eventArgs.Shift);
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (assetPanel.Visible && rightPanelTabs.SelectedTab == scriptsTab
                && scriptEditor is { ContainsFocus: true })
            {
                if (eventArgs.Control && eventArgs.KeyCode == Keys.S)
                {
                    scriptEditor.SaveCurrent(eventArgs.Shift);
                    eventArgs.SuppressKeyPress = true;
                    return;
                }
                return;
            }
            if (eventArgs.KeyCode is Keys.D1 or Keys.D2 or Keys.D3)
            {
                SetGizmoMode(eventArgs.KeyCode switch
                {
                    Keys.D1 => GizmoMode.Translate,
                    Keys.D2 => GizmoMode.Rotate,
                    _ => GizmoMode.Scale,
                });
                return;
            }
            if (eventArgs.KeyCode == Keys.Escape && placement is not null)
            {
                CancelPlacement();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.Control && eventArgs.KeyCode == Keys.Z)
            {
                document.Undo();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.Control && eventArgs.KeyCode == Keys.S)
            {
                SaveOps(eventArgs.Shift || savedOpsPath is null);
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.Control && eventArgs.KeyCode == Keys.D)
            {
                DuplicateSelectedElement();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.KeyCode == Keys.Delete)
            {
                DeleteSelectedElement();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.Control && eventArgs.KeyCode == Keys.Y)
            {
                document.Redo();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            pressedKeys.Add(eventArgs.KeyCode);
            if (eventArgs.KeyCode == Keys.F) FocusSelection();
        };
        KeyUp += (_, eventArgs) => pressedKeys.Remove(eventArgs.KeyCode);
        viewportHost.MouseDown += (_, eventArgs) =>
        {
            viewportHost.Focus();
            if (eventArgs.Button == MouseButtons.Left)
            {
                if (placement is not null)
                {
                    ConfirmPlacement();
                    return;
                }
                if (SelectCameraHandleAt(eventArgs.Location)) return;
                if (BeginGizmoDrag(eventArgs.Location))
                {
                    viewportHost.Capture = true;
                    return;
                }
                pendingLeftClick = true;
                leftMouseDown = eventArgs.Location;
                previousMouse = eventArgs.Location;
                viewportHost.Capture = true;
                return;
            }
            if (eventArgs.Button is not (MouseButtons.Right or MouseButtons.Middle)) return;
            if (eventArgs.Button == MouseButtons.Right)
            {
                rightLookMoved = false;
                BeginLookDrag(eventArgs.Location);
            }
            else
            {
                cameraDrag = CameraDragMode.Pan;
                previousMouse = eventArgs.Location;
            }
            viewportHost.Capture = true;
        };
        viewportHost.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left && gizmoDrag is not null)
            {
                if (gizmoDrag.CameraHandle == SceneCameraHandle.LookAt)
                {
                    document.CommitCameraLookAtPreview(gizmoDrag.Selection, gizmoDrag.OriginalTransform.Position);
                }
                else
                {
                    document.CommitPreview(gizmoDrag.Selection, gizmoDrag.OriginalTransform);
                }
                gizmoDrag = null;
                viewportHost.Capture = false;
                RefreshOverlay();
                return;
            }
            if (eventArgs.Button == MouseButtons.Left)
            {
                if (pendingLeftClick)
                {
                    pendingLeftClick = false;
                    viewportHost.Capture = false;
                    SelectAt(eventArgs.Location);
                }
                else if (cameraDrag == CameraDragMode.Look)
                {
                    EndCameraDrag();
                }
                return;
            }
            if (eventArgs.Button is not (MouseButtons.Right or MouseButtons.Middle)) return;
            if ((eventArgs.Button == MouseButtons.Right && cameraDrag != CameraDragMode.Look)
                || (eventArgs.Button == MouseButtons.Middle && cameraDrag != CameraDragMode.Pan)) return;
            var deselect = eventArgs.Button == MouseButtons.Right
                && cameraDrag == CameraDragMode.Look
                && !rightLookMoved;
            EndCameraDrag();
            if (deselect) ClearSelection();
        };
        viewportHost.MouseMove += (_, eventArgs) =>
        {
            if (UpdateLeftMouseGesture(eventArgs.Location)) return;
            if (placement is not null) UpdatePlacement(eventArgs.Location);
            else if (gizmoDrag is not null) UpdateGizmoDrag(eventArgs.Location);
            else MoveCamera(eventArgs.Location);
        };
        viewportHost.MouseCaptureChanged += (_, _) =>
        {
            if (!viewportHost.Capture)
            {
                pendingLeftClick = false;
                cameraDrag = CameraDragMode.None;
                rightLookMoved = false;
                ReleaseLookCursor();
            }
        };
        viewportHost.MouseWheel += (_, eventArgs) => ZoomCamera(eventArgs.Delta);
        Deactivate += (_, _) =>
        {
            pressedKeys.Clear();
            EndCameraDrag();
        };
    }

    protected override async void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        try
        {
            InitializeRenderer();
            InitializeAssetCatalog();
            effectMetadataStatus.Text = "Phyre effects: loading...";
            var effectCount = await LoadEffectMetadataAsync();
            if (IsDisposed) return;
            var monsterCount = await LoadScriptMonstersAsync();
            if (IsDisposed) return;
            var modelCount = session.AssetModels.Values.Count(value => value.Model is not null);
            if (effectCount >= 0)
            {
                effectMetadataStatus.Text = $"Phyre effects: {effectCount}/{modelCount}; monsters: {monsterCount}";
                effectMetadataStatus.ForeColor = effectCount == modelCount ? Color.DarkGreen : Color.DarkOrange;
            }
            if (smokeTest)
            {
                OpenScriptEditor();
                PerformLayout();
                scriptEditor?.VerifyEmbeddedInteractionSmoke();
                RenderFrame();
                Close();
            }
            else
            {
                Text = baseTitle;
                renderTimer.Start();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Renderer initialization failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        ResizeViewport();
    }

    private void ResizeViewport()
    {
        if (WindowState != FormWindowState.Minimized
            && viewportHost.ClientSize.Width > 0
            && viewportHost.ClientSize.Height > 0)
            viewport?.Resize(viewportHost.ClientSize.Width, viewportHost.ClientSize.Height);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (scriptEditor is not null && !scriptEditor.ConfirmClose())
        {
            eventArgs.Cancel = true;
            return;
        }
        if (!smokeTest && document.IsDirty)
        {
            var result = MessageBox.Show(
                "Save the OPS changes before closing?",
                "Unsaved OPS changes",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Cancel || result == DialogResult.Yes && !SaveOps(saveAs: false))
            {
                eventArgs.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            renderTimer.Stop();
            renderTimer.Dispose();
            viewport?.Dispose();
            effectMetadataCancellation.Cancel();
            effectMetadataCancellation.Dispose();
            foreach (var model in uploadedModels) model.Dispose();
            graphics?.Dispose();
            viewportHost.Capture = false;
            ReleaseLookCursor();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        // Direct3D owns the complete client area.
    }

    private void InitializeRenderer()
    {
        graphics = D3D11GraphicsDevice.Create();
        var uploader = new D3D11ModelUploader(graphics.Device);
        var resourcesByAsset = new Dictionary<string, D3D11ModelResources>(StringComparer.OrdinalIgnoreCase);
        foreach (var load in session.AssetModels.Values.Where(value => value.Model is not null))
        {
            loadedModelsByAsset[load.AssetId] = load.Model!;
            var uploaded = uploader.Upload(load.Model!);
            uploadedModels.Add(uploaded);
            resourcesByAsset.Add(load.AssetId, uploaded);
        }

        sceneInstances = document.CreateModelInstances();
        currentMap = document.CreateMapSnapshot();
        SetEnvironmentVariant(SceneEnvironmentVariantSelector.FromProfileName(
            currentMap?.DefaultEnvironment?.ProfileName));
        RefreshRenderInstances(resourcesByAsset);

        var bounds = new SceneBoundsCalculator().Calculate(VisibleSceneInstances());
        var center = bounds.HasGeometry ? bounds.Center : Vector3.Zero;
        sceneRadius = Math.Max(bounds.Radius, 1f);
        var initialPosition = center + new Vector3(0, sceneRadius * 0.35f, -sceneRadius * 1.6f);
        cameraNavigation.Initialize(center, initialPosition);
        viewport = new D3D11Viewport(graphics, viewportHost.Handle, viewportHost.ClientSize.Width, viewportHost.ClientSize.Height);
        viewport.SetEnvironmentVariant(ActiveEnvironmentVariant);
        if (currentMap?.DefaultEnvironment is { } environment)
        {
            viewport.SetClearColor(new Vector4(environment.FogColor, 1f));
        }
        overlayMarkerSize = Math.Clamp(sceneRadius * 0.008f, 0.08f, 1.5f);
        RefreshOverlay();
        RefreshOutliner();
        previousFrameTicks = frameClock.ElapsedTicks;
    }

    private async Task<int> LoadEffectMetadataAsync()
    {
        try
        {
            var models = await projectLoader.LoadEffectMetadataAsync(
                session,
                effectMetadataCancellation.Token);
            if (IsDisposed) return -1;
            foreach (var uploaded in uploadedModels)
            {
                if (models.TryGetValue(uploaded.AssetId, out var model))
                {
                    uploaded.UpdateMaterialSources(model);
                }
            }
            return models.Count;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            Debug.WriteLine($"Could not load Phyre effect metadata: {exception}");
            effectMetadataStatus.Text = $"Phyre effects: failed — {exception.Message}";
            effectMetadataStatus.ForeColor = Color.DarkRed;
            return -1;
        }
    }

    private async Task<int> LoadScriptMonstersAsync()
    {
        if (session.Script.GameDataPath is null || graphics is null) return 0;
        try
        {
            var script = await Task.Run(
                () => ScriptDecompiler.Decompile(
                    session.Script.Header.SourcePath,
                    instructionDefinitionsPath),
                effectMetadataCancellation.Token);
            var spawns = ScriptMonsterSpawnReader.Read(script);
            foreach (var assetId in spawns.Select(value => value.AssetId)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                effectMetadataCancellation.Token.ThrowIfCancellationRequested();
                if (!loadedModelsByAsset.TryGetValue(assetId, out var model))
                {
                    var load = await Task.Run(
                        () => projectLoader.LoadAsset(assetId, session.Script.GameDataPath),
                        effectMetadataCancellation.Token);
                    if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null)
                    {
                        Debug.WriteLine($"Could not load script monster asset '{assetId}': {load.Error}");
                        continue;
                    }
                    model = load.Model;
                    loadedModelsByAsset[assetId] = model;
                }
                if (uploadedModels.All(value => !value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
                {
                    uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
                }
                if (model.Skeleton is not null && !loadedCharacterAnimations.ContainsKey(assetId))
                {
                    var animation = await Task.Run(
                        () => projectLoader.LoadAnimationAsset(assetId, "BTL_WAIT", session.Script.GameDataPath),
                        effectMetadataCancellation.Token);
                    if (animation.Status == AssetAnimationLoadStatus.Loaded && animation.Clip is not null)
                        loadedCharacterAnimations[assetId] = animation.Clip;
                    else
                        Debug.WriteLine($"Could not load current field-monster animation for '{assetId}': {animation.Error}");
                }
            }

            var loaded = new List<SceneModelInstance>();
            for (var index = 0; index < spawns.Count; index++)
            {
                var spawn = spawns[index];
                if (!loadedModelsByAsset.TryGetValue(spawn.AssetId, out var model)) continue;
                var transform = Matrix4x4.CreateRotationY(spawn.HeadingDegrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(spawn.Position);
                loaded.Add(new SceneModelInstance(
                    int.MinValue + index,
                    spawn.AssetId,
                    $"Monster {spawn.EntityId}",
                    model,
                    transform,
                    Vector4.One,
                    Vector3.Zero,
                    SceneElementKind.ScriptCharacter));
            }
            scriptMonsterInstances = loaded;
            RefreshRenderInstances(uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
            return loaded.Count;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not load script monsters: {exception}");
            return 0;
        }
    }

    private void RenderFrame()
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ticks = frameClock.ElapsedTicks;
        var elapsed = Math.Clamp((float)(ticks - previousFrameTicks) / Stopwatch.Frequency, 0f, 0.1f);
        previousFrameTicks = ticks;
        UpdateCamera(elapsed);
        UpdateCameraTextFields();
        RefreshAnimationPoses();
        var camera = CreateCamera();
        viewport.Render(instances, camera);
    }

    private void RefreshAnimationPoses()
    {
        var characters = scriptMonsterInstances.ToDictionary(value => value.Id);
        var elapsed = (float)frameClock.Elapsed.TotalSeconds;
        var sceneryPoses = new Dictionary<string, IReadOnlyList<Matrix4x4>>(StringComparer.OrdinalIgnoreCase);
        instances = instances.Select(instance =>
        {
            if (!loadedModelsByAsset.TryGetValue(instance.Model.AssetId, out var model)) return instance;
            var animated = instance;
            if (model is { Skeleton: null, SceneNodes: { Count: > 0 } nodes, EmbeddedAnimation: { } sceneryClip })
            {
                if (!sceneryPoses.TryGetValue(model.AssetId, out var nodeTransforms))
                {
                    var sceneryTime = sceneryClip.Duration > 0f
                        ? sceneryClip.StartTime + elapsed % sceneryClip.Duration
                        : sceneryClip.StartTime;
                    nodeTransforms = sceneAnimationEvaluator.Evaluate(nodes, sceneryClip, sceneryTime).WorldTransforms;
                    sceneryPoses.Add(model.AssetId, nodeTransforms);
                }
                animated = animated with { SceneNodeTransforms = nodeTransforms };
            }
            if (!characters.TryGetValue(instance.SceneInstanceId, out var character)
                || model.Skeleton is null
                || !loadedCharacterAnimations.TryGetValue(character.AssetId, out var clip))
                return animated;
            var selectedCharacter = selection is { Kind: SceneElementKind.ScriptCharacter }
                && selection.SourceIndex == character.Id;
            var duration = clip.Duration;
            var time = selectedCharacter && duration > 0f
                ? clip.StartTime + elapsed % duration
                : clip.StartTime;
            var pose = poseEvaluator.Evaluate(model.Skeleton, clip, time);
            return animated with { SkinMatrices = pose.SkinMatrices };
        }).ToArray();
    }

    private ViewportCamera CreateCamera()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            CameraVerticalFieldOfView,
            viewportHost.ClientSize.Width / (float)viewportHost.ClientSize.Height,
            Math.Max(0.01f, sceneRadius / 10000f),
            Math.Max(1000f, sceneRadius * 20f));
        var forward = cameraNavigation.Forward;
        var view = Matrix4x4.CreateLookAt(
            cameraNavigation.Position,
            cameraNavigation.Position + forward,
            cameraNavigation.WorldUp);
        return new ViewportCamera(view, projection);
    }

    private void UpdateCamera(float elapsed)
    {
        var forward = cameraNavigation.Forward;
        var right = cameraNavigation.ScreenRight;
        var movement = Vector3.Zero;
        if (pressedKeys.Contains(keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Z : Keys.W)) movement += forward;
        if (pressedKeys.Contains(Keys.S)) movement -= forward;
        if (pressedKeys.Contains(Keys.D)) movement += right;
        if (pressedKeys.Contains(keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Q : Keys.A)) movement -= right;
        if (pressedKeys.Contains(Keys.E) || pressedKeys.Contains(Keys.Space)) movement += Vector3.UnitY;
        if (pressedKeys.Contains(Keys.C)) movement -= Vector3.UnitY;
        if (movement != Vector3.Zero)
        {
            var fast = pressedKeys.Contains(Keys.ShiftKey) ? 4f : 1f;
            var speed = Math.Max(sceneRadius * 0.12f, 2f);
            var translation = Vector3.Normalize(movement) * speed * fast * elapsed;
            cameraNavigation.Translate(translation);
        }
        var dollyDistance = cameraDollySmoother.Advance(elapsed);
        if (dollyDistance != 0f) cameraNavigation.Dolly(dollyDistance);
    }

    private void ChangeKeyboardLayout()
    {
        keyboardLayout = keyboardLayoutList.SelectedIndex == 0
            ? EditorKeyboardLayout.Azerty
            : EditorKeyboardLayout.Qwerty;
        settingsStore.Save(settingsStore.Load() with { KeyboardLayout = keyboardLayout });
        pressedKeys.Clear();
    }

    private void ChangeEnvironmentVariant()
    {
        if (refreshingEnvironmentVariant || environmentVariantList.SelectedIndex < 0) return;
        SetEnvironmentVariant((SceneEnvironmentVariant)environmentVariantList.SelectedIndex);
        if (uploadedModels.Count != 0)
        {
            var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
            RefreshRenderInstances(resourcesByAsset);
        }
        Text = $"{baseTitle} — environment: {environmentVariant}";
    }

    private void SetEnvironmentVariant(SceneEnvironmentVariant value)
    {
        environmentVariant = value;
        viewport?.SetEnvironmentVariant(value);
        refreshingEnvironmentVariant = true;
        try
        {
            environmentVariantList.SelectedIndex = (int)value;
        }
        finally
        {
            refreshingEnvironmentVariant = false;
        }
    }

    private bool UpdateLeftMouseGesture(Point current)
    {
        if (!pendingLeftClick) return false;
        var dragSize = SystemInformation.DragSize;
        if (Math.Abs(current.X - leftMouseDown.X) < Math.Max(2, dragSize.Width / 2)
            && Math.Abs(current.Y - leftMouseDown.Y) < Math.Max(2, dragSize.Height / 2)) return false;
        pendingLeftClick = false;
        BeginLookDrag(leftMouseDown);
        return true;
    }

    private void MoveCamera(Point current)
    {
        if (cameraDrag == CameraDragMode.None) return;
        if (cameraDrag == CameraDragMode.Look)
        {
            var center = new Point(viewportHost.ClientSize.Width / 2, viewportHost.ClientSize.Height / 2);
            var lookDeltaX = current.X - center.X;
            var lookDeltaY = current.Y - center.Y;
            if (lookDeltaX == 0 && lookDeltaY == 0) return;
            rightLookMoved = true;
            cameraNavigation.Look(lookDeltaX, lookDeltaY);
            CenterLookCursor();
            return;
        }
        var deltaX = current.X - previousMouse.X;
        var deltaY = current.Y - previousMouse.Y;
        previousMouse = current;
        cameraNavigation.Pan(deltaX, deltaY, viewportHost.ClientSize.Height, CameraVerticalFieldOfView);
    }

    private void BeginLookDrag(Point restoreLocation)
    {
        cameraDrag = CameraDragMode.Look;
        if (lookCursorLocked) return;
        lookCursorRestoreScreen = viewportHost.PointToScreen(restoreLocation);
        lookCursorLocked = true;
        Cursor.Hide();
        CenterLookCursor();
    }

    private void CenterLookCursor()
    {
        if (!lookCursorLocked || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var center = new Point(viewportHost.ClientSize.Width / 2, viewportHost.ClientSize.Height / 2);
        Cursor.Position = viewportHost.PointToScreen(center);
    }

    private void ReleaseLookCursor()
    {
        if (!lookCursorLocked) return;
        lookCursorLocked = false;
        Cursor.Position = lookCursorRestoreScreen;
        Cursor.Show();
    }

    private void EndCameraDrag()
    {
        pendingLeftClick = false;
        cameraDrag = CameraDragMode.None;
        if (viewportHost.Capture) viewportHost.Capture = false;
        ReleaseLookCursor();
    }

    private void ZoomCamera(int wheelDelta)
    {
        if (wheelDelta == 0) return;
        var hit = surfaceRaycaster.Cast(
            new SceneRay(cameraNavigation.Position, cameraNavigation.Forward),
            VisibleSceneInstances()).Hit;
        var referenceDistance = hit?.Distance ?? sceneRadius * 0.25f;
        var minimumStep = Math.Max(sceneRadius * 0.002f, 0.05f);
        var maximumStep = Math.Max(sceneRadius * 0.25f, 1f);
        var step = Math.Clamp(referenceDistance * 0.2f, minimumStep, maximumStep);
        cameraDollySmoother.Add(wheelDelta / 120f * step);
    }

    private void SetGizmoMode(GizmoMode mode)
    {
        gizmoMode = mode;
        translateToolButton.Checked = mode == GizmoMode.Translate;
        rotateToolButton.Checked = mode == GizmoMode.Rotate;
        scaleToolButton.Checked = mode == GizmoMode.Scale;
        RefreshOverlay();
    }

    private void BuildMainMenu()
    {
        var options = new ToolStripMenuItem("Options");
        options.DropDownItems.Add(new ToolStripMenuItem(
            "Instruction definitions...", null, (_, _) => ConfigureInstructionDefinitions()));
        mainMenu.Items.Add(options);
    }

    private void ConfigureInstructionDefinitions()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the CS1 instruction definitions",
            Filter = "Instruction definitions (cs1_instructions.json)|cs1_instructions.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = instructionDefinitionsPath is null
                ? Path.GetDirectoryName(ScriptDecompiler.DefaultInstructionsPath)
                : Path.GetDirectoryName(instructionDefinitionsPath),
            FileName = instructionDefinitionsPath is null
                ? Path.GetFileName(ScriptDecompiler.DefaultInstructionsPath)
                : Path.GetFileName(instructionDefinitionsPath),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selectedPath = Path.GetFullPath(dialog.FileName);
        try
        {
            using var stream = File.OpenRead(selectedPath);
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !json.RootElement.TryGetProperty("instructions", out var instructions)
                || instructions.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException("The JSON document has no 'instructions' array.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, exception.Message, "Invalid instruction definitions",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        instructionDefinitionsPath = selectedPath;
        settingsStore.Save(settingsStore.Load() with { InstructionDefinitionsPath = selectedPath });
        var message = scriptEditor is null
            ? "The instruction definitions will be used when the Scripts tab is opened."
            : "The instruction definitions were saved. Restart ED8Editor to reload the native instruction registry safely.";
        MessageBox.Show(this, message, "Instruction definitions", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private ScriptEditorForm? scriptEditor;
    private TblEditorControl? tblEditor;
    private Cs1TableCatalog? tableCatalog;

    private bool IsCameraMovementKey(Keys key) => key == Keys.ShiftKey
        || key == Keys.S
        || key == Keys.D
        || key == Keys.E
        || key == Keys.C
        || key == Keys.Space
        || key == (keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Z : Keys.W)
        || key == (keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Q : Keys.A);

    private void OpenScriptEditor()
    {
        var editor = scriptEditor;
        if (editor is null || editor.IsDisposed)
        {
            editor = new ScriptEditorForm(
                GetTableChoices,
                new ScriptEditorSemanticContext(() => new ScriptCameraSnapshot(
                    cameraNavigation.Position,
                    cameraNavigation.Target,
                    cameraNavigation.Forward,
                    cameraNavigation.Distance,
                    cameraNavigation.Yaw * 180f / MathF.PI,
                    cameraNavigation.Pitch * 180f / MathF.PI,
                    cameraFovSlider.Value)),
                instructionDefinitionsPath);
            scriptEditor = editor;
            editor.TopLevel = false;
            editor.FormBorderStyle = FormBorderStyle.None;
            editor.Dock = DockStyle.Fill;
            editor.ViewportKeyDown += key =>
            {
                if (IsCameraMovementKey(key)) pressedKeys.Add(key);
            };
            editor.ViewportKeyUp += key => pressedKeys.Remove(key);
            editor.InstructionSelected += ApplySelectedScriptCamera;
            scriptsTab.Controls.Add(editor);
            editor.Show();
        }

        rightPanelTabs.SelectedTab = scriptsTab;
        var path = session.Script.Header.SourcePath;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            editor.LoadDat(path);
        }
    }

    private void ApplySelectedScriptCamera(DecompiledFunction function, DecompiledInstruction instruction)
    {
        var state = ScriptCameraStateResolver.Resolve(function, instruction.Index);
        if (!state.HasViewValue) return;

        var distance = state.Distance is > 0f ? state.Distance.Value : cameraNavigation.Distance;
        var forward = state.Forward ?? cameraNavigation.Forward;
        var roll = 0f;

        // OP45_4 : angles en degrés → conversion en radians, avec shortest-path
        if (state.YawDegrees is not null || state.PitchDegrees is not null)
        {
            var targetYaw = (state.YawDegrees ?? cameraNavigation.Yaw * 180f / MathF.PI) * MathF.PI / 180f;
            var targetPitch = (state.PitchDegrees ?? cameraNavigation.Pitch * 180f / MathF.PI) * MathF.PI / 180f;

            // Shortest path: normaliser la différence entre -PI et PI
            if (state.UseShortestPath)
            {
                var deltaYaw = targetYaw - cameraNavigation.Yaw;
                var deltaPitch = targetPitch - cameraNavigation.Pitch;
                deltaYaw = (deltaYaw + MathF.PI) % (2f * MathF.PI) - MathF.PI;
                deltaPitch = (deltaPitch + MathF.PI) % (2f * MathF.PI) - MathF.PI;
                targetYaw = cameraNavigation.Yaw + deltaYaw;
                targetPitch = cameraNavigation.Pitch + deltaPitch;
            }

            var cosPitch = MathF.Cos(targetPitch);
            forward = Vector3.Normalize(new Vector3(
                MathF.Sin(targetYaw) * cosPitch,
                MathF.Sin(targetPitch),
                MathF.Cos(targetYaw) * cosPitch));

            if (state.RollDegrees is { } rollDeg)
                roll = rollDeg * MathF.PI / 180f;
        }

        // La caméra orbite autour de Target (si défini) ou conserve sa position
        var position = state.Position ?? cameraNavigation.Position;
        if (state.Target is { } target)
        {
            // Eye = Target - Forward * Distance
            position = target - forward * distance;
        }
        else if (state.Position is null && (state.YawDegrees is not null || state.PitchDegrees is not null))
        {
            // Garder le target actuel, recalculer Eye depuis les nouveaux angles
            position = cameraNavigation.Target - forward * distance;
        }
        // CameraSetTarget_Relative : decaler le target actuel
        if (state.TargetOffset is { } offset)
            position = (cameraNavigation.Target + offset) - forward * distance;
        // CameraSetEye_Relative : decaler l'oeil (target fixe -> angles/distance changent)
        if (state.PositionOffset is { } eyeOffset)
            position = cameraNavigation.Position + eyeOffset;
        cameraDollySmoother.Reset();
        cameraNavigation.SetRoll(roll);
        cameraNavigation.SetView(position, forward, distance);
        if (state.VerticalFieldOfViewDegrees is { } fov && float.IsFinite(fov))
            cameraFovSlider.Value = Math.Clamp((int)MathF.Round(fov), cameraFovSlider.Minimum, cameraFovSlider.Maximum);
        UpdateCameraTextFields();
        Text = $"{baseTitle} — camera state at {function.Name} #{instruction.Index}";
    }

    private void UpdateCameraTextFields()
    {
        suppressCameraTextUpdate = true;
        if (!cameraPosX.Focused) cameraPosX.Text = cameraNavigation.Position.X.ToString("0.00");
        if (!cameraPosY.Focused) cameraPosY.Text = cameraNavigation.Position.Y.ToString("0.00");
        if (!cameraPosZ.Focused) cameraPosZ.Text = cameraNavigation.Position.Z.ToString("0.00");
        if (!cameraYaw.Focused) cameraYaw.Text = cameraNavigation.Yaw.ToString("0.0000");
        if (!cameraPitch.Focused) cameraPitch.Text = cameraNavigation.Pitch.ToString("0.0000");
        if (!cameraRoll.Focused) cameraRoll.Text = cameraNavigation.Roll.ToString("0.0000");
        suppressCameraTextUpdate = false;
    }

    private void OpenTblEditor()
    {
        if (session.Script.GameDataPath is null)
        {
            tblTab.Controls.Clear();
            tblTab.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Game data directory not found.",
            });
            return;
        }
        if (tblEditor is null || tblEditor.IsDisposed)
        {
            tblEditor = new TblEditorControl(session.Script.GameDataPath, session.Script.Header.SourcePath);
            tblEditor.CatalogChanged += (_, _) => tableCatalog = null;
            tblTab.Controls.Add(tblEditor);
        }
    }

    private IReadOnlyList<Cs1TableChoice> GetTableChoices(Cs1TableReference reference)
    {
        OpenTblEditor();
        if (tblEditor?.CurrentDirectory is not { } directory) return Array.Empty<Cs1TableChoice>();
        if (tableCatalog is null || !tableCatalog.DirectoryPath.Equals(directory, StringComparison.OrdinalIgnoreCase))
            tableCatalog = new Cs1TableCatalog(directory);
        return tableCatalog.GetChoices(reference);
    }

    private void ClearSelection()
    {
        if (selection is null) return;
        selection = null;
        cameraHandle = SceneCameraHandle.Eye;
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        SyncOutlinerSelection();
        Text = $"{baseTitle} - no selection";
    }

    private void SelectAt(Point location)
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ray = CreatePointerRay(location);
        var hits = elementPicker.PickAll(ray, VisibleSceneInstances(), currentMap, overlayMarkerSize);
        var hit = SelectPickCandidate(hits, ModifierKeys.HasFlag(Keys.Control));
        selection = hit?.Selection;
        cameraHandle = SceneCameraHandle.Eye;
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        SyncOutlinerSelection();
        Text = hit is null
            ? $"{baseTitle} — no selection"
            : $"{baseTitle} — selected: {DescribeSelection(hit.Selection)}";
    }

    private SceneElementPickHit? SelectPickCandidate(
        IReadOnlyList<SceneElementPickHit> hits,
        bool selectThrough)
    {
        if (hits.Count == 0) return null;
        if (!selectThrough) return hits[0];
        if (selection is null) return hits.Count > 1 ? hits[1] : hits[0];
        var currentIndex = hits
            .Select((value, index) => (value, index))
            .Where(value => value.value.Selection == selection)
            .Select(value => value.index)
            .DefaultIfEmpty(-1)
            .First();
        return currentIndex >= 0 && currentIndex < hits.Count - 1
            ? hits[currentIndex + 1]
            : hits[0];
    }

    private void FocusSelection()
    {
        if (selection is null) return;
        cameraDollySmoother.Reset();
        if (selection.Kind is SceneElementKind.Prop or SceneElementKind.ScriptCharacter)
        {
            var selected = selection.Kind == SceneElementKind.Prop
                ? sceneInstances.FirstOrDefault(value => value.Id == selection.SourceIndex)
                : scriptMonsterInstances.FirstOrDefault(value => value.Id == selection.SourceIndex);
            if (selected is not null)
            {
                var bounds = new SceneBoundsCalculator().Calculate(new[] { selected });
                if (bounds.HasGeometry)
                {
                    cameraNavigation.Focus(bounds.Center, Math.Max(bounds.Radius * 2.5f, sceneRadius * 0.01f));
                    return;
                }
            }
            if (selection.Kind == SceneElementKind.Prop && document.Find(selection) is { } propElement)
            {
                cameraNavigation.Focus(propElement.Transform.Position, Math.Max(sceneRadius * 0.025f, 1f));
            }
            return;
        }
        if (!TryGetOverlayFocus(selection, out var center, out var radius)) return;
        cameraNavigation.Focus(center, Math.Max(radius * 2.5f, sceneRadius * 0.01f));
    }

    private string DescribeSelection(SceneElementSelection selected)
    {
        var assetId = selected.Kind switch
        {
            SceneElementKind.Prop => document.FindProp(selected)?.AssetId,
            SceneElementKind.ScriptCharacter => scriptMonsterInstances
                .FirstOrDefault(value => value.Id == selected.SourceIndex)?.AssetId,
            _ => null,
        };
        return assetId is null
            ? $"{selected.Name} [{selected.Kind}]"
            : $"{selected.Name} [{selected.Kind}] — asset: {assetId}";
    }

    private void RefreshRenderInstances(IReadOnlyDictionary<string, D3D11ModelResources> resourcesByAsset)
    {
        var rendered = sceneInstances
            .Where(value => SceneEnvironmentVariantSelector.IsVisible(value.Name, ActiveEnvironmentVariant))
            .Where(value => resourcesByAsset.ContainsKey(value.AssetId))
            .Select(value => new D3D11SceneInstance(
                value.Id,
                resourcesByAsset[value.AssetId],
                value.Transform,
                selection is { Kind: SceneElementKind.Prop }
                    && value.Id == selection.SourceIndex,
                MaterialDiffuse: value.MaterialDiffuse,
                MaterialEmission: value.MaterialEmission))
            .ToList();
        if (showIndicatorsCheckBox.Checked)
        {
            rendered.AddRange(scriptMonsterInstances
                .Where(value => resourcesByAsset.ContainsKey(value.AssetId))
                .Select(value => new D3D11SceneInstance(
                    value.Id,
                    resourcesByAsset[value.AssetId],
                    value.Transform,
                    selection is { Kind: SceneElementKind.ScriptCharacter }
                        && value.Id == selection.SourceIndex,
                    MaterialDiffuse: value.MaterialDiffuse,
                    MaterialEmission: value.MaterialEmission)));
        }
        if (placement is { Position: { } position, Model: not null, AssetId: not null } preview
            && resourcesByAsset.TryGetValue(preview.AssetId, out var previewResources))
        {
            var transform = Matrix4x4.CreateTranslation(position);
            rendered.Add(new D3D11SceneInstance(-1, previewResources, transform, false, true, Vector4.One, Vector3.Zero));
        }
        instances = rendered;
    }

    private void RefreshOverlay()
    {
        if (viewport is null) return;
        var overlay = showIndicatorsCheckBox.Checked
            ? new SceneOverlayBuilder().BuildGeometry(
                currentMap,
                new SceneOverlayOptions(PointMarkerSize: overlayMarkerSize, Selection: selection))
            : new SceneOverlayGeometry(Array.Empty<SceneOverlayLine>(), Array.Empty<SceneOverlayTriangle>());
        var overlayLines = overlay.Lines.ToList();
        var selectedElement = selection is null ? null : document.Find(selection);
        var showSelectedGizmo = selection is { Kind: SceneElementKind.Prop }
            || showIndicatorsCheckBox.Checked;
        if (showSelectedGizmo && selectedElement is not null && SupportsMode(selectedElement, gizmoMode))
        {
            var gizmoTransform = selectedElement.Transform;
            if (selection is { Kind: SceneElementKind.Camera }
                && cameraHandle == SceneCameraHandle.LookAt
                && document.FindCamera(selection) is { } selectedCamera)
            {
                gizmoTransform = gizmoTransform with { Position = selectedCamera.LookAt };
            }
            var length = GetGizmoLength(gizmoTransform.Position);
            if (gizmoMode == GizmoMode.Rotate)
            {
                overlayLines.AddRange(rotationGizmo.Build(gizmoTransform.Position, length, gizmoDrag?.Axis));
            }
            else
            {
                overlayLines.AddRange(translationGizmo.Build(gizmoTransform.Position, length, gizmoDrag?.Axis));
            }
        }
        if (showIndicatorsCheckBox.Checked
            && selection is { Kind: SceneElementKind.Camera } cameraSelection
            && document.FindCamera(cameraSelection) is { } camera)
        {
            overlayLines.AddRange(cameraGizmo.Build(camera, overlayMarkerSize * 1.5f, cameraHandle));
        }
        if (placement is { Position: { } previewPosition, OpsProfile: not null })
        {
            var size = overlayMarkerSize * 2f;
            var color = new Vector4(0.25f, 1f, 0.35f, 1f);
            overlayLines.Add(new SceneOverlayLine(previewPosition - Vector3.UnitX * size, previewPosition + Vector3.UnitX * size, color));
            overlayLines.Add(new SceneOverlayLine(previewPosition - Vector3.UnitY * size, previewPosition + Vector3.UnitY * size, color));
            overlayLines.Add(new SceneOverlayLine(previewPosition - Vector3.UnitZ * size, previewPosition + Vector3.UnitZ * size, color));
        }
        viewport.SetDebugLines(overlayLines
            .Select(line => new D3D11DebugLine(line.Start, line.End, line.Color, line.Thickness))
            .ToArray());
        viewport.SetDebugTriangles(overlay.Triangles
            .Select(triangle => new D3D11DebugTriangle(
                triangle.A, triangle.B, triangle.C, triangle.Color))
            .ToArray());
    }

    private bool SelectCameraHandleAt(Point location)
    {
        if (selection is not { Kind: SceneElementKind.Camera } selected
            || document.FindCamera(selected) is not { } camera) return false;
        var ray = CreatePointerRay(location);
        if (!cameraGizmo.TryPickHandle(ray, camera, overlayMarkerSize * 2f, out var handle)) return false;
        cameraHandle = handle;
        SetGizmoMode(GizmoMode.Translate);
        Text = $"{baseTitle} - camera handle: {handle}";
        return true;
    }

    private bool BeginGizmoDrag(Point location)
    {
        if (selection is null) return false;
        var element = document.Find(selection);
        if (element is null || !SupportsMode(element, gizmoMode)) return false;
        var dragTransform = element.Transform;
        SceneCameraHandle? dragCameraHandle = null;
        if (selection.Kind == SceneElementKind.Camera && cameraHandle == SceneCameraHandle.LookAt)
        {
            var camera = document.FindCamera(selection);
            if (camera is null) return false;
            dragTransform = dragTransform with { Position = camera.LookAt };
            dragCameraHandle = SceneCameraHandle.LookAt;
        }
        var ray = CreatePointerRay(location);
        var length = GetGizmoLength(dragTransform.Position);
        if (gizmoMode == GizmoMode.Rotate)
        {
            if (!rotationGizmo.TryPickAxis(
                ray, dragTransform.Position, length, length * 0.08f, out var rotationAxis, out var ringVector))
            {
                return false;
            }
            gizmoDrag = new GizmoDragState(selection, gizmoMode, rotationAxis, dragTransform, 0f, ringVector, length, dragCameraHandle);
        }
        else
        {
            if (!translationGizmo.TryPickAxis(
                ray, dragTransform.Position, length, length * 0.08f, out var linearAxis)
                || !translationGizmo.TryGetAxisParameter(
                    ray, dragTransform.Position, linearAxis, out var parameter))
            {
                return false;
            }
            gizmoDrag = new GizmoDragState(
                selection, gizmoMode, linearAxis, dragTransform, parameter, Vector3.Zero, length, dragCameraHandle);
        }
        RefreshOverlay();
        return true;
    }

    private void UpdateGizmoDrag(Point location)
    {
        if (gizmoDrag is not { } drag) return;
        var ray = CreatePointerRay(location);
        if (drag.Mode == GizmoMode.Rotate)
        {
            if (!rotationGizmo.TryGetRingVector(
                ray, drag.OriginalTransform.Position, drag.Axis, out var currentVector)) return;
            var angle = SceneRotationGizmo.SignedAngle(drag.Axis, drag.StartVector, currentVector);
            if (snapCheckBox.Checked) angle = snapSettings.SnapRotation(angle);
            var delta = Quaternion.CreateFromAxisAngle(SceneTranslationGizmo.AxisDirection(drag.Axis), angle);
            document.PreviewTransform(
                drag.Selection,
                drag.OriginalTransform with
                {
                    Rotation = Quaternion.Normalize(drag.OriginalTransform.Rotation * delta),
                });
            return;
        }
        if (!translationGizmo.TryGetAxisParameter(
            ray,
            drag.OriginalTransform.Position,
            drag.Axis,
            out var parameter)) return;
        var direction = SceneTranslationGizmo.AxisDirection(drag.Axis);
        var deltaParameter = parameter - drag.StartParameter;
        if (drag.Mode == GizmoMode.Translate)
        {
            if (snapCheckBox.Checked) deltaParameter = snapSettings.SnapTranslation(deltaParameter);
            var position = drag.OriginalTransform.Position + direction * deltaParameter;
            if (drag.CameraHandle == SceneCameraHandle.LookAt)
            {
                document.PreviewCameraLookAt(drag.Selection, position);
            }
            else
            {
                document.PreviewTransform(drag.Selection, drag.OriginalTransform with { Position = position });
            }
            return;
        }
        var factor = MathF.Exp(deltaParameter / drag.GizmoLength);
        var scale = drag.OriginalTransform.Scale;
        scale = drag.Axis switch
        {
            SceneGizmoAxis.X => scale with { X = scale.X * factor },
            SceneGizmoAxis.Y => scale with { Y = scale.Y * factor },
            SceneGizmoAxis.Z => scale with { Z = scale.Z * factor },
            _ => scale,
        };
        if (snapCheckBox.Checked)
        {
            scale = drag.Axis switch
            {
                SceneGizmoAxis.X => scale with { X = snapSettings.SnapScale(scale.X) },
                SceneGizmoAxis.Y => scale with { Y = snapSettings.SnapScale(scale.Y) },
                SceneGizmoAxis.Z => scale with { Z = snapSettings.SnapScale(scale.Z) },
                _ => scale,
            };
        }
        document.PreviewTransform(drag.Selection, drag.OriginalTransform with { Scale = scale });
    }

    private SceneRay CreatePointerRay(Point location)
    {
        var camera = CreateCamera();
        return SceneRayFactory.FromViewport(
            location.X,
            location.Y,
            viewportHost.ClientSize.Width,
            viewportHost.ClientSize.Height,
            camera.View,
            camera.Projection);
    }

    private float GetGizmoLength(Vector3 position)
    {
        var distance = Vector3.Distance(cameraNavigation.Position, position);
        var worldUnitsPerPixel = 2f * distance * MathF.Tan(CameraVerticalFieldOfView * 0.5f)
            / Math.Max(1, viewportHost.ClientSize.Height);
        return Math.Max(worldUnitsPerPixel * 115f, overlayMarkerSize * 2.5f);
    }

    private static bool SupportsMode(EditableSceneElement element, GizmoMode mode)
        => mode switch
        {
            GizmoMode.Translate => element.Capabilities.HasFlag(SceneTransformCapabilities.Translate),
            GizmoMode.Rotate => element.Capabilities.HasFlag(SceneTransformCapabilities.Rotate),
            GizmoMode.Scale => element.Capabilities.HasFlag(SceneTransformCapabilities.Scale),
            _ => false,
        };

    private void RefreshSceneFromDocument()
    {
        sceneInstances = document.CreateModelInstances();
        currentMap = document.CreateMapSnapshot();
        viewport?.SetEnvironmentVariant(environmentVariant);
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        RefreshOutliner();
    }

    private void RefreshScenePreviewFromDocument()
    {
        sceneInstances = document.CreateModelInstances();
        currentMap = document.CreateMapSnapshot();
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
    }

    private void RefreshOutliner()
    {
        var groups = outlinerBuilder.Build(document.Elements);
        var selections = groups.SelectMany(group => group.Elements).ToArray();
        if (selections.SequenceEqual(outlinerSelections))
        {
            SyncOutlinerSelection();
            return;
        }

        refreshingOutliner = true;
        try
        {
            sceneOutliner.BeginUpdate();
            sceneOutliner.Nodes.Clear();
            foreach (var group in groups)
            {
                var groupNode = sceneOutliner.Nodes.Add(group.Name);
                foreach (var element in group.Elements)
                {
                    var assetId = element.Kind == SceneElementKind.Prop
                        ? document.FindProp(element)?.AssetId
                        : null;
                    var assetSuffix = assetId is null ? string.Empty : $" — {assetId}";
                    groupNode.Nodes.Add(new TreeNode(
                        $"{element.Name} — {group.ElementTypeName} [{element.SourceIndex}]{assetSuffix}") { Tag = element });
                }
                groupNode.Expand();
            }
            outlinerSelections = selections;
            SyncOutlinerSelection();
        }
        finally
        {
            sceneOutliner.EndUpdate();
            refreshingOutliner = false;
        }
    }

    private void SelectFromOutliner()
    {
        if (refreshingOutliner || sceneOutliner.SelectedNode?.Tag is not SceneElementSelection selected) return;
        selection = selected;
        cameraHandle = SceneCameraHandle.Eye;
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        Text = $"{baseTitle} — selected: {DescribeSelection(selected)}";
    }

    private void FocusOutlinerNode(TreeNode node)
    {
        if (node.Tag is not SceneElementSelection selected) return;
        if (selection != selected)
        {
            sceneOutliner.SelectedNode = node;
        }
        FocusSelection();
    }

    private void SyncOutlinerSelection()
    {
        refreshingOutliner = true;
        try
        {
            sceneOutliner.SelectedNode = sceneOutliner.Nodes
                .Cast<TreeNode>()
                .SelectMany(group => group.Nodes.Cast<TreeNode>())
                .FirstOrDefault(node => node.Tag is SceneElementSelection candidate && candidate == selection);
        }
        finally
        {
            refreshingOutliner = false;
        }
    }

    private bool SaveOps(bool saveAs)
    {
        if (session.Map is null || currentMap is null) return false;
        var targetPath = savedOpsPath;
        if (saveAs || string.IsNullOrEmpty(targetPath))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save edited OPS",
                Filter = "Cold Steel map settings (*.ops)|*.ops|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(session.Map.SourcePath),
                FileName = $"{Path.GetFileNameWithoutExtension(session.Map.SourcePath)}.edited.ops",
                AddExtension = true,
                DefaultExt = "ops",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != DialogResult.OK) return false;
            targetPath = dialog.FileName;
        }
        try
        {
            opsWriter.Write(targetPath!, session.Map, currentMap);
            savedOpsPath = targetPath;
            document.MarkSaved();
            Text = $"{baseTitle} — saved: {targetPath}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Cannot save OPS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void InitializeAssetCatalog()
    {
        if (session.Script.GameDataPath is null)
        {
            assetSearch.Enabled = false;
            assetList.Enabled = false;
            addAssetButton.Enabled = false;
            return;
        }
        assetCatalog = new GameAssetCatalog(session.Script.GameDataPath).Entries;
        assetList.DisplayMember = nameof(AssetCatalogEntry.AssetId);
        FilterAssetCatalog();
    }

    private void FilterAssetCatalog()
    {
        var query = assetSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? assetCatalog
            : assetCatalog.Where(value => value.AssetId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        assetList.BeginUpdate();
        assetList.DataSource = filtered.ToArray();
        assetList.EndUpdate();
    }

    private async Task AddSelectedAssetAsync()
    {
        if (assetList.SelectedItem is not AssetCatalogEntry catalogEntry) return;
        if (session.Script.GameDataPath is null || graphics is null) return;
        addAssetButton.Enabled = false;
        try
        {
            if (!loadedModelsByAsset.TryGetValue(catalogEntry.AssetId, out var model))
            {
                var load = await Task.Run(() => projectLoader.LoadAsset(catalogEntry.AssetId, session.Script.GameDataPath));
                if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null)
                {
                    MessageBox.Show(load.Error ?? "The selected asset has no loadable model.", "Cannot add prop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                model = load.Model;
                loadedModelsByAsset.Add(catalogEntry.AssetId, model);
            }
            if (uploadedModels.All(value => !value.AssetId.Equals(catalogEntry.AssetId, StringComparison.OrdinalIgnoreCase)))
            {
                uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
            }
            placement = new PlacementState(
                catalogEntry.AssetId, catalogEntry.AssetId, model, null,
                new Dictionary<string, string>(), null, Vector3.UnitY);
            var pointer = viewportHost.PointToClient(Cursor.Position);
            if (viewportHost.ClientRectangle.Contains(pointer)) UpdatePlacement(pointer);
            Text = $"{baseTitle} — place {catalogEntry.AssetId}: click surface, Esc cancel";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(exception.Message, "Cannot add prop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            addAssetButton.Enabled = true;
        }
    }

    private void DuplicateSelectedElement()
    {
        if (selection is null) return;
        if (selection.Kind == SceneElementKind.Prop)
        {
            var source = sceneInstances.FirstOrDefault(value => value.Id == selection.SourceIndex);
            if (source is null || !loadedModelsByAsset.TryGetValue(source.AssetId, out var model)) return;
            selection = document.AddPropFromTemplate(selection, source.AssetId, $"{source.Name}_copy", model);
        }
        else
        {
            selection = document.DuplicateElement(selection);
        }
        RefreshSceneFromDocument();
    }

    private void DeleteSelectedElement()
    {
        if (selection is not { } selected) return;
        selection = null;
        if (document.DeleteElement(selected)) RefreshSceneFromDocument();
    }

    private void UpdatePlacement(Point location)
    {
        if (placement is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var result = surfaceRaycaster.Cast(CreatePointerRay(location), VisibleSceneInstances());
        placement = result.Hit is null
            ? placement with { Position = null }
            : placement with { Position = result.Hit.Position, SurfaceNormal = result.Hit.Normal };
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
    }

    private void ConfirmPlacement()
    {
        if (placement is not { Position: { } position } pending) return;
        selection = pending.OpsProfile is not null
            ? document.AddSpatialElement(pending.OpsProfile, position, pending.Inputs)
            : document.AddProp(pending.AssetId!, pending.Name, pending.Model!, position);
        placement = null;
        RefreshSceneFromDocument();
        Text = $"{baseTitle} - added: {selection.Name}";
    }

    private void CancelPlacement()
    {
        placement = null;
        RefreshSceneFromDocument();
        Text = baseTitle;
    }

    private void RefreshOpsCreationInputs()
    {
        opsInputFields.Clear();
        opsInputPanel.Controls.Clear();
        if (opsProfileList.SelectedItem is not OpsSpatialCreationProfile profile) return;
        opsProfileEvidence.Text = profile.Evidence;
        foreach (var input in profile.Inputs)
        {
            var row = new FlowLayoutPanel
            {
                Width = Math.Max(220, opsInputPanel.ClientSize.Width - 20),
                Height = 28,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            row.Controls.Add(new Label
            {
                Width = 110,
                Text = input.DisplayName,
                TextAlign = ContentAlignment.MiddleLeft,
            });
            var field = new TextBox { Width = 120 };
            row.Controls.Add(field);
            opsInputFields.Add(input.Name, field);
            opsInputPanel.Controls.Add(row);
        }
    }

    private void BeginOpsPlacement()
    {
        if (opsProfileList.SelectedItem is not OpsSpatialCreationProfile profile) return;
        try
        {
            var inputs = opsInputFields.ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal);
            profile.ValidateInputs(inputs);
            placement = new PlacementState(null, profile.DisplayName, null, profile, inputs, null, Vector3.UnitY);
            var pointer = viewportHost.PointToClient(Cursor.Position);
            if (viewportHost.ClientRectangle.Contains(pointer)) UpdatePlacement(pointer);
            Text = $"{baseTitle} - place {profile.DisplayName}: click surface, Esc cancel";
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Cannot create OPS element", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshElementProperties()
    {
        propertyGrid.Rows.Clear();
        var attributeSet = selection is null ? null : document.FindElementAttributes(selection);
        propertyGroup.Enabled = attributeSet is not null;
        if (attributeSet is null) return;
        foreach (var attribute in attributeSet.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var rowIndex = propertyGrid.Rows.Add(attribute.Key, attribute.Value);
            var row = propertyGrid.Rows[rowIndex];
            if (attributeSet.ProtectedNames.Contains(attribute.Key))
            {
                row.ReadOnly = true;
                row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
        }
    }

    private void ApplyElementProperties()
    {
        if (selection is not { } selected) return;
        try
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DataGridViewRow row in propertyGrid.Rows)
            {
                if (row.IsNewRow) continue;
                var name = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!attributes.TryAdd(name, row.Cells[1].Value?.ToString() ?? string.Empty))
                {
                    throw new ArgumentException($"Duplicate OPS attribute '{name}'.");
                }
            }
            document.ApplyElementAttributes(selected, attributes);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Invalid OPS attributes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool TryGetOverlayFocus(SceneElementSelection selected, out Vector3 center, out float radius)
    {
        center = Vector3.Zero;
        radius = overlayMarkerSize;
        if (currentMap is null) return false;
        if (selected.Kind is SceneElementKind.EntryVolume or SceneElementKind.GroupVolume)
        {
            var sourceKind = selected.Kind == SceneElementKind.EntryVolume ? MapVolumeKind.Entry : MapVolumeKind.Group;
            var volume = currentMap.Volumes.FirstOrDefault(value => value.Kind == sourceKind && value.SourceIndex == selected.SourceIndex);
            if (volume is null) return false;
            center = volume.Transform.Position;
            radius = volume.Transform.Scale.Length() * 0.5f;
            return true;
        }
        if (selected.Kind == SceneElementKind.LookPoint)
        {
            var point = currentMap.Points.FirstOrDefault(value => value.SourceIndex == selected.SourceIndex);
            if (point is null) return false;
            center = point.Position;
            radius = point.Radius ?? overlayMarkerSize;
            return true;
        }
        if (selected.Kind == SceneElementKind.Camera)
        {
            var camera = currentMap.Cameras.FirstOrDefault(value => value.SourceIndex == selected.SourceIndex);
            if (camera is null) return false;
            center = (camera.Eye + camera.LookAt) * 0.5f;
            radius = Math.Max(Vector3.Distance(camera.Eye, camera.LookAt) * 0.5f, overlayMarkerSize);
            return true;
        }
        if (selected.Kind == SceneElementKind.Sound)
        {
            var sound = currentMap.Sounds.FirstOrDefault(value => value.SourceIndex == selected.SourceIndex);
            if (sound is null) return false;
            center = sound.Position;
            radius = sound.Kind == MapSoundKind.Box
                ? Math.Max(sound.SourceScale.Length() * 0.5f, overlayMarkerSize)
                : Math.Max(sound.Range, overlayMarkerSize);
            return true;
        }
        if (selected.Kind == SceneElementKind.Light)
        {
            var light = currentMap.Lights.FirstOrDefault(value => value.SourceIndex == selected.SourceIndex);
            if (light is null) return false;
            center = light.Position;
            radius = Math.Max(light.OuterRange, overlayMarkerSize);
            return true;
        }
        return false;
    }

    private SceneEnvironmentVariant ActiveEnvironmentVariant
        => environmentVariant;

    private IReadOnlyList<SceneModelInstance> VisibleSceneInstances()
        => sceneInstances
            .Where(value => SceneEnvironmentVariantSelector.IsVisible(value.Name, ActiveEnvironmentVariant))
            .Concat(showIndicatorsCheckBox.Checked
                ? scriptMonsterInstances
                : Array.Empty<SceneModelInstance>())
            .ToArray();

    private enum CameraDragMode
    {
        None,
        Look,
        Pan,
    }

    private sealed record GizmoDragState(
        SceneElementSelection Selection,
        GizmoMode Mode,
        SceneGizmoAxis Axis,
        SceneTransform OriginalTransform,
        float StartParameter,
        Vector3 StartVector,
        float GizmoLength,
        SceneCameraHandle? CameraHandle);

    private enum GizmoMode
    {
        Translate,
        Rotate,
        Scale,
    }

    private sealed record PlacementState(
        string? AssetId,
        string Name,
        CpuModel? Model,
        OpsSpatialCreationProfile? OpsProfile,
        IReadOnlyDictionary<string, string> Inputs,
        Vector3? Position,
        Vector3 SurfaceNormal);

}
