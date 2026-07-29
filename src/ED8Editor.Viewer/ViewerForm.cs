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
using ED8Editor.ScriptHeaders;
using ED8Editor.Tables;
using Vortice.Direct3D11;

namespace ED8Editor.Viewer;

public sealed class ViewerForm : Form
{
    private EditorSession session;
    private readonly EditorProjectLoader projectLoader;
    private readonly EditorSettingsStore settingsStore;
    private EditorSceneDocument document;
    private readonly bool smokeTest;
    private string baseTitle;
    private readonly SceneElementPicker elementPicker = new();
    private readonly SceneRaycaster surfaceRaycaster = new();
    private readonly SceneRaycaster surfacePreviewRaycaster = new();
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
    private readonly Dictionary<string, Dictionary<string, CpuAnimationClip>> loadedCharacterAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, D3D11FacialTextureResources> loadedFacialTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unavailableFacialTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unavailableCharacterAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CpuSkeletonPoseEvaluator poseEvaluator = new();
    private readonly CpuSceneAnimationEvaluator sceneAnimationEvaluator = new();
    private readonly Dictionary<string, LoadedPropAnimation> loadedPropAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unavailablePropAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Panel viewportHost = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly TabControl openFileTabs = new()
    {
        Dock = DockStyle.Top,
        Height = 26,
        Visible = false,
    };
    private readonly List<string> openFiles = new();
    private bool switchingFile;
    private ScriptSubject? scriptSubject;
    private ScriptAttachTable attachTable = ScriptAttachTable.Empty;

    /// <summary>Rendered attachment -> the actor carrying it and the node it hangs from.</summary>
    private readonly Dictionary<int, (int OwnerInstanceId, int EntityId, string AttachPoint)>
        attachmentOwners = new();

    /// <summary>Pose of each rendered actor this frame, for its attachments.</summary>
    private readonly Dictionary<int, (Matrix4x4 Transform, CpuSkeletonPose Pose, CpuSkeleton Skeleton)>
        posedCharacters = new();
    private readonly ToolStrip gizmoToolStrip = new()
    {
        Dock = DockStyle.Top,
        GripStyle = ToolStripGripStyle.Hidden,
        RenderMode = ToolStripRenderMode.System,
    };
    private readonly TableLayoutPanel topChrome = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        RowCount = 3,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
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
    private readonly ToolStripButton cameraPlayButton = new("▶ Preview") { ToolTipText = "Replay camera interpolation preview", Enabled = false };
    private readonly ToolStripButton ignoreScriptCameraButton = new("Ignore script camera")
    {
        CheckOnClick = true,
        ToolTipText = "Keep entity/animation playback running while ignoring script camera commands",
    };
    private bool suppressCameraTextUpdate;
    private readonly Panel scenePanel = new() { Dock = DockStyle.Left, Width = 340, Padding = new Padding(8) };
    private readonly TabControl leftPanelTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage mapTab = new("Map");
    private readonly TabPage encountersTab = new("Encounters");
    private readonly TabPage modTab = new("Mod project");
    private readonly Panel mapPanel = new() { Dock = DockStyle.Fill };
    private readonly Panel modPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(6) };
    private readonly TreeView encountersTree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
    };
    private readonly Button newEncounterButton = new() { AutoSize = true, Text = "New encounter…" };
    private readonly Button editEncounterButton = new() { AutoSize = true, Text = "Edit…" };
    private readonly Button instantiateEncounterButton = new() { AutoSize = true, Text = "Instantiate on map…" };
    private readonly TreeView modFileTree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
    };
    private readonly Label modProjectLabel = new()
    {
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 34,
        Text = "No mod project open.",
    };
    private ModProject? modProject;
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
    private readonly TabPage assetsTab = new("Assets");
    private readonly TabPage opsTab = new("OPS");
    private readonly TabPage scriptsTab = new("Scripts");
    private readonly TreeView opsElementTree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
    };
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
    private readonly CheckBox showFieldMonstersCheckBox = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        Text = "Show field monsters",
        Checked = true,
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
    private readonly Button editFieldMonstersButton = new()
    {
        Dock = DockStyle.Bottom,
        Height = 30,
        Text = "Edit encounters…",
    };
    private readonly Button addFieldMonsterButton = new()
    {
        Dock = DockStyle.Bottom,
        Height = 30,
        Text = "Place a field monster…",
    };
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
    private readonly Dictionary<string, Control> opsInputFields = new(StringComparer.Ordinal);
    private IReadOnlyList<AssetCatalogEntry> assetCatalog = Array.Empty<AssetCatalogEntry>();
    private D3D11GraphicsDevice? graphics;
    private D3D11Viewport? viewport;
    private IReadOnlyList<D3D11SceneInstance> instances = Array.Empty<D3D11SceneInstance>();
    private IReadOnlyList<SceneModelInstance> sceneInstances = Array.Empty<SceneModelInstance>();
    private IReadOnlyList<SceneModelInstance> scriptMonsterInstances = Array.Empty<SceneModelInstance>();
    private IReadOnlyList<SceneModelInstance> fieldMonsterInstances = Array.Empty<SceneModelInstance>();
    private IReadOnlyDictionary<int, ScriptMonsterSpawn> fieldMonsterSpawns =
        new Dictionary<int, ScriptMonsterSpawn>();
    private IReadOnlyDictionary<int, ScriptEntityState> activeScriptEntities =
        new Dictionary<int, ScriptEntityState>();
    private IReadOnlyDictionary<string, ScriptPropAnimation> activeScriptPropAnimations =
        new Dictionary<string, ScriptPropAnimation>(StringComparer.Ordinal);
    private int scriptEntityRefreshGeneration;
    private int fieldMonsterRefreshGeneration;
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
    private Action<Vector3>? scriptSurfacePositionCapture;
    private ScenePickHit? scriptSurfacePositionPreview;
    private (int RequestId, SceneRay Ray, SceneModelInstance[] Instances)?
        queuedScriptSurfacePreview;
    private int scriptSurfacePreviewRequestId;
    private bool scriptSurfacePreviewRaycastRunning;
    private int opsCreationDestinationLoadGeneration;
    private int opsPropertyDestinationLoadGeneration;
    private SceneCameraHandle cameraHandle = SceneCameraHandle.Eye;
    private IReadOnlyList<SceneElementSelection> outlinerSelections = Array.Empty<SceneElementSelection>();
    private bool refreshingOutliner;
    private EditorKeyboardLayout keyboardLayout;
    private SceneEnvironmentVariant environmentVariant = SceneEnvironmentVariant.Daylight;
    private bool refreshingOpsElementTree;
    private string? instructionDefinitionsPath;
    private ScriptAnimationLibrary? scriptAnimationLibrary;
    private IReadOnlyList<MonsterTableChoice> monsterTableChoices =
        Array.Empty<MonsterTableChoice>();
    private DecompiledScript? systemScript;
    private bool manualScriptCameraOverride;
    // The slider only moves in whole degrees; the authored value keeps its
    // decimals so applying a shot back into a script does not round 43.2 to 43.
    private float cameraFieldOfViewDegrees = 45f;
    private float CameraVerticalFieldOfView => cameraFieldOfViewDegrees * MathF.PI / 180f;

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
        if (session.Script.GameDataPath is { } gameDataPath)
        {
            scriptAnimationLibrary = new ScriptAnimationLibrary(
                gameDataPath,
                session.Script.Header.SourcePath,
                instructionDefinitionsPath);
            systemScript = new ScriptSystemLibrary(
                session.Script.Header.SourcePath,
                instructionDefinitionsPath).Script;
            scriptSubject = ScriptSubjectResolver.Resolve(
                session.Script.Header.SourcePath, gameDataPath, scriptAnimationLibrary);
            attachTable = ScriptAttachTable.Load(gameDataPath);
            monsterTableChoices = MonsterTableCatalog.Load(gameDataPath);
        }
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
        assetControlsPanel.Controls.Add(showFieldMonstersCheckBox);
        assetControlsPanel.Controls.Add(effectMetadataStatus);
        assetControlsPanel.Controls.Add(assetSearch);
        propertyGroup.Controls.Add(propertyGrid);
        propertyGroup.Controls.Add(applyPropertiesButton);
        sceneOutlinerGroup.Controls.Add(sceneOutliner);
        mapPanel.Controls.Add(sceneOutlinerGroup);
        mapTab.Controls.Add(mapPanel);
        BuildEncountersTab();
        BuildModProjectTab();
        RefreshModProjectTab();
        modTab.Controls.Add(modPanel);
        leftPanelTabs.TabPages.Add(mapTab);
        leftPanelTabs.TabPages.Add(encountersTab);
        leftPanelTabs.TabPages.Add(modTab);
        scenePanel.Controls.Add(leftPanelTabs);
        opsCreationGroup.Controls.Add(opsInputPanel);
        opsCreationGroup.Controls.Add(opsProfileEvidence);
        opsCreationGroup.Controls.Add(opsProfileList);
        opsCreationGroup.Controls.Add(addOpsElementButton);
        assetControlsPanel.Controls.Add(deleteButton);
        assetControlsPanel.Controls.Add(duplicateButton);
        assetControlsPanel.Controls.Add(addAssetButton);
        assetControlsPanel.Controls.Add(addFieldMonsterButton);
        assetControlsPanel.Controls.Add(editFieldMonstersButton);
        assetsTab.Controls.Add(assetControlsPanel);
        var opsWorkspaceTabs = new TabControl { Dock = DockStyle.Fill };
        var opsElementsPage = new TabPage("Elements");
        var opsCreatePage = new TabPage("Create");
        propertyGroup.Dock = DockStyle.Bottom;
        propertyGroup.Height = 300;
        opsCreationGroup.Dock = DockStyle.Fill;
        opsCreationGroup.Height = 300;
        opsElementsPage.Controls.Add(opsElementTree);
        opsElementsPage.Controls.Add(propertyGroup);
        opsCreatePage.Controls.Add(opsCreationGroup);
        opsWorkspaceTabs.TabPages.Add(opsElementsPage);
        opsWorkspaceTabs.TabPages.Add(opsCreatePage);
        opsTab.Controls.Add(opsWorkspaceTabs);
        rightPanelTabs.TabPages.Add(assetsTab);
        rightPanelTabs.TabPages.Add(opsTab);
        rightPanelTabs.TabPages.Add(scriptsTab);
        assetPanel.Controls.Add(rightPanelTabs);
        BuildMainMenu();
        openFileTabs.SelectedIndexChanged += (_, _) => SelectOpenFileTab();
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
            new ToolStripSeparator(),
            cameraPlayButton,
            ignoreScriptCameraButton,
        });
        topChrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topChrome.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topChrome.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topChrome.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainMenu.Dock = DockStyle.Fill;
        openFileTabs.Dock = DockStyle.Fill;
        gizmoToolStrip.Dock = DockStyle.Fill;
        topChrome.Controls.Add(mainMenu, 0, 0);
        topChrome.Controls.Add(openFileTabs, 0, 1);
        topChrome.Controls.Add(gizmoToolStrip, 0, 2);
        Controls.Add(topChrome);
        MainMenuStrip = mainMenu;
        translateToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Translate);
        rotateToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Rotate);
        scaleToolButton.Click += (_, _) => SetGizmoMode(GizmoMode.Scale);
        cameraFovSlider.ValueChanged += (_, _) =>
        {
            cameraFieldOfViewDegrees = cameraFovSlider.Value;
            cameraFovLabel.Text = $"FOV: {cameraFovSlider.Value}°";
        };

        cameraPlayButton.Click += (_, _) =>
        {
            if (!ignoreScriptCameraButton.Checked
                && animationBefore is not null
                && animationAfter is not null)
            {
                manualScriptCameraOverride = false;
                animationElapsed = 0f;
                isCameraAnimating = true;
                cameraAnimationLoops = true;
            }
        };
        ignoreScriptCameraButton.CheckedChanged += (_, _) =>
        {
            if (ignoreScriptCameraButton.Checked)
                BeginManualScriptCameraOverride();
            else
                manualScriptCameraOverride = false;
        };

        // Camera position edit handlers (angles in radians)
        EventHandler onCameraEdit = (_, _) =>
        {
            if (suppressCameraTextUpdate) return;
            BeginManualScriptCameraOverride();
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
            ApplyScriptModeLayout(eventArgs.TabPage == scriptsTab);
            if (eventArgs.TabPage == scriptsTab) OpenScriptEditor();
        };
        viewportHost.Resize += (_, _) => ResizeViewport();
        Resize += (_, _) =>
        {
            if (scriptModeLayoutActive)
                assetPanel.Width = Math.Max(assetPanel.MinimumSize.Width, ClientSize.Width / 2);
        };
        SetGizmoMode(GizmoMode.Translate);
        assetSearch.TextChanged += (_, _) => FilterAssetCatalog();
        showIndicatorsCheckBox.CheckedChanged += (_, _) =>
        {
            RefreshRenderInstances(uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
            RefreshOverlay();
        };
        showFieldMonstersCheckBox.CheckedChanged += (_, _) =>
        {
            RefreshRenderInstances(uploadedModels.ToDictionary(
                value => value.AssetId, StringComparer.OrdinalIgnoreCase));
            RefreshOutliner();
        };
        editFieldMonstersButton.Click += (_, _) =>
        {
            OpenScriptEditor();
            scriptEditor?.OpenCreateMonstersEditor();
        };
        addFieldMonsterButton.Click += (_, _) =>
        {
            OpenScriptEditor();
            scriptEditor?.StartFieldMonsterPlacement();
        };
        addAssetButton.Click += async (_, _) => await AddSelectedAssetAsync();
        duplicateButton.Click += (_, _) => DuplicateSelectedElement();
        deleteButton.Click += (_, _) => DeleteSelectedElement();
        propertyGrid.Columns.Add("Attribute", "Attribute");
        propertyGrid.Columns.Add("Value", "Value");
        propertyGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (propertyGrid.IsCurrentCellDirty)
                propertyGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        propertyGrid.CellValueChanged += async (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex != 1) return;
            var row = propertyGrid.Rows[eventArgs.RowIndex];
            if (!string.Equals(
                    row.Cells[0].Value?.ToString(), "next", StringComparison.Ordinal))
            {
                return;
            }
            await RefreshPropertyDestinationEntriesAsync();
        };
        propertyGrid.DataError += (_, eventArgs) =>
        {
            Debug.WriteLine($"OPS property grid value error: {eventArgs.Exception?.Message}");
            eventArgs.ThrowException = false;
        };
        applyPropertiesButton.Click += (_, _) => ApplyElementProperties();
        sceneOutliner.AfterSelect += (_, _) => SelectFromOutliner();
        sceneOutliner.NodeMouseDoubleClick += (_, eventArgs) => FocusOutlinerNode(eventArgs.Node);
        opsElementTree.AfterSelect += (_, _) => SelectFromOpsTree();
        opsElementTree.NodeMouseDoubleClick += (_, eventArgs) => EditOpsNode(eventArgs.Node);
        opsProfileList.DisplayMember = nameof(OpsSpatialCreationProfile.DisplayName);
        opsProfileList.DataSource = OpsSpatialCreationCatalog.Profiles.ToArray();
        opsProfileList.SelectedIndexChanged += (_, _) => RefreshOpsCreationInputs();
        addOpsElementButton.Click += (_, _) => BeginOpsPlacement();
        RefreshOpsCreationInputs();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        renderTimer.Tick += (_, _) => RenderFrame();
        KeyDown += (_, eventArgs) =>
        {
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
            if (eventArgs.KeyCode == Keys.Escape && scriptSurfacePositionCapture is not null)
            {
                CancelScriptSurfacePositionCapture();
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
                if (scriptSurfacePositionCapture is not null)
                {
                    CaptureScriptSurfacePosition(eventArgs.Location);
                    return;
                }
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
                && !rightLookMoved
                && scriptSurfacePositionCapture is null;
            EndCameraDrag();
            if (eventArgs.Button == MouseButtons.Right
                && scriptSurfacePositionCapture is not null)
            {
                var pointer = viewportHost.PointToClient(Cursor.Position);
                if (viewportHost.ClientRectangle.Contains(pointer))
                    QueueScriptSurfacePositionPreview(pointer);
            }
            if (deselect) ClearSelection();
        };
        viewportHost.MouseMove += (_, eventArgs) =>
        {
            if (UpdateLeftMouseGesture(eventArgs.Location)) return;
            if (scriptSurfacePositionCapture is not null)
            {
                if (cameraDrag == CameraDragMode.None)
                {
                    QueueScriptSurfacePositionPreview(eventArgs.Location);
                }
                else
                {
                    MoveCamera(eventArgs.Location);
                    QueueScriptSurfacePositionPreview(
                        new Point(
                            viewportHost.ClientSize.Width / 2,
                            viewportHost.ClientSize.Height / 2));
                }
            }
            else if (placement is not null) UpdatePlacement(eventArgs.Location);
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
            var monsterCount = await LoadFieldMonstersFromPathAsync(
                session.Script.Header.SourcePath);
            effectMetadataStatus.Text = "Phyre effects: loading...";
            var effectCount = await LoadEffectMetadataAsync();
            if (IsDisposed) return;
            var modelCount = session.AssetModels.Values.Count(value => value.Model is not null);
            if (effectCount >= 0)
            {
                effectMetadataStatus.Text = $"Phyre effects: {effectCount}/{modelCount}; monsters: {monsterCount}";
                effectMetadataStatus.ForeColor = effectCount == modelCount ? Color.DarkGreen : Color.DarkOrange;
            }
            if (!smokeTest) AddFileTab(session.Script.Header.SourcePath);
            if (smokeTest)
            {
                OpenScriptEditor();
                PerformLayout();
                scriptEditor?.VerifyEmbeddedInteractionSmoke();
                ScriptCameraOrbit.VerifySmoke();
                ScriptFacialPattern.VerifySmoke();
                ScriptDialogText.VerifySmoke();
                await VerifyScriptPropAnimationSmokeAsync();
                await VerifyScriptAnimationReplaySmokeAsync();
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
            if (smokeTest)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
                Close();
                return;
            }
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
            foreach (var facialTextures in loadedFacialTextures.Values) facialTextures.Dispose();
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
        viewport = new D3D11Viewport(
            graphics, viewportHost.Handle, viewportHost.ClientSize.Width, viewportHost.ClientSize.Height);
        LoadSceneForSession();
    }

    /// <summary>
    /// Builds everything that belongs to the open file: its map, its models and
    /// the camera that frames them. Switching to another file of the project runs
    /// this again on the new session instead of restarting the editor.
    /// </summary>
    private void LoadSceneForSession()
    {
        if (graphics is null || viewport is null) return;
        var uploader = new D3D11ModelUploader(graphics.Device);
        var resourcesByAsset = new Dictionary<string, D3D11ModelResources>(StringComparer.OrdinalIgnoreCase);
        foreach (var load in session.AssetModels.Values.Where(value => value.Model is not null))
        {
            loadedModelsByAsset[load.AssetId] = load.Model!;
            if (uploadedModels.FirstOrDefault(value =>
                    value.AssetId.Equals(load.AssetId, StringComparison.OrdinalIgnoreCase))
                is { } existing)
            {
                resourcesByAsset[load.AssetId] = existing;
                continue;
            }
            var uploaded = uploader.Upload(load.Model!);
            uploadedModels.Add(uploaded);
            resourcesByAsset[load.AssetId] = uploaded;
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
        viewport.SetEnvironmentVariant(ActiveEnvironmentVariant);
        if (currentMap?.DefaultEnvironment is { } environment)
        {
            viewport.SetClearColor(new Vector4(environment.FogColor, 1f));
        }
        else if (session.Map is null)
        {
            // No map: a bright backdrop so the actor and the ground stand out.
            viewport.SetClearColor(new Vector4(0.86f, 0.88f, 0.91f, 1f));
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

    private void RenderFrame()
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ticks = frameClock.ElapsedTicks;
        var elapsed = Math.Clamp((float)(ticks - previousFrameTicks) / Stopwatch.Frequency, 0f, 0.1f);
        previousFrameTicks = ticks;
        UpdateCamera(elapsed);
        UpdateScriptTimeline(elapsed);
        UpdateCameraAnimation(elapsed);
        UpdateCameraTextFields();
        RefreshAnimationPoses();
        // Effects move on their own clock and their billboards face the camera,
        // so they are rebuilt every frame rather than with the static overlay.
        viewport.SetEffectQuads(BuildEffectQuads());
        var camera = CreateCamera();
        viewport.Render(instances, camera);
        // The effect editor draws on the same device and the same clock.
        if (effectEditorWindow is { IsDisposed: false, Visible: true } effects)
        {
            effects.RenderPreview();
        }
    }

    private void RefreshAnimationPoses()
    {
        var characters = activeScriptEntities.Values.ToDictionary(
            value => ScriptEntitySceneInstanceId(value.EntityId));
        var elapsed = (float)frameClock.Elapsed.TotalSeconds;
        posedCharacters.Clear();
        instances = instances.Select(instance =>
        {
            if (!loadedModelsByAsset.TryGetValue(instance.Model.AssetId, out var model)) return instance;
            // Animation fields are derived from the current script state every
            // frame. Do not retain a pose from the previously selected block
            // while a different clip is loading or no longer active.
            var animatedInstance = instance with
            {
                SceneNodeTransforms = null,
                SkinMatrices = null,
                TexturesByGameMaterialId = null,
            };
            if (TryGetActivePropAnimation(
                    instance.SceneInstanceId, model.AssetId, out var propAnimation, out var loadedPropAnimation)
                && model.SceneNodes is { Count: > 0 } nodes)
            {
                var propClip = loadedPropAnimation.Clip;
                var propAnimationElapsed = scriptTimeline is null
                    ? elapsed
                    : Math.Max(0f, scriptTimelineFrame - propAnimation.StartFrame)
                        / ScriptWaitDuration.PreviewFramesPerSecond;
                var progress = loadedPropAnimation.Loop && propClip.Duration > 0f
                    ? propAnimationElapsed % propClip.Duration
                    : propAnimation.HoldFinalFrame
                        ? propClip.Duration
                        : Math.Min(propClip.Duration, propAnimationElapsed);
                var propTime = loadedPropAnimation.Reverse
                    ? propClip.EndTime - progress
                    : propClip.StartTime + progress;
                var propPose = sceneAnimationEvaluator.Evaluate(nodes, propClip, propTime);
                animatedInstance = animatedInstance with
                {
                    SceneNodeTransforms = propPose.WorldTransforms,
                };
            }
            if (!characters.TryGetValue(instance.SceneInstanceId, out var character))
                return animatedInstance;
            var position = scriptTimeline is not null
                ? EvaluateEntityMotionPosition(character, scriptTimelineFrame)
                : character.Position;
            // Facial patterns run on their own clock, started by the command
            // that set the expression, exactly like a character animation.
            var facialElapsed = scriptTimeline is not null
                ? Math.Max(
                    0f,
                    scriptTimelineFrame
                        - (character.FacialExpression?.StartFrame ?? 0))
                    / ScriptWaitDuration.PreviewFramesPerSecond
                : elapsed;
            // A walking actor faces where it walks: the engine turns it towards
            // its own movement instead of keeping the heading it was spawned with.
            var yawDegrees = scriptTimeline is not null
                ? character.Motion?.HeadingAt(scriptTimelineFrame) ?? character.YawDegrees
                : character.YawDegrees;
            animatedInstance = animatedInstance with
            {
                Transform = Matrix4x4.CreateScale(character.Scale)
                    * Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(position),
                TexturesByGameMaterialId =
                    CreateFacialTextureOverrides(character, facialElapsed),
            };
            if (model.Skeleton is null
                || !TryGetBaseAnimation(character, out var activeAnimation)
                || !TryGetCharacterAnimation(character.AssetId, activeAnimation.Name, out var clip))
                return animatedInstance;
            var selectedCharacter = selection is { Kind: SceneElementKind.ScriptCharacter }
                && selection.SourceIndex == instance.SceneInstanceId;
            var duration = clip.Duration;
            var play = scriptTimeline is not null || selectedCharacter;
            var animationElapsed = scriptTimeline is not null
                ? Math.Max(0f, scriptTimelineFrame - activeAnimation.StartFrame)
                    / ScriptWaitDuration.PreviewFramesPerSecond
                : elapsed;
            var time = clip.StartTime;
            if (activeAnimation.HoldFinalFrame)
            {
                time = clip.EndTime;
            }
            else if (play && duration > 0f)
            {
                time = activeAnimation.Loop
                    ? clip.StartTime + animationElapsed % duration
                    : Math.Min(clip.EndTime, clip.StartTime + animationElapsed);
            }
            // Character clips also animate authored controller/weapon targets
            // (for example effector1..6 and buki) which are not skin joints in
            // every model. Apply the exact intersection with the render skeleton.
            var pose = poseEvaluator.Evaluate(
                model.Skeleton,
                clip,
                time,
                CpuAnimationUnboundTargetBehavior.Ignore);
            posedCharacters[instance.SceneInstanceId] =
                (animatedInstance.Transform, pose, model.Skeleton);
            return animatedInstance with { SkinMatrices = pose.SkinMatrices };
        }).ToArray();
        PlaceAttachments();
    }

    /// <summary>
    /// Hangs what an actor carries on the node it is attached to: the weapon
    /// follows the animated skeleton instead of floating at the actor's feet.
    /// An actor with no clip playing is posed at its bind pose, which is what the
    /// engine shows too.
    /// </summary>
    private void PlaceAttachments()
    {
        if (attachmentOwners.Count == 0) return;
        instances = instances.Select(instance =>
        {
            if (!attachmentOwners.TryGetValue(instance.SceneInstanceId, out var owner))
                return instance;
            if (!TryGetOwnerPose(owner.OwnerInstanceId, out var posed))
                return instance with { Transform = HiddenTransform };
            var jointIndex = IndexOfJoint(posed.Skeleton, owner.AttachPoint);
            if (jointIndex < 0) return instance with { Transform = HiddenTransform };
            activeScriptEntities.TryGetValue(owner.EntityId, out var ownerEntity);
            return instance with
            {
                Transform = AttachmentPlacement(ownerEntity, owner.AttachPoint)
                    * posed.Pose.WorldTransforms[jointIndex]
                    * posed.Transform,
            };
        }).ToArray();
    }

    private bool TryGetOwnerPose(
        int ownerInstanceId,
        out (Matrix4x4 Transform, CpuSkeletonPose Pose, CpuSkeleton Skeleton) posed)
    {
        if (posedCharacters.TryGetValue(ownerInstanceId, out posed)) return true;
        // The actor is rendered but no clip is playing on it: its nodes are where
        // the bind pose puts them.
        var owner = instances.FirstOrDefault(value => value.SceneInstanceId == ownerInstanceId);
        if (owner is null
            || !loadedModelsByAsset.TryGetValue(owner.Model.AssetId, out var ownerModel)
            || ownerModel.Skeleton is not { } skeleton)
        {
            posed = default;
            return false;
        }
        posed = (owner.Transform, poseEvaluator.Evaluate(skeleton, null, 0f), skeleton);
        posedCharacters[ownerInstanceId] = posed;
        return true;
    }

    private static int IndexOfJoint(CpuSkeleton skeleton, string name)
    {
        for (var index = 0; index < skeleton.Joints.Count; index++)
        {
            if (skeleton.Joints[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    /// <summary>Where an attachment goes when its node cannot be resolved.</summary>
    private static Matrix4x4 HiddenTransform => Matrix4x4.CreateScale(0f);

    private bool TryGetActivePropAnimation(
        int sceneInstanceId,
        string assetId,
        out ScriptPropAnimation animation,
        out LoadedPropAnimation loadedAnimation)
    {
        animation = null!;
        loadedAnimation = null!;
        var prop = sceneInstances.FirstOrDefault(value => value.Id == sceneInstanceId);
        if (prop is null
            || !activeScriptPropAnimations.TryGetValue(
                prop.Name, out var resolvedAnimation))
        {
            return false;
        }
        animation = resolvedAnimation;
        return loadedPropAnimations.TryGetValue(
            PropAnimationKey(assetId, animation.AnimationName), out loadedAnimation!);
    }

    private static Vector3 EvaluateEntityMotionPosition(
        ScriptEntityState entity,
        float timelineFrame)
        => entity.Motion?.PositionAt(timelineFrame) ?? entity.Position;

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
            scriptCameraAngles = null;   // the user is flying the camera now
            BeginManualScriptCameraOverride();
            var fast = pressedKeys.Contains(Keys.ShiftKey) ? 4f : 1f;
            var speed = Math.Max(sceneRadius * 0.12f, 2f);
            var translation = Vector3.Normalize(movement) * speed * fast * elapsed;
            cameraNavigation.Translate(translation);
        }
        var dollyDistance = cameraDollySmoother.Advance(elapsed);
        if (dollyDistance != 0f)
        {
            BeginManualScriptCameraOverride();
            cameraNavigation.Dolly(dollyDistance);
        }
    }

    private void SetKeyboardLayout(EditorKeyboardLayout value)
    {
        keyboardLayout = value;
        settingsStore.Save(settingsStore.Load() with { KeyboardLayout = keyboardLayout });
        pressedKeys.Clear();
    }

    private void SetEnvironmentVariant(SceneEnvironmentVariant value)
    {
        environmentVariant = value;
        viewport?.SetEnvironmentVariant(value);
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
            BeginManualScriptCameraOverride();
            rightLookMoved = true;
            cameraNavigation.Look(lookDeltaX, lookDeltaY);
            CenterLookCursor();
            return;
        }
        var deltaX = current.X - previousMouse.X;
        var deltaY = current.Y - previousMouse.Y;
        if (deltaX != 0 || deltaY != 0)
            BeginManualScriptCameraOverride();
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
        var file = new ToolStripMenuItem("File");
        file.DropDownItems.Add(new ToolStripMenuItem(
            "New project…", null, (_, _) => CreateModProject()));
        file.DropDownItems.Add(new ToolStripMenuItem(
            "Open project…", null, (_, _) => OpenModProject())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem(
            "Add script to the project…", null, (_, _) => AddScriptToProject()));
        file.DropDownItems.Add(new ToolStripMenuItem(
            "Export the mod as .zip…", null, (_, _) => ExportModArchive()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Quit", null, (_, _) => Close()));
        mainMenu.Items.Add(file);

        // The editors that stand on their own get a window of their own, which
        // leaves the main window to the scene and its script.
        var windows = new ToolStripMenuItem("Windows");
        windows.DropDownItems.Add(new ToolStripMenuItem(
            "Effect editor…", null, (_, _) => ShowEffectEditor()));
        windows.DropDownItems.Add(new ToolStripMenuItem(
            "Table editor…", null, (_, _) => ShowTableEditor()));
        windows.DropDownItems.Add(new ToolStripSeparator());
        windows.DropDownItems.Add(new ToolStripMenuItem(
            "Character studio…", null, (_, _) => ShowCharacterStudio(CharacterAuthoringKind.Character)));
        windows.DropDownItems.Add(new ToolStripMenuItem(
            "Enemy studio…", null, (_, _) => ShowCharacterStudio(CharacterAuthoringKind.Enemy)));
        windows.DropDownItems.Add(new ToolStripMenuItem(
            "Quest editor…", null, (_, _) => ShowQuestEditor()));
        mainMenu.Items.Add(windows);

        var options = new ToolStripMenuItem("Options");
        options.DropDownItems.Add(new ToolStripMenuItem(
            "Instruction definitions...", null, (_, _) => ConfigureInstructionDefinitions()));
        var navigation = new ToolStripMenuItem("Keyboard navigation");
        var azerty = new ToolStripMenuItem("AZERTY (ZQSD)")
        {
            CheckOnClick = true,
            Checked = keyboardLayout == EditorKeyboardLayout.Azerty,
        };
        var qwerty = new ToolStripMenuItem("QWERTY (WASD)")
        {
            CheckOnClick = true,
            Checked = keyboardLayout == EditorKeyboardLayout.Qwerty,
        };
        azerty.Click += (_, _) =>
        {
            SetKeyboardLayout(EditorKeyboardLayout.Azerty);
            azerty.Checked = true;
            qwerty.Checked = false;
        };
        qwerty.Click += (_, _) =>
        {
            SetKeyboardLayout(EditorKeyboardLayout.Qwerty);
            qwerty.Checked = true;
            azerty.Checked = false;
        };
        navigation.DropDownItems.Add(azerty);
        navigation.DropDownItems.Add(qwerty);
        options.DropDownItems.Add(navigation);
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
    private Cs1TableCatalog? tableCatalog;

    // Camera animation interpolation
    private ScriptCameraState? animationBefore;
    private ScriptCameraState? animationAfter;
    private ScriptCameraSnapshot? animationStart;
    private int animationDurationMs;
    private int animationEasingType;
    private float animationElapsed;
    private bool isCameraAnimating;
    private bool cameraAnimationLoops;
    private EffEditorWindow? effectEditorWindow;
    private TblEditorWindow? tableEditorWindow;
    private CharacterStudioForm? characterStudioWindow;
    private CharacterStudioForm? enemyStudioWindow;
    private QuestEditorWindow? questEditorWindow;
    private D3D11EffectTextureResources? effectTextures;
    private readonly HashSet<string> unavailableEffectTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffFile?> loadedEffects =
        new(StringComparer.OrdinalIgnoreCase);
    private ScriptSceneTimeline? scriptTimeline;
    private int scriptTimelinePointIndex;
    private float scriptTimelineFrame;
    private int scriptTimelineGeneration;

    private bool IsCameraMovementKey(Keys key) => key == Keys.ShiftKey
        || key == Keys.S
        || key == Keys.D
        || key == Keys.E
        || key == Keys.C
        || key == Keys.Space
        || key == (keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Z : Keys.W)
        || key == (keyboardLayout == EditorKeyboardLayout.Azerty ? Keys.Q : Keys.A);

    private int assetPanelWidthBeforeScriptMode;
    private bool scriptModeLayoutActive;

    /// <summary>
    /// In script mode the left panel (entity list) is hidden and the editor takes the
    /// right half of the window, leaving the left half to the viewport.
    /// </summary>
    private void ApplyScriptModeLayout(bool scriptMode)
    {
        if (scriptMode == scriptModeLayoutActive) return;
        scriptModeLayoutActive = scriptMode;
        if (scriptMode)
        {
            assetPanelWidthBeforeScriptMode = assetPanel.Width;
            scenePanel.Visible = false;
            assetPanel.Width = Math.Max(assetPanel.MinimumSize.Width, ClientSize.Width / 2);
        }
        else
        {
            scenePanel.Visible = true;
            if (assetPanelWidthBeforeScriptMode > 0) assetPanel.Width = assetPanelWidthBeforeScriptMode;
        }
        ResizeViewport();
    }

    private void OpenScriptEditor(bool activateTab = true)
    {
        var editor = scriptEditor;
        if (editor is null || editor.IsDisposed)
        {
            editor = new ScriptEditorForm(
                GetTableChoices,
                new ScriptEditorSemanticContext(() => CaptureCameraSnapshot(),
                    () => activeScriptEntities.Values
                        .OrderBy(value => value.EntityId)
                        .Select(value => new ScriptEntityChoice(
                            value.EntityId, value.AssetId, value.DisplayName))
                        .ToArray(),
                    entityId => activeScriptEntities.TryGetValue(entityId, out var entity)
                        && scriptAnimationLibrary is not null
                            ? scriptAnimationLibrary.GetFunctionNames(entity)
                            : Array.Empty<string>(),
                    BeginScriptSurfacePositionCapture),
                instructionDefinitionsPath,
                monsterTableChoices,
                GetBattleMapAssets(),
                CreateBattleMapInf);
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
            editor.FunctionSelected += _ => ClearScriptEntityPreview();
            editor.ScriptChanged += script => _ = RefreshFieldMonstersAsync(script);
            editor.EntityActivated += FocusScriptEntity;
            editor.StopPreview += () => { isCameraAnimating = false; cameraPlayButton.Enabled = true; };
            editor.OpenRequested += path => OpenScriptFile(path);
            editor.PlayFunctionRequested += PlayScriptFunction;
            editor.StopPlaybackRequested += StopScriptTimeline;
            editor.SkipToNextCommandRequested += SkipToNextScriptCommand;
            editor.FileSaving += path => TrackModSave(path, beforeWrite: true);
            editor.FileSaved += path => TrackModSave(path, beforeWrite: false);
            scriptsTab.Controls.Add(editor);
            editor.Show();
            editor.SetRuntimeEntities(CreateScriptEntityChoices(activeScriptEntities));
        }

        if (activateTab) rightPanelTabs.SelectedTab = scriptsTab;
        var path = session.Script.Header.SourcePath;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            editor.LoadDat(path);
            if (editor.CurrentScript is { } script) _ = RefreshFieldMonstersAsync(script);
        }
    }

    private async Task RefreshFieldMonstersAsync(DecompiledScript script)
    {
        var generation = ++fieldMonsterRefreshGeneration;
        RefreshEncounterBrowser(script);
        if (session.Script.GameDataPath is not { } gameDataPath || graphics is null)
        {
            fieldMonsterInstances = Array.Empty<SceneModelInstance>();
            fieldMonsterSpawns = new Dictionary<int, ScriptMonsterSpawn>();
            return;
        }

        var spawns = ScriptMonsterSpawnReader.Read(script);
        var modelAssetsByMonster = spawns
            .Select(value => value.AssetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(monsterAssetId => (
                MonsterAssetId: monsterAssetId,
                ModelAssetId: monsterTableChoices.FirstOrDefault(value =>
                    value.AssetId.Equals(monsterAssetId, StringComparison.OrdinalIgnoreCase))
                    ?.ModelAssetId))
            .Where(value => !string.IsNullOrWhiteSpace(value.ModelAssetId))
            .ToDictionary(
                value => value.MonsterAssetId,
                value => value.ModelAssetId!,
                StringComparer.OrdinalIgnoreCase);
        foreach (var modelAssetId in modelAssetsByMonster.Values
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (generation != fieldMonsterRefreshGeneration || IsDisposed) return;
            if (!loadedModelsByAsset.TryGetValue(modelAssetId, out var model))
            {
                model = await LoadScriptEntityModelAsync(modelAssetId, gameDataPath);
                if (generation != fieldMonsterRefreshGeneration || IsDisposed) return;
                if (model is null) continue;
                loadedModelsByAsset[modelAssetId] = model;
            }
            if (uploadedModels.All(value =>
                    !value.AssetId.Equals(modelAssetId, StringComparison.OrdinalIgnoreCase)))
            {
                uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
            }
        }

        var byInstance = new Dictionary<int, ScriptMonsterSpawn>();
        var rendered = new List<SceneModelInstance>();
        for (var index = 0; index < spawns.Count; index++)
        {
            var spawn = spawns[index];
            if (!modelAssetsByMonster.TryGetValue(spawn.AssetId, out var modelAssetId)
                || !loadedModelsByAsset.TryGetValue(modelAssetId, out var model))
            {
                continue;
            }
            var instanceId = FieldMonsterSceneInstanceBase + index;
            var monsterName = monsterTableChoices.FirstOrDefault(value =>
                value.AssetId.Equals(spawn.AssetId, StringComparison.OrdinalIgnoreCase))
                ?.DisplayName;
            var label = string.IsNullOrWhiteSpace(monsterName)
                ? spawn.AssetId
                : $"{monsterName} — {spawn.AssetId}";
            byInstance.Add(instanceId, spawn);
            rendered.Add(new SceneModelInstance(
                instanceId,
                modelAssetId,
                $"{label} — encounter {spawn.EncounterIndex}",
                model,
                Matrix4x4.CreateRotationY(spawn.HeadingDegrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(spawn.Position),
                Vector4.One,
                Vector3.Zero,
                SceneElementKind.FieldMonster));
        }
        if (generation != fieldMonsterRefreshGeneration || IsDisposed) return;
        fieldMonsterSpawns = byInstance;
        fieldMonsterInstances = rendered;
        RefreshRenderInstances(
            uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
        RefreshOutliner();
    }

    private const int FieldMonsterSceneInstanceBase = int.MinValue + 400000;

    private async Task<int> LoadFieldMonstersFromPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try
        {
            var script = await Task.Run(() =>
                ScriptDecompiler.Decompile(path, instructionDefinitionsPath));
            if (IsDisposed) return 0;
            await RefreshFieldMonstersAsync(script);
            return ScriptMonsterSpawnReader.Read(script).Count;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine($"Could not load field monsters from '{path}': {exception}");
            return 0;
        }
    }

    /// <summary>
    /// Runs a whole scene on a loop. Selecting a single instruction shows the
    /// scene as it stands at that point; playing the function instead lets it
    /// run — waits, movements, animations and effects in the order the script
    /// writes them — which is the only way to judge what an effect looks like
    /// while it plays.
    /// </summary>
    private void PlayScriptFunction(DecompiledFunction function)
    {
        var script = scriptEditor?.CurrentScript;
        if (script is null) return;
        manualScriptCameraOverride = false;
        StopScriptTimeline();
        if (ScriptSceneStateResolver.BuildFunctionTimeline(
                script,
                function,
                scriptAnimationLibrary,
                systemScript,
                scriptSubject,
                scriptEditor?.SelectedInstruction)
            is not { } timeline)
        {
            return;
        }
        activeScriptEntities = PrepareEntities(timeline.InitialState.Entities);
        scriptEditor?.SetRuntimeEntities(CreateScriptEntityChoices(timeline.InitialState.Entities));
        _ = StartScriptTimelineAsync(timeline);
    }

    /// <summary>
    /// Moves the playback clock to the next command of the scene. The timing the
    /// script authored is untouched — the reader simply stops waiting through a
    /// pause the game fills with a dialogue box the editor does not show.
    /// </summary>
    private void SkipToNextScriptCommand()
    {
        if (scriptTimeline is null) return;
        var next = scriptTimeline.Points
            .Where(point => point.Frame > scriptTimelineFrame)
            .Select(point => (float?)point.Frame)
            .FirstOrDefault();
        scriptTimelineFrame = next ?? GetScriptTimelineDurationFrames(scriptTimeline);
        ApplyTimelinePointsAtCurrentFrame();
    }

    private void ApplySelectedScriptCamera(DecompiledFunction function, DecompiledInstruction instruction)
    {
        var script = scriptEditor?.CurrentScript;
        if (script is null) return;
        manualScriptCameraOverride = false;
        StopScriptTimeline();
        var sceneState = ScriptSceneStateResolver.Resolve(
            script, function, instruction.Index, scriptAnimationLibrary, systemScript, scriptSubject);
        activeScriptEntities = PrepareEntities(sceneState.Entities);
        scriptEditor?.SetRuntimeEntities(CreateScriptEntityChoices(sceneState.Entities));
        if (instruction.Opcode == 2
            && ScriptSceneStateResolver.BuildCallTimeline(
                script, function, instruction, scriptAnimationLibrary, systemScript, scriptSubject) is { } timeline)
        {
            _ = StartScriptTimelineAsync(timeline);
            return;
        }
        if (instruction.Opcode == 47
            && ScriptSceneStateResolver.BuildAnimationCallTimeline(
                script, function, instruction, scriptAnimationLibrary, systemScript, scriptSubject) is { } animationTimeline)
        {
            _ = StartScriptTimelineAsync(animationTimeline);
            return;
        }
        if (instruction.Opcode == 69
            && ScriptSceneStateResolver.BuildPropAnimationTimeline(
                script, function, instruction, scriptAnimationLibrary, systemScript, scriptSubject)
                is { } propTimeline)
        {
            _ = StartScriptTimelineAsync(propTimeline);
            return;
        }
        if (instruction.Opcode == 54
            && ScriptSceneStateResolver.BuildMovementTimeline(
                script, function, instruction, scriptAnimationLibrary, systemScript, scriptSubject)
                is { } movementTimeline)
        {
            _ = StartScriptTimelineAsync(movementTimeline);
            return;
        }
        activeScriptPropAnimations = sceneState.PropAnimations;
        _ = RefreshScriptEntitiesAsync(sceneState.Entities);
        _ = RefreshScriptPropAnimationsAsync(sceneState.PropAnimations);
        RefreshOverlay();
        var state = sceneState.Camera;
        if (!state.HasViewValue) return;

        // Une commande caméra n'est interpolée que si elle porte une durée : le
        // moteur applique une durée nulle immédiatement (aucun mouvement).
        // Boucler une durée nulle « repassée » à 1 s faisait tourner la caméra
        // en continu au lieu de poser le plan.
        if (ScriptCameraStateResolver.HasInterpolation(instruction)
            && ScriptCameraStateResolver.ReadDurationMs(instruction) > 0)
        {
            animationBefore = ScriptSceneStateResolver.ResolveBefore(
                script, function, instruction.Index, scriptAnimationLibrary, systemScript, scriptSubject).Camera;
            animationAfter = state;
            animationDurationMs = ScriptCameraStateResolver.ReadDurationMs(instruction);
            animationEasingType = ScriptCameraStateResolver.ReadInterpolationType(instruction);
            animationElapsed = 0f;
            isCameraAnimating = true;
            cameraAnimationLoops = true;
            cameraPlayButton.Enabled = true;
            CaptureCameraAnimationStart();
            return;
        }

        // Comportement normal : appliquer l'état final directement
        isCameraAnimating = false;
        cameraPlayButton.Enabled = false;
        if (ShouldApplyScriptCamera)
            ApplyCameraState(state, sceneState.Entities);
    }

    // The shot the script last defined. The engine keeps its camera angles until
    // a command changes them; deriving them back from the viewport instead let a
    // manual orbit — or the rounding of a direction through asin/atan2 — leak into
    // the next command, which is how a look-at ended up under the map.
    private (float PitchDegrees, float YawDegrees)? scriptCameraAngles;

    private void ApplyCameraState(
        ScriptCameraState state,
        IReadOnlyDictionary<int, ScriptEntityState>? entities = null)
    {
        var distance = state.Distance is > 0f ? state.Distance.Value : cameraNavigation.Distance;
        distance += state.DistanceDelta ?? 0f;
        var forward = state.Forward ?? cameraNavigation.Forward;
        var roll = (state.RollDegrees ?? cameraNavigation.Roll * 180f / MathF.PI) * MathF.PI / 180f;

        // OP45_4 : les angles autorisés placent l'OEIL autour du point visé.
        // La normalisation shortest-path ne concerne que le trajet d'une
        // interpolation, pas l'état final : elle est appliquée au démarrage de
        // l'animation, pas ici.
        if (state.YawDegrees is not null || state.PitchDegrees is not null)
        {
            var current = CurrentScriptAngles();
            var angles = (
                PitchDegrees: state.PitchDegrees ?? current.PitchDegrees,
                YawDegrees: state.YawDegrees ?? current.YawDegrees);
            scriptCameraAngles = angles;
            forward = ScriptCameraOrbit.ViewDirection(angles.PitchDegrees, angles.YawDegrees);

            if (state.RollDegrees is { } rollDeg)
                roll = rollDeg * MathF.PI / 180f;
        }
        else if (scriptCameraAngles is { } pinned && state.Forward is null)
        {
            // Moving only the look-at keeps the authored angles: the engine
            // rebuilds the eye around the new centre without touching them.
            forward = ScriptCameraOrbit.ViewDirection(pinned.PitchDegrees, pinned.YawDegrees);
        }

        var resolvedTarget = state.Target;
        if (state.TargetEntityId is { } targetEntityId
            && entities is not null
            && entities.TryGetValue(targetEntityId, out var targetEntity))
        {
            var center = targetEntity.Position;
            if (state.SecondaryTargetEntityId is { } secondEntityId
                && entities.TryGetValue(secondEntityId, out var secondEntity))
                center = (center + secondEntity.Position) * 0.5f;
            var entityOffset = state.TargetEntityOffset ?? Vector3.Zero;
            if (state.TargetOffsetUsesEntityRotation)
                entityOffset = Vector3.Transform(
                    entityOffset,
                    Quaternion.CreateFromAxisAngle(
                        Vector3.UnitY, targetEntity.YawDegrees * MathF.PI / 180f));
            resolvedTarget = center + entityOffset;
        }

        var position = state.Position ?? cameraNavigation.Position;
        if (resolvedTarget is { } target)
            position = target - forward * distance;
        else if (state.Position is null && (state.YawDegrees is not null || state.PitchDegrees is not null))
            position = cameraNavigation.Target - forward * distance;

        if (state.AlignEntityId is { } entId && state.AlignYawOffsetDegrees is { } yawOffDeg)
        {
            var entYaw = entities is not null && entities.TryGetValue(entId, out var entity)
                ? entity.YawDegrees
                : 0f;
            var current = CurrentScriptAngles();
            scriptCameraAngles = (state.PitchDegrees ?? current.PitchDegrees, entYaw + yawOffDeg);
            forward = ScriptCameraOrbit.ViewDirection(
                scriptCameraAngles.Value.PitchDegrees, scriptCameraAngles.Value.YawDegrees);
            if (state.RollDegrees is { } rollDeg) roll = rollDeg * MathF.PI / 180f;
        }

        if (state.AngleDeltaDegrees is { } deltaDeg)
        {
            // The deltas are authored in the script's own angle convention, so
            // they are added to the current angles expressed the same way.
            var current = CurrentScriptAngles();
            scriptCameraAngles = (current.PitchDegrees + deltaDeg.X, current.YawDegrees + deltaDeg.Y);
            forward = ScriptCameraOrbit.ViewDirection(
                scriptCameraAngles.Value.PitchDegrees, scriptCameraAngles.Value.YawDegrees);
            roll += deltaDeg.Z * MathF.PI / 180f;
            position = cameraNavigation.Target - forward * distance;
        }

        if (state.TargetOffset is { } offset)
            position = (cameraNavigation.Target + offset) - forward * distance;
        if (state.PositionOffset is { } eyeOffset)
            position = cameraNavigation.Position + eyeOffset;

        cameraDollySmoother.Reset();
        cameraNavigation.SetRoll(roll);
        cameraNavigation.SetView(position, forward, distance);
        if (state.VerticalFieldOfViewDegrees is { } fov && float.IsFinite(fov))
        {
            cameraFieldOfViewDegrees = fov;
            cameraFovSlider.Value = Math.Clamp(
                (int)MathF.Round(fov), cameraFovSlider.Minimum, cameraFovSlider.Maximum);
        }
        UpdateCameraTextFields();
    }

    private async Task RefreshScriptEntitiesAsync(
        IReadOnlyDictionary<int, ScriptEntityState> entities)
    {
        try
        {
            await RefreshScriptEntitiesCoreAsync(entities);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not refresh script entities: {exception}");
        }
    }

    private async Task RefreshScriptEntitiesCoreAsync(
        IReadOnlyDictionary<int, ScriptEntityState> entities)
    {
        var generation = ++scriptEntityRefreshGeneration;
        if (session.Script.GameDataPath is null || graphics is null)
        {
            scriptMonsterInstances = Array.Empty<SceneModelInstance>();
            return;
        }

        foreach (var assetId in entities.Values
                     .Select(value => value.AssetId)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
            if (!loadedModelsByAsset.TryGetValue(assetId, out var model))
            {
                model = await LoadScriptEntityModelAsync(
                    assetId, session.Script.GameDataPath);
                if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
                if (model is null)
                {
                    continue;
                }
                loadedModelsByAsset[assetId] = model;
            }
            if (uploadedModels.All(value =>
                    !value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
                uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
        }

        foreach (var request in entities.Values
                     .Select(value => (
                         value.AssetId,
                         AnimationName: GetRequestedBaseAnimationName(value),
                         AnimationBanks: value.AnimationBanks ?? Array.Empty<string>()))
                     .Where(value => !string.IsNullOrWhiteSpace(value.AssetId)
                         && !string.IsNullOrWhiteSpace(value.AnimationName))
                     .DistinctBy(
                         value => AnimationRequestKey(
                             value.AssetId, value.AnimationName!, value.AnimationBanks),
                         StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
            await EnsureCharacterAnimationAsync(
                request.AssetId,
                request.AnimationName!,
                request.AnimationBanks,
                session.Script.GameDataPath);
        }

        foreach (var facialAssetId in entities.Values
                     .Select(value => value.FacialAssetId)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
            await EnsureFacialTexturesAsync(facialAssetId, session.Script.GameDataPath);
        }

        await EnsureAttachmentModelsAsync(entities, generation);
        if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
        scriptMonsterInstances = entities.Values
            .Where(value => value.HasPosition
                && loadedModelsByAsset.ContainsKey(value.AssetId))
            .OrderBy(value => value.EntityId)
            .Select(value =>
            {
                var model = loadedModelsByAsset[value.AssetId];
                var transform = Matrix4x4.CreateScale(value.Scale)
                    * Matrix4x4.CreateRotationY(value.YawDegrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(value.Position);
                var label = string.IsNullOrWhiteSpace(value.DisplayName)
                    ? value.AssetId
                    : value.DisplayName;
                return new SceneModelInstance(
                    ScriptEntitySceneInstanceId(value.EntityId),
                    value.AssetId,
                    $"{label} (entity {value.EntityId})",
                    model,
                    transform,
                    Vector4.One,
                    Vector3.Zero,
                    SceneElementKind.ScriptCharacter);
            })
            .Concat(CreateAttachmentInstances(entities))
            .ToArray();
        RefreshRenderInstances(
            uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads what the actors of the scene carry. t_attach.tbl gives a character
    /// its default equipment — a weapon on a hand node, a scabbard on a back one —
    /// which the game draws attached to that node of the animated skeleton.
    /// </summary>
    private async Task EnsureAttachmentModelsAsync(
        IReadOnlyDictionary<int, ScriptEntityState> entities,
        int generation)
    {
        if (session.Script.GameDataPath is not { } gameDataPath
            || scriptAnimationLibrary is null
            || attachTable.Count == 0)
        {
            return;
        }
        foreach (var assetId in entities.Values
                     .SelectMany(FindAttachments)
                     .Select(value => value.ModelAssetId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
            if (!loadedModelsByAsset.TryGetValue(assetId, out var model))
            {
                model = await LoadScriptEntityModelAsync(assetId, gameDataPath);
                if (generation != scriptEntityRefreshGeneration || IsDisposed) return;
                if (model is null) continue;
                loadedModelsByAsset[assetId] = model;
            }
            if (graphics is not null && uploadedModels.All(value =>
                    !value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
            {
                uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
            }
        }
    }

    /// <summary>
    /// What an actor carries right now: its default loadout from t_attach.tbl,
    /// overridden by what the script attached, cleared or hid on each node
    /// (OP37 / OP32_0). A node the script emptied stays empty, and a node it
    /// hid keeps its model but is not drawn.
    /// </summary>
    private IReadOnlyList<ScriptAttachment> FindAttachments(ScriptEntityState entity)
    {
        if (string.IsNullOrWhiteSpace(entity.AssetId)) return Array.Empty<ScriptAttachment>();
        var characterId = scriptAnimationLibrary?.FindCharacterByModel(entity.AssetId);
        var defaults = characterId is { } id
            ? attachTable.FindByCharacter(id)
            : Array.Empty<ScriptAttachment>();
        if (entity.Attachments is not { Count: > 0 } runtime) return defaults;

        var byNode = defaults.ToDictionary(
            value => value.AttachPoint, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in runtime)
        {
            if (!pair.Value.Visible)
            {
                byNode.Remove(pair.Key);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(pair.Value.ModelAssetId))
            {
                byNode[pair.Key] = new ScriptAttachment(
                    characterId ?? 0, pair.Value.ModelAssetId, pair.Key);
            }
            // Visible with no model of its own: the default equipment shows.
        }
        return byNode.Values.ToArray();
    }

    /// <summary>Local placement a script gave an attachment, if any.</summary>
    private static Matrix4x4 AttachmentPlacement(ScriptEntityState? owner, string attachPoint)
    {
        if (owner?.Attachments is null
            || !owner.Attachments.TryGetValue(attachPoint, out var attachment)
            || (attachment.Offset == Vector3.Zero
                && attachment.RotationDegrees == Vector3.Zero
                && attachment.Scale == Vector3.One))
        {
            return Matrix4x4.Identity;
        }
        const float toRadians = MathF.PI / 180f;
        return Matrix4x4.CreateScale(attachment.Scale)
            * Matrix4x4.CreateFromYawPitchRoll(
                attachment.RotationDegrees.Y * toRadians,
                attachment.RotationDegrees.X * toRadians,
                attachment.RotationDegrees.Z * toRadians)
            * Matrix4x4.CreateTranslation(attachment.Offset);
    }

    private IEnumerable<SceneModelInstance> CreateAttachmentInstances(
        IReadOnlyDictionary<int, ScriptEntityState> entities)
    {
        attachmentOwners.Clear();
        var instanceId = AttachmentSceneInstanceBase;
        foreach (var entity in entities.Values
                     .Where(value => value.HasPosition)
                     .OrderBy(value => value.EntityId))
        {
            foreach (var attachment in FindAttachments(entity))
            {
                if (!loadedModelsByAsset.TryGetValue(attachment.ModelAssetId, out var model)) continue;
                var id = instanceId++;
                attachmentOwners[id] = (
                    ScriptEntitySceneInstanceId(entity.EntityId),
                    entity.EntityId,
                    attachment.AttachPoint);
                yield return new SceneModelInstance(
                    id,
                    attachment.ModelAssetId,
                    $"{attachment.ModelAssetId} on {attachment.AttachPoint}",
                    model,
                    Matrix4x4.Identity,
                    Vector4.One,
                    Vector3.Zero,
                    SceneElementKind.ScriptCharacter);
            }
        }
    }

    /// <summary>Identity space of attachment instances, disjoint from the actors'.</summary>
    private const int AttachmentSceneInstanceBase = int.MinValue + 200000;

    private static int ScriptEntitySceneInstanceId(int entityId)
        => int.MinValue + (entityId - short.MinValue);

    private async Task<CpuModel?> LoadScriptEntityModelAsync(
        string assetId,
        string gameDataPath)
    {
        var sourceAssetId = assetId.Equals(
            ScriptEntityReferences.PlaceholderAssetId,
            StringComparison.OrdinalIgnoreCase)
                ? ScriptEntityReferences.PlaceholderSourceAssetId
                : assetId;
        var load = await Task.Run(() => projectLoader.LoadAsset(sourceAssetId, gameDataPath));
        if (load.Status != AssetModelLoadStatus.Loaded || load.Model is null)
        {
            Debug.WriteLine(
                $"Could not load script entity asset '{sourceAssetId}'"
                + $" for '{assetId}': {load.Error}");
            return null;
        }
        return assetId.Equals(
            ScriptEntityReferences.PlaceholderAssetId,
            StringComparison.OrdinalIgnoreCase)
                ? CreateUntexturedEntityPlaceholder(load.Model)
                : load.Model;
    }

    private static CpuModel CreateUntexturedEntityPlaceholder(CpuModel source)
    {
        var materials = source.Materials.Select(material => material with
        {
            BaseColor = new Vector4(0.58f, 0.62f, 0.68f, 1f),
            BaseColorTextureIndex = null,
            SourceParameters = new Dictionary<string, float[]>(),
            SourceTextureReferences = new Dictionary<string, string>(),
            TextureBindings = new Dictionary<string, int>(),
            RenderPassType = null,
            EffectAssetName = null,
            RenderPassState = null,
            EffectSwitches = null,
            RenderPhase = CpuMaterialRenderPhase.Opaque,
            ResolvedRenderPassName = null,
            EffectProgram = null,
        }).ToArray();
        return source with
        {
            AssetId = ScriptEntityReferences.PlaceholderAssetId,
            Materials = materials,
            Textures = Array.Empty<CpuTexture>(),
            EmbeddedAnimation = null,
        };
    }

    private static IReadOnlyList<ScriptEntityChoice> CreateScriptEntityChoices(
        IReadOnlyDictionary<int, ScriptEntityState> entities)
        => entities.Values
            .OrderBy(value => value.EntityId)
            .Select(value => new ScriptEntityChoice(
                value.EntityId, value.AssetId, value.DisplayName))
            .ToArray();

    private static string? GetRequestedBaseAnimationName(ScriptEntityState entity)
        => entity.AnimationSlots is not null
            && entity.AnimationSlots.TryGetValue(0, out var animation)
            && !string.IsNullOrWhiteSpace(animation.Name)
                ? animation.Name
                : !string.IsNullOrWhiteSpace(entity.InitialAnimation)
                    ? entity.InitialAnimation
                    : null;

    private static bool TryGetBaseAnimation(
        ScriptEntityState entity,
        out ScriptEntityAnimation animation)
    {
        if (entity.AnimationSlots is not null
            && entity.AnimationSlots.TryGetValue(0, out animation!)
            && !string.IsNullOrWhiteSpace(animation.Name))
        {
            return true;
        }
        var requested = GetRequestedBaseAnimationName(entity);
        if (string.IsNullOrWhiteSpace(requested))
        {
            animation = null!;
            return false;
        }
        animation = new ScriptEntityAnimation(
            0,
            requested,
            true,
            0, 0, 0, 0,
            0f, -1f, -1f, -1f,
            0);
        return true;
    }

    private void ClearScriptEntityPreview()
    {
        StopScriptTimeline();
        activeScriptEntities = new Dictionary<int, ScriptEntityState>();
        activeScriptPropAnimations =
            new Dictionary<string, ScriptPropAnimation>(StringComparer.Ordinal);
        if (selection is { Kind: SceneElementKind.ScriptCharacter })
            selection = null;
        scriptEditor?.SetRuntimeEntities(Array.Empty<ScriptEntityChoice>());
        _ = RefreshScriptEntitiesAsync(activeScriptEntities);
        RefreshOverlay();
    }

    private bool TryGetCharacterAnimation(
        string assetId,
        string animationName,
        out CpuAnimationClip clip)
    {
        if (loadedCharacterAnimations.TryGetValue(assetId, out var clips)
            && clips.TryGetValue(animationName, out var found))
        {
            clip = found;
            return true;
        }
        clip = null!;
        return false;
    }

    private async Task EnsureFacialTexturesAsync(
        string facialAssetId,
        string gameDataPath)
    {
        if (graphics is null
            || loadedFacialTextures.ContainsKey(facialAssetId)
            || unavailableFacialTextures.Contains(facialAssetId))
        {
            return;
        }
        try
        {
            var source = await Task.Run(() =>
                projectLoader.LoadFacialTextures(facialAssetId, gameDataPath));
            if (IsDisposed || graphics is null) return;
            loadedFacialTextures.Add(
                facialAssetId,
                new D3D11FacialTextureResources(
                    source, new D3D11ModelUploader(graphics.Device)));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            unavailableFacialTextures.Add(facialAssetId);
            Debug.WriteLine(
                $"Could not load facial textures '{facialAssetId}': {exception}");
        }
    }

    /// <summary>
    /// Selects the facial overlay each face material samples for the entity's
    /// current expression. A character model already binds its face texture on
    /// <c>DiffuseMapSampler</c> and its frame-0 overlay on
    /// <c>DiffuseMap2Sampler</c>; playing an expression only swaps that second
    /// map, per game material ID: 11 and 12 are the two eyes (mirrored by the
    /// "#b" tag), 13 is the mouth and 14 the complexion. The base face and hair
    /// maps are never replaced — the FC package only ships copies of them for
    /// the engine's costume swap.
    /// </summary>
    private IReadOnlyDictionary<int, D3D11MaterialTextureOverride>?
        CreateFacialTextureOverrides(ScriptEntityState entity, float seconds)
    {
        if (string.IsNullOrWhiteSpace(entity.FacialAssetId)
            || !loadedFacialTextures.TryGetValue(
                entity.FacialAssetId, out var textures))
        {
            return null;
        }
        var pose = (entity.FacialExpression ?? ScriptFacialExpression.Neutral)
            .Evaluate(seconds);
        var overrides = new Dictionary<int, D3D11MaterialTextureOverride>();
        Add(11, textures.Find('e', pose.PrimaryEyes));
        Add(12, textures.Find('e', pose.SecondaryEyes));
        Add(13, textures.Find('m', pose.Mouth));
        Add(14, textures.Find('c', pose.Complexion));
        return overrides.Count == 0 ? null : overrides;

        void Add(int materialId, Vortice.Direct3D11.ID3D11ShaderResourceView? overlay)
        {
            if (overlay is not null)
                overrides.Add(
                    materialId, new D3D11MaterialTextureOverride(null, overlay));
        }
    }

    private async Task EnsureCharacterAnimationAsync(
        string modelAssetId,
        string animationName,
        IReadOnlyList<string> animationBanks,
        string gameDataPath)
    {
        if (TryGetCharacterAnimation(modelAssetId, animationName, out _)) return;
        var requestKey = AnimationRequestKey(
            modelAssetId, animationName, animationBanks);
        if (unavailableCharacterAnimations.Contains(requestKey)) return;
        if (!loadedModelsByAsset.TryGetValue(modelAssetId, out var model)
            || model.Skeleton is null)
        {
            return;
        }

        var sourceAssets = animationBanks
            .Reverse()
            .Append(modelAssetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var errors = new List<string>();
        foreach (var sourceAssetId in sourceAssets)
        {
            var load = await Task.Run(
                () => projectLoader.LoadAnimationAsset(
                    sourceAssetId, animationName, gameDataPath));
            if (load.Status == AssetAnimationLoadStatus.Loaded && load.Clip is not null)
            {
                if (!loadedCharacterAnimations.TryGetValue(modelAssetId, out var clips))
                {
                    clips = new Dictionary<string, CpuAnimationClip>(
                        StringComparer.OrdinalIgnoreCase);
                    loadedCharacterAnimations.Add(modelAssetId, clips);
                }
                clips[animationName] = load.Clip;
                return;
            }
            errors.Add($"{sourceAssetId}: {load.Error}");
        }
        unavailableCharacterAnimations.Add(requestKey);
        Debug.WriteLine(
            $"Could not load script animation '{modelAssetId}:{animationName}'"
            + $" from [{string.Join(", ", sourceAssets)}]: {string.Join(" | ", errors)}");
    }

    private static string AnimationRequestKey(
        string modelAssetId,
        string animationName,
        IReadOnlyList<string> animationBanks)
        => $"{modelAssetId}\0{animationName}\0"
            + string.Join("\0", animationBanks);

    private async Task RefreshScriptPropAnimationsAsync(
        IReadOnlyDictionary<string, ScriptPropAnimation> animations)
    {
        if (session.Script.GameDataPath is null) return;
        try
        {
            foreach (var animation in animations.Values)
            {
                await EnsurePropAnimationAsync(animation, session.Script.GameDataPath);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not refresh script prop animations: {exception}");
        }
    }

    private async Task EnsurePropAnimationAsync(
        ScriptPropAnimation animation,
        string gameDataPath)
    {
        var assets = sceneInstances
            .Where(value => value.Name.Equals(
                animation.PropName, StringComparison.Ordinal))
            .Select(value => value.AssetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var assetId in assets)
        {
            var key = PropAnimationKey(assetId, animation.AnimationName);
            if (loadedPropAnimations.ContainsKey(key)
                || unavailablePropAnimations.Contains(key))
            {
                continue;
            }
            var load = await Task.Run(
                () => projectLoader.LoadAnimationAsset(
                    assetId, animation.AnimationName, gameDataPath));
            if (load.Status == AssetAnimationLoadStatus.Loaded && load.Clip is not null)
            {
                loadedPropAnimations.Add(
                    key,
                    new LoadedPropAnimation(load.Clip, Loop: false, Reverse: false));
            }
            else if (loadedModelsByAsset.TryGetValue(assetId, out var model)
                && model.EmbeddedAnimation is { } embeddedClip)
            {
                var actions = await Task.Run(
                    () => projectLoader.LoadObjectAnimationInfo(assetId, gameDataPath));
                if (actions.TryGetValue(animation.AnimationName, out var action))
                {
                    loadedPropAnimations.Add(
                        key,
                        new LoadedPropAnimation(
                            CpuAnimationClipSegment.FromFrames(
                                embeddedClip,
                                action.Name,
                                action.StartFrame,
                                action.EndFrame),
                            action.Loop,
                            action.Reverse));
                }
                else
                {
                    unavailablePropAnimations.Add(key);
                    Debug.WriteLine(
                        $"Object animation action '{animation.AnimationName}' is not declared"
                        + $" in the .inf metadata for '{assetId}'.");
                }
            }
            else
            {
                unavailablePropAnimations.Add(key);
                Debug.WriteLine(
                    $"Could not load prop animation '{animation.PropName}'"
                    + $" ({assetId}:{animation.AnimationName}): {load.Error}");
            }
        }
    }

    private static string PropAnimationKey(string assetId, string animationName)
        => $"{assetId}\0{animationName}";

    private async Task StartScriptTimelineAsync(ScriptSceneTimeline timeline)
    {
        var generation = ++scriptTimelineGeneration;
        try
        {
            await EnsureTimelineResourcesAsync(timeline, generation);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not prepare script timeline '{timeline.FunctionName}': {exception}");
        }
        if (generation != scriptTimelineGeneration || IsDisposed) return;

        timeline = ResolveAnimationWaits(timeline);
        scriptTimeline = timeline;
        scriptTimelinePointIndex = 0;
        scriptTimelineFrame = 0f;
        isCameraAnimating = false;
        ApplyTimelineSceneState(timeline.InitialState, applyCamera: ShouldApplyScriptCamera);
        ApplyTimelinePointsAtCurrentFrame();
        Text = $"{baseTitle} - previewing call: {timeline.FunctionName}";
    }

    private async Task VerifyScriptAnimationReplaySmokeAsync()
    {
        var script = scriptEditor?.CurrentScript;
        if (scriptAnimationLibrary is null || script is null) return;
        if (systemScript is not null)
        {
            var systemCall = script.Functions.Where(value => value.IsCode)
                .SelectMany(function => function.Instructions
                    .Where(instruction => instruction.Opcode == 2
                        && instruction.Arguments.FirstOrDefault()?.IntValue == 0x0A)
                    .Select(instruction => (Function: function, Instruction: instruction)))
                .FirstOrDefault(value =>
                {
                    var functionName = value.Instruction.Arguments.Count >= 2
                        ? System.Text.Encoding.ASCII.GetString(
                            value.Instruction.Arguments[1].Raw).TrimEnd('\0')
                        : string.Empty;
                    return systemScript.Functions.Any(candidate =>
                        candidate.IsCode
                        && candidate.Name.Equals(functionName, StringComparison.Ordinal));
                });
            if (systemCall.Instruction is not null)
            {
                var systemTimeline = ScriptSceneStateResolver.BuildCallTimeline(
                    script,
                    systemCall.Function,
                    systemCall.Instruction,
                    scriptAnimationLibrary,
                    systemScript);
                var expectedFunction = System.Text.Encoding.ASCII.GetString(
                    systemCall.Instruction.Arguments[1].Raw).TrimEnd('\0');
                if (systemTimeline is null
                    || !systemTimeline.FunctionName.Equals(
                        expectedFunction, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "CALL variant 0x0A did not execute its system.dat function.");
                }
            }
        }
        var attemptedCalls = new List<string>();
        var calls = script.Functions.Where(value => value.IsCode)
            .SelectMany(function => function.Instructions
                .Where(instruction => instruction.Opcode == 47)
                .Select(instruction => (Function: function, Instruction: instruction)))
            .OrderByDescending(value =>
                value.Function.Name.Equals("EV_C05E14S02", StringComparison.Ordinal)
                && value.Instruction.Arguments.Count >= 3
                && value.Instruction.Arguments[0].IntValue == 1005
                && System.Text.Encoding.ASCII.GetString(
                    value.Instruction.Arguments[2].Raw).TrimEnd('\0').Equals(
                        "AniEv0006", StringComparison.Ordinal))
            .ThenByDescending(value =>
                value.Instruction.Arguments.Count >= 3
                && value.Instruction.Arguments[0].IntValue == 0
                && System.Text.Encoding.ASCII.GetString(
                    value.Instruction.Arguments[2].Raw).TrimEnd('\0').Equals(
                        "AniEv0555", StringComparison.Ordinal))
            .ThenByDescending(value =>
                value.Instruction.Arguments.Count >= 3
                && System.Text.Encoding.ASCII.GetString(
                    value.Instruction.Arguments[2].Raw).TrimEnd('\0').Equals(
                        "AniEv0555", StringComparison.Ordinal))
            .ToArray();
        var idZeroCall = calls.FirstOrDefault(value =>
            value.Instruction.Arguments.Count >= 3
            && value.Instruction.Arguments[0].IntValue == 0);
        if (idZeroCall.Instruction is not null)
        {
            var idZeroState = ScriptSceneStateResolver.Resolve(
                script,
                idZeroCall.Function,
                idZeroCall.Instruction.Index,
                scriptAnimationLibrary,
                systemScript);
            if (!idZeroState.Entities.TryGetValue(0, out var rean)
                || rean.AssetId.Equals(
                    ScriptEntityReferences.PlaceholderAssetId,
                    StringComparison.OrdinalIgnoreCase))
            {
                var mapping = scriptAnimationLibrary.TryGetCharacter(0, out var character)
                    ? $"t_name model='{character.ModelAssetId}', ANI='{character.AnimationScript}'"
                    : $"no t_name character 0 mapping; {scriptAnimationLibrary.NameTableDiagnostics}";
                throw new InvalidOperationException(
                    "Entity ID 0 did not resolve to Rean's concrete character asset"
                    + $" ({mapping}; runtime asset='{rean?.AssetId ?? "<missing>"}').");
            }
        }
        var mistyCall = calls.FirstOrDefault(value =>
            value.Function.Name.Equals("EV_C05E14S02", StringComparison.Ordinal)
            && value.Instruction.Arguments.Count >= 3
            && value.Instruction.Arguments[0].IntValue == 1005
            && System.Text.Encoding.ASCII.GetString(
                value.Instruction.Arguments[2].Raw).TrimEnd('\0').Equals(
                    "AniEv0006", StringComparison.Ordinal));
        var validationCalls = mistyCall.Instruction is not null
            ? new[] { mistyCall }
            : calls;
        foreach (var call in validationCalls)
        {
            var requestedFunction = call.Instruction.Arguments.Count >= 3
                ? System.Text.Encoding.ASCII.GetString(
                    call.Instruction.Arguments[2].Raw).TrimEnd('\0')
                : "<invalid OP47>";
            var timeline = ScriptSceneStateResolver.BuildAnimationCallTimeline(
                script, call.Function, call.Instruction, scriptAnimationLibrary, systemScript, scriptSubject);
            if (timeline is null)
            {
                if (mistyCall.Instruction is not null)
                {
                    var before = ScriptSceneStateResolver.ResolveBefore(
                        script,
                        call.Function,
                        call.Instruction.Index,
                        scriptAnimationLibrary,
                        systemScript);
                    before.Entities.TryGetValue(1005, out var misty);
                    var functions = misty is null
                        ? Array.Empty<string>()
                        : scriptAnimationLibrary.GetFunctionNames(misty);
                    var mapping = scriptAnimationLibrary.TryGetCharacter(
                        1005, out var mistyDefinition)
                            ? $"model='{mistyDefinition.ModelAssetId}',"
                                + $" ani='{mistyDefinition.AnimationScript}'"
                            : "no t_name mapping";
                    throw new InvalidOperationException(
                        "Misty AniEv0006 could not resolve its ANI function: "
                        + $"{mapping}; runtime model='{misty?.AssetId ?? "<missing>"}',"
                        + $" script='{misty?.ScriptFile ?? "<missing>"}',"
                        + $" functions={functions.Count},"
                        + $" contains AniEv0006={functions.Contains("AniEv0006", StringComparer.Ordinal)};"
                        + $" {scriptAnimationLibrary.NameTableDiagnostics}");
                }
                attemptedCalls.Add($"{requestedFunction}: ANI function unresolved");
                continue;
            }
            var generation = ++scriptTimelineGeneration;
            await EnsureTimelineResourcesAsync(timeline, generation);
            var animationPoint = timeline.Points.LastOrDefault(value =>
                value.IsExternalScript && value.Instruction.Opcode == 34);
            if (animationPoint?.SubjectEntityId is not { } entityId
                || !animationPoint.After.Entities.TryGetValue(entityId, out var entity)
                || !TryGetBaseAnimation(entity, out var animation)
                || !TryGetCharacterAnimation(entity.AssetId, animation.Name, out var clip))
            {
                attemptedCalls.Add(
                    $"{timeline.FunctionName}: no loadable OP34 character clip");
                continue;
            }
            if (mistyCall.Instruction is not null
                && (entity.AnimationBanks is null
                    || !entity.AnimationBanks.Contains(
                        "C_NPC017_EV0", StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Misty's AniEv0006 preview did not retain the C_NPC017_EV0"
                    + " animation bank attached by Ani_EV0_Load.");
            }
            Console.WriteLine(
                $"Animation smoke timeline: {timeline.FunctionName},"
                + $" {timeline.Points.Count} points,"
                + $" duration={GetScriptTimelineDurationFrames(timeline):R} frames,"
                + $" animation={animation.Name}@{animation.StartFrame},"
                + $" banks=[{string.Join(", ", entity.AnimationBanks ?? Array.Empty<string>())}]");
            var skeleton = loadedModelsByAsset[entity.AssetId].Skeleton
                ?? throw new InvalidOperationException(
                    $"Animation model '{entity.AssetId}' has no skeleton.");
            var pose = poseEvaluator.Evaluate(
                skeleton,
                clip,
                (clip.StartTime + clip.EndTime) * 0.5f,
                CpuAnimationUnboundTargetBehavior.Ignore);
            var bindPose = poseEvaluator.Evaluate(skeleton, null, 0f);
            if (!pose.SkinMatrices.Zip(
                    bindPose.SkinMatrices,
                    static (animated, bind) => !MatrixNearlyEqual(animated, bind))
                .Any(static changed => changed))
            {
                var skeletonNames = skeleton.Joints
                    .Select(value => value.Name)
                    .ToHashSet(StringComparer.Ordinal);
                var boundChannels = clip.Channels.Count(value =>
                    skeletonNames.Contains(value.TargetName));
                throw new InvalidOperationException(
                    $"Animation '{clip.Name}' produced the bind pose for '{entity.AssetId}'"
                    + $" ({boundChannels}/{clip.Channels.Count} channels target model joints).");
            }
            activeScriptEntities = animationPoint.After.Entities;
            await RefreshScriptEntitiesCoreAsync(activeScriptEntities);
            scriptTimeline = timeline;
            scriptTimelineFrame = animation.StartFrame
                + clip.Duration * ScriptWaitDuration.PreviewFramesPerSecond * 0.5f;
            RefreshAnimationPoses();
            var renderedCharacter = instances.FirstOrDefault(value =>
                value.SceneInstanceId == ScriptEntitySceneInstanceId(entityId));
            if (renderedCharacter?.SkinMatrices is not { Count: > 0 } renderedPose
                || !renderedPose.Zip(
                        bindPose.SkinMatrices,
                        static (animated, bind) => !MatrixNearlyEqual(animated, bind))
                    .Any(static changed => changed))
            {
                throw new InvalidOperationException(
                    $"Animation '{clip.Name}' was evaluated but not attached to rendered"
                    + $" entity {entityId} ({entity.AssetId}).");
            }
            if (!renderedCharacter.Model.Meshes
                    .SelectMany(value => value.Primitives)
                    .Any(D3D11Viewport.SupportsSkinningInputs))
            {
                var formats = renderedCharacter.Model.Meshes
                    .SelectMany(value => value.Primitives)
                    .SelectMany(value => value.VertexBuffers)
                    .SelectMany(value => value.Attributes)
                    .Where(value => value.Semantic is VertexSemantic.JointIndices
                        or VertexSemantic.JointWeights)
                    .Select(value => $"{value.Semantic}:{value.SourceFormat}")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                throw new InvalidOperationException(
                    $"Rendered entity {entityId} ({entity.AssetId}) has no primitive"
                    + " accepted by the skinned vertex shader"
                    + $" (inputs: {string.Join(", ", formats)}).");
            }
            if (viewport is null)
                throw new InvalidOperationException("Animation smoke has no D3D11 viewport.");
            cameraNavigation.Focus(entity.Position + Vector3.UnitY, 2.5f);
            var animatedInstances = instances;
            viewport.Render(animatedInstances, CreateCamera(), verticalSync: false);
            var animatedPixels = viewport.CaptureBackBufferBgra();
            var bindInstances = animatedInstances.Select(value =>
                value.SceneInstanceId == renderedCharacter.SceneInstanceId
                    ? value with { SkinMatrices = null }
                    : value).ToArray();
            viewport.Render(bindInstances, CreateCamera(), verticalSync: false);
            var bindPixels = viewport.CaptureBackBufferBgra();
            instances = animatedInstances;
            var changedPixelBytes = animatedPixels.Zip(
                    bindPixels,
                    static (animated, bind) => animated != bind)
                .Count(static changed => changed);
            if (changedPixelBytes == 0)
            {
                throw new InvalidOperationException(
                    $"Animation '{clip.Name}' changes CPU-skinned vertices but produced"
                    + " the same D3D11 frame as the bind pose.");
            }
            if (mistyCall.Instruction is not null)
            {
                StopScriptTimeline();
                ApplySelectedScriptCamera(mistyCall.Function, mistyCall.Instruction);
                for (var attempt = 0;
                     attempt < 100
                     && (scriptTimeline is null
                         || !scriptTimeline.FunctionName.Equals(
                             "AniEv0006", StringComparison.Ordinal));
                     attempt++)
                {
                    await Task.Delay(25);
                }
                if (scriptTimeline is null
                    || !scriptTimeline.FunctionName.Equals(
                        "AniEv0006", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Selecting Misty's Script_CallAniFun block did not start"
                        + " the resolved npc017.dat/AniEv0006 timeline.");
                }
                var interactiveAnimationPoint = scriptTimeline.Points.Last(value =>
                    value.IsExternalScript
                    && value.Instruction.Opcode == 34
                    && value.SubjectEntityId == 1005);
                scriptTimelineFrame = interactiveAnimationPoint.Frame;
                scriptTimelinePointIndex = 0;
                ApplyTimelineSceneState(
                    scriptTimeline.InitialState, applyCamera: false);
                ApplyTimelinePointsAtCurrentFrame();
                await RefreshScriptEntitiesCoreAsync(activeScriptEntities);
                RefreshAnimationPoses();
                var interactiveMisty = instances.FirstOrDefault(value =>
                    value.SceneInstanceId == ScriptEntitySceneInstanceId(1005));
                if (interactiveMisty?.SkinMatrices is not { Count: > 0 } interactivePose
                    || !interactivePose.Zip(
                            bindPose.SkinMatrices,
                            static (animated, bind) => !MatrixNearlyEqual(animated, bind))
                        .Any(static changed => changed))
                {
                    throw new InvalidOperationException(
                        "The interactive Misty CallAniFun preview started, but its"
                        + " rendered entity remained in the bind pose.");
                }
            }
            return;
        }
        throw new InvalidOperationException(
            "No OP47 animation call resolved to a loadable character clip."
            + (attemptedCalls.Count == 0
                ? string.Empty
                : $"{Environment.NewLine}{string.Join(Environment.NewLine, attemptedCalls.Take(12))}"));
    }

    private async Task VerifyScriptPropAnimationSmokeAsync()
    {
        var script = scriptEditor?.CurrentScript;
        if (script is null || session.Script.GameDataPath is null) return;
        var calls = script.Functions.Where(value => value.IsCode)
            .SelectMany(function => function.Instructions
                .Where(instruction => instruction.Opcode == 69
                    && instruction.Arguments.Count >= 2)
                .Select(instruction => (Function: function, Instruction: instruction)))
            .Where(value =>
            {
                var propName = ScriptSceneStateResolver.ReadInstructionString(
                    value.Instruction.Arguments[0]);
                return sceneInstances.Any(instance =>
                    instance.Name.Equals(propName, StringComparison.Ordinal));
            })
            .OrderByDescending(value =>
                ScriptSceneStateResolver.ReadInstructionString(
                    value.Instruction.Arguments[0]).Equals(
                        "door07", StringComparison.Ordinal)
                && ScriptSceneStateResolver.ReadInstructionString(
                    value.Instruction.Arguments[1]).Equals(
                        "open1", StringComparison.Ordinal))
            .ToArray();
        foreach (var call in calls)
        {
            var propName = ScriptSceneStateResolver.ReadInstructionString(
                call.Instruction.Arguments[0]);
            var animationName = ScriptSceneStateResolver.ReadInstructionString(
                call.Instruction.Arguments[1]);
            var prop = sceneInstances.First(value =>
                value.Name.Equals(propName, StringComparison.Ordinal));
            await EnsurePropAnimationAsync(
                new ScriptPropAnimation(propName, animationName, 0, false),
                session.Script.GameDataPath);
            if (!loadedPropAnimations.TryGetValue(
                    PropAnimationKey(prop.AssetId, animationName), out var loadedAnimation)
                || !loadedModelsByAsset.TryGetValue(prop.AssetId, out var model)
                || model.SceneNodes is not { Count: > 0 } nodes)
            {
                continue;
            }
            var clip = loadedAnimation.Clip;
            var start = sceneAnimationEvaluator.Evaluate(nodes, clip, clip.StartTime);
            var middle = sceneAnimationEvaluator.Evaluate(
                nodes, clip, (clip.StartTime + clip.EndTime) * 0.5f);
            if (!start.WorldTransforms.Zip(
                    middle.WorldTransforms,
                    static (left, right) => !MatrixNearlyEqual(left, right))
                .Any(static changed => changed))
            {
                continue;
            }
            var affectedMeshes = model.Meshes.Where(mesh =>
                    mesh.Purpose == CpuMeshPurpose.Render
                    && (uint)mesh.SceneNodeIndex < middle.WorldTransforms.Count
                    && !MatrixNearlyEqual(
                        start.WorldTransforms[mesh.SceneNodeIndex],
                        middle.WorldTransforms[mesh.SceneNodeIndex]))
                .ToArray();
            if (affectedMeshes.Length == 0)
            {
                var changedNodes = nodes.Select((node, index) => (node, index))
                    .Where(value => !MatrixNearlyEqual(
                        start.WorldTransforms[value.index],
                        middle.WorldTransforms[value.index]))
                    .Select(value => $"{value.index}:{value.node.Name}")
                    .ToArray();
                var meshBindings = model.Meshes
                    .Select(mesh => $"{mesh.Name}->{mesh.SceneNodeIndex}")
                    .ToArray();
                throw new InvalidOperationException(
                    $"OP69 '{propName}:{animationName}' changes scene nodes but no rendered mesh."
                    + $"{Environment.NewLine}Changed nodes: {string.Join(", ", changedNodes)}"
                    + $"{Environment.NewLine}Mesh bindings: {string.Join(", ", meshBindings)}");
            }

            StopScriptTimeline();
            ApplySelectedScriptCamera(call.Function, call.Instruction);
            for (var attempt = 0;
                 attempt < 100
                 && (scriptTimeline is null
                     || !scriptTimeline.FunctionName.Equals(
                         animationName, StringComparison.Ordinal));
                 attempt++)
            {
                await Task.Delay(25);
            }
            if (scriptTimeline is null)
                throw new InvalidOperationException(
                    $"Selecting OP69 for '{propName}:{animationName}' did not start playback.");
            if (scriptTimeline.LoopPlayback)
                throw new InvalidOperationException(
                    $"OP69 '{propName}:{animationName}' incorrectly started as a looping timeline.");
            scriptTimelineFrame = clip.Duration
                * ScriptWaitDuration.PreviewFramesPerSecond * 0.5f;
            scriptTimelinePointIndex = 0;
            ApplyTimelineSceneState(scriptTimeline.InitialState, applyCamera: false);
            ApplyTimelinePointsAtCurrentFrame();
            RefreshAnimationPoses();
            var rendered = instances.FirstOrDefault(value =>
                value.SceneInstanceId == prop.Id);
            if (rendered?.SceneNodeTransforms is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"OP69 '{propName}:{animationName}' did not reach the rendered prop.");
            if (viewport is null)
                throw new InvalidOperationException("Prop animation smoke has no D3D11 viewport.");
            var propBounds = new SceneBoundsCalculator().Calculate(new[] { prop });
            cameraNavigation.Focus(
                propBounds.HasGeometry ? propBounds.Center : prop.Transform.Translation,
                Math.Max(propBounds.Radius * 2.5f, 1f));
            var animatedInstances = instances;
            viewport.Render(animatedInstances, CreateCamera(), verticalSync: false);
            var animatedPixels = viewport.CaptureBackBufferBgra();
            var startInstances = animatedInstances.Select(value =>
                value.SceneInstanceId == prop.Id
                    ? value with { SceneNodeTransforms = start.WorldTransforms }
                    : value).ToArray();
            viewport.Render(startInstances, CreateCamera(), verticalSync: false);
            var startPixels = viewport.CaptureBackBufferBgra();
            instances = animatedInstances;
            if (!animatedPixels.Zip(startPixels, static (animated, initial) =>
                    animated != initial).Any(static changed => changed))
            {
                throw new InvalidOperationException(
                    $"OP69 '{propName}:{animationName}' changes CPU scene nodes but"
                    + " produces the same D3D11 frame.");
            }
            StopScriptTimeline();
            var following = call.Function.Instructions.FirstOrDefault(value =>
                value.Index > call.Instruction.Index);
            if (following is not null)
            {
                var finalState = ScriptSceneStateResolver.Resolve(
                    script,
                    call.Function,
                    following.Index,
                    scriptAnimationLibrary,
                    systemScript);
                if (!finalState.PropAnimations.TryGetValue(
                        propName, out var finalAnimation)
                    || !finalAnimation.AnimationName.Equals(
                        animationName, StringComparison.Ordinal)
                    || !finalAnimation.HoldFinalFrame)
                {
                    throw new InvalidOperationException(
                        $"OP69 '{propName}:{animationName}' did not persist its final"
                        + " frame in the following script state.");
                }
            }
            return;
        }
        // A script that animates no prop (an animation or craft script) has
        // nothing to check here: the smoke covers whatever the file contains.
        if (script.Functions.Where(value => value.IsCode)
            .SelectMany(value => value.Instructions)
            .Any(value => value.Opcode == 69))
        {
            throw new InvalidOperationException(
                "No OP69 instruction resolved to a changing prop animation in this scene.");
        }
        Console.WriteLine("Prop animation smoke: skipped, this script animates no prop.");
    }

    private static bool MatrixNearlyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(left.M11 - right.M11) <= epsilon
            && MathF.Abs(left.M12 - right.M12) <= epsilon
            && MathF.Abs(left.M13 - right.M13) <= epsilon
            && MathF.Abs(left.M14 - right.M14) <= epsilon
            && MathF.Abs(left.M21 - right.M21) <= epsilon
            && MathF.Abs(left.M22 - right.M22) <= epsilon
            && MathF.Abs(left.M23 - right.M23) <= epsilon
            && MathF.Abs(left.M24 - right.M24) <= epsilon
            && MathF.Abs(left.M31 - right.M31) <= epsilon
            && MathF.Abs(left.M32 - right.M32) <= epsilon
            && MathF.Abs(left.M33 - right.M33) <= epsilon
            && MathF.Abs(left.M34 - right.M34) <= epsilon
            && MathF.Abs(left.M41 - right.M41) <= epsilon
            && MathF.Abs(left.M42 - right.M42) <= epsilon
            && MathF.Abs(left.M43 - right.M43) <= epsilon
            && MathF.Abs(left.M44 - right.M44) <= epsilon;
    }

    private async Task EnsureTimelineResourcesAsync(
        ScriptSceneTimeline timeline,
        int generation)
    {
        if (session.Script.GameDataPath is null || graphics is null) return;
        var states = timeline.Points
            .Select(value => value.After)
            .Prepend(timeline.InitialState)
            .ToArray();
        var entities = states.SelectMany(value => value.Entities.Values).ToArray();

        foreach (var assetId in entities
                     .Select(value => value.AssetId)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptTimelineGeneration || IsDisposed) return;
            if (!loadedModelsByAsset.TryGetValue(assetId, out var model))
            {
                model = await LoadScriptEntityModelAsync(
                    assetId, session.Script.GameDataPath);
                if (generation != scriptTimelineGeneration || IsDisposed) return;
                if (model is null)
                {
                    continue;
                }
                loadedModelsByAsset[assetId] = model;
            }
            if (uploadedModels.All(value =>
                    !value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
            {
                uploadedModels.Add(new D3D11ModelUploader(graphics.Device).Upload(model));
            }
        }

        foreach (var request in entities
                     .Select(value => (
                         value.AssetId,
                         AnimationName: GetRequestedBaseAnimationName(value),
                         AnimationBanks: value.AnimationBanks ?? Array.Empty<string>()))
                     .Where(value => !string.IsNullOrWhiteSpace(value.AssetId)
                         && !string.IsNullOrWhiteSpace(value.AnimationName))
                     .DistinctBy(
                         value => AnimationRequestKey(
                             value.AssetId, value.AnimationName!, value.AnimationBanks),
                         StringComparer.OrdinalIgnoreCase))
        {
            if (generation != scriptTimelineGeneration || IsDisposed) return;
            await EnsureCharacterAnimationAsync(
                request.AssetId,
                request.AnimationName!,
                request.AnimationBanks,
                session.Script.GameDataPath);
        }
        foreach (var animation in states
                     .SelectMany(value => value.PropAnimations.Values)
                     .DistinctBy(
                         value => $"{value.PropName}\0{value.AnimationName}",
                         StringComparer.Ordinal))
        {
            if (generation != scriptTimelineGeneration || IsDisposed) return;
            await EnsurePropAnimationAsync(animation, session.Script.GameDataPath);
        }
    }

    private ScriptSceneTimeline ResolveAnimationWaits(ScriptSceneTimeline source)
    {
        var adjustedStarts = new Dictionary<ScriptEntityAnimation, int>(
            ReferenceEqualityComparer.Instance);
        var initialState = ShiftAnimationStartFrames(
            source.InitialState, 0, adjustedStarts);
        var points = new List<ScriptSceneTimelinePoint>(source.Points.Count);
        var addedFrames = 0;
        foreach (var original in source.Points)
        {
            var before = ShiftAnimationStartFrames(
                original.Before, addedFrames, adjustedStarts);
            var after = ShiftAnimationStartFrames(
                original.After, addedFrames, adjustedStarts);
            var frame = original.Frame + addedFrames;
            points.Add(original with
            {
                Frame = frame,
                Before = before,
                After = after,
            });

            if (original.Instruction.Opcode != 35
                || original.SubjectEntityId is not { } entityId
                || !after.Entities.TryGetValue(entityId, out var entity)
                || !TryGetBaseAnimation(entity, out var animation)
                || !TryGetCharacterAnimation(entity.AssetId, animation.Name, out var clip))
            {
                continue;
            }
            var elapsedFrames = Math.Max(0, frame - animation.StartFrame);
            var clipFrames = (int)MathF.Ceiling(
                clip.Duration * ScriptWaitDuration.PreviewFramesPerSecond);
            addedFrames += Math.Max(0, clipFrames - elapsedFrames);
        }
        return source with
        {
            InitialState = initialState,
            Points = points,
            DurationFrames = Math.Max(1, source.DurationFrames + addedFrames),
        };
    }

    private static ScriptSceneState ShiftAnimationStartFrames(
        ScriptSceneState state,
        int addedFrames,
        IDictionary<ScriptEntityAnimation, int> adjustedStarts)
    {
        var changed = false;
        var entities = new Dictionary<int, ScriptEntityState>(state.Entities.Count);
        foreach (var pair in state.Entities)
        {
            var entity = pair.Value;
            if (entity.AnimationSlots is not { Count: > 0 })
            {
                entities.Add(pair.Key, entity);
                continue;
            }
            var animations = new Dictionary<int, ScriptEntityAnimation>(
                entity.AnimationSlots.Count);
            foreach (var animationPair in entity.AnimationSlots)
            {
                var animation = animationPair.Value;
                if (!adjustedStarts.TryGetValue(animation, out var adjustedStart))
                {
                    adjustedStart = animation.StartFrame + addedFrames;
                    adjustedStarts.Add(animation, adjustedStart);
                }
                var adjusted = animation.StartFrame == adjustedStart
                    ? animation
                    : animation with { StartFrame = adjustedStart };
                changed |= !ReferenceEquals(adjusted, animation);
                animations.Add(animationPair.Key, adjusted);
            }
            entities.Add(pair.Key, entity with { AnimationSlots = animations });
        }
        return changed ? state with { Entities = entities } : state;
    }

    private void UpdateScriptTimeline(float elapsedSeconds)
    {
        if (scriptTimeline is null) return;
        // The speed only changes how fast the preview walks the timeline; the
        // frames the script itself authored are untouched.
        var speed = scriptEditor is { IsDisposed: false } ? scriptEditor.PlaybackSpeed : 1f;
        scriptTimelineFrame += elapsedSeconds * speed * ScriptWaitDuration.PreviewFramesPerSecond;
        var duration = GetScriptTimelineDurationFrames(scriptTimeline);
        ShowPlaybackPosition(duration);
        if (scriptTimelineFrame >= duration)
        {
            if (scriptTimeline.LoopPlayback)
            {
                scriptTimelineFrame %= duration;
                scriptTimelinePointIndex = 0;
                isCameraAnimating = false;
                ApplyTimelineSceneState(
                    scriptTimeline.InitialState,
                    applyCamera: ShouldApplyScriptCamera);
            }
            else
            {
                scriptTimelineFrame = duration;
            }
        }
        ApplyTimelinePointsAtCurrentFrame();
    }

    /// <summary>
    /// Where the playback stands, in the scene's own seconds and in the commands
    /// it has run: a scene is mostly authored waits, so a reader needs to see
    /// the clock move even while no block changes.
    /// </summary>
    private void ShowPlaybackPosition(float durationFrames)
    {
        if (scriptTimeline is null || scriptEditor is not { IsDisposed: false } editor) return;
        editor.ShowPlaybackPosition(
            $"▶ {scriptTimelineFrame / ScriptWaitDuration.PreviewFramesPerSecond:0.0}"
            + $" / {durationFrames / ScriptWaitDuration.PreviewFramesPerSecond:0.0} s"
            + $"  ({scriptTimelinePointIndex}/{scriptTimeline.Points.Count})");
    }

    private void ApplyTimelinePointsAtCurrentFrame()
    {
        if (scriptTimeline is null) return;
        while (scriptTimelinePointIndex < scriptTimeline.Points.Count
               && scriptTimeline.Points[scriptTimelinePointIndex].Frame <= scriptTimelineFrame)
        {
            var point = scriptTimeline.Points[scriptTimelinePointIndex++];
            if (!point.IsExternalScript)
                scriptEditor?.ShowPlaybackInstruction(point.FunctionIndex, point.InstructionIndex);
            ApplyTimelineSceneState(point.After, applyCamera: false);
            if (point.Instruction.Opcode == 45 && ShouldApplyScriptCamera)
            {
                if (ScriptCameraStateResolver.HasInterpolation(point.Instruction)
                    && ScriptCameraStateResolver.ReadDurationMs(point.Instruction) > 0)
                {
                    animationBefore = point.Before.Camera;
                    animationAfter = point.After.Camera;
                    animationDurationMs = ScriptCameraStateResolver.ReadDurationMs(point.Instruction);
                    animationEasingType = ScriptCameraStateResolver.ReadInterpolationType(point.Instruction);
                    animationElapsed = 0f;
                    isCameraAnimating = true;
                    cameraAnimationLoops = false;
                    CaptureCameraAnimationStart();
                }
                else
                {
                    isCameraAnimating = false;
                    ApplyCameraState(point.After.Camera, point.After.Entities);
                }
            }
            else if (ShouldApplyScriptCamera
                     && point.Before.Camera != point.After.Camera)
            {
                ApplyCameraState(point.After.Camera, point.After.Entities);
            }
        }
    }

    private void ApplyTimelineSceneState(ScriptSceneState state, bool applyCamera)
    {
        activeScriptEntities = PrepareEntities(state.Entities);
        activeScriptPropAnimations = state.PropAnimations;
        scriptEditor?.SetRuntimeEntities(CreateScriptEntityChoices(activeScriptEntities));
        _ = RefreshScriptEntitiesAsync(activeScriptEntities);
        _ = RefreshScriptPropAnimationsAsync(state.PropAnimations);
        RefreshOverlay();
        if (applyCamera && ShouldApplyScriptCamera && state.Camera.HasViewValue)
            ApplyCameraState(state.Camera, state.Entities);
    }

    private float GetScriptTimelineDurationFrames(ScriptSceneTimeline timeline)
    {
        var duration = (float)timeline.DurationFrames;
        var finalState = timeline.Points.LastOrDefault()?.After ?? timeline.InitialState;
        foreach (var entity in finalState.Entities.Values)
        {
            if (!TryGetBaseAnimation(entity, out var animation)
                || !TryGetCharacterAnimation(entity.AssetId, animation.Name, out var clip))
            {
                continue;
            }
            duration = Math.Max(
                duration,
                animation.StartFrame + clip.Duration * ScriptWaitDuration.PreviewFramesPerSecond);
        }
        foreach (var animation in finalState.PropAnimations.Values)
        {
            foreach (var prop in sceneInstances.Where(value =>
                         value.Name.Equals(animation.PropName, StringComparison.Ordinal)))
            {
                if (!loadedPropAnimations.TryGetValue(
                        PropAnimationKey(prop.AssetId, animation.AnimationName), out var loadedAnimation))
                {
                    continue;
                }
                var clip = loadedAnimation.Clip;
                duration = Math.Max(
                    duration,
                    animation.StartFrame
                        + clip.Duration * ScriptWaitDuration.PreviewFramesPerSecond);
            }
        }
        // An effect the scene started outlives the command that started it: a
        // loop that restarted before it had played would never show it.
        if (session.Script.GameDataPath is { } gameDataPath)
        {
            foreach (var entity in finalState.Entities.Values)
            {
                foreach (var instance in entity.Effects?.Values ?? Enumerable.Empty<ScriptEffectInstance>())
                {
                    if (LoadEffect(gameDataPath, instance.EffectPath) is not { } effect
                        || EffSimulation.FiniteDuration(effect) is not { } seconds)
                    {
                        continue;
                    }
                    duration = Math.Max(
                        duration,
                        instance.StartFrame + seconds * ScriptWaitDuration.PreviewFramesPerSecond);
                }
            }
        }
        return Math.Max(1f, duration);
    }

    private void StopScriptTimeline()
    {
        if (scriptEditor is { IsDisposed: false } editor) editor.ShowPlaybackPosition(string.Empty);
        scriptTimelineGeneration++;
        scriptTimeline = null;
        scriptTimelinePointIndex = 0;
        scriptTimelineFrame = 0f;
        cameraAnimationLoops = false;
    }

    /// <summary>
    /// The live editor camera expressed the way the scenario scripts author it:
    /// the angles say where the eye sits around the point it looks at, not where
    /// it looks. Capturing a shot into a camera command goes through here, so a
    /// saved plan frames in game exactly what the viewport shows.
    /// </summary>
    /// <summary>
    /// Angles a relative command starts from: the shot the script last set, or
    /// the viewport when the scene has not defined one yet.
    /// </summary>
    private (float PitchDegrees, float YawDegrees) CurrentScriptAngles()
        => scriptCameraAngles ?? ScriptCameraOrbit.FromViewDirection(cameraNavigation.Forward);

    private ScriptCameraSnapshot CaptureCameraSnapshot()
    {
        var angles = ScriptCameraOrbit.FromViewDirection(cameraNavigation.Forward);
        return new ScriptCameraSnapshot(
            cameraNavigation.Position,
            cameraNavigation.Target,
            cameraNavigation.Forward,
            cameraNavigation.Distance,
            angles.YawDegrees,
            angles.PitchDegrees,
            cameraNavigation.Roll * 180f / MathF.PI,
            cameraFieldOfViewDegrees);
    }

    /// <summary>
    /// Freezes the camera an interpolation starts from. The engine snapshots the
    /// live angles and position once, when the command runs, then drives the
    /// interpolation from that fixed pair. Reading the live camera on every
    /// frame instead fed the interpolation back into itself.
    /// </summary>
    private void CaptureCameraAnimationStart()
    {
        var start = CaptureCameraSnapshot();
        var yaw = start.YawDegrees;
        var pitch = start.PitchDegrees;
        // The shortest-arc flag rewrites the destination angle once, here, so the
        // move never travels more than half a turn. Without the flag the script
        // deliberately asks for the long way round.
        if (animationAfter is { UseShortestPath: true } target)
        {
            animationAfter = target with
            {
                YawDegrees = target.YawDegrees is { } targetYaw
                    ? ScriptCameraOrbit.NormalizeTowards(yaw, targetYaw)
                    : null,
                PitchDegrees = target.PitchDegrees is { } targetPitch
                    ? ScriptCameraOrbit.NormalizeTowards(pitch, targetPitch)
                    : null,
            };
        }
        animationStart = start;
    }

    private void UpdateCameraAnimation(float deltaSeconds)
    {
        if (!ShouldApplyScriptCamera
            || !isCameraAnimating
            || animationBefore is null
            || animationAfter is null
            || animationStart is null)
        {
            return;
        }

        animationElapsed += deltaSeconds * 1000f; // convertir en ms
        var duration = (float)animationDurationMs;
        if (duration <= 0f) duration = 1000f;

        // t normalisé 0..1, reboucle
        var tRaw = animationElapsed / duration;
        if (tRaw >= 1f)
        {
            if (cameraAnimationLoops)
            {
                animationElapsed %= duration;
                tRaw = animationElapsed / duration;
            }
            else
            {
                ApplyCameraState(animationAfter, activeScriptEntities);
                isCameraAnimating = false;
                cameraPlayButton.Enabled = true;
                return;
            }
        }
        var t = CameraEasing.Apply(tRaw, animationEasingType);

        // Interpole entre before et after
        var lerpFov = t;
        var lerpDistance = t;
        var lerpPosition = t;
        var lerpAngles = t;

        var state = new ScriptCameraState();

        // Distance
        var distBefore = animationBefore.Distance ?? animationStart.Distance;
        var distAfter = animationAfter.Distance ?? distBefore;
        state = state with { Distance = distBefore + (distAfter - distBefore) * lerpDistance };

        // Distance delta relatif
        if (animationAfter.DistanceDelta is { } dd)
            state = state with { DistanceDelta = dd * lerpDistance };

        // FOV
        var fovBefore = animationBefore.VerticalFieldOfViewDegrees
            ?? animationStart.VerticalFieldOfViewDegrees;
        var fovAfter = animationAfter.VerticalFieldOfViewDegrees ?? fovBefore;
        state = state with { VerticalFieldOfViewDegrees = fovBefore + (fovAfter - fovBefore) * lerpFov };

        // Position
        if (animationAfter.Position is { } posAfter)
        {
            var posBefore = animationBefore.Position ?? animationStart.Position;
            state = state with { Position = posBefore + (posAfter - posBefore) * lerpPosition };
        }

        // Target
        if (animationAfter.Target is { } tgtAfter)
        {
            var tgtBefore = animationBefore.Target ?? animationStart.Target;
            state = state with { Target = tgtBefore + (tgtAfter - tgtBefore) * lerpPosition };
        }
        if (animationAfter.TargetEntityId is not null)
        {
            state = state with
            {
                TargetEntityId = animationAfter.TargetEntityId,
                SecondaryTargetEntityId = animationAfter.SecondaryTargetEntityId,
                TargetEntityOffset = animationAfter.TargetEntityOffset,
                TargetOffsetUsesEntityRotation = animationAfter.TargetOffsetUsesEntityRotation,
            };
        }

        // Angles (en degrés). Ils sont TOUJOURS transmis : une commande qui ne
        // bouge que le point visé doit garder les angles du plan, pas les laisser
        // se redériver de la caméra vivante à chaque frame.
        {
            var yawB = animationBefore.YawDegrees ?? animationStart.YawDegrees;
            var pitchB = animationBefore.PitchDegrees ?? animationStart.PitchDegrees;
            var rollB = animationBefore.RollDegrees ?? animationStart.RollDegrees;
            var yawA = animationAfter.YawDegrees ?? yawB;
            var pitchA = animationAfter.PitchDegrees ?? pitchB;
            var rollA = animationAfter.RollDegrees ?? rollB;
            state = state with
            {
                YawDegrees = yawB + (yawA - yawB) * lerpAngles,
                PitchDegrees = pitchB + (pitchA - pitchB) * lerpAngles,
                RollDegrees = rollB + (rollA - rollB) * lerpAngles,
            };
        }

        // Angle deltas (RotateBy, AddYaw)
        if (animationAfter.AngleDeltaDegrees is { } deltaDeg)
        {
            // Le delta est interpolé linéairement
            var effectiveDelta = new Vector3(
                deltaDeg.X * lerpAngles,
                deltaDeg.Y * lerpAngles,
                deltaDeg.Z * lerpAngles);
            // Appliquer le delta partiel aux angles courants de l'état before
            var yawBDeg = animationBefore.YawDegrees ?? animationStart.YawDegrees;
            var pitchBDeg = animationBefore.PitchDegrees ?? animationStart.PitchDegrees;
            var rollBDeg = animationBefore.RollDegrees ?? animationStart.RollDegrees;
            state = state with
            {
                YawDegrees = yawBDeg + effectiveDelta.Y,
                PitchDegrees = pitchBDeg + effectiveDelta.X,
                RollDegrees = rollBDeg + effectiveDelta.Z,
            };
        }

        // Target offset
        if (animationAfter.TargetOffset is { } tgtOff)
            state = state with { TargetOffset = tgtOff * lerpPosition };

        // Eye offset
        if (animationAfter.PositionOffset is { } eyeOff)
            state = state with { PositionOffset = eyeOff * lerpPosition };

        ApplyCameraState(state, activeScriptEntities);
    }

    private void PauseAnimation()
    {
        if (scriptTimeline is not null) StopScriptTimeline();
        isCameraAnimating = false;
        cameraPlayButton.Enabled = true;
    }

    private bool ShouldApplyScriptCamera
        => !ignoreScriptCameraButton.Checked && !manualScriptCameraOverride;

    private void BeginManualScriptCameraOverride()
    {
        manualScriptCameraOverride = true;
        isCameraAnimating = false;
        cameraPlayButton.Enabled = true;
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

    /// <summary>
    /// Opens the effect editor, with its own preview: an effect is judged on its
    /// own against a plain background, not buried in the map.
    /// </summary>
    private void ShowEffectEditor()
    {
        if (session.Script.GameDataPath is not { } gameDataPath || graphics is null)
        {
            MessageBox.Show(
                this,
                "The game data directory was not found, so no effect can be read.",
                "Effect editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (effectEditorWindow is { IsDisposed: false } opened)
        {
            opened.BringToFront();
            opened.Focus();
            return;
        }
        var window = new EffEditorWindow(
            gameDataPath,
            graphics,
            ResolveEffectTexture,
            (_, eventArgs) => TrackModSave(eventArgs.Path, eventArgs.BeforeWrite));
        window.FormClosed += (_, _) => effectEditorWindow = null;
        effectEditorWindow = window;
        window.Show(this);
    }

    private void ShowTableEditor()
    {
        if (session.Script.GameDataPath is not { } gameDataPath)
        {
            MessageBox.Show(
                this,
                "The game data directory was not found, so no table can be read.",
                "Table editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (tableEditorWindow is { IsDisposed: false } opened)
        {
            opened.BringToFront();
            opened.Focus();
            return;
        }
        var window = new TblEditorWindow(
            gameDataPath,
            session.Script.Header.SourcePath,
            (_, _) => tableCatalog = null);
        window.FormClosed += (_, _) => tableEditorWindow = null;
        tableEditorWindow = window;
        window.Show(this);
    }

    private void ShowCharacterStudio(CharacterAuthoringKind kind)
    {
        if (session.Script.GameDataPath is not { } gameDataPath
            || graphics is null
            || scriptAnimationLibrary is null)
        {
            MessageBox.Show(
                this,
                "The character studios require the game data directory and decoded character tables.",
                kind == CharacterAuthoringKind.Character ? "Character studio" : "Enemy studio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        var existing = kind == CharacterAuthoringKind.Character
            ? characterStudioWindow
            : enemyStudioWindow;
        if (existing is { IsDisposed: false })
        {
            existing.BringToFront();
            existing.Focus();
            return;
        }
        CharacterStudioForm window;
        try
        {
            window = new CharacterStudioForm(
                gameDataPath,
                projectLoader,
                graphics,
                scriptAnimationLibrary,
                kind,
                (target, beforeWrite) => TrackModSave(target, beforeWrite));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                kind == CharacterAuthoringKind.Character
                    ? "Cannot open Character studio"
                    : "Cannot open Enemy studio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }
        window.FormClosed += (_, _) =>
        {
            if (kind == CharacterAuthoringKind.Character) characterStudioWindow = null;
            else enemyStudioWindow = null;
        };
        if (kind == CharacterAuthoringKind.Character) characterStudioWindow = window;
        else enemyStudioWindow = window;
        window.Show(this);
    }

    private void ShowQuestEditor()
    {
        if (questEditorWindow is { IsDisposed: false } opened)
        {
            opened.BringToFront();
            opened.Focus();
            return;
        }
        var directory = ResolveTableDirectory();
        var path = directory is null ? null : Path.Combine(directory, "t_quest.tbl");
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show(
                this,
                "t_quest.tbl was not found for the current script locale.",
                "Quest editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        QuestEditorWindow window;
        try
        {
            window = new QuestEditorWindow(
                path,
                session.Script.Header.SourcePath,
                GetQuestScriptSources(),
                () =>
                {
                    rightPanelTabs.SelectedTab = scriptsTab;
                    OpenScriptEditor();
                },
                NavigateToQuestMutation,
                (target, beforeWrite) => TrackModSave(target, beforeWrite));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Cannot open Quest editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }
        window.FormClosed += (_, _) => questEditorWindow = null;
        questEditorWindow = window;
        window.Show(this);
        if (ResolveScenarioScriptCorpusDirectory() is { } corpusDirectory)
            _ = window.IndexScriptCorpusAsync(corpusDirectory, instructionDefinitionsPath);
    }

    private string? ResolveScenarioScriptCorpusDirectory()
    {
        if (session.Script.GameDataPath is not { } gameDataPath) return null;
        var sourceSegments = Path.GetFullPath(session.Script.Header.SourcePath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var locale = sourceSegments.Any(value =>
            value.Equals("dat_us", StringComparison.OrdinalIgnoreCase))
            ? "dat_us"
            : "dat";
        var directory = Path.Combine(gameDataPath, "scripts", "scena", locale);
        return Directory.Exists(directory) ? directory : null;
    }

    private void NavigateToQuestMutation(QuestScriptMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        BringToFront();
        OpenScriptEditor();
        if (!session.Script.Header.SourcePath.Equals(
                mutation.ScriptPath,
                StringComparison.OrdinalIgnoreCase)
            && !OpenScriptFile(mutation.ScriptPath))
        {
            return;
        }
        if (scriptEditor is not { } editor
            || editor.CurrentPath is not { } editorPath
            || !Path.GetFullPath(editorPath).Equals(
                Path.GetFullPath(mutation.ScriptPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        rightPanelTabs.SelectedTab = scriptsTab;
        editor.GoToInstruction(mutation.FunctionIndex, mutation.InstructionIndex);
        editor.Focus();
    }

    private IReadOnlyList<QuestScriptSource> GetQuestScriptSources()
    {
        var sources = new List<QuestScriptSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPath = Path.GetFullPath(session.Script.Header.SourcePath);
        var currentScript = scriptEditor?.CurrentScript;
        if (currentScript is not null)
        {
            sources.Add(new QuestScriptSource(currentPath, currentScript));
            seen.Add(currentPath);
        }
        else if (File.Exists(currentPath))
        {
            sources.Add(new QuestScriptSource(
                currentPath,
                ScriptDecompiler.Decompile(currentPath, instructionDefinitionsPath)));
            seen.Add(currentPath);
        }

        if (modProject is null) return sources;
        foreach (var file in modProject.Files.Where(value =>
                     value.HasModCopy
                     && Path.GetExtension(value.RelativePath).Equals(".dat", StringComparison.OrdinalIgnoreCase)
                     && value.RelativePath.Split('/')
                         .Any(segment => segment.Equals("scripts", StringComparison.OrdinalIgnoreCase))))
        {
            var scriptFile = Path.GetFullPath(modProject.GameFilePath(file.RelativePath));
            if (!seen.Add(scriptFile) || !File.Exists(scriptFile)) continue;
            try
            {
                sources.Add(new QuestScriptSource(
                    scriptFile,
                    ScriptDecompiler.Decompile(scriptFile, instructionDefinitionsPath)));
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                Debug.WriteLine(
                    $"Quest index skipped unreadable script '{scriptFile}': {exception.Message}");
            }
        }
        return sources;
    }

    /// <summary>
    /// The entries an operand can point at, read from the game's tables. The
    /// script's own path chooses the locale: a script under dat_us is the
    /// English build, anything else is the Japanese one.
    /// </summary>
    private IReadOnlyList<Cs1TableChoice> GetTableChoices(Cs1TableReference reference)
    {
        if (ResolveTableDirectory() is not { } directory) return Array.Empty<Cs1TableChoice>();
        if (tableCatalog is null || !tableCatalog.DirectoryPath.Equals(directory, StringComparison.OrdinalIgnoreCase))
            tableCatalog = new Cs1TableCatalog(directory);
        return tableCatalog.GetChoices(reference);
    }

    private string? ResolveTableDirectory()
    {
        if (session.Script.GameDataPath is not { } gameDataPath) return null;
        var textRoot = Path.Combine(gameDataPath, "text");
        var english = Path.GetFullPath(session.Script.Header.SourcePath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => value.Equals("dat_us", StringComparison.OrdinalIgnoreCase));
        foreach (var locale in english ? new[] { "dat_us", "dat" } : new[] { "dat", "dat_us" })
        {
            var directory = Path.Combine(textRoot, locale);
            if (Directory.Exists(directory)) return directory;
        }
        return null;
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
        if (selection.Kind is SceneElementKind.Prop
            or SceneElementKind.ScriptCharacter
            or SceneElementKind.FieldMonster)
        {
            var selected = selection.Kind == SceneElementKind.Prop
                ? sceneInstances.FirstOrDefault(value => value.Id == selection.SourceIndex)
                : selection.Kind == SceneElementKind.ScriptCharacter
                    ? scriptMonsterInstances.FirstOrDefault(value => value.Id == selection.SourceIndex)
                    : fieldMonsterInstances.FirstOrDefault(value => value.Id == selection.SourceIndex);
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
            else if (selection.Kind == SceneElementKind.ScriptCharacter
                     && activeScriptEntities.Values.FirstOrDefault(value =>
                         ScriptEntitySceneInstanceId(value.EntityId) == selection.SourceIndex
                         && value.HasPosition) is { } entity)
            {
                var entityRadius = Math.Max(
                    Math.Max(entity.CollisionHeight, entity.CollisionRadius * 2f) * entity.Scale,
                    sceneRadius * 0.01f);
                cameraNavigation.Focus(entity.Position, Math.Max(entityRadius * 2.5f, 1f));
            }
            else if (selection.Kind == SceneElementKind.FieldMonster
                     && fieldMonsterSpawns.TryGetValue(selection.SourceIndex, out var spawn))
            {
                cameraNavigation.Focus(spawn.Position, Math.Max(sceneRadius * 0.025f, 1f));
            }
            return;
        }
        if (!TryGetOverlayFocus(selection, out var center, out var radius)) return;
        cameraNavigation.Focus(center, Math.Max(radius * 2.5f, sceneRadius * 0.01f));
    }

    private void FocusScriptEntity(int entityId)
    {
        if (!activeScriptEntities.TryGetValue(entityId, out var entity)) return;
        if (!entity.HasPosition)
        {
            Text = $"{baseTitle} — entity {entityId}: position unresolved at this instruction";
            return;
        }
        PauseAnimation();
        var name = string.IsNullOrWhiteSpace(entity.DisplayName)
            ? entity.AssetId
            : entity.DisplayName;
        selection = new SceneElementSelection(
            SceneElementKind.ScriptCharacter,
            ScriptEntitySceneInstanceId(entityId),
            name);
        RefreshRenderInstances(
            uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase));
        RefreshOverlay();
        RefreshElementProperties();
        FocusSelection();
        Text = $"{baseTitle} — focused entity {entityId}: {name}";
    }

    private string DescribeSelection(SceneElementSelection selected)
    {
        var assetId = selected.Kind switch
        {
            SceneElementKind.Prop => document.FindProp(selected)?.AssetId,
            SceneElementKind.ScriptCharacter => scriptMonsterInstances
                .FirstOrDefault(value => value.Id == selected.SourceIndex)?.AssetId,
            SceneElementKind.FieldMonster => fieldMonsterInstances
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
        if (showFieldMonstersCheckBox.Checked)
        {
            rendered.AddRange(fieldMonsterInstances
                .Where(value => resourcesByAsset.ContainsKey(value.AssetId))
                .Where(value => !fieldMonsterSpawns.TryGetValue(value.Id, out var spawn)
                    || scriptMonsterInstances.All(entity =>
                        entity.Id != ScriptEntitySceneInstanceId(spawn.EntityId)))
                .Select(value => new D3D11SceneInstance(
                    value.Id,
                    resourcesByAsset[value.AssetId],
                    value.Transform,
                    selection is { Kind: SceneElementKind.FieldMonster }
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

    /// <summary>Ground grid shown when the open script has no map of its own.</summary>
    private static IEnumerable<SceneOverlayLine> BuildDebugGround()
    {
        const int halfSize = 20;
        const float spacing = 1f;
        // Light grid on a light background: a character or a camera framing is
        // read against the ground, and the dark scene made both invisible.
        var minor = new Vector4(0.62f, 0.66f, 0.72f, 1f);
        var major = new Vector4(0.32f, 0.38f, 0.48f, 1f);
        for (var step = -halfSize; step <= halfSize; step++)
        {
            var offset = step * spacing;
            var extent = halfSize * spacing;
            var color = step == 0 ? major : minor;
            yield return new SceneOverlayLine(
                new Vector3(-extent, 0f, offset), new Vector3(extent, 0f, offset), color);
            yield return new SceneOverlayLine(
                new Vector3(offset, 0f, -extent), new Vector3(offset, 0f, extent), color);
        }
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
        var overlayTriangles = overlay.Triangles.ToList();
        // A script with no map has nothing to stand on: draw a plain ground so a
        // camera or an animation can still be judged against something.
        if (session.Map is null) overlayLines.AddRange(BuildDebugGround());

        if (showIndicatorsCheckBox.Checked)
        {
            foreach (var entity in activeScriptEntities.Values.Where(value =>
                         !value.HasSpawnDefinition && value.HasPosition))
            {
                AddReferencedEntityMarker(overlayLines, overlayTriangles, entity);
            }
        }
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
        if (scriptSurfacePositionPreview is { } surfacePreview)
        {
            var toCamera = cameraNavigation.Position - surfacePreview.Position;
            var markerNormal = Vector3.Dot(surfacePreview.Normal, toCamera) < 0f
                ? -surfacePreview.Normal
                : surfacePreview.Normal;
            var marker = SceneSurfacePlacementMarker.Build(
                surfacePreview.Position,
                markerNormal,
                GetSurfacePlacementMarkerRadius(surfacePreview.Position));
            overlayLines.AddRange(marker.Lines);
            overlayTriangles.AddRange(marker.Triangles);
        }
        viewport.SetDebugLines(overlayLines
            .Select(line => new D3D11DebugLine(line.Start, line.End, line.Color, line.Thickness))
            .ToArray());
        viewport.SetDebugTriangles(overlayTriangles
            .Select(triangle => new D3D11DebugTriangle(
                triangle.A, triangle.B, triangle.C, triangle.Color))
            .ToArray());
    }

    /// <summary>
    /// The quads of the effects the script has running, plus the one the effect
    /// editor is previewing. OP39 loads an .eff into a slot and starts it on an
    /// entity, one of its nodes, or the map; the file itself says which segments
    /// it spawns, when and where, and each one is drawn with its own texture,
    /// colour and blend mode at this point of the timeline.
    /// </summary>
    private IReadOnlyList<D3D11EffectQuad> BuildEffectQuads()
    {
        var quads = new List<D3D11EffectQuad>();
        if (session.Script.GameDataPath is not { } gameDataPath) return quads;
        foreach (var owner in activeScriptEntities.Values)
        {
            if (owner.Effects is not { Count: > 0 } playing) continue;
            foreach (var instance in playing.Values)
            {
                if (LoadEffect(gameDataPath, instance.EffectPath) is not { } effect) continue;
                const float toRadians = MathF.PI / 180f;
                var placement = Matrix4x4.CreateScale(instance.Scale)
                    * Matrix4x4.CreateFromYawPitchRoll(
                        instance.RotationDegrees.Y * toRadians,
                        instance.RotationDegrees.X * toRadians,
                        instance.RotationDegrees.Z * toRadians)
                    * Matrix4x4.CreateTranslation(instance.Position)
                    * ResolveEffectAnchor(instance);
                // The effect runs on its own clock, started by the command that
                // played it, exactly like an animation or a facial pattern.
                var elapsedSeconds = scriptTimeline is null
                    ? 0f
                    : Math.Max(0f, scriptTimelineFrame - instance.StartFrame)
                        / ScriptWaitDuration.PreviewFramesPerSecond;
                DrawEffect(quads, effect, elapsedSeconds, placement);
            }
        }
        return quads;
    }

    private void DrawEffect(
        ICollection<D3D11EffectQuad> quads,
        EffFile effect,
        float seconds,
        Matrix4x4 placement)
    {
        foreach (var node in EffSimulation.Evaluate(effect, seconds).Nodes)
        {
            if (!node.Drawn) continue;
            AddEffectNodeQuad(quads, effect.Segments[node.SegmentIndex], node, placement);
        }
    }

    /// <summary>
    /// Where an effect hangs: on one of an actor's nodes, on the actor itself, or
    /// in the world when the script anchored it to the scene.
    /// </summary>
    private Matrix4x4 ResolveEffectAnchor(ScriptEffectInstance instance)
    {
        if (!activeScriptEntities.TryGetValue(instance.AnchorEntityId, out var host))
            return Matrix4x4.Identity;
        if (instance.AnchorNode.Length > 0
            && TryGetOwnerPose(ScriptEntitySceneInstanceId(host.EntityId), out var posed)
            && IndexOfJoint(posed.Skeleton, instance.AnchorNode) is var joint and >= 0)
        {
            return posed.Pose.WorldTransforms[joint] * posed.Transform;
        }
        var position = scriptTimeline is not null
            ? EvaluateEntityMotionPosition(host, scriptTimelineFrame)
            : host.Position;
        var yaw = scriptTimeline is not null
            ? host.Motion?.HeadingAt(scriptTimelineFrame) ?? host.YawDegrees
            : host.YawDegrees;
        return Matrix4x4.CreateRotationY(yaw * MathF.PI / 180f)
            * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// One segment of a playing effect: a unit quad the segment's scale track
    /// sizes, showing the piece of its texture the segment's crop selects,
    /// multiplied by its colour track and lit by its glow track.
    /// </summary>
    private void AddEffectNodeQuad(
        ICollection<D3D11EffectQuad> quads,
        EffSegment segment,
        EffNode node,
        Matrix4x4 placement)
    {
        // A segment that names no texture draws nothing in the engine: it only
        // exists to carry its children.
        if (segment.TextureName.Length == 0) return;
        if (ResolveEffectTexture(segment.TextureName) is not { } texture) return;
        var world = node.Rotation
            * Matrix4x4.CreateTranslation(node.Position)
            * placement;
        var halfWidth = node.Scale.X / 2f;
        var halfHeight = node.Scale.Y / 2f;
        Vector3[] corners;
        if (node.Billboard)
        {
            // A billboarded segment keeps its place but turns its face to the
            // camera, so it is read the same way from every angle.
            var center = Vector3.Transform(Vector3.Zero, world);
            var view = CreateCamera().View;
            var right = new Vector3(view.M11, view.M21, view.M31);
            var up = new Vector3(view.M12, view.M22, view.M32);
            corners = new[]
            {
                center - right * halfWidth - up * halfHeight,
                center + right * halfWidth - up * halfHeight,
                center + right * halfWidth + up * halfHeight,
                center - right * halfWidth + up * halfHeight,
            };
        }
        else
        {
            corners = new[]
            {
                Vector3.Transform(new Vector3(-halfWidth, -halfHeight, 0f), world),
                Vector3.Transform(new Vector3(halfWidth, -halfHeight, 0f), world),
                Vector3.Transform(new Vector3(halfWidth, halfHeight, 0f), world),
                Vector3.Transform(new Vector3(-halfWidth, halfHeight, 0f), world),
            };
        }

        // The crop is authored as texture coordinates, and is allowed to run past
        // 0..1 (a tiling rain streak) or to be flipped. Only a crop that is all
        // zeroes means "the whole texture".
        var crop = segment.Data04;
        var cropped = crop[0] != 0f || crop[1] != 0f || crop[2] != 0f || crop[3] != 0f;
        quads.Add(new D3D11EffectQuad(
            corners[0],
            corners[1],
            corners[2],
            corners[3],
            cropped ? new Vector2(crop[0], crop[1]) : Vector2.Zero,
            cropped ? new Vector2(crop[2], crop[3]) : Vector2.One,
            node.ColorMultiply,
            node.ColorAdd,
            texture,
            BlendModeOf(segment),
            (int)((segment.Data02[3] >> 8) & 0xFF)));
    }

    /// <summary>
    /// How the segment is blended: its second flag word carries the blend byte
    /// the engine switches on — 0x02 adds, 0x04 subtracts, anything else lays
    /// the segment over the scene with its alpha.
    /// </summary>
    private static EffBlendMode BlendModeOf(EffSegment segment)
        => ((segment.Data02[4] >> 8) & 0xFF) switch
        {
            0x02 => EffBlendMode.Additive,
            0x04 => EffBlendMode.Subtractive,
            _ => EffBlendMode.Alpha,
        };

    /// <summary>
    /// The texture a segment draws with, uploaded once. A segment names an
    /// effect texture package the editor's asset pipeline already resolves; one
    /// that cannot be read is remembered so it is not retried every frame.
    /// </summary>
    private ID3D11ShaderResourceView? ResolveEffectTexture(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId) || graphics is null) return null;
        if (session.Script.GameDataPath is not { } gameDataPath) return null;
        effectTextures ??= new D3D11EffectTextureResources(new D3D11ModelUploader(graphics.Device));
        if (effectTextures.Knows(assetId)) return effectTextures.Find(assetId);
        if (unavailableEffectTextures.Contains(assetId)) return null;
        try
        {
            effectTextures.Add(assetId, projectLoader.LoadEffectTexture(assetId, gameDataPath));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            unavailableEffectTextures.Add(assetId);
            Debug.WriteLine($"Could not load effect texture '{assetId}': {exception.Message}");
            return null;
        }
        return effectTextures.Find(assetId);
    }

    /// <summary>
    /// An effect file, read from the path the script named. A file that cannot
    /// be read is remembered as missing so the viewport does not try again on
    /// every frame.
    /// </summary>
    private EffFile? LoadEffect(string gameDataPath, string effectPath)
    {
        if (string.IsNullOrWhiteSpace(effectPath)) return null;
        if (loadedEffects.TryGetValue(effectPath, out var cached)) return cached;
        var relative = effectPath.Replace('/', Path.DirectorySeparatorChar);
        if (!relative.EndsWith(".eff", StringComparison.OrdinalIgnoreCase)) relative += ".eff";
        var full = Path.Combine(gameDataPath, "effects", relative);
        EffFile? effect = null;
        try
        {
            if (File.Exists(full)) effect = EffFileReader.Read(full);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            effect = null;
        }
        loadedEffects[effectPath] = effect;
        return effect;
    }

    private void AddReferencedEntityMarker(
        ICollection<SceneOverlayLine> lines,
        ICollection<SceneOverlayTriangle> triangles,
        ScriptEntityState entity)
    {
        var center = entity.Position;
        var size = overlayMarkerSize * 1.75f;
        var isSelected = selection is { Kind: SceneElementKind.ScriptCharacter }
            && selection.SourceIndex == ScriptEntitySceneInstanceId(entity.EntityId);
        var solidColor = isSelected
            ? new Vector4(1f, 0.85f, 0.15f, 1f)
            : new Vector4(0.85f, 0.25f, 1f, 1f);
        var fillColor = solidColor with { W = isSelected ? 0.55f : 0.3f };
        var top = center + Vector3.UnitY * size;
        var bottom = center - Vector3.UnitY * size;
        var east = center + Vector3.UnitX * size;
        var west = center - Vector3.UnitX * size;
        var north = center + Vector3.UnitZ * size;
        var south = center - Vector3.UnitZ * size;

        lines.Add(new SceneOverlayLine(west, east, solidColor, 3f));
        lines.Add(new SceneOverlayLine(bottom, top, solidColor, 3f));
        lines.Add(new SceneOverlayLine(south, north, solidColor, 3f));
        var yaw = entity.YawDegrees * MathF.PI / 180f;
        var facing = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        lines.Add(new SceneOverlayLine(center, center + facing * size * 2f, solidColor, 4f));

        triangles.Add(new SceneOverlayTriangle(top, east, north, fillColor));
        triangles.Add(new SceneOverlayTriangle(top, north, west, fillColor));
        triangles.Add(new SceneOverlayTriangle(top, west, south, fillColor));
        triangles.Add(new SceneOverlayTriangle(top, south, east, fillColor));
        triangles.Add(new SceneOverlayTriangle(bottom, north, east, fillColor));
        triangles.Add(new SceneOverlayTriangle(bottom, west, north, fillColor));
        triangles.Add(new SceneOverlayTriangle(bottom, south, west, fillColor));
        triangles.Add(new SceneOverlayTriangle(bottom, east, south, fillColor));
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
        RefreshOpsElementTree(groups);
        var fieldMonsterSelections = showFieldMonstersCheckBox.Checked
            ? fieldMonsterInstances.Select(value => new SceneElementSelection(
                SceneElementKind.FieldMonster,
                value.Id,
                value.Name)).ToArray()
            : Array.Empty<SceneElementSelection>();
        var selections = groups.SelectMany(group => group.Elements)
            .Concat(fieldMonsterSelections)
            .ToArray();
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
            if (fieldMonsterSelections.Length > 0)
            {
                var monsterNode = sceneOutliner.Nodes.Add(
                    $"Field monsters ({fieldMonsterSelections.Length})");
                foreach (var monster in fieldMonsterSelections)
                {
                    var spawn = fieldMonsterSpawns.GetValueOrDefault(monster.SourceIndex);
                    var encounter = spawn is null
                        ? string.Empty
                        : $" — encounter {spawn.EncounterIndex}";
                    monsterNode.Nodes.Add(new TreeNode(
                        $"{monster.Name} — entity {spawn?.EntityId}{encounter}")
                    {
                        Tag = monster,
                    });
                }
                monsterNode.Expand();
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
        SyncOpsElementSelection();
    }

    private void RefreshOpsElementTree(IReadOnlyList<SceneOutlinerGroup> groups)
    {
        refreshingOpsElementTree = true;
        try
        {
            opsElementTree.BeginUpdate();
            opsElementTree.Nodes.Clear();
            foreach (var group in groups.Where(value =>
                         value.Elements.Any(element => element.Kind != SceneElementKind.Prop)))
            {
                var elements = group.Elements
                    .Where(element => element.Kind != SceneElementKind.Prop)
                    .ToArray();
                if (elements.Length == 0) continue;
                var groupNode = opsElementTree.Nodes.Add($"{group.Name} ({elements.Length})");
                foreach (var element in elements)
                {
                    groupNode.Nodes.Add(new TreeNode(
                        $"{DescribeOpsTreeElement(element)} [{element.SourceIndex}]")
                    {
                        Tag = element,
                    });
                }
                groupNode.Expand();
            }
            SyncOpsElementSelection();
        }
        finally
        {
            opsElementTree.EndUpdate();
            refreshingOpsElementTree = false;
        }
    }

    private string DescribeOpsTreeElement(SceneElementSelection element)
    {
        if (element.Kind != SceneElementKind.LookPoint)
            return element.Name;
        var point = currentMap?.Points.FirstOrDefault(value =>
            value.SourceIndex == element.SourceIndex);
        return point?.SourceAttributes.GetValueOrDefault("type") switch
        {
            "5" => $"Shop — {element.Name}",
            "7" => $"Fishing spot — {element.Name}",
            _ => element.Name,
        };
    }

    private void SelectFromOpsTree()
    {
        if (refreshingOpsElementTree
            || opsElementTree.SelectedNode?.Tag is not SceneElementSelection selected)
        {
            return;
        }
        selection = selected;
        cameraHandle = SceneCameraHandle.Eye;
        RefreshRenderInstances(uploadedModels.ToDictionary(
            value => value.AssetId,
            StringComparer.OrdinalIgnoreCase));
        RefreshOverlay();
        RefreshElementProperties();
        SyncOutlinerSelection();
        Text = $"{baseTitle} — selected: {DescribeSelection(selected)}";
    }

    private void FocusOpsNode(TreeNode node)
    {
        if (node.Tag is not SceneElementSelection selected) return;
        if (selection != selected) opsElementTree.SelectedNode = node;
        FocusSelection();
    }

    private void EditOpsNode(TreeNode node)
    {
        if (node.Tag is not SceneElementSelection selected) return;
        if (selection != selected) opsElementTree.SelectedNode = node;
        FocusSelection();
        OpenOpsElementEditor(selected);
    }

    private void OpenOpsElementEditor(SceneElementSelection selected)
    {
        if (selected.Kind == SceneElementKind.LookPoint
            && currentMap?.Points.FirstOrDefault(value =>
                value.SourceIndex == selected.SourceIndex) is { } point
            && point.SourceAttributes.TryGetValue("type", out var pointType))
        {
            if (pointType == "5")
            {
                OpenShopEditor(point);
                return;
            }
            if (pointType == "7")
            {
                OpenFishingSpotEditor(selected, point);
                return;
            }
        }

        var attributeSet = document.FindElementAttributes(selected);
        if (attributeSet is null) return;
        var descriptors = attributeSet.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(attribute =>
            {
                var kind = OpsAttributeValueKinds.Resolve(
                    selected, attribute.Key, attributeSet.Values);
                var choices = GetOpsAttributeChoices(kind)
                    .Select(value => new OpsAttributeChoice(value.Value, value.Label))
                    .ToArray();
                return new OpsAttributeEditorDescriptor(
                    attribute.Key,
                    attribute.Value,
                    attributeSet.ProtectedNames.Contains(attribute.Key),
                    choices);
            })
            .ToArray();

        using var editor = new OpsElementEditorForm(
            $"{OpsElementKindLabel(selected.Kind)} — {selected.Name}",
            descriptors);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var attributes = editor.ReadAttributes();
            if (!document.ApplyElementAttributes(selected, attributes)) return;
            var displayNameAttribute = selected.Kind == SceneElementKind.Sound
                ? "seName"
                : "name";
            if (attributes.TryGetValue(displayNameAttribute, out var editedName)
                && !string.IsNullOrWhiteSpace(editedName))
            {
                selection = selected with { Name = editedName.Trim() };
            }
            RefreshSceneFromDocument();
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(
                this, exception.Message, "Invalid OPS element",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenShopEditor(MapPoint point)
    {
        var tableDirectory = ResolveTableDirectory();
        var shopPath = tableDirectory is null
            ? null
            : Path.Combine(tableDirectory, "t_shop.tbl");
        if (shopPath is null || !File.Exists(shopPath))
        {
            MessageBox.Show(
                this,
                "t_shop.tbl was not found for the current script locale.",
                "Shop editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            OpenScriptEditor(activateTab: false);
            var bindings = scriptEditor?.FindShopBindings(point.Name)
                ?? Array.Empty<ShopScriptBinding>();
            var table = Cs1ShopTable.Read(shopPath);
            var items = GetTableChoices(new Cs1TableReference("t_item.tbl", "item"));
            using var editor = new ShopEditorForm(point.Name, bindings, table, items);
            if (editor.ShowDialog(this) != DialogResult.OK) return;

            var result = editor.ReadResult();
            TrackModSave(shopPath, beforeWrite: true);
            table.SetTitleName(result.ShopId, result.Title);
            table.ReplaceItems(result.ShopId, result.Items);
            table.Write();
            TrackModSave(shopPath, beforeWrite: false);
            tableCatalog?.Invalidate(shopPath);
            scriptEditor?.UpdateShopBinding(result.OriginalBinding, result.ShopId);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Cannot edit shop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenFishingSpotEditor(
        SceneElementSelection selected,
        MapPoint point)
    {
        OpenScriptEditor(activateTab: false);
        var bindings = scriptEditor?.FindFishingSpotBindings(point.Name)
            ?? Array.Empty<FishingSpotScriptBinding>();
        var waterTarget = point.SourceAttributes.TryGetValue("markPos", out var markPos)
            && TryParseOpsVector(markPos, out var parsedTarget)
                ? parsedTarget
                : (Vector3?)null;
        using var editor = new FishingSpotEditorForm(
            point.Name,
            point.Position,
            point.Radius ?? 1.5f,
            waterTarget,
            bindings);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var result = editor.ReadResult();
            if (result.OriginalBinding is not null && result.UpdatedBinding is not null)
            {
                scriptEditor?.UpdateFishingSpotBinding(
                    result.OriginalBinding,
                    result.UpdatedBinding);
            }

            var attributeSet = document.FindElementAttributes(selected)
                ?? throw new InvalidOperationException("The fishing LookPoint no longer exists.");
            var attributes = new Dictionary<string, string>(
                attributeSet.Values,
                StringComparer.Ordinal)
            {
                ["name"] = result.FunctionName,
                ["pos"] = FormatOpsVector(result.InteractionPosition),
                ["radius"] = result.Radius.ToString(
                    "G9", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (result.UpdatedBinding is { } binding)
                attributes["markPos"] = FormatOpsVector(binding.WaterTarget);
            document.ApplyElementAttributes(selected, attributes);
            selection = selected with { Name = result.FunctionName };
            RefreshSceneFromDocument();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot update fishing spot",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private IReadOnlyList<OpsInputChoice> GetOpsAttributeChoices(OpsValueKind kind) =>
        kind switch
        {
            OpsValueKind.ScriptFunction => GetCurrentScriptFunctionChoices(),
            OpsValueKind.DestinationMap => GetDestinationMapChoices(),
            OpsValueKind.MapSoundSource => GetMapSoundChoices(),
            _ => Array.Empty<OpsInputChoice>(),
        };

    private static string OpsElementKindLabel(SceneElementKind kind) =>
        kind switch
        {
            SceneElementKind.EntryVolume => "Map transition",
            SceneElementKind.GroupVolume => "Trigger volume",
            SceneElementKind.LookPoint => "Look point",
            SceneElementKind.Camera => "Map camera",
            SceneElementKind.Sound => "Sound object",
            SceneElementKind.Light => "Light",
            _ => "OPS element",
        };

    private static bool TryParseOpsVector(string text, out Vector3 value)
    {
        value = Vector3.Zero;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out var x)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out var y)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out var z))
        {
            return false;
        }
        value = new Vector3(x, y, z);
        return true;
    }

    private static string FormatOpsVector(Vector3 value)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(", ",
            value.X.ToString("G9", culture),
            value.Y.ToString("G9", culture),
            value.Z.ToString("G9", culture));
    }

    private void SyncOpsElementSelection()
    {
        refreshingOpsElementTree = true;
        try
        {
            opsElementTree.SelectedNode = opsElementTree.Nodes
                .Cast<TreeNode>()
                .SelectMany(group => group.Nodes.Cast<TreeNode>())
                .FirstOrDefault(node =>
                    node.Tag is SceneElementSelection candidate
                    && candidate == selection);
        }
        finally
        {
            refreshingOpsElementTree = false;
        }
    }

    /// <summary>
    /// Encounters are battle definitions (CreateMonsters tables) plus zero or
    /// more OP19 instances scattered through code functions. Keeping both in
    /// one tree makes the one-to-many relationship explicit.
    /// </summary>
    private void BuildEncountersTab()
    {
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(4),
        };
        tools.Controls.Add(newEncounterButton);
        tools.Controls.Add(editEncounterButton);
        tools.Controls.Add(instantiateEncounterButton);
        encountersTab.Controls.Add(encountersTree);
        encountersTab.Controls.Add(tools);

        newEncounterButton.Click += (_, _) =>
        {
            OpenScriptEditor();
            scriptEditor?.CreateEncounter();
        };
        editEncounterButton.Click += (_, _) => EditSelectedEncounter();
        instantiateEncounterButton.Click += (_, _) => InstantiateSelectedEncounter();
        encountersTree.AfterSelect += (_, _) => RefreshEncounterButtons();
        encountersTree.NodeMouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left
                && eventArgs.Node.Tag is EncounterInstanceNode instance)
            {
                FocusEncounterInstanceOnMap(instance.Spawn);
            }
        };
        encountersTree.NodeMouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Node.Tag is EncounterInstanceNode instance)
                NavigateToEncounterInstanceScript(instance.Spawn);
            else
                EditSelectedEncounter();
        };
        RefreshEncounterButtons();
    }

    private void RefreshEncounterBrowser(DecompiledScript script)
    {
        var spawns = ScriptMonsterSpawnReader.Read(script);
        encountersTree.BeginUpdate();
        try
        {
            encountersTree.Nodes.Clear();
            foreach (var function in script.Functions.Where(value =>
                         value.Table is not null
                         && CreateMonstersTableReader.TryRead(value.Table, out _)))
            {
                CreateMonstersTableReader.TryRead(function.Table!, out var table);
                var tableNode = new TreeNode(
                    $"{function.Name} — {table!.MapAsset} ({table.Encounters.Count})")
                {
                    Tag = new EncounterTableNode(function.Index),
                };
                foreach (var encounter in table.Encounters)
                {
                    var monsterNames = encounter.MonsterAssets
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(value =>
                        {
                            var name = monsterTableChoices.FirstOrDefault(choice =>
                                choice.AssetId.Equals(value, StringComparison.OrdinalIgnoreCase))
                                ?.DisplayName;
                            return string.IsNullOrWhiteSpace(name)
                                ? value
                                : $"{name} ({value})";
                        });
                    var linked = spawns.Where(value =>
                            value.BattleFunctionIndex == function.Index
                            && value.EncounterIndex == encounter.Id)
                        .ToArray();
                    var encounterNode = new TreeNode(
                        $"Encounter {encounter.Id} — {string.Join(", ", monsterNames)}"
                        + $" — {linked.Length} instance(s)")
                    {
                        Tag = new EncounterDefinitionNode(
                            function.Index,
                            encounter.Index,
                            encounter.Id),
                    };
                    foreach (var spawn in linked)
                    {
                        var owner = script.Functions.ElementAtOrDefault(spawn.SourceFunctionIndex)?.Name
                            ?? $"function #{spawn.SourceFunctionIndex}";
                        encounterNode.Nodes.Add(new TreeNode(
                            $"{owner} #{spawn.SourceInstructionIndex}"
                            + $" — ({spawn.Position.X:0.##}, {spawn.Position.Y:0.##}, {spawn.Position.Z:0.##})")
                        {
                            Tag = new EncounterInstanceNode(spawn),
                        });
                    }
                    tableNode.Nodes.Add(encounterNode);
                }
                tableNode.Expand();
                encountersTree.Nodes.Add(tableNode);
            }
        }
        finally
        {
            encountersTree.EndUpdate();
        }
        RefreshEncounterButtons();
    }

    private void RefreshEncounterButtons()
    {
        var hasEncounter = SelectedEncounterNode() is not null;
        editEncounterButton.Enabled = hasEncounter;
        instantiateEncounterButton.Enabled = hasEncounter;
    }

    private EncounterDefinitionNode? SelectedEncounterNode()
    {
        var node = encountersTree.SelectedNode;
        if (node?.Tag is EncounterDefinitionNode encounter) return encounter;
        if (node?.Tag is EncounterInstanceNode
            && node.Parent?.Tag is EncounterDefinitionNode owner)
        {
            return owner;
        }
        return null;
    }

    private void EditSelectedEncounter()
    {
        if (SelectedEncounterNode() is not { } encounter) return;
        OpenScriptEditor();
        scriptEditor?.EditEncounter(encounter.TableFunctionIndex, encounter.EncounterIndex);
    }

    private void InstantiateSelectedEncounter()
    {
        if (SelectedEncounterNode() is not { } encounter) return;
        OpenScriptEditor(activateTab: false);
        scriptEditor?.InstantiateEncounter(
            encounter.TableFunctionIndex,
            encounter.EncounterIndex);
    }

    private void FocusEncounterInstanceOnMap(ScriptMonsterSpawn spawn)
    {
        if (!showFieldMonstersCheckBox.Checked)
            showFieldMonstersCheckBox.Checked = true;
        var rendered = fieldMonsterSpawns.FirstOrDefault(value =>
            value.Value.SourceFunctionIndex == spawn.SourceFunctionIndex
            && value.Value.SourceInstructionIndex == spawn.SourceInstructionIndex);
        if (rendered.Value is null)
        {
            cameraNavigation.Focus(
                spawn.Position,
                Math.Max(sceneRadius * 0.02f, 1f));
            Text = $"{baseTitle} — encounter {spawn.EncounterIndex}: "
                + $"{spawn.AssetId} model unavailable";
            return;
        }
        selection = new SceneElementSelection(
            SceneElementKind.FieldMonster,
            rendered.Key,
            $"{spawn.AssetId} — encounter {spawn.EncounterIndex}");
        FocusSelection();
        RefreshRenderInstances(uploadedModels.ToDictionary(
            value => value.AssetId,
            StringComparer.OrdinalIgnoreCase));
        RefreshOutliner();
    }

    private void NavigateToEncounterInstanceScript(ScriptMonsterSpawn spawn)
    {
        OpenScriptEditor();
        rightPanelTabs.SelectedTab = scriptsTab;
        scriptEditor?.GoToInstruction(
            spawn.SourceFunctionIndex,
            spawn.SourceInstructionIndex);
    }

    private IReadOnlyList<BattleMapAssetEntry> GetBattleMapAssets()
    {
        if (session.Script.GameDataPath is not { } gameDataPath)
            return Array.Empty<BattleMapAssetEntry>();
        try
        {
            return new BattleMapAssetCatalog(gameDataPath).Entries;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Could not enumerate battle maps: {exception.Message}");
            return Array.Empty<BattleMapAssetEntry>();
        }
    }

    private BattleMapAssetEntry? CreateBattleMapInf(IWin32Window owner)
    {
        if (session.Script.GameDataPath is not { } gameDataPath) return null;
        using var dialog = new Form
        {
            Text = "New battle map metadata",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(480, 145),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var explanation = new Label
        {
            Left = 12,
            Top = 10,
            Width = 450,
            Height = 42,
            Text = "Creates data/map/battle/<asset>/<asset>.inf. "
                + "This metadata file does not create battle geometry.",
        };
        var input = new TextBox
        {
            Left = 12,
            Top = 58,
            Width = 450,
            PlaceholderText = "Battle map asset, e.g. bm9990",
        };
        var create = new Button
        {
            Text = "Create .inf",
            DialogResult = DialogResult.OK,
            Left = 278,
            Top = 100,
            Width = 90,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 374,
            Top = 100,
            Width = 88,
        };
        dialog.Controls.AddRange(new Control[] { explanation, input, create, cancel });
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;

        try
        {
            var catalog = new BattleMapAssetCatalog(gameDataPath);
            var assetId = input.Text.Trim();
            var result = catalog.CreateMinimalInf(assetId);
            TrackModSave(result.InfPath, beforeWrite: false);
            return result;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(
                owner,
                exception.Message,
                "Cannot create battle map metadata",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }
    }

    private sealed record EncounterTableNode(int TableFunctionIndex);
    private sealed record EncounterDefinitionNode(
        int TableFunctionIndex,
        int EncounterIndex,
        int EncounterId);
    private sealed record EncounterInstanceNode(ScriptMonsterSpawn Spawn);

    /// <summary>
    /// The mod tab lists every game file this project has written, keeps the
    /// pristine copies that make the install restorable, and ships the whole set
    /// as an archive that extracts straight over a game folder.
    /// </summary>
    private void BuildModProjectTab()
    {
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4),
        };
        var addScript = new Button { AutoSize = true, Text = "Add script…" };
        addScript.Click += (_, _) => AddScriptToProject();
        var newProject = new Button { AutoSize = true, Text = "New…" };
        var openProject = new Button { AutoSize = true, Text = "Open…" };
        var exportArchive = new Button { AutoSize = true, Text = "Export .zip…" };
        var applyMod = new Button { AutoSize = true, Text = "Re-apply mod" };
        var restoreAll = new Button { AutoSize = true, Text = "Restore originals" };
        newProject.Click += (_, _) => CreateModProject();
        openProject.Click += (_, _) => OpenModProject();
        exportArchive.Click += (_, _) => ExportModArchive();
        applyMod.Click += (_, _) => ApplyModFiles(selectedOnly: false);
        restoreAll.Click += (_, _) => RestoreModOriginals(selectedOnly: false);
        tools.Controls.AddRange(new Control[]
        {
            addScript, newProject, openProject, exportArchive, applyMod, restoreAll,
        });

        var fileMenu = new ContextMenuStrip();
        fileMenu.Items.Add("Restore this file", null, (_, _) => RestoreModOriginals(selectedOnly: true));
        fileMenu.Items.Add("Re-apply this file", null, (_, _) => ApplyModFiles(selectedOnly: true));
        fileMenu.Items.Add(
            "Add an existing file to the mod… (no original is kept)",
            null,
            (_, _) => IncludeModFile());
        fileMenu.Items.Add("Remove from the mod", null, (_, _) => RemoveModFile());
        modFileTree.ContextMenuStrip = fileMenu;
        modFileTree.NodeMouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right)
                modFileTree.SelectedNode = eventArgs.Node;
        };
        modFileTree.NodeMouseDoubleClick += (_, eventArgs) =>
        {
            if (modProject is null || eventArgs.Node.Tag is not string relative) return;
            var path = modProject.GameFilePath(relative);
            if (path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) OpenScriptFile(path);
            else if (path.EndsWith(".ops", StringComparison.OrdinalIgnoreCase))
            {
                // An OPS belongs to a scenario: open the script that drives it.
                var script = Path.ChangeExtension(
                    Path.Combine(
                        Path.GetDirectoryName(session.Script.Header.SourcePath)!,
                        Path.GetFileName(path)),
                    ".dat");
                if (File.Exists(script)) OpenScriptFile(script);
            }
        };

        var treeGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Mod files (game-relative paths)" };
        treeGroup.Controls.Add(modFileTree);
        modPanel.Controls.Add(treeGroup);
        modPanel.Controls.Add(tools);
        modPanel.Controls.Add(modProjectLabel);
    }

    /// <summary>Opens a project and lists its files, without touching the scene.</summary>
    public void LoadModProject(string projectPath)
    {
        RunModProjectAction(() =>
        {
            modProject = ModProject.Open(projectPath);
            settingsStore.Save(settingsStore.Load() with { LastProjectPath = modProject.ProjectPath });
            RefreshModProjectTab();
        });
    }

    /// <summary>
    /// Adds a script to the project and opens it. This is how a project fills up:
    /// it starts empty, and the other files (its OPS, its packages) join it on
    /// their own the first time they are written.
    /// </summary>
    private void AddScriptToProject()
    {
        if (modProject is null)
        {
            MessageBox.Show(
                this, "Create or open a mod project first.", "No project",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var scenario = Path.Combine(modProject.GameDirectory, "data", "scripts", "scena");
        using var dialog = new OpenFileDialog
        {
            Title = "Add a script to the mod project",
            Filter = "Cold Steel scripts (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(scenario)
                ? scenario
                : Path.Combine(modProject.GameDirectory, "data"),
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunModProjectAction(() =>
        {
            foreach (var file in dialog.FileNames) modProject.Include(file);
            RefreshModProjectTab();
        });
        if (dialog.FileNames.Length > 0) OpenScriptFile(dialog.FileNames[0]);
    }

    private void CreateModProject()
    {
        if (session.Script.GameDataPath is null) return;
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(session.Script.GameDataPath));
        if (gameRoot is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Create a mod project",
            Filter = "ED8 mod project (*.ed8mod)|*.ed8mod",
            FileName = "my-mod.ed8mod",
            AddExtension = true,
            DefaultExt = "ed8mod",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunModProjectAction(() =>
        {
            modProject = ModProject.Create(dialog.FileName, gameRoot);
            settingsStore.Save(settingsStore.Load() with { LastProjectPath = modProject.ProjectPath });
            RefreshModProjectTab();
        });
    }

    private void OpenModProject()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a mod project",
            Filter = "ED8 mod project (*.ed8mod)|*.ed8mod|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunModProjectAction(() =>
        {
            modProject = ModProject.Open(dialog.FileName);
            settingsStore.Save(settingsStore.Load() with { LastProjectPath = modProject.ProjectPath });
            RefreshModProjectTab();
        });
    }

    private void ExportModArchive()
    {
        if (modProject is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Export the mod for distribution",
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = $"{modProject.Name}.zip",
            AddExtension = true,
            DefaultExt = "zip",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunModProjectAction(() =>
        {
            var count = modProject.ExportArchive(dialog.FileName);
            MessageBox.Show(
                this,
                $"{count} file(s) written to {dialog.FileName}.\n\n"
                + "The archive keeps the game-relative paths, so it extracts straight"
                + " over a Trails of Cold Steel folder.",
                "Mod exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void ApplyModFiles(bool selectedOnly)
    {
        if (modProject is null) return;
        var selection = selectedOnly ? SelectedModFiles() : null;
        if (selectedOnly && selection is { Count: 0 }) return;
        RunModProjectAction(() =>
        {
            var count = modProject.ApplyMod(selection);
            RefreshModProjectTab();
            MessageBox.Show(
                this, $"{count} file(s) copied into the game folder.",
                "Mod applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void RestoreModOriginals(bool selectedOnly)
    {
        if (modProject is null) return;
        var selection = selectedOnly ? SelectedModFiles() : null;
        if (selectedOnly && selection is { Count: 0 }) return;
        var scope = selectedOnly ? "the selected file(s)" : "every file of this mod";
        var confirmation = MessageBox.Show(
            this,
            $"Put the game's original version of {scope} back?\n\n"
            + "Files the mod added and the game never had are deleted.",
            "Restore originals", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes) return;
        RunModProjectAction(() =>
        {
            var count = modProject.RestoreOriginals(selection);
            RefreshModProjectTab();
            MessageBox.Show(
                this, $"{count} file(s) restored.",
                "Originals restored", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void IncludeModFile()
    {
        if (modProject is null) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Add a game file to the mod",
            InitialDirectory = modProject.GameDirectory,
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunModProjectAction(() =>
        {
            foreach (var file in dialog.FileNames) modProject.Include(file);
            RefreshModProjectTab();
        });
    }

    private void RemoveModFile()
    {
        if (modProject is null) return;
        if (modFileTree.SelectedNode?.Tag is not string relative)
        {
            MessageBox.Show(
                this,
                "Select one file, not a folder, before removing it from the mod.",
                "Remove mod file",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(
                this,
                $"Remove only this file from the mod project?\n\n{relative}\n\n"
                + "The game file itself is not deleted or restored.",
                "Remove mod file",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        RunModProjectAction(() =>
        {
            modProject.Remove(relative);
            RefreshModProjectTab();
        });
    }

    private IReadOnlyList<string> SelectedModFiles()
    {
        if (modFileTree.SelectedNode is null) return Array.Empty<string>();
        var selected = new List<string>();
        Collect(modFileTree.SelectedNode);
        return selected;

        void Collect(TreeNode node)
        {
            if (node.Tag is string relative) selected.Add(relative);
            foreach (TreeNode child in node.Nodes) Collect(child);
        }
    }

    private void RunModProjectAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidDataException or DirectoryNotFoundException)
        {
            MessageBox.Show(
                this, exception.Message, "Mod project", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshModProjectTab()
    {
        modFileTree.BeginUpdate();
        try
        {
            modFileTree.Nodes.Clear();
            if (modProject is null)
            {
                modProjectLabel.Text = "No mod project open. Create one to track and ship your edits.";
                return;
            }
            modProjectLabel.Text = $"{modProject.Name} — {modProject.Files.Count} file(s)"
                + $"\n{modProject.ProjectPath}";
            foreach (var file in modProject.Files)
            {
                var nodes = modFileTree.Nodes;
                TreeNode? current = null;
                for (var index = 0; index < file.Segments.Count; index++)
                {
                    var segment = file.Segments[index];
                    var existing = nodes.Cast<TreeNode>()
                        .FirstOrDefault(value => value.Text.Equals(segment, StringComparison.OrdinalIgnoreCase));
                    current = existing ?? nodes.Add(segment);
                    nodes = current.Nodes;
                }
                if (current is null) continue;
                current.Tag = file.RelativePath;
                if (!file.HasOriginal) current.Text += "  (new file)";
            }
            modFileTree.ExpandAll();
        }
        finally
        {
            modFileTree.EndUpdate();
        }
    }

    /// <summary>
    /// Keeps the mod project in step with a save into the game folder: the
    /// pristine copy is taken before the write, the mod's own copy after it.
    /// </summary>
    private void TrackModSave(string path, bool beforeWrite)
    {
        if (modProject is null) return;
        try
        {
            if (beforeWrite) modProject.CaptureOriginal(path);
            else
            {
                modProject.TrackSave(path);
                RefreshModProjectTab();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or FileNotFoundException)
        {
            // A save outside the game folder is not part of the mod; never let
            // bookkeeping fail the save itself.
            Debug.WriteLine($"Mod project could not track '{path}': {exception.Message}");
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
            TrackModSave(targetPath!, beforeWrite: true);
            opsWriter.Write(targetPath!, session.Map, currentMap);
            TrackModSave(targetPath!, beforeWrite: false);
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

    /// <summary>
    /// Opens a script of the project. A scenario brings its map; a script that has
    /// none (an animation or a craft) is shown over a plain ground, with the actor
    /// it drives if the game's tables name one.
    /// </summary>
    public bool OpenScriptFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var full = Path.GetFullPath(path);
        if (session.Script.Header.SourcePath.Equals(full, StringComparison.OrdinalIgnoreCase))
        {
            SelectFileTab(full);
            return true;
        }
        EditorSession opened;
        try
        {
            opened = projectLoader.OpenScript(full, session.Script.GameDataPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot open script", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        ClearScriptEntityPreview();
        session = opened;
        document = new EditorSceneDocument(session);
        document.Changed += (_, _) => RefreshSceneFromDocument();
        document.PreviewChanged += (_, _) => RefreshScenePreviewFromDocument();
        savedOpsPath = null;
        selection = null;
        scriptSubject = null;
        if (session.Script.GameDataPath is { } gameDataPath)
        {
            scriptAnimationLibrary = new ScriptAnimationLibrary(
                gameDataPath, session.Script.Header.SourcePath, instructionDefinitionsPath);
            systemScript = new ScriptSystemLibrary(
                session.Script.Header.SourcePath, instructionDefinitionsPath).Script;
            scriptSubject = ScriptSubjectResolver.Resolve(
                session.Script.Header.SourcePath, gameDataPath, scriptAnimationLibrary);
            attachTable = ScriptAttachTable.Load(gameDataPath);
        }
        baseTitle = $"ED8Editor — {session.Script.Header.Identifier}"
            + (scriptSubject is null ? string.Empty : $" — {scriptSubject.ModelAssetId}")
            + " — 1: move, 2: rotate, 3: scale, Ctrl+click: select through, Ctrl+Z/Y: undo/redo";
        Text = baseTitle;
        LoadSceneForSession();
        InitializeAssetCatalog();
        _ = LoadEffectMetadataAsync();
        scriptEditor?.LoadDat(full);
        _ = LoadFieldMonstersFromPathAsync(full);
        AddFileTab(full);
        _ = RefreshSubjectPreviewAsync();
        return true;
    }

    private void AddFileTab(string path)
    {
        if (!openFiles.Any(value => value.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            openFiles.Add(path);
            openFileTabs.TabPages.Add(new TabPage(Path.GetFileName(path)) { ToolTipText = path });
        }
        openFileTabs.Visible = openFiles.Count > 0;
        SelectFileTab(path);
    }

    private void SelectFileTab(string path)
    {
        var index = openFiles.FindIndex(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || openFileTabs.SelectedIndex == index) return;
        switchingFile = true;
        try
        {
            openFileTabs.SelectedIndex = index;
        }
        finally
        {
            switchingFile = false;
        }
    }

    private void SelectOpenFileTab()
    {
        if (switchingFile) return;
        var index = openFileTabs.SelectedIndex;
        if (index < 0 || index >= openFiles.Count) return;
        OpenScriptFile(openFiles[index]);
    }

    /// <summary>
    /// Shows the actor a map-less script drives, bound to the script's own "self"
    /// reference so its animation and camera commands play on it.
    /// </summary>
    private async Task RefreshSubjectPreviewAsync()
    {
        if (scriptSubject is null || session.Script.GameDataPath is null) return;
        var entities = new Dictionary<int, ScriptEntityState>
        {
            [ScriptEntityReferences.SelfEntityId] = CreateSubjectEntity(scriptSubject),
        };
        activeScriptEntities = entities;
        await RefreshScriptEntitiesAsync(entities);
    }

    private ScriptEntityState CreateSubjectEntity(ScriptSubject subject)
        => new(
            ScriptEntityReferences.SelfEntityId,
            subject.ModelAssetId,
            subject.ScriptName,
            string.Empty,
            0,
            0,
            Vector3.Zero,
            0f,
            1f,
            0f,
            0f,
            subject.ScriptName,
            string.Empty,
            0, 0, 0, 0, 0,
            Array.Empty<Vector3>(),
            HasSpawnDefinition: true,
            HasPosition: true,
            ReferenceSymbol: "Self",
            FacialAssetId: scriptAnimationLibrary?.ResolveFacialAsset(-1, subject.ModelAssetId) ?? string.Empty);

    /// <summary>
    /// Entities of a replay, with the actor of a map-less script filled in: its
    /// script only ever refers to itself, so "self" carries no model of its own.
    /// </summary>
    private IReadOnlyDictionary<int, ScriptEntityState> PrepareEntities(
        IReadOnlyDictionary<int, ScriptEntityState> entities)
    {
        if (scriptSubject is null) return entities;
        var prepared = new Dictionary<int, ScriptEntityState>(entities);
        var self = ScriptEntityReferences.SelfEntityId;
        prepared[self] = prepared.TryGetValue(self, out var existing)
            ? existing with
            {
                AssetId = scriptSubject.ModelAssetId,
                DisplayName = scriptSubject.ScriptName,
                IsPlaceholder = false,
                HasPosition = true,
            }
            : CreateSubjectEntity(scriptSubject);
        return prepared;
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
        if (selected.Kind == SceneElementKind.FieldMonster)
        {
            DeleteSelectedFieldMonster(selected);
            return;
        }
        selection = null;
        if (document.DeleteElement(selected)) RefreshSceneFromDocument();
    }

    private void DeleteSelectedFieldMonster(SceneElementSelection selected)
    {
        if (!fieldMonsterSpawns.TryGetValue(selected.SourceIndex, out var spawn)) return;
        if (MessageBox.Show(
                this,
                $"Delete field monster {spawn.AssetId} (entity {spawn.EntityId}) from the script?",
                "Delete field monster",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        OpenScriptEditor();
        try
        {
            scriptEditor!.DeleteFieldMonster(spawn);
            selection = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            MessageBox.Show(
                this, exception.Message, "Cannot delete field monster",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    private void BeginScriptSurfacePositionCapture(Action<Vector3> applyPosition)
    {
        scriptSurfacePositionCapture =
            applyPosition ?? throw new ArgumentNullException(nameof(applyPosition));
        scriptSurfacePositionPreview = null;
        Text = $"{baseTitle} — click a map surface to capture a script position; Esc cancels";
        viewportHost.Focus();
        var pointer = viewportHost.PointToClient(Cursor.Position);
        if (viewportHost.ClientRectangle.Contains(pointer))
            QueueScriptSurfacePositionPreview(pointer);
    }

    private void QueueScriptSurfacePositionPreview(Point location)
    {
        if (scriptSurfacePositionCapture is null
            || viewportHost.ClientSize.Width <= 0
            || viewportHost.ClientSize.Height <= 0)
        {
            return;
        }
        queuedScriptSurfacePreview = (
            ++scriptSurfacePreviewRequestId,
            CreatePointerRay(location),
            VisibleSceneInstances().ToArray());
        RunQueuedScriptSurfacePreviewRaycast();
    }

    private async void RunQueuedScriptSurfacePreviewRaycast()
    {
        if (scriptSurfacePreviewRaycastRunning) return;
        scriptSurfacePreviewRaycastRunning = true;
        try
        {
            while (queuedScriptSurfacePreview is { } request
                   && scriptSurfacePositionCapture is not null
                   && !IsDisposed)
            {
                queuedScriptSurfacePreview = null;
                var hit = await Task.Run(() =>
                    surfacePreviewRaycaster.Cast(request.Ray, request.Instances).Hit);
                if (IsDisposed) return;
                if (scriptSurfacePositionCapture is not null
                    && request.RequestId == scriptSurfacePreviewRequestId)
                {
                    scriptSurfacePositionPreview = hit;
                    RefreshOverlay();
                }
            }
        }
        finally
        {
            scriptSurfacePreviewRaycastRunning = false;
            if (queuedScriptSurfacePreview is not null
                && scriptSurfacePositionCapture is not null
                && !IsDisposed)
            {
                RunQueuedScriptSurfacePreviewRaycast();
            }
        }
    }

    private float GetSurfacePlacementMarkerRadius(Vector3 position)
    {
        var distance = Math.Max(Vector3.Distance(cameraNavigation.Position, position), 0.01f);
        var worldUnitsPerPixel = 2f * distance * MathF.Tan(CameraVerticalFieldOfView * 0.5f)
            / Math.Max(1, viewportHost.ClientSize.Height);
        return Math.Clamp(worldUnitsPerPixel * 15f, 0.06f, 0.75f);
    }

    private void CaptureScriptSurfacePosition(Point location)
    {
        if (scriptSurfacePositionCapture is not { } applyPosition
            || viewportHost.ClientSize.Width <= 0
            || viewportHost.ClientSize.Height <= 0)
        {
            return;
        }
        var hit = surfaceRaycaster.Cast(
            CreatePointerRay(location), VisibleSceneInstances()).Hit;
        if (hit is null)
        {
            Text = $"{baseTitle} — no surface at cursor; click another point or press Esc";
            return;
        }
        scriptSurfacePositionCapture = null;
        scriptSurfacePositionPreview = null;
        queuedScriptSurfacePreview = null;
        scriptSurfacePreviewRequestId++;
        RefreshOverlay();
        applyPosition(hit.Position);
        Text = $"{baseTitle} — script position captured: "
            + $"{hit.Position.X:0.###}, {hit.Position.Y:0.###}, {hit.Position.Z:0.###}";
    }

    private void CancelScriptSurfacePositionCapture()
    {
        scriptSurfacePositionCapture = null;
        scriptSurfacePositionPreview = null;
        queuedScriptSurfacePreview = null;
        scriptSurfacePreviewRequestId++;
        RefreshOverlay();
        Text = baseTitle;
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
            var row = new TableLayoutPanel
            {
                Width = Math.Max(220, opsInputPanel.ClientSize.Width - 20),
                Height = input.Kind == OpsValueKind.ScriptFunction ? 58 : 32,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = input.DisplayName,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, 0);
            var field = CreateOpsInputEditor(input);
            row.Controls.Add(field, 1, 0);
            opsInputFields.Add(input.Name, field);
            opsInputPanel.Controls.Add(row);
        }

        if (opsInputFields.TryGetValue("next", out var destinationMap)
            && destinationMap is ComboBox mapList
            && opsInputFields.TryGetValue("entry", out var destinationEntry)
            && destinationEntry is ComboBox entryList)
        {
            mapList.SelectedIndexChanged += async (_, _) =>
                await PopulateDestinationEntriesAsync(mapList, entryList);
            mapList.TextChanged += async (_, _) =>
                await PopulateDestinationEntriesAsync(mapList, entryList);
            if (mapList.Items.Count > 0) mapList.SelectedIndex = 0;
        }
    }

    private void BeginOpsPlacement()
    {
        if (opsProfileList.SelectedItem is not OpsSpatialCreationProfile profile) return;
        try
        {
            var inputs = opsInputFields.ToDictionary(
                pair => pair.Key,
                pair => ReadOpsInputValue(pair.Value),
                StringComparer.Ordinal);
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

    private Control CreateOpsInputEditor(OpsCreationInput input)
    {
        if (input.Kind == OpsValueKind.Text)
            return new TextBox { Dock = DockStyle.Fill };

        if (input.Kind == OpsValueKind.ScriptFunction)
        {
            var currentScriptName = Path.GetFileName(session.Script.Header.SourcePath);
            var functionList = ChoiceCombo(GetCurrentScriptFunctionChoices());
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = $"Script: {currentScriptName}",
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true,
            }, 0, 0);
            panel.Controls.Add(functionList, 0, 1);
            panel.Tag = functionList;
            return panel;
        }

        var choices = input.Kind switch
        {
            OpsValueKind.DestinationMap => GetDestinationMapChoices(),
            OpsValueKind.MapSoundSource => GetMapSoundChoices(),
            _ => Array.Empty<OpsInputChoice>(),
        };
        return ChoiceCombo(choices);
    }

    private static ComboBox ChoiceCombo(IReadOnlyList<OpsInputChoice> choices)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        return combo;
    }

    private static string ReadOpsInputValue(Control control)
    {
        if (control.Tag is ComboBox nested) return ReadOpsInputValue(nested);
        if (control is ComboBox combo)
            return combo.SelectedItem is OpsInputChoice choice ? choice.Value : combo.Text.Trim();
        return control.Text.Trim();
    }

    private IReadOnlyList<OpsInputChoice> GetCurrentScriptFunctionChoices()
    {
        try
        {
            var script = scriptEditor?.CurrentScript
                ?? ScriptDecompiler.Decompile(
                    session.Script.Header.SourcePath,
                    instructionDefinitionsPath);
            return script.Functions
                .Where(value => value.IsCode)
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(value => new OpsInputChoice(value.Name, value.Name))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not enumerate OPS script functions: {exception.Message}");
            return Array.Empty<OpsInputChoice>();
        }
    }

    private IReadOnlyList<OpsInputChoice> GetDestinationMapChoices()
    {
        var directory = Path.GetDirectoryName(session.Script.Header.SourcePath);
        if (directory is null || !Directory.Exists(directory))
            return Array.Empty<OpsInputChoice>();
        return Directory.EnumerateFiles(directory, "*.dat", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(
                "system.dat", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new OpsInputChoice(value, value))
            .ToArray();
    }

    private IReadOnlyList<OpsInputChoice> GetMapSoundChoices()
    {
        var names = new List<string>();
        if (currentMap is not null)
            names.AddRange(currentMap.Sounds.Select(value => value.SoundName));
        try
        {
            var script = scriptEditor?.CurrentScript
                ?? ScriptDecompiler.Decompile(
                    session.Script.Header.SourcePath,
                    instructionDefinitionsPath);
            names.AddRange(script.Functions
                .Where(value => value.IsCode)
                .SelectMany(value => value.Instructions)
                // OP49 selector 0 registers the map sound source consumed by
                // SoundObject.seName. Its final operand is the source name.
                .Where(value => value.Opcode == 49 && value.Name == "OP49_0")
                .Select(value => value.Arguments.LastOrDefault(argument =>
                    argument.Kind == "string"))
                .Where(argument => argument is not null)
                .Select(argument => DecodeScriptString(argument!.Raw)));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not enumerate map sound sources: {exception.Message}");
        }
        return names
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new OpsInputChoice(value!, value!))
            .ToArray();
    }

    private static string DecodeScriptString(byte[] raw)
        => System.Text.Encoding.Latin1.GetString(raw).TrimEnd('\0');

    private async Task PopulateDestinationEntriesAsync(
        ComboBox mapList,
        ComboBox entryList)
    {
        var generation = ++opsCreationDestinationLoadGeneration;
        var mapId = ReadOpsInputValue(mapList);
        entryList.Items.Clear();
        var entries = await GetDestinationEntryChoicesAsync(mapId);
        if (IsDisposed
            || generation != opsCreationDestinationLoadGeneration
            || !mapId.Equals(
                ReadOpsInputValue(mapList), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        entryList.Items.AddRange(entries.Cast<object>().ToArray());
        if (entryList.Items.Count > 0) entryList.SelectedIndex = 0;
    }

    private async Task<IReadOnlyList<OpsInputChoice>> GetDestinationEntryChoicesAsync(
        string mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId)
            || session.Script.GameDataPath is not { } gameDataPath
            || Path.GetDirectoryName(session.Script.Header.SourcePath) is not { } scriptDirectory)
        {
            return Array.Empty<OpsInputChoice>();
        }
        var scriptPath = Path.Combine(scriptDirectory, mapId + ".dat");
        if (!File.Exists(scriptPath)) return Array.Empty<OpsInputChoice>();
        try
        {
            var entries = await Task.Run(() =>
            {
                var target = new ScriptBootstrapper().Open(scriptPath, gameDataPath);
                if (target.MapOpsPath is null) return Array.Empty<string>();
                var map = new OpsReader().Read(target.MapOpsPath);
                return map.Volumes
                    .Where(value => value.Kind == MapVolumeKind.Entry)
                    .Select(value => value.Name)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            });
            return entries
                .Select(value => new OpsInputChoice(value, value))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException
            or ArgumentException)
        {
            Debug.WriteLine(
                $"Could not enumerate destination entries for '{mapId}': {exception.Message}");
            return Array.Empty<OpsInputChoice>();
        }
    }

    private void RefreshElementProperties()
    {
        propertyGrid.Rows.Clear();
        var selected = selection;
        var attributeSet = selected is null ? null : document.FindElementAttributes(selected);
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
                continue;
            }
            var kind = OpsAttributeValueKinds.Resolve(
                selected!, attribute.Key, attributeSet.Values);
            if (kind != OpsValueKind.Text)
                row.Cells[1] = CreateOpsPropertyValueCell(kind, attribute.Value);
        }
        _ = RefreshPropertyDestinationEntriesAsync();
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
            if (!document.ApplyElementAttributes(selected, attributes)) return;
            var displayNameAttribute = selected.Kind == SceneElementKind.Sound
                ? "seName"
                : "name";
            if (attributes.TryGetValue(displayNameAttribute, out var editedName)
                && !string.IsNullOrWhiteSpace(editedName))
            {
                selection = selected with { Name = editedName.Trim() };
                RefreshOutliner();
                RefreshElementProperties();
            }
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Invalid OPS attributes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private DataGridViewComboBoxCell CreateOpsPropertyValueCell(
        OpsValueKind kind,
        string currentValue)
    {
        IReadOnlyList<OpsInputChoice> choices = kind switch
        {
            OpsValueKind.ScriptFunction => GetCurrentScriptFunctionChoices(),
            OpsValueKind.DestinationMap => GetDestinationMapChoices(),
            OpsValueKind.MapSoundSource => GetMapSoundChoices(),
            _ => Array.Empty<OpsInputChoice>(),
        };
        return CreateOpsPropertyChoiceCell(choices, currentValue);
    }

    private static DataGridViewComboBoxCell CreateOpsPropertyChoiceCell(
        IReadOnlyList<OpsInputChoice> choices,
        string currentValue)
    {
        var allChoices = choices.ToList();
        if (allChoices.All(value =>
                !value.Value.Equals(currentValue, StringComparison.OrdinalIgnoreCase)))
        {
            allChoices.Insert(0, new OpsInputChoice(
                currentValue,
                string.IsNullOrEmpty(currentValue) ? "(none)" : currentValue));
        }
        var cell = new DataGridViewComboBoxCell
        {
            DisplayMember = nameof(OpsInputChoice.Label),
            ValueMember = nameof(OpsInputChoice.Value),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
        cell.Items.AddRange(allChoices.Cast<object>().ToArray());
        cell.Value = currentValue;
        return cell;
    }

    private async Task RefreshPropertyDestinationEntriesAsync()
    {
        var selected = selection;
        if (selected is null) return;
        var mapRow = FindPropertyRow("next");
        var entryRow = FindPropertyRow("entry");
        if (mapRow is null || entryRow is null || entryRow.ReadOnly) return;
        var mapId = mapRow.Cells[1].Value?.ToString()?.Trim() ?? string.Empty;
        var currentEntry = entryRow.Cells[1].Value?.ToString() ?? string.Empty;
        var generation = ++opsPropertyDestinationLoadGeneration;
        var choices = await GetDestinationEntryChoicesAsync(mapId);
        if (IsDisposed
            || generation != opsPropertyDestinationLoadGeneration
            || selection != selected)
        {
            return;
        }
        entryRow.Cells[1] = CreateOpsPropertyChoiceCell(choices, currentEntry);
    }

    private DataGridViewRow? FindPropertyRow(string attributeName)
        => propertyGrid.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(row => !row.IsNewRow
                && string.Equals(
                    row.Cells[0].Value?.ToString(), attributeName,
                    StringComparison.Ordinal));

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
            .Concat(showFieldMonstersCheckBox.Checked
                ? fieldMonsterInstances
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

    private sealed record LoadedPropAnimation(
        CpuAnimationClip Clip,
        bool Loop,
        bool Reverse);

    private sealed record OpsInputChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

}
