using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed record SceneOverlayLine(Vector3 Start, Vector3 End, Vector4 Color, float Thickness = 1f);
public sealed record SceneOverlayTriangle(Vector3 A, Vector3 B, Vector3 C, Vector4 Color);
public sealed record SceneOverlayGeometry(
    IReadOnlyList<SceneOverlayLine> Lines,
    IReadOnlyList<SceneOverlayTriangle> Triangles);

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
        => BuildGeometry(map, options).Lines;

    public SceneOverlayGeometry BuildGeometry(MapScene? map, SceneOverlayOptions? options = null)
    {
        if (map is null) return new SceneOverlayGeometry(
            Array.Empty<SceneOverlayLine>(), Array.Empty<SceneOverlayTriangle>());
        options ??= new SceneOverlayOptions();
        if (!float.IsFinite(options.PointMarkerSize) || options.PointMarkerSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Point marker size must be finite and positive.");
        }

        var lines = new List<SceneOverlayLine>();
        var triangles = new List<SceneOverlayTriangle>();
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
            AddBox(lines, triangles, volume.Transform, color);
        }
        if (options.ShowLookPoints)
        {
            foreach (var point in map.Points)
            {
                var color = IsSelected(SceneElementKind.LookPoint, point.SourceIndex) ? Vector4.One : LookPointColor;
                AddPoint(lines, point.Position, point.Radius ?? options.PointMarkerSize, color);
                if (point.Radius is { } radius && radius > 0f)
                    AddSphere(triangles, point.Position, radius, WithAlpha(color, 0.12f));
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
                switch (sound.Kind)
                {
                    case MapSoundKind.Point:
                        AddRange(lines, sound.Position, sound.Range, color);
                        AddSphere(triangles, sound.Position, sound.Range, WithAlpha(color, 0.07f));
                        break;
                    case MapSoundKind.Box:
                        var transform = Matrix4x4.CreateScale(sound.SourceScale)
                            * Matrix4x4.CreateRotationY(sound.SourceRotation * MathF.PI / 180f)
                            * Matrix4x4.CreateTranslation(sound.Position);
                        AddBox(lines, triangles, transform, color, 0.09f);
                        break;
                }
            }
            foreach (var group in map.Sounds
                         .Where(value => value.Kind == MapSoundKind.Line)
                         .GroupBy(value => value.GroupId))
            {
                var points = group.OrderBy(value => value.SourceIndex).ToArray();
                for (var index = 1; index < points.Length; index++)
                {
                    var selected = IsSelected(SceneElementKind.Sound, points[index - 1].SourceIndex)
                        || IsSelected(SceneElementKind.Sound, points[index].SourceIndex);
                    lines.Add(new SceneOverlayLine(
                        points[index - 1].Position,
                        points[index].Position,
                        selected ? Vector4.One : SoundColor,
                        3f));
                }
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
                AddSphere(triangles, light.Position, light.OuterRange, WithAlpha(color, 0.055f));
            }
        }
        return new SceneOverlayGeometry(lines, triangles);

        bool IsSelected(SceneElementKind kind, int sourceIndex)
            => options.Selection is { } selection
                && selection.Kind == kind
                && selection.SourceIndex == sourceIndex;
    }

    private static void AddBox(
        List<SceneOverlayLine> lines,
        List<SceneOverlayTriangle> triangles,
        MapTransform transform,
        Vector4 color)
    {
        var matrix = Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
        AddBox(lines, triangles, matrix, color, 0.18f);
    }

    private static void AddBox(
        List<SceneOverlayLine> lines,
        List<SceneOverlayTriangle> triangles,
        Matrix4x4 matrix,
        Vector4 color,
        float fillAlpha)
    {
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
        foreach (var (a, b) in edges) lines.Add(new SceneOverlayLine(corners[a], corners[b], color, 2.6f));
        var faces = new (int A, int B, int C)[]
        {
            (0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7),
            (0, 1, 5), (0, 5, 4), (3, 7, 6), (3, 6, 2),
            (0, 4, 7), (0, 7, 3), (1, 2, 6), (1, 6, 5),
        };
        var fill = WithAlpha(color, fillAlpha);
        foreach (var (a, b, c) in faces)
            triangles.Add(new SceneOverlayTriangle(corners[a], corners[b], corners[c], fill));
    }

    private static void AddPoint(List<SceneOverlayLine> lines, Vector3 center, float size, Vector4 color)
    {
        lines.Add(new SceneOverlayLine(center - Vector3.UnitX * size, center + Vector3.UnitX * size, color, 2.2f));
        lines.Add(new SceneOverlayLine(center - Vector3.UnitY * size, center + Vector3.UnitY * size, color, 2.2f));
        lines.Add(new SceneOverlayLine(center - Vector3.UnitZ * size, center + Vector3.UnitZ * size, color, 2.2f));
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
                lines.Add(new SceneOverlayLine(start, end, color, 2f));
            }
        }
    }

    private static void AddSphere(
        List<SceneOverlayTriangle> triangles, Vector3 center, float radius, Vector4 color)
    {
        if (!float.IsFinite(radius) || radius <= 0f) return;
        const int latitudeSegments = 8;
        const int longitudeSegments = 12;
        for (var latitude = 0; latitude < latitudeSegments; latitude++)
        {
            var latitudeA = -MathF.PI / 2f + latitude * MathF.PI / latitudeSegments;
            var latitudeB = -MathF.PI / 2f + (latitude + 1) * MathF.PI / latitudeSegments;
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var longitudeA = longitude * MathF.Tau / longitudeSegments;
                var longitudeB = (longitude + 1) * MathF.Tau / longitudeSegments;
                var a = center + SpherePoint(latitudeA, longitudeA) * radius;
                var b = center + SpherePoint(latitudeB, longitudeA) * radius;
                var c = center + SpherePoint(latitudeB, longitudeB) * radius;
                var d = center + SpherePoint(latitudeA, longitudeB) * radius;
                triangles.Add(new SceneOverlayTriangle(a, b, c, color));
                triangles.Add(new SceneOverlayTriangle(a, c, d, color));
            }
        }
    }

    private static Vector3 SpherePoint(float latitude, float longitude)
    {
        var horizontal = MathF.Cos(latitude);
        return new Vector3(horizontal * MathF.Cos(longitude), MathF.Sin(latitude), horizontal * MathF.Sin(longitude));
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) => color with { W = alpha };
}
