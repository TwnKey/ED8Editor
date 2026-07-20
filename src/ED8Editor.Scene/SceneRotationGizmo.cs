using System.Numerics;

namespace ED8Editor.Scene;

public sealed class SceneRotationGizmo
{
    private static readonly Vector4 XColor = new(1f, 0.12f, 0.12f, 1f);
    private static readonly Vector4 YColor = new(0.15f, 1f, 0.2f, 1f);
    private static readonly Vector4 ZColor = new(0.15f, 0.4f, 1f, 1f);
    private static readonly Vector4 ActiveColor = new(1f, 0.85f, 0.1f, 1f);

    public IReadOnlyList<SceneOverlayLine> Build(Vector3 origin, float radius, SceneGizmoAxis? activeAxis = null)
    {
        Validate(origin, radius);
        var lines = new List<SceneOverlayLine>(144);
        AddRing(SceneGizmoAxis.X, Vector3.UnitY, Vector3.UnitZ, XColor);
        AddRing(SceneGizmoAxis.Y, Vector3.UnitX, Vector3.UnitZ, YColor);
        AddRing(SceneGizmoAxis.Z, Vector3.UnitX, Vector3.UnitY, ZColor);
        return lines;

        void AddRing(SceneGizmoAxis axis, Vector3 basisA, Vector3 basisB, Vector4 color)
        {
            var thickness = axis == activeAxis ? 4f : 3f;
            if (axis == activeAxis) color = ActiveColor;
            const int segments = 48;
            for (var segment = 0; segment < segments; segment++)
            {
                var angleA = segment * MathF.Tau / segments;
                var angleB = (segment + 1) * MathF.Tau / segments;
                var start = origin + (basisA * MathF.Cos(angleA) + basisB * MathF.Sin(angleA)) * radius;
                var end = origin + (basisA * MathF.Cos(angleB) + basisB * MathF.Sin(angleB)) * radius;
                lines.Add(new SceneOverlayLine(start, end, color, thickness));
            }
        }
    }

    public bool TryPickAxis(
        SceneRay ray,
        Vector3 origin,
        float radius,
        float pickThickness,
        out SceneGizmoAxis axis,
        out Vector3 ringVector)
    {
        Validate(origin, radius);
        if (!float.IsFinite(pickThickness) || pickThickness <= 0) throw new ArgumentOutOfRangeException(nameof(pickThickness));
        axis = default;
        ringVector = Vector3.Zero;
        var nearestDistance = float.PositiveInfinity;
        var found = false;
        foreach (var candidate in Enum.GetValues<SceneGizmoAxis>())
        {
            if (!TryIntersectPlane(ray, origin, SceneTranslationGizmo.AxisDirection(candidate), out var distance, out var vector)) continue;
            var radialError = MathF.Abs(vector.Length() - radius);
            if (radialError > pickThickness || distance >= nearestDistance) continue;
            found = true;
            nearestDistance = distance;
            axis = candidate;
            ringVector = Vector3.Normalize(vector);
        }
        return found;
    }

    public bool TryGetRingVector(SceneRay ray, Vector3 origin, SceneGizmoAxis axis, out Vector3 ringVector)
    {
        if (!TryIntersectPlane(ray, origin, SceneTranslationGizmo.AxisDirection(axis), out _, out var vector)
            || vector.LengthSquared() == 0f)
        {
            ringVector = Vector3.Zero;
            return false;
        }
        ringVector = Vector3.Normalize(vector);
        return true;
    }

    public static float SignedAngle(SceneGizmoAxis axis, Vector3 from, Vector3 to)
    {
        var direction = SceneTranslationGizmo.AxisDirection(axis);
        return MathF.Atan2(Vector3.Dot(direction, Vector3.Cross(from, to)), Vector3.Dot(from, to));
    }

    private static bool TryIntersectPlane(
        SceneRay ray,
        Vector3 origin,
        Vector3 normal,
        out float distance,
        out Vector3 radialVector)
    {
        var denominator = Vector3.Dot(ray.Direction, normal);
        if (denominator == 0f)
        {
            distance = 0;
            radialVector = Vector3.Zero;
            return false;
        }
        distance = Vector3.Dot(origin - ray.Origin, normal) / denominator;
        if (distance < 0 || !float.IsFinite(distance))
        {
            radialVector = Vector3.Zero;
            return false;
        }
        radialVector = ray.Origin + ray.Direction * distance - origin;
        return true;
    }

    private static void Validate(Vector3 origin, float radius)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
    }
}
