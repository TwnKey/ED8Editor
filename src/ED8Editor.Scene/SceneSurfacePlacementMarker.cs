using System.Numerics;

namespace ED8Editor.Scene;

/// <summary>
/// Builds a flat, translucent placement decal in the tangent plane of the
/// picked surface. It is renderer-independent overlay geometry, so every
/// surface-placement workflow can share the same cursor.
/// </summary>
public static class SceneSurfacePlacementMarker
{
    private const int SegmentCount = 20;

    public static SceneOverlayGeometry Build(
        Vector3 position,
        Vector3 surfaceNormal,
        float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));

        var normal = surfaceNormal == Vector3.Zero
            ? Vector3.UnitY
            : Vector3.Normalize(surfaceNormal);
        var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) < 0.95f
            ? Vector3.UnitY
            : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(reference, normal));
        var bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        var center = position + normal * Math.Max(radius * 0.015f, 0.005f);
        var lines = new List<SceneOverlayLine>(SegmentCount + 2);
        var triangles = new List<SceneOverlayTriangle>(SegmentCount);
        var outline = new Vector4(0.12f, 1f, 0.74f, 0.95f);
        var fill = new Vector4(0.08f, 0.9f, 0.68f, 0.08f);

        var previous = PointOnCircle(center, tangent, bitangent, radius, 0f);
        for (var segment = 1; segment <= SegmentCount; segment++)
        {
            var angle = segment * MathF.Tau / SegmentCount;
            var current = PointOnCircle(center, tangent, bitangent, radius, angle);
            lines.Add(new SceneOverlayLine(previous, current, outline, 3f));
            triangles.Add(new SceneOverlayTriangle(center, previous, current, fill));
            previous = current;
        }

        var crossRadius = radius * 0.45f;
        lines.Add(new SceneOverlayLine(
            center - tangent * crossRadius,
            center + tangent * crossRadius,
            outline,
            3f));
        lines.Add(new SceneOverlayLine(
            center - bitangent * crossRadius,
            center + bitangent * crossRadius,
            outline,
            3f));
        return new SceneOverlayGeometry(lines, triangles);
    }

    private static Vector3 PointOnCircle(
        Vector3 center,
        Vector3 tangent,
        Vector3 bitangent,
        float radius,
        float angle)
        => center + (tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * radius;
}
