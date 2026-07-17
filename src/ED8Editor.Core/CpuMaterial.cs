using System.Numerics;

namespace ED8Editor.Core;

public sealed record CpuMaterial(
    string Name,
    Vector4 BaseColor,
    int? BaseColorTextureIndex,
    IReadOnlyDictionary<string, float[]> SourceParameters,
    IReadOnlyDictionary<string, string> SourceTextureReferences,
    IReadOnlyDictionary<string, int> TextureBindings);
