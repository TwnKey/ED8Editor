namespace ED8Editor.Core;

public sealed record CpuVertexBuffer(
    byte[] Data,
    int Stride,
    int VertexCount,
    IReadOnlyList<CpuVertexAttribute> Attributes);
