using System.Numerics;

namespace ED8Editor.Core;

public sealed record MapProp(
    int SourceIndex,
    string AssetId,
    string Name,
    MapTransform Transform,
    uint? Flags,
    Vector4 MaterialDiffuse,
    Vector3 MaterialEmission,
    IReadOnlyDictionary<string, string> SourceAttributes);
