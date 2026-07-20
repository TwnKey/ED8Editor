using System.Numerics;

namespace ED8Editor.Scene;

public enum SceneGizmoAxis
{
    X,
    Y,
    Z,
}

public sealed class SceneTranslationGizmo
{
    private static readonly Vector4 XColor = new(1f, 0.12f, 0.12f, 1f);
    private static readonly Vector4 YColor = new(0.15f, 1f, 0.2f, 1f);
    private static readonly Vector4 ZColor = new(0.15f, 0.4f, 1f, 1f);
    private static readonly Vector4 ActiveColor = new(1f, 0.85f, 0.1f, 1f);

    public IReadOnlyList<SceneOverlayLine> Build(Vector3 origin, float length, SceneGizmoAxis? activeAxis = null)
    {
        Validate(origin, length);
        var lines = new List<SceneOverlayLine>(9);
        AddAxis(SceneGizmoAxis.X, Vector3.UnitX, XColor);
        AddAxis(SceneGizmoAxis.Y, Vector3.UnitY, YColor);
        AddAxis(SceneGizmoAxis.Z, Vector3.UnitZ, ZColor);
        return lines;

        void AddAxis(SceneGizmoAxis axis, Vector3 direction, Vector4 color)
        {
            var thickness = axis == activeAxis ? 4f : 3f;
            if (axis == activeAxis) color = ActiveColor;
            var end = origin + direction * length;
            lines.Add(new SceneOverlayLine(origin, end, color, thickness));
            var arrowSize = length * 0.12f;
            var sideA = axis == SceneGizmoAxis.Y ? Vector3.UnitX : Vector3.UnitY;
            var sideB = Vector3.Normalize(Vector3.Cross(direction, sideA));
            lines.Add(new SceneOverlayLine(end, end - direction * arrowSize + sideA * arrowSize * 0.45f, color, thickness));
            lines.Add(new SceneOverlayLine(end, end - direction * arrowSize + sideB * arrowSize * 0.45f, color, thickness));
        }
    }

    public bool TryPickAxis(
        SceneRay ray,
        Vector3 origin,
        float length,
        float pickRadius,
        out SceneGizmoAxis axis)
    {
        Validate(origin, length);
        if (!float.IsFinite(pickRadius) || pickRadius <= 0) throw new ArgumentOutOfRangeException(nameof(pickRadius));
        axis = default;
        var found = false;
        var nearestRayDistance = float.PositiveInfinity;
        foreach (var candidate in Enum.GetValues<SceneGizmoAxis>())
        {
            var direction = AxisDirection(candidate);
            if (!TryClosestParameters(ray, origin, direction, out var rayDistance, out var axisDistance)) continue;
            axisDistance = Math.Clamp(axisDistance, 0f, length);
            var axisPoint = origin + direction * axisDistance;
            rayDistance = Math.Max(0f, Vector3.Dot(axisPoint - ray.Origin, ray.Direction));
            var rayPoint = ray.Origin + ray.Direction * rayDistance;
            if (Vector3.DistanceSquared(axisPoint, rayPoint) > pickRadius * pickRadius
                || rayDistance >= nearestRayDistance) continue;
            found = true;
            nearestRayDistance = rayDistance;
            axis = candidate;
        }
        return found;
    }

    public bool TryGetAxisParameter(SceneRay ray, Vector3 origin, SceneGizmoAxis axis, out float parameter)
    {
        if (!TryClosestParameters(ray, origin, AxisDirection(axis), out var rayDistance, out parameter)
            || rayDistance < 0)
        {
            parameter = 0;
            return false;
        }
        return true;
    }

    public static Vector3 AxisDirection(SceneGizmoAxis axis) => axis switch
    {
        SceneGizmoAxis.X => Vector3.UnitX,
        SceneGizmoAxis.Y => Vector3.UnitY,
        SceneGizmoAxis.Z => Vector3.UnitZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static bool TryClosestParameters(
        SceneRay ray,
        Vector3 axisOrigin,
        Vector3 axisDirection,
        out float rayParameter,
        out float axisParameter)
    {
        var offset = ray.Origin - axisOrigin;
        var directionDotAxis = Vector3.Dot(ray.Direction, axisDirection);
        var denominator = 1f - directionDotAxis * directionDotAxis;
        if (denominator == 0f)
        {
            rayParameter = 0;
            axisParameter = 0;
            return false;
        }
        var directionDotOffset = Vector3.Dot(ray.Direction, offset);
        var axisDotOffset = Vector3.Dot(axisDirection, offset);
        rayParameter = (directionDotAxis * axisDotOffset - directionDotOffset) / denominator;
        axisParameter = (axisDotOffset - directionDotAxis * directionDotOffset) / denominator;
        return float.IsFinite(rayParameter) && float.IsFinite(axisParameter);
    }

    private static void Validate(Vector3 origin, float length)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }
        if (!float.IsFinite(length) || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
    }
}
