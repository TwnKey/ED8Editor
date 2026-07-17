namespace ED8Editor.Core;

public sealed record CpuModel(
    string AssetId,
    IReadOnlyList<CpuMesh> Meshes,
    IReadOnlyList<CpuMaterial> Materials,
    IReadOnlyList<CpuTexture> Textures);
