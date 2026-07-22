namespace ED8Editor.Core;

public sealed record CpuMeshPrimitive(
    IReadOnlyList<CpuVertexBuffer> VertexBuffers,
    CpuIndexBuffer Indices,
    int MaterialIndex,
    PrimitiveTopology Topology,
    IReadOnlyList<CpuSkinBoneRemap>? SkinBones = null);
