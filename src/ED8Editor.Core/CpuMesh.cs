using System.Numerics;

namespace ED8Editor.Core;

public sealed record CpuMesh(
    string Name,
    Matrix4x4 LocalTransform,
    IReadOnlyList<CpuMeshPrimitive> Primitives);
