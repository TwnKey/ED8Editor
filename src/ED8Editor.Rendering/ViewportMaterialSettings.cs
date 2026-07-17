using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Rendering;

public sealed record ViewportMaterialSettings(Vector4 BaseColor, float? AlphaThreshold)
{
    public static ViewportMaterialSettings Fallback { get; } = new(
        new Vector4(0.72f, 0.78f, 0.86f, 1f),
        null);

    public static ViewportMaterialSettings FromMaterial(CpuMaterial? material)
    {
        if (material is null) return Fallback;
        float? alphaThreshold = null;
        if (material.SourceParameters.TryGetValue("AlphaThreshold", out var values)
            && values.Length != 0
            && float.IsFinite(values[0]))
        {
            alphaThreshold = values[0];
        }
        return new ViewportMaterialSettings(material.BaseColor, alphaThreshold);
    }
}
