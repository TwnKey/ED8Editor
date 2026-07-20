using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Rendering;

public enum ViewportMultiUvBlendMode
{
    Disabled,
    Alpha,
    Additive,
    Multiplicative,
    Shadow,
}

public sealed record ViewportMaterialSettings(
    Vector4 BaseColor,
    float? AlphaThreshold,
    bool AlphaTestingEnabled,
    bool LightingEnabled,
    bool VertexColorEnabled,
    bool AlphaBlendingEnabled,
    bool GlareHighPassEnabled,
    float GlareIntensity,
    ViewportMultiUvBlendMode MultiUvBlendMode,
    Vector4 MultiUvColor,
    Vector4 MultiUvTransform)
{
    public static ViewportMaterialSettings Fallback { get; } = new(
        new Vector4(0.72f, 0.78f, 0.86f, 1f),
        null,
        false,
        true,
        false,
        false,
        false,
        1f,
        ViewportMultiUvBlendMode.Disabled,
        Vector4.One,
        new Vector4(0f, 0f, 1f, 1f));

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
        var switches = material.EffectSwitches;
        var glareIntensity = 1f;
        if (material.SourceParameters.TryGetValue("GlareIntensity", out var glareValues)
            && glareValues.Length != 0
            && float.IsFinite(glareValues[0]))
        {
            glareIntensity = glareValues[0];
        }
        var multiUvBlendMode = switches?.ContainsKey("MULTI_UV_ENANLED") == true
            && switches.ContainsKey("MULTI_UV_NO_DIFFUSE_MAPPING_ENANLED") == false
                ? switches.ContainsKey("MULTI_UV_ADDITIVE_BLENDING_ENANLED")
                    ? ViewportMultiUvBlendMode.Additive
                    : switches.ContainsKey("MULTI_UV_MULTIPLICATIVE_BLENDING_ENANLED")
                        ? ViewportMultiUvBlendMode.Multiplicative
                        : switches.ContainsKey("MULTI_UV_SHADOW_ENANLED")
                            ? ViewportMultiUvBlendMode.Shadow
                            : ViewportMultiUvBlendMode.Alpha
                : ViewportMultiUvBlendMode.Disabled;
        return new ViewportMaterialSettings(
            material.BaseColor,
            alphaThreshold,
            switches?.ContainsKey("ALPHA_TESTING_ENABLED") == true,
            switches?.ContainsKey("NO_ALL_LIGHTING_ENABLED") != true,
            switches?.ContainsKey("VERTEX_COLOR_ENABLED") == true,
            switches?.ContainsKey("ALPHA_BLENDING_ENABLED") == true,
            switches?.ContainsKey("GLARE_HIGHTPASS_ENABLED") == true,
            glareIntensity,
            multiUvBlendMode,
            ReadVector4(material, "UVaMUvColor", Vector4.One),
            ReadVector4(material, "UVaMUvTexcoord", new Vector4(0f, 0f, 1f, 1f)));
    }

    private static Vector4 ReadVector4(CpuMaterial material, string name, Vector4 fallback)
    {
        if (!material.SourceParameters.TryGetValue(name, out var values) || values.Length < 4) return fallback;
        var value = new Vector4(values[0], values[1], values[2], values[3]);
        return float.IsFinite(value.X) && float.IsFinite(value.Y)
            && float.IsFinite(value.Z) && float.IsFinite(value.W)
                ? value
                : fallback;
    }
}
