using System.Numerics;

namespace ED8Editor.Core;

public sealed record MapEnvironment(
    string ProfileName,
    Vector3 FogColor,
    float FogNearDistance,
    float FogFarDistance,
    IReadOnlyDictionary<string, string> SourceFogAttributes);
