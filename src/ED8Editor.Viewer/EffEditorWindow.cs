using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Rendering;
using Vortice.Direct3D11;

namespace ED8Editor.Viewer;

/// <summary>
/// The effect editor, in a window of its own: the file and its segments on the
/// left, and a preview of the effect on the right. The preview stands apart from
/// the scene viewport — an effect is judged on its own, against a plain
/// background, not buried in a map.
/// </summary>
internal sealed class EffEditorWindow : Form
{
    private readonly EffEditorControl editor;
    private readonly Panel previewHost = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly TrackBar orbitYaw = new()
    {
        Dock = DockStyle.Bottom,
        Minimum = -180,
        Maximum = 180,
        Value = 0,
        TickFrequency = 45,
    };
    private readonly Label previewStatus = new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        AutoEllipsis = true,
        Padding = new Padding(6, 4, 0, 0),
    };
    private readonly ToolStrip previewTools = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripButton showCropButton = new("Crop the texture")
    {
        CheckOnClick = true,
        ToolTipText = "Show the segment's texture and drag a rectangle to set its crop",
    };
    private readonly ToolStripLabel cropLabel = new(string.Empty) { AutoSize = true };
    private readonly Func<string, ID3D11ShaderResourceView?> resolveTexture;
    private readonly D3D11GraphicsDevice graphics;
    private D3D11Viewport? preview;
    private EffFile? previewed;
    private float previewSeconds;
    private float distance = 6f;
    private (Vector2 Start, Vector2 End)? cropDrag;

    public EffEditorWindow(
        string gameDataPath,
        D3D11GraphicsDevice graphics,
        Func<string, ID3D11ShaderResourceView?> resolveTexture,
        EventHandler<EffSaveEventArgs> onSaving)
    {
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        this.resolveTexture = resolveTexture ?? throw new ArgumentNullException(nameof(resolveTexture));
        Text = "Effect editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(900, 560);

        editor = new EffEditorControl(gameDataPath);
        editor.Saving += onSaving;
        editor.TextureImported += onSaving;
        editor.PreviewChanged += (_, request) =>
        {
            previewed = request.Effect;
            previewSeconds = request.Seconds;
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 780,
        };
        split.Panel1.Controls.Add(editor);
        previewTools.Items.Add(showCropButton);
        previewTools.Items.Add(cropLabel);
        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(previewHost);
        previewPanel.Controls.Add(orbitYaw);
        previewPanel.Controls.Add(previewStatus);
        previewPanel.Controls.Add(previewTools);
        split.Panel2.Controls.Add(previewPanel);
        Controls.Add(split);

        previewHost.MouseWheel += (_, eventArgs) =>
        {
            // Closer or further from the effect, in steps of its own size.
            distance = Math.Clamp(distance * (eventArgs.Delta > 0 ? 0.85f : 1.18f), 0.2f, 200f);
        };
        previewHost.MouseEnter += (_, _) => previewHost.Focus();
        showCropButton.CheckedChanged += (_, _) =>
        {
            orbitYaw.Visible = !showCropButton.Checked;
            cropDrag = null;
        };
        editor.SegmentSelected += (_, _) => cropDrag = null;
        previewHost.MouseDown += (_, eventArgs) =>
        {
            if (!showCropButton.Checked || eventArgs.Button != MouseButtons.Left) return;
            cropDrag = (ToTextureCoordinates(eventArgs.Location), ToTextureCoordinates(eventArgs.Location));
        };
        previewHost.MouseMove += (_, eventArgs) =>
        {
            if (cropDrag is not { } drag) return;
            cropDrag = (drag.Start, ToTextureCoordinates(eventArgs.Location));
        };
        previewHost.MouseUp += (_, eventArgs) =>
        {
            if (cropDrag is not { } drag || eventArgs.Button != MouseButtons.Left) return;
            cropDrag = null;
            // A click without a drag clears the crop back to the whole texture.
            var width = Math.Abs(drag.End.X - drag.Start.X);
            var height = Math.Abs(drag.End.Y - drag.Start.Y);
            if (width < 0.002f || height < 0.002f)
            {
                editor.SetCrop(0f, 0f, 0f, 0f);
                return;
            }
            // The crop keeps the direction it was dragged in: the format allows
            // a flipped rectangle, which mirrors the texture.
            editor.SetCrop(drag.Start.X, drag.Start.Y, drag.End.X, drag.End.Y);
        };
    }

    /// <summary>Where a point of the preview panel falls on the texture, in 0..1.</summary>
    private Vector2 ToTextureCoordinates(Point point)
    {
        var area = TextureArea();
        return new Vector2(
            Math.Clamp((point.X - area.X) / (float)area.Width, 0f, 1f),
            Math.Clamp((point.Y - area.Y) / (float)area.Height, 0f, 1f));
    }

    /// <summary>The square the texture is drawn in, centred and as large as fits.</summary>
    private Rectangle TextureArea()
    {
        var size = Math.Max(16, Math.Min(previewHost.ClientSize.Width, previewHost.ClientSize.Height) - 24);
        return new Rectangle(
            (previewHost.ClientSize.Width - size) / 2,
            (previewHost.ClientSize.Height - size) / 2,
            size,
            size);
    }

    /// <summary>Draws the effect at the frame the editor is asking for.</summary>
    public void RenderPreview()
    {
        if (previewHost.ClientSize.Width <= 0 || previewHost.ClientSize.Height <= 0) return;
        preview ??= new D3D11Viewport(
            graphics, previewHost.Handle, previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.Resize(previewHost.ClientSize.Width, previewHost.ClientSize.Height);
        preview.SetClearColor(new Vector4(0.05f, 0.06f, 0.08f, 1f));

        if (showCropButton.Checked)
        {
            RenderCrop();
            return;
        }
        cropLabel.Text = string.Empty;
        var quads = new List<D3D11EffectQuad>();
        var truncated = false;
        if (previewed is { } effect)
        {
            var frame = EffSimulation.Evaluate(effect, previewSeconds);
            truncated = frame.Truncated;
            foreach (var node in frame.Nodes)
            {
                if (!node.Drawn) continue;
                var segment = effect.Segments[node.SegmentIndex];
                if (segment.TextureName.Length == 0) continue;
                if (resolveTexture(segment.TextureName) is not { } texture) continue;
                AddQuad(quads, segment, node, texture);
            }
            previewStatus.Text =
                $"{effect.EffectName} at {previewSeconds:0.00} s — {quads.Count} quads"
                + (truncated ? ", endless emitter cut short" : string.Empty);
        }
        else
        {
            previewStatus.Text = "Select an effect to preview it.";
        }

        preview.SetEffectQuads(quads);
        preview.SetDebugLines(BuildGround());
        preview.SetDebugTriangles(Array.Empty<D3D11DebugTriangle>());
        preview.Render(Array.Empty<D3D11SceneInstance>(), CreateCamera(), verticalSync: false);
    }

    /// <summary>
    /// Shows the segment's texture flat, with its crop drawn over it. The crop is
    /// authored as plain texture coordinates, so the rectangle on screen is the
    /// rectangle in the file — including one dragged backwards, which the format
    /// reads as a mirrored texture.
    /// </summary>
    private void RenderCrop()
    {
        if (preview is null) return;
        var segment = editor.CurrentSegment;
        var texture = segment is null || segment.TextureName.Length == 0
            ? null
            : resolveTexture(segment.TextureName);
        var quads = new List<D3D11EffectQuad>();
        var lines = new List<D3D11DebugLine>();
        if (texture is not null && segment is not null)
        {
            // The texture fills a square of the view, drawn straight in clip
            // space through an orthographic camera.
            var area = TextureArea();
            var left = area.X / (float)previewHost.ClientSize.Width * 2f - 1f;
            var right = (area.X + area.Width) / (float)previewHost.ClientSize.Width * 2f - 1f;
            var top = 1f - area.Y / (float)previewHost.ClientSize.Height * 2f;
            var bottom = 1f - (area.Y + area.Height) / (float)previewHost.ClientSize.Height * 2f;
            quads.Add(new D3D11EffectQuad(
                new Vector3(left, bottom, 0f),
                new Vector3(right, bottom, 0f),
                new Vector3(right, top, 0f),
                new Vector3(left, top, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 0f),
                Vector4.One,
                Vector4.Zero,
                texture,
                EffBlendMode.Alpha,
                0));

            var crop = cropDrag is { } drag
                ? (drag.Start.X, drag.Start.Y, drag.End.X, drag.End.Y)
                : (segment.Data04[0], segment.Data04[1], segment.Data04[2], segment.Data04[3]);
            var whole = crop is (0f, 0f, 0f, 0f);
            var (u0, v0, u1, v1) = whole ? (0f, 0f, 1f, 1f) : crop;
            var color = new Vector4(1f, 0.85f, 0.2f, 1f);
            var x0 = left + (right - left) * u0;
            var x1 = left + (right - left) * u1;
            var y0 = top + (bottom - top) * v0;
            var y1 = top + (bottom - top) * v1;
            var corners = new[]
            {
                new Vector3(x0, y0, 0f), new Vector3(x1, y0, 0f),
                new Vector3(x1, y1, 0f), new Vector3(x0, y1, 0f),
            };
            for (var index = 0; index < corners.Length; index++)
            {
                lines.Add(new D3D11DebugLine(
                    corners[index], corners[(index + 1) % corners.Length], color, 2f));
            }
            cropLabel.Text = whole
                ? $"{segment.TextureName}: whole texture — drag to crop, click to reset"
                : $"{segment.TextureName}: {u0:0.###}, {v0:0.###} → {u1:0.###}, {v1:0.###}";
            previewStatus.Text = $"Segment {segment.Name}";
        }
        else
        {
            cropLabel.Text = "This segment names no texture.";
            previewStatus.Text = string.Empty;
        }

        preview.SetEffectQuads(quads);
        // The geometry is already in clip space, so the camera stays out of it.
        preview.SetDebugLines(lines);
        preview.SetDebugTriangles(Array.Empty<D3D11DebugTriangle>());
        preview.Render(
            Array.Empty<D3D11SceneInstance>(),
            new ViewportCamera(Matrix4x4.Identity, Matrix4x4.Identity),
            verticalSync: false);
    }

    /// <summary>
    /// A quad of the previewed effect. The preview always billboards: an effect
    /// authored flat in its own plane would otherwise be seen edge-on here,
    /// where there is no scene to orient it.
    /// </summary>
    private void AddQuad(
        ICollection<D3D11EffectQuad> quads,
        EffSegment segment,
        EffNode node,
        ID3D11ShaderResourceView texture)
    {
        var world = node.Rotation * Matrix4x4.CreateTranslation(node.Position);
        var halfWidth = node.Scale.X / 2f;
        var halfHeight = node.Scale.Y / 2f;
        Vector3[] corners;
        if (node.Billboard)
        {
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
            ((segment.Data02[4] >> 8) & 0xFF) switch
            {
                0x02 => EffBlendMode.Additive,
                0x04 => EffBlendMode.Subtractive,
                _ => EffBlendMode.Alpha,
            },
            (int)((segment.Data02[3] >> 8) & 0xFF)));
    }

    /// <summary>A plain grid at the effect's origin, to read its size against.</summary>
    private static D3D11DebugLine[] BuildGround()
    {
        var lines = new List<D3D11DebugLine>();
        var color = new Vector4(0.25f, 0.28f, 0.34f, 1f);
        for (var step = -5; step <= 5; step++)
        {
            lines.Add(new D3D11DebugLine(
                new Vector3(step, 0f, -5f), new Vector3(step, 0f, 5f), color));
            lines.Add(new D3D11DebugLine(
                new Vector3(-5f, 0f, step), new Vector3(5f, 0f, step), color));
        }
        return lines.ToArray();
    }

    private ViewportCamera CreateCamera()
    {
        var yaw = orbitYaw.Value * MathF.PI / 180f;
        var eye = new Vector3(
            MathF.Sin(yaw) * distance, distance * 0.35f, MathF.Cos(yaw) * distance);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 0.5f, 0f), Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            Math.Max(1, previewHost.ClientSize.Width) / (float)Math.Max(1, previewHost.ClientSize.Height),
            0.05f,
            500f);
        return new ViewportCamera(view, projection);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        preview?.Dispose();
        preview = null;
        base.OnFormClosed(eventArgs);
    }
}
