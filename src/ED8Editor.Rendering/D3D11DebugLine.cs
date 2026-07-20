using System.Numerics;

namespace ED8Editor.Rendering;

public sealed record D3D11DebugLine(Vector3 Start, Vector3 End, Vector4 Color, float Thickness = 1f);
