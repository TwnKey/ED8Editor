using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed record SceneModelInstance(
    int Id,
    string AssetId,
    string Name,
    CpuModel Model,
    Matrix4x4 Transform,
    Vector4 MaterialDiffuse = default,
    Vector3 MaterialEmission = default,
    SceneElementKind SelectionKind = SceneElementKind.Prop);

public readonly record struct SceneRay
{
    public SceneRay(Vector3 origin, Vector3 direction)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            throw new ArgumentException("Ray origin must be finite.", nameof(origin));
        }
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z)
            || direction == Vector3.Zero)
        {
            throw new ArgumentException("Ray direction must be finite and non-zero.", nameof(direction));
        }
        Origin = origin;
        Direction = Vector3.Normalize(direction);
    }

    public Vector3 Origin { get; }
    public Vector3 Direction { get; }
}

public sealed record ScenePickHit(
    SceneModelInstance Instance,
    int MeshIndex,
    int PrimitiveIndex,
    int TriangleIndex,
    Vector3 Position,
    Vector3 Normal,
    float Distance);

public sealed record SceneGeometryIssue(
    int InstanceId,
    int MeshIndex,
    int PrimitiveIndex,
    string Reason);

public sealed record SceneRaycastResult(
    ScenePickHit? Hit,
    int TestedTriangles,
    IReadOnlyList<SceneGeometryIssue> Issues);

public sealed record SceneRaycastHitsResult(
    IReadOnlyList<ScenePickHit> Hits,
    int TestedTriangles,
    IReadOnlyList<SceneGeometryIssue> Issues);
