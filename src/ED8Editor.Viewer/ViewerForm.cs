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
    private readonly EditorSceneDocument document;
    private readonly bool smokeTest;
    private readonly string baseTitle;
    private readonly SceneElementPicker elementPicker = new();
    private readonly EditorOrbitCamera cameraNavigation = new();
    private readonly SceneTranslationGizmo translationGizmo = new();
    private readonly SceneRotationGizmo rotationGizmo = new();
    private readonly OpsWriter opsWriter = new();
    private readonly HashSet<Keys> pressedKeys = new();
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly List<D3D11ModelResources> uploadedModels = new();
    private readonly Dictionary<string, CpuModel> loadedModelsByAsset = new(StringComparer.OrdinalIgnoreCase);
    private readonly Panel viewportHost = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly Panel assetPanel = new() { Dock = DockStyle.Right, Width = 300, Padding = new Padding(8) };
    private readonly TextBox assetSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search PKG assets..." };
    private readonly ListBox assetList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button addAssetButton = new() { Dock = DockStyle.Bottom, Height = 34, Text = "Add selected asset" };
    private readonly Button duplicateButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Duplicate selected prop" };
    private readonly Button deleteButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Delete selected prop" };
    private readonly GroupBox propertyGroup = new() { Dock = DockStyle.Bottom, Height = 250, Text = "Selected prop OPS attributes" };
    private readonly DataGridView propertyGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Button applyPropertiesButton = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Apply attributes" };
    private IReadOnlyList<AssetCatalogEntry> assetCatalog = Array.Empty<AssetCatalogEntry>();
    private D3D11GraphicsDevice? graphics;
    private D3D11Viewport? viewport;
    private IReadOnlyList<D3D11SceneInstance> instances = Array.Empty<D3D11SceneInstance>();
    private IReadOnlyList<SceneModelInstance> sceneInstances = Array.Empty<SceneModelInstance>();
    private MapScene? currentMap;
    private float sceneRadius = 10f;
    private float overlayMarkerSize = 0.3f;
    private Point previousMouse;
    private CameraDragMode cameraDrag;
    private long previousFrameTicks;
    private SceneElementSelection? selection;
    private GizmoDragState? gizmoDrag;
    private GizmoMode gizmoMode = GizmoMode.Translate;
    private string? savedOpsPath;

    public ViewerForm(EditorSession session, bool smokeTest, EditorProjectLoader? projectLoader = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
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
        KeyPreview = true;
        assetPanel.Controls.Add(assetList);
        assetPanel.Controls.Add(assetSearch);
        propertyGroup.Controls.Add(propertyGrid);
        propertyGroup.Controls.Add(applyPropertiesButton);
        assetPanel.Controls.Add(propertyGroup);
        assetPanel.Controls.Add(deleteButton);
        assetPanel.Controls.Add(duplicateButton);
        assetPanel.Controls.Add(addAssetButton);
        Controls.Add(viewportHost);
        Controls.Add(assetPanel);
        assetSearch.TextChanged += (_, _) => FilterAssetCatalog();
        addAssetButton.Click += async (_, _) => await AddSelectedAssetAsync();
        duplicateButton.Click += (_, _) => DuplicateSelectedProp();
        deleteButton.Click += (_, _) => DeleteSelectedProp();
        propertyGrid.Columns.Add("Attribute", "Attribute");
        propertyGrid.Columns.Add("Value", "Value");
        applyPropertiesButton.Click += (_, _) => ApplyPropProperties();
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
                DuplicateSelectedProp();
                eventArgs.SuppressKeyPress = true;
                return;
            }
            if (eventArgs.KeyCode == Keys.Delete)
            {
                DeleteSelectedProp();
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
                if (BeginGizmoDrag(eventArgs.Location)) return;
                SelectAt(eventArgs.Location);
                return;
            }
            if (eventArgs.Button is not (MouseButtons.Right or MouseButtons.Middle)) return;
            cameraDrag = eventArgs.Button == MouseButtons.Right ? CameraDragMode.Orbit : CameraDragMode.Pan;
            previousMouse = eventArgs.Location;
            Capture = true;
            Cursor.Hide();
        };
        viewportHost.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left && gizmoDrag is not null)
            {
                document.CommitPreview(gizmoDrag.Selection, gizmoDrag.OriginalTransform);
                gizmoDrag = null;
                RefreshOverlay();
                return;
            }
            if (eventArgs.Button is not (MouseButtons.Right or MouseButtons.Middle)) return;
            if ((eventArgs.Button == MouseButtons.Right && cameraDrag != CameraDragMode.Orbit)
                || (eventArgs.Button == MouseButtons.Middle && cameraDrag != CameraDragMode.Pan)) return;
            cameraDrag = CameraDragMode.None;
            Capture = false;
            Cursor.Show();
        };
        viewportHost.MouseMove += (_, eventArgs) =>
        {
            if (gizmoDrag is not null) UpdateGizmoDrag(eventArgs.Location);
            else MoveCamera(eventArgs.Location);
        };
        viewportHost.MouseWheel += (_, eventArgs) => ZoomCamera(eventArgs.Delta);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            renderTimer.Stop();
            renderTimer.Dispose();
            viewport?.Dispose();
            foreach (var model in uploadedModels) model.Dispose();
            graphics?.Dispose();
            if (cameraDrag != CameraDragMode.None) Cursor.Show();
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
        overlayMarkerSize = Math.Clamp(sceneRadius * 0.008f, 0.08f, 1.5f);
        RefreshOverlay();
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
        var view = Matrix4x4.CreateLookAt(cameraNavigation.Position, cameraNavigation.Position + forward, Vector3.UnitY);
        return new ViewportCamera(view, projection);
    }

    private void UpdateCamera(float elapsed)
    {
        var forward = cameraNavigation.Forward;
        var flatForward = Vector3.Normalize(new Vector3(forward.X, 0, forward.Z));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flatForward));
        var movement = Vector3.Zero;
        if (pressedKeys.Contains(Keys.W)) movement += flatForward;
        if (pressedKeys.Contains(Keys.S)) movement -= flatForward;
        if (pressedKeys.Contains(Keys.D)) movement += right;
        if (pressedKeys.Contains(Keys.A)) movement -= right;
        if (pressedKeys.Contains(Keys.E)) movement += Vector3.UnitY;
        if (pressedKeys.Contains(Keys.Q)) movement -= Vector3.UnitY;
        if (movement != Vector3.Zero)
        {
            var fast = pressedKeys.Contains(Keys.ShiftKey) ? 4f : 1f;
            var translation = Vector3.Normalize(movement) * sceneRadius * 0.8f * fast * elapsed;
            cameraNavigation.Translate(translation);
        }
    }

    private void MoveCamera(Point current)
    {
        if (cameraDrag == CameraDragMode.None) return;
        var deltaX = current.X - previousMouse.X;
        var deltaY = current.Y - previousMouse.Y;
        previousMouse = current;
        if (cameraDrag == CameraDragMode.Orbit)
        {
            cameraNavigation.Orbit(deltaX, deltaY);
            return;
        }
        cameraNavigation.Pan(deltaX, deltaY, viewportHost.ClientSize.Height, MathF.PI / 3f);
    }

    private void ZoomCamera(int wheelDelta)
    {
        if (wheelDelta == 0) return;
        cameraNavigation.Zoom(
            wheelDelta / 120f,
            Math.Max(sceneRadius * 0.0005f, 0.01f),
            sceneRadius * 100f);
    }

    private void SelectAt(Point location)
    {
        if (viewport is null || viewportHost.ClientSize.Width <= 0 || viewportHost.ClientSize.Height <= 0) return;
        var ray = CreatePointerRay(location);
        var hit = elementPicker.Pick(ray, sceneInstances, currentMap, overlayMarkerSize);
        selection = hit?.Selection;
        var resourcesByAsset = uploadedModels.ToDictionary(value => value.AssetId, StringComparer.OrdinalIgnoreCase);
        RefreshRenderInstances(resourcesByAsset);
        RefreshOverlay();
        RefreshPropProperties();
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
            if (selected is null) return;
            var bounds = new SceneBoundsCalculator().Calculate(new[] { selected });
            if (bounds.HasGeometry)
            {
                cameraNavigation.Focus(bounds.Center, Math.Max(bounds.Radius * 2.5f, sceneRadius * 0.01f));
            }
            return;
        }
        if (!TryGetOverlayFocus(selection, out var center, out var radius)) return;
        cameraNavigation.Focus(center, Math.Max(radius * 2.5f, sceneRadius * 0.01f));
    }

    private void RefreshRenderInstances(IReadOnlyDictionary<string, D3D11ModelResources> resourcesByAsset)
    {
        instances = sceneInstances
            .Where(value => resourcesByAsset.ContainsKey(value.AssetId))
            .Select(value => new D3D11SceneInstance(
                value.Id,
                resourcesByAsset[value.AssetId],
                value.Transform,
                selection is { Kind: SceneElementKind.Prop }
                    && value.Id == selection.SourceIndex))
            .ToArray();
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
            var length = GetGizmoLength(selectedElement.Transform.Position);
            if (gizmoMode == GizmoMode.Rotate)
            {
                overlayLines.AddRange(rotationGizmo.Build(selectedElement.Transform.Position, length, gizmoDrag?.Axis));
            }
            else
            {
                overlayLines.AddRange(translationGizmo.Build(selectedElement.Transform.Position, length, gizmoDrag?.Axis));
            }
        }
        viewport.SetDebugLines(overlayLines
            .Select(line => new D3D11DebugLine(line.Start, line.End, line.Color))
            .ToArray());
    }

    private bool BeginGizmoDrag(Point location)
    {
        if (selection is null) return false;
        var element = document.Find(selection);
        if (element is null || !SupportsMode(element, gizmoMode)) return false;
        var ray = CreatePointerRay(location);
        var length = GetGizmoLength(element.Transform.Position);
        if (gizmoMode == GizmoMode.Rotate)
        {
            if (!rotationGizmo.TryPickAxis(
                ray, element.Transform.Position, length, length * 0.08f, out var rotationAxis, out var ringVector))
            {
                return false;
            }
            gizmoDrag = new GizmoDragState(selection, gizmoMode, rotationAxis, element.Transform, 0f, ringVector, length);
        }
        else
        {
            if (!translationGizmo.TryPickAxis(
                ray, element.Transform.Position, length, length * 0.08f, out var linearAxis)
                || !translationGizmo.TryGetAxisParameter(
                    ray, element.Transform.Position, linearAxis, out var parameter))
            {
                return false;
            }
            gizmoDrag = new GizmoDragState(
                selection, gizmoMode, linearAxis, element.Transform, parameter, Vector3.Zero, length);
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
            document.PreviewTransform(
                drag.Selection,
                drag.OriginalTransform with { Position = drag.OriginalTransform.Position + direction * deltaParameter });
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
        RefreshPropProperties();
    }

    private void SaveOps(bool saveAs)
    {
        if (session.Map is null || currentMap is null) return;
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
            if (dialog.ShowDialog() != DialogResult.OK) return;
            targetPath = dialog.FileName;
        }
        try
        {
            opsWriter.Write(targetPath!, session.Map, currentMap);
            savedOpsPath = targetPath;
            Text = $"{baseTitle} — saved: {targetPath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Cannot save OPS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void InitializeAssetCatalog()
    {
        if (session.Script.GameDataPath is null)
        {
            assetPanel.Enabled = false;
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
            selection = document.AddProp(
                catalogEntry.AssetId,
                catalogEntry.AssetId,
                model,
                cameraNavigation.Target);
            RefreshSceneFromDocument();
            Text = $"{baseTitle} — added: {catalogEntry.AssetId}";
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

    private void DuplicateSelectedProp()
    {
        if (selection is not { Kind: SceneElementKind.Prop } templateSelection) return;
        var source = sceneInstances.FirstOrDefault(value => value.Id == templateSelection.SourceIndex);
        if (source is null || !loadedModelsByAsset.TryGetValue(source.AssetId, out var model)) return;
        selection = document.AddPropFromTemplate(templateSelection, source.AssetId, $"{source.Name}_copy", model);
        RefreshSceneFromDocument();
    }

    private void DeleteSelectedProp()
    {
        if (selection is not { Kind: SceneElementKind.Prop } selected) return;
        selection = null;
        if (document.DeleteProp(selected)) RefreshSceneFromDocument();
    }

    private void RefreshPropProperties()
    {
        propertyGrid.Rows.Clear();
        var prop = selection is null ? null : document.FindProp(selection);
        propertyGroup.Enabled = prop is not null;
        if (prop is null) return;
        var protectedNames = new HashSet<string>(new[] { "asset", "name", "pos", "rot", "scl" }, StringComparer.Ordinal);
        foreach (var attribute in prop.SourceAttributes.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var rowIndex = propertyGrid.Rows.Add(attribute.Key, attribute.Value);
            var row = propertyGrid.Rows[rowIndex];
            if (protectedNames.Contains(attribute.Key))
            {
                row.ReadOnly = true;
                row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
        }
    }

    private void ApplyPropProperties()
    {
        if (selection is not { Kind: SceneElementKind.Prop } selected) return;
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
            document.ApplyPropAttributes(selected, attributes);
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
        Orbit,
        Pan,
    }

    private sealed record GizmoDragState(
        SceneElementSelection Selection,
        GizmoMode Mode,
        SceneGizmoAxis Axis,
        SceneTransform OriginalTransform,
        float StartParameter,
        Vector3 StartVector,
        float GizmoLength);

    private enum GizmoMode
    {
        Translate,
        Rotate,
        Scale,
    }

}
