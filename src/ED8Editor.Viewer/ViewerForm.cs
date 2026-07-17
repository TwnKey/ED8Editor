using System.Diagnostics;
using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Ops;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Rendering;
using ED8Editor.Scene;

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
    private readonly SceneTranslationGizmo translationGizmo = new();
    private readonly SceneRotationGizmo rotationGizmo = new();
    private readonly SceneCameraGizmo cameraGizmo = new();
    private readonly OpsWriter opsWriter = new();
    private readonly HashSet<Keys> pressedKeys = new();
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly List<D3D11ModelResources> uploadedModels = new();
    private readonly Dictionary<string, CpuModel> loadedModelsByAsset = new(StringComparer.OrdinalIgnoreCase);
    private readonly Panel viewportHost = new() { Dock = DockStyle.Fill, TabStop = true };
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
    private readonly Panel assetPanel = new() { Dock = DockStyle.Right, Width = 300, Padding = new Padding(8) };
    private readonly TextBox assetSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search PKG assets..." };
    private readonly CheckBox snapCheckBox = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        Text = "Snap: 0.25 units / 15 degrees / 0.1 scale",
    };
    private readonly ComboBox keyboardLayoutList = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        DropDownStyle = ComboBoxStyle.DropDownList,
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
    private MapScene? currentMap;
    private float sceneRadius = 10f;
    private float overlayMarkerSize = 0.3f;
    private Point previousMouse;
    private Point leftMouseDown;
    private bool pendingLeftClick;
    private bool lookCursorLocked;
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

    public ViewerForm(
        EditorSession session,
        bool smokeTest,
        EditorProjectLoader? projectLoader = null,
        EditorSettingsStore? settingsStore = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.settingsStore = settingsStore ?? new EditorSettingsStore();
        keyboardLayout = this.settingsStore.Load().KeyboardLayout;
        this.projectLoader = projectLoader ?? new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        document = new EditorSceneDocument(session);
        document.Changed += (_, _) => RefreshSceneFromDocument();
        this.smokeTest = smokeTest;
        baseTitle = $"ED8Editor — {session.Script.Header.Identifier} — 1: move, 2: rotate, 3: scale, Ctrl+Z/Y: undo/redo";
        Text = baseTitle;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(640, 360);
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        assetPanel.Controls.Add(assetList);
        assetPanel.Controls.Add(snapCheckBox);
        assetPanel.Controls.Add(keyboardLayoutList);
        assetPanel.Controls.Add(assetSearch);
        propertyGroup.Controls.Add(propertyGrid);
        propertyGroup.Controls.Add(applyPropertiesButton);
        sceneOutlinerGroup.Controls.Add(sceneOutliner);
        scenePanel.Controls.Add(sceneOutlinerGroup);
        scenePanel.Controls.Add(propertyGroup);
        opsCreationGroup.Controls.Add(opsInputPanel);
        opsCreationGroup.Controls.Add(opsProfileEvidence);
        opsCreationGroup.Controls.Add(opsProfileList);
        opsCreationGroup.Controls.Add(addOpsElementButton);
        assetPanel.Controls.Add(deleteButton);
        assetPanel.Controls.Add(duplicateButton);
        assetPanel.Controls.Add(addAssetButton);
        assetPanel.Controls.Add(opsCreationGroup);
        Controls.Add(viewportHost);
        Controls.Add(assetPanel);
        Controls.Add(scenePanel);
        assetSearch.TextChanged += (_, _) => FilterAssetCatalog();
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
        RefreshOpsCreationInputs();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        renderTimer.Tick += (_, _) => RenderFrame();
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode is Keys.D1 or Keys.D2 or Keys.D3)
            {
                gizmoMode = eventArgs.KeyCode switch
                {
                    Keys.D1 => GizmoMode.Translate,
                    Keys.D2 => GizmoMode.Rotate,
                    _ => GizmoMode.Scale,
                };
                RefreshOverlay();
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
            if (eventArgs.Button == MouseButtons.Right) BeginLookDrag(eventArgs.Location);
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
            EndCameraDrag();
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

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        try
        {
            InitializeRenderer();
            InitializeAssetCatalog();
            if (smokeTest)
            {
                RenderFrame();
                Close();
            }
            else
            {
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
        if (WindowState != FormWindowState.Minimized && viewportHost.ClientSize.Width > 0 && viewportHost.ClientSize.Height > 0)
        {
            viewport?.Resize(viewportHost.ClientSize.Width, viewportHost.ClientSize.Height);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
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
        RefreshRenderInstances(resourcesByAsset);

        var bounds = new SceneBoundsCalculator().Calculate(sceneInstances);
        var center = bounds.HasGeometry ? bounds.Center : Vector3.Zero;
        sceneRadius = Math.Max(bounds.Radius, 1f);
        var initialPosition = center + new Vector3(0, sceneRadius * 0.35f, -sceneRadius * 1.6f);
        cameraNavigation.Initialize(center, initialPosition);
        viewport = new D3D11Viewport(graphics, viewportHost.Handle, viewportHost.ClientSize.Width, viewportHost.ClientSize.Height);
        if (currentMap?.DefaultEnvironment is { } environment)
        {
            viewport.SetClearColor(new Vector4(environment.FogColor, 1f));
        }
        overlayMarkerSize = Math.Clamp(sceneRadius * 0.008f, 0.08f, 1.5f);
        RefreshOverlay();
        RefreshOutliner();
        previousFrameTicks = frameClock.ElapsedTicks;
    }

    private void RenderFrame()
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ticks = frameClock.ElapsedTicks;
        var elapsed = Math.Clamp((float)(ticks - previousFrameTicks) / Stopwatch.Frequency, 0f, 0.1f);
        previousFrameTicks = ticks;
        UpdateCamera(elapsed);
        var camera = CreateCamera();
        viewport.Render(instances, camera);
    }

    private ViewportCamera CreateCamera()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
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
    }

    private void ChangeKeyboardLayout()
    {
        keyboardLayout = keyboardLayoutList.SelectedIndex == 0
            ? EditorKeyboardLayout.Azerty
            : EditorKeyboardLayout.Qwerty;
        settingsStore.Save(settingsStore.Load() with { KeyboardLayout = keyboardLayout });
        pressedKeys.Clear();
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
            cameraNavigation.Look(lookDeltaX, lookDeltaY);
            CenterLookCursor();
            return;
        }
        var deltaX = current.X - previousMouse.X;
        var deltaY = current.Y - previousMouse.Y;
        previousMouse = current;
        cameraNavigation.Pan(deltaX, deltaY, viewportHost.ClientSize.Height, MathF.PI / 3f);
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
            sceneInstances).Hit;
        var referenceDistance = hit?.Distance ?? sceneRadius * 0.25f;
        var minimumStep = Math.Max(sceneRadius * 0.002f, 0.05f);
        var maximumStep = Math.Max(sceneRadius * 0.25f, 1f);
        var step = Math.Clamp(referenceDistance * 0.2f, minimumStep, maximumStep);
        cameraNavigation.Dolly(wheelDelta / 120f * step);
    }

    private void SelectAt(Point location)
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ray = CreatePointerRay(location);
        var hit = elementPicker.Pick(ray, sceneInstances, currentMap, overlayMarkerSize);
        selection = hit?.Selection;
        cameraHandle = SceneCameraHandle.Eye;
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        SyncOutlinerSelection();
        Text = hit is null
            ? $"{baseTitle} — no selection"
            : $"{baseTitle} — selected: {hit.Selection.Name} [{hit.Selection.Kind}]";
    }

    private void FocusSelection()
    {
        if (selection is null) return;
        if (selection.Kind == SceneElementKind.Prop)
        {
            var selected = sceneInstances.FirstOrDefault(value => value.Id == selection.SourceIndex);
            if (selected is not null)
            {
                var bounds = new SceneBoundsCalculator().Calculate(new[] { selected });
                if (bounds.HasGeometry)
                {
                    cameraNavigation.Focus(bounds.Center, Math.Max(bounds.Radius * 2.5f, sceneRadius * 0.01f));
                    return;
                }
            }
            if (document.Find(selection) is { } propElement)
            {
                cameraNavigation.Focus(propElement.Transform.Position, Math.Max(sceneRadius * 0.025f, 1f));
            }
            return;
        }
        if (!TryGetOverlayFocus(selection, out var center, out var radius)) return;
        cameraNavigation.Focus(center, Math.Max(radius * 2.5f, sceneRadius * 0.01f));
    }

    private void RefreshRenderInstances(IReadOnlyDictionary<string, D3D11ModelResources> resourcesByAsset)
    {
        var rendered = sceneInstances
            .Where(value => resourcesByAsset.ContainsKey(value.AssetId))
            .Select(value => new D3D11SceneInstance(
                value.Id,
                resourcesByAsset[value.AssetId],
                value.Transform,
                selection is { Kind: SceneElementKind.Prop }
                    && value.Id == selection.SourceIndex))
            .ToList();
        if (placement is { Position: { } position, Model: not null, AssetId: not null } preview
            && resourcesByAsset.TryGetValue(preview.AssetId, out var previewResources))
        {
            var transform = Matrix4x4.CreateFromQuaternion(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f))
                * Matrix4x4.CreateTranslation(position);
            rendered.Add(new D3D11SceneInstance(-1, previewResources, transform, false, true));
        }
        instances = rendered;
    }

    private void RefreshOverlay()
    {
        if (viewport is null) return;
        var overlayLines = new SceneOverlayBuilder().Build(
            currentMap,
            new SceneOverlayOptions(PointMarkerSize: overlayMarkerSize, Selection: selection)).ToList();
        var selectedElement = selection is null ? null : document.Find(selection);
        if (selectedElement is not null && SupportsMode(selectedElement, gizmoMode))
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
        if (selection is { Kind: SceneElementKind.Camera } cameraSelection
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
            .Select(line => new D3D11DebugLine(line.Start, line.End, line.Color))
            .ToArray());
    }

    private bool SelectCameraHandleAt(Point location)
    {
        if (selection is not { Kind: SceneElementKind.Camera } selected
            || document.FindCamera(selected) is not { } camera) return false;
        var ray = CreatePointerRay(location);
        if (!cameraGizmo.TryPickHandle(ray, camera, overlayMarkerSize * 2f, out var handle)) return false;
        cameraHandle = handle;
        gizmoMode = GizmoMode.Translate;
        RefreshOverlay();
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
        var worldUnitsPerPixel = 2f * distance * MathF.Tan(MathF.PI / 6f) / Math.Max(1, viewportHost.ClientSize.Height);
        return Math.Max(worldUnitsPerPixel * 90f, overlayMarkerSize * 2f);
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
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshElementProperties();
        RefreshOutliner();
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
                    groupNode.Nodes.Add(new TreeNode(
                        $"{element.Name} — {group.ElementTypeName} [{element.SourceIndex}]") { Tag = element });
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
        Text = $"{baseTitle} - selected: {selected.Name} [{selected.Kind}]";
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
        var result = surfaceRaycaster.Cast(CreatePointerRay(location), sceneInstances);
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
            radius = Math.Max(sound.Range, overlayMarkerSize);
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
