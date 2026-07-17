using System.Diagnostics;
using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Rendering;

namespace ED8Editor.Viewer;

public sealed class ViewerForm : Form
{
    private readonly EditorSession session;
    private readonly bool smokeTest;
    private readonly HashSet<Keys> pressedKeys = new();
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly List<D3D11ModelResources> uploadedModels = new();
    private D3D11GraphicsDevice? graphics;
    private D3D11Viewport? viewport;
    private IReadOnlyList<D3D11SceneInstance> instances = Array.Empty<D3D11SceneInstance>();
    private Vector3 cameraPosition;
    private float cameraYaw;
    private float cameraPitch;
    private float sceneRadius = 10f;
    private Point previousMouse;
    private bool rotating;
    private long previousFrameTicks;

    public ViewerForm(EditorSession session, bool smokeTest)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.smokeTest = smokeTest;
        Text = $"ED8Editor — {session.Script.Header.Identifier} — RMB: look, WASD/QE: move, Shift: fast";
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(640, 360);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        renderTimer.Tick += (_, _) => RenderFrame();
        KeyDown += (_, eventArgs) => pressedKeys.Add(eventArgs.KeyCode);
        KeyUp += (_, eventArgs) => pressedKeys.Remove(eventArgs.KeyCode);
        MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right) return;
            rotating = true;
            previousMouse = eventArgs.Location;
            Cursor.Hide();
        };
        MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right) return;
            rotating = false;
            Cursor.Show();
        };
        MouseMove += (_, eventArgs) => RotateCamera(eventArgs.Location);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        try
        {
            InitializeRenderer();
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
        if (WindowState != FormWindowState.Minimized && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            viewport?.Resize(ClientSize.Width, ClientSize.Height);
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
            if (rotating) Cursor.Show();
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
            var uploaded = uploader.Upload(load.Model!);
            uploadedModels.Add(uploaded);
            resourcesByAsset.Add(load.AssetId, uploaded);
        }

        var sceneInstances = new List<D3D11SceneInstance>();
        if (session.Map is not null)
        {
            foreach (var prop in session.Map.Props)
            {
                if (!resourcesByAsset.TryGetValue(prop.AssetId, out var model)) continue;
                sceneInstances.Add(new D3D11SceneInstance(model, CreateTransform(prop.Transform)));
            }
        }
        if (sceneInstances.Count == 0)
        {
            sceneInstances.AddRange(resourcesByAsset.Values.Select(value => new D3D11SceneInstance(value, Matrix4x4.Identity)));
        }
        instances = sceneInstances;

        var (center, radius) = EstimateBounds();
        sceneRadius = Math.Max(radius, 1f);
        cameraPosition = center + new Vector3(0, sceneRadius * 0.35f, -sceneRadius * 1.6f);
        SetCameraDirection(Vector3.Normalize(center - cameraPosition));
        viewport = new D3D11Viewport(graphics, Handle, ClientSize.Width, ClientSize.Height);
        previousFrameTicks = frameClock.ElapsedTicks;
    }

    private void RenderFrame()
    {
        if (viewport is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var ticks = frameClock.ElapsedTicks;
        var elapsed = Math.Clamp((float)(ticks - previousFrameTicks) / Stopwatch.Frequency, 0f, 0.1f);
        previousFrameTicks = ticks;
        UpdateCamera(elapsed);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            ClientSize.Width / (float)ClientSize.Height,
            Math.Max(0.01f, sceneRadius / 10000f),
            Math.Max(1000f, sceneRadius * 20f));
        var forward = GetForward();
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + forward, Vector3.UnitY);
        viewport.Render(instances, new ViewportCamera(view, projection));
    }

    private void UpdateCamera(float elapsed)
    {
        var forward = GetForward();
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
            cameraPosition += Vector3.Normalize(movement) * sceneRadius * 0.8f * fast * elapsed;
        }
    }

    private void RotateCamera(Point current)
    {
        if (!rotating) return;
        var deltaX = current.X - previousMouse.X;
        var deltaY = current.Y - previousMouse.Y;
        previousMouse = current;
        cameraYaw += deltaX * 0.004f;
        cameraPitch = Math.Clamp(cameraPitch - deltaY * 0.004f, -1.5f, 1.5f);
    }

    private Vector3 GetForward()
    {
        var cosPitch = MathF.Cos(cameraPitch);
        return Vector3.Normalize(new Vector3(
            MathF.Sin(cameraYaw) * cosPitch,
            MathF.Sin(cameraPitch),
            MathF.Cos(cameraYaw) * cosPitch));
    }

    private void SetCameraDirection(Vector3 direction)
    {
        cameraPitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        cameraYaw = MathF.Atan2(direction.X, direction.Z);
    }

    private (Vector3 Center, float Radius) EstimateBounds()
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var found = false;
        foreach (var instance in instances)
        {
            var source = session.AssetModels[instance.Model.AssetId].Model!;
            foreach (var mesh in source.Meshes)
            {
                var transform = mesh.LocalTransform * instance.Transform;
                foreach (var primitive in mesh.Primitives)
                {
                    foreach (var buffer in primitive.VertexBuffers)
                    {
                        var attribute = buffer.Attributes.FirstOrDefault(value => value.Semantic == VertexSemantic.Position);
                        if (attribute is null || attribute.SourceFormat != "Float32x3") continue;
                        for (var vertex = 0; vertex < buffer.VertexCount; vertex++)
                        {
                            var offset = vertex * buffer.Stride + attribute.Offset;
                            var position = new Vector3(
                                BitConverter.ToSingle(buffer.Data, offset),
                                BitConverter.ToSingle(buffer.Data, offset + 4),
                                BitConverter.ToSingle(buffer.Data, offset + 8));
                            position = Vector3.Transform(position, transform);
                            minimum = Vector3.Min(minimum, position);
                            maximum = Vector3.Max(maximum, position);
                            found = true;
                        }
                        break;
                    }
                }
            }
        }
        if (!found) return (Vector3.Zero, 10f);
        var center = (minimum + maximum) * 0.5f;
        return (center, Vector3.Distance(minimum, maximum) * 0.5f);
    }

    private static Matrix4x4 CreateTransform(MapTransform transform)
        => Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
}
