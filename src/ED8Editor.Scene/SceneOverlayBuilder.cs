using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed record SceneOverlayLine(Vector3 Start, Vector3 End, Vector4 Color, float Thickness = 1f);

public sealed record SceneOverlayOptions(
    bool ShowEntryVolumes = true,
    bool ShowGroupVolumes = true,
    bool ShowLookPoints = true,
    bool ShowCameras = true,
    bool ShowSounds = true,
    bool ShowLights = true,
    float PointMarkerSize = 0.3f,
    SceneElementSelection? Selection = null);

public sealed class SceneOverlayBuilder
{
    private static readonly Vector4 EntryColor = new(0.05f, 0.85f, 1f, 1f);
    private static readonly Vector4 TransitionColor = new(0.1f, 0.35f, 1f, 1f);
    private static readonly Vector4 GroupColor = new(0.85f, 0.2f, 1f, 1f);
    private static readonly Vector4 LookPointColor = new(1f, 0.82f, 0.05f, 1f);
    private static readonly Vector4 CameraColor = new(0.25f, 1f, 0.35f, 1f);
    private static readonly Vector4 SoundColor = new(1f, 0.42f, 0.08f, 1f);

    public IReadOnlyList<SceneOverlayLine> Build(MapScene? map, SceneOverlayOptions? options = null)
    {
        if (map is null) return Array.Empty<SceneOverlayLine>();
        options ??= new SceneOverlayOptions();
        if (!float.IsFinite(options.PointMarkerSize) || options.PointMarkerSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Point marker size must be finite and positive.");
        }

        var lines = new List<SceneOverlayLine>();
        foreach (var volume in map.Volumes)
        {
            var visible = volume.Kind switch
            {
                MapVolumeKind.Entry => options.ShowEntryVolumes,
                MapVolumeKind.Group => options.ShowGroupVolumes,
                _ => false,
            };
            if (!visible) continue;
            var color = volume.Kind switch
            {
                MapVolumeKind.Entry when !string.IsNullOrWhiteSpace(volume.DestinationMap) => TransitionColor,
                MapVolumeKind.Entry => EntryColor,
                _ => GroupColor,
            };
            var selectionKind = volume.Kind == MapVolumeKind.Entry
                ? SceneElementKind.EntryVolume
                : SceneElementKind.GroupVolume;
            if (IsSelected(selectionKind, volume.SourceIndex)) color = Vector4.One;
            AddBox(lines, volume.Transform, color);
        }
        if (options.ShowLookPoints)
        {
            foreach (var point in map.Points)
            {
                var color = IsSelected(SceneElementKind.LookPoint, point.SourceIndex) ? Vector4.One : LookPointColor;
                AddPoint(lines, point.Position, point.Radius ?? options.PointMarkerSize, color);
            }
        }
        if (options.ShowCameras)
        {
            foreach (var camera in map.Cameras)
            {
                var color = IsSelected(SceneElementKind.Camera, camera.SourceIndex) ? Vector4.One : CameraColor;
                lines.Add(new SceneOverlayLine(camera.Eye, camera.LookAt, color));
                AddPoint(lines, camera.Eye, options.PointMarkerSize, color);
                AddPoint(lines, camera.LookAt, options.PointMarkerSize * 0.65f, color);
            }
        }
        if (options.ShowSounds)
        {
            foreach (var sound in map.Sounds)
            {
                var color = IsSelected(SceneElementKind.Sound, sound.SourceIndex) ? Vector4.One : SoundColor;
                AddPoint(lines, sound.Position, options.PointMarkerSize, color);
                AddRange(lines, sound.Position, sound.Range, color);
            }
        }
        if (options.ShowLights)
        {
            foreach (var light in map.Lights)
            {
                var color = new Vector4(
                    Math.Clamp(light.Color.X, 0f, 1f),
                    Math.Clamp(light.Color.Y, 0f, 1f),
                    Math.Clamp(light.Color.Z, 0f, 1f),
                    1f);
                if (IsSelected(SceneElementKind.Light, light.SourceIndex)) color = Vector4.One;
                AddPoint(lines, light.Position, options.PointMarkerSize, color);
                AddRange(lines, light.Position, light.OuterRange, color);
            }
        }
        return lines;

        bool IsSelected(SceneElementKind kind, int sourceIndex)
            => options.Selection is { } selection
                && selection.Kind == kind
                && selection.SourceIndex == sourceIndex;
    }

    private static void AddBox(List<SceneOverlayLine> lines, MapTransform transform, Vector4 color)
    {
        var matrix = Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
        var corners = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        };
        for (var index = 0; index < corners.Length; index++) corners[index] = Vector3.Transform(corners[index], matrix);
        var edges = new (int A, int B)[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (a, b) in edges) lines.Add(new SceneOverlayLine(corners[a], corners[b], color));
    }

    private static void AddPoint(List<SceneOverlayLine> lines, Vector3 center, float size, Vector4 color)
    {
        lines.Add(new SceneOverlayLine(center - Vector3.UnitX * size, center + Vector3.UnitX * size, color));
        lines.Add(new SceneOverlayLine(center - Vector3.UnitY * size, center + Vector3.UnitY * size, color));
        lines.Add(new SceneOverlayLine(center - Vector3.UnitZ * size, center + Vector3.UnitZ * size, color));
    }

    private static void AddRange(List<SceneOverlayLine> lines, Vector3 center, float radius, Vector4 color)
    {
        if (!float.IsFinite(radius) || radius <= 0) return;
        const int segments = 24;
        AddCircle(Vector3.UnitX, Vector3.UnitY);
        AddCircle(Vector3.UnitX, Vector3.UnitZ);
        AddCircle(Vector3.UnitY, Vector3.UnitZ);

        void AddCircle(Vector3 axisA, Vector3 axisB)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var angleA = segment * MathF.Tau / segments;
                var angleB = (segment + 1) * MathF.Tau / segments;
                var start = center + (axisA * MathF.Cos(angleA) + axisB * MathF.Sin(angleA)) * radius;
                var end = center + (axisA * MathF.Cos(angleB) + axisB * MathF.Sin(angleB)) * radius;
                lines.Add(new SceneOverlayLine(start, end, color));
            }
        }
    }
}
