using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public enum SceneCameraHandle
{
    Eye,
    LookAt,
}

public sealed class SceneCameraGizmo
{
    private static readonly Vector4 EyeColor = new(0.2f, 0.85f, 1f, 1f);
    private static readonly Vector4 LookAtColor = new(1f, 0.25f, 0.8f, 1f);
    private static readonly Vector4 ActiveColor = new(1f, 0.85f, 0.1f, 1f);

    public IReadOnlyList<SceneOverlayLine> Build(
        MapCameraMarker camera,
        float markerSize,
        SceneCameraHandle activeHandle)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ValidateSize(markerSize);
        var lines = new List<SceneOverlayLine>(7)
        {
            new(camera.Eye, camera.LookAt, new Vector4(0.45f, 1f, 0.55f, 1f)),
        };
        AddMarker(camera.Eye, activeHandle == SceneCameraHandle.Eye ? ActiveColor : EyeColor);
        AddMarker(camera.LookAt, activeHandle == SceneCameraHandle.LookAt ? ActiveColor : LookAtColor);
        return lines;

        void AddMarker(Vector3 center, Vector4 color)
        {
            lines.Add(new SceneOverlayLine(center - Vector3.UnitX * markerSize, center + Vector3.UnitX * markerSize, color));
            lines.Add(new SceneOverlayLine(center - Vector3.UnitY * markerSize, center + Vector3.UnitY * markerSize, color));
            lines.Add(new SceneOverlayLine(center - Vector3.UnitZ * markerSize, center + Vector3.UnitZ * markerSize, color));
        }
    }

    public bool TryPickHandle(
        SceneRay ray,
        MapCameraMarker camera,
        float pickRadius,
        out SceneCameraHandle handle)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ValidateSize(pickRadius);
        var eyeDistance = IntersectSphere(ray, camera.Eye, pickRadius);
        var lookAtDistance = IntersectSphere(ray, camera.LookAt, pickRadius);
        if (eyeDistance is null && lookAtDistance is null)
        {
            handle = default;
            return false;
        }
        handle = lookAtDistance is not null && (eyeDistance is null || lookAtDistance < eyeDistance)
            ? SceneCameraHandle.LookAt
            : SceneCameraHandle.Eye;
        return true;
    }

    private static float? IntersectSphere(SceneRay ray, Vector3 center, float radius)
    {
        var offset = ray.Origin - center;
        var b = Vector3.Dot(offset, ray.Direction);
        var c = Vector3.Dot(offset, offset) - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0f) return null;
        var root = MathF.Sqrt(discriminant);
        var near = -b - root;
        if (near >= 0f) return near;
        var far = -b + root;
        return far >= 0f ? far : null;
    }

    private static void ValidateSize(float value)
    {
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
