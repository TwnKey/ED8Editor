using System.Numerics;

namespace ED8Editor.Core;

public sealed record MapTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector3 SourcePosition,
    Vector3 SourceEulerRadians);
