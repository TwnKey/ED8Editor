namespace ED8Editor.Shaders.Forge;

/// <summary>
/// Which engine parameter a shader declaration asks for, from the semantic it is
/// written with.
///
/// This is PhyreEngine's own resolution, transcribed from
/// <c>Core/Rendering/PhyreSemantic.cpp</c> and
/// <c>Include/Rendering/PhyreShaderParameterDefinition.h</c>: an exact table of
/// semantic names first, then the parameter's own name against the same table, then
/// a search through the string for the words the engine looks for. A declaration
/// nothing recognises is a material parameter, which is what makes a shader with
/// invented uniforms writable at all.
///
/// The bytecode does not keep these semantics — D3D drops them for uniforms — so
/// they are read from the HLSL source, which is where they are written.
/// </summary>
public static class Semantic
{
    // PShaderParameter, by block: the scene at 0, the material at 64, the node at
    // 128 and the node context at 192.
    public const byte Constant = 64;
    public const byte Color = 65;
    public const byte Texture2D = 66;
    public const byte Texture3D = 67;
    public const byte TextureCube = 68;
    public const byte Sampler = 71;
    public const byte Unknown = 70;

    private const byte MatrixProjection = 2;
    private const byte MatrixView = 3;
    private const byte MatrixViewInv = 4;
    private const byte MatrixViewInvTranspose = 5;
    private const byte MatrixViewProjection = 6;
    private const byte GlobalAmbientColor = 16;
    private const byte Time = 17;

    private const byte EyeDirectionObject = 128;
    private const byte EyePositionObject = 129;
    private const byte MatrixModel = 130;
    private const byte MatrixModelView = 131;
    private const byte MatrixModelViewProjection = 132;
    private const byte MatrixModelInv = 133;
    private const byte MatrixModelViewInv = 134;
    private const byte MatrixModelViewProjectionInv = 135;
    private const byte MatrixModelInvTranspose = 136;
    private const byte MatrixModelViewInvTranspose = 137;
    private const byte MatrixModelViewProjectionInvTranspose = 138;
    private const byte MatrixModelTranspose = 139;
    private const byte MatrixModelViewTranspose = 140;

    private const byte EyeDirectionWorld = 0;
    private const byte EyePositionWorld = 1;

    private const byte LightColor = 192;
    private const byte LightIntensity = 193;
    private const byte LightColorTimesIntensity = 194;
    private const byte LightAttenuation = 195;
    private const byte LightInnerConeAngle = 196;
    private const byte LightOuterConeAngle = 197;
    private const byte LightCosInnerConeAngle = 198;
    private const byte LightCosOuterConeAngle = 199;
    private const byte LightSpotAngles = 200;
    private const byte LightDirectionObject = 201;
    private const byte LightPositionObject = 202;
    private const byte LightDirectionWorld = 203;
    private const byte LightPositionWorld = 204;
    private const byte LightDirectionCamera = 205;
    private const byte LightPositionCamera = 206;
    private const byte ShadowTransform0 = 211;
    private const byte ShadowTransform1 = 212;
    private const byte ShadowTransform2 = 213;
    private const byte ShadowTransform3 = 214;
    private const byte ShadowTransformArray = 215;
    private const byte ShadowSplitDistances = 216;
    private const byte ShadowMap0 = 217;
    private const byte ShadowMap1 = 218;
    private const byte ShadowMap2 = 219;
    private const byte ShadowMap3 = 220;

    /// <summary>PhyreEngine's table of semantic names, plus what Falcom added to it.</summary>
    private static readonly Dictionary<string, byte> Mappings = new(StringComparer.Ordinal)
    {
        ["world"] = MatrixModel,
        ["worldinverse"] = MatrixModelInv,
        ["worldinversetranspose"] = MatrixModelInvTranspose,
        ["worldview"] = MatrixModelView,
        ["worldviewinverse"] = MatrixModelViewInv,
        ["worldviewinversetranspose"] = MatrixModelViewInvTranspose,
        ["worldviewproj"] = MatrixModelViewProjection,
        ["worldviewprojection"] = MatrixModelViewProjection,
        ["worldviewprojectioninverse"] = MatrixModelViewProjectionInv,
        ["worldviewprojectioninversetranspose"] = MatrixModelViewProjectionInvTranspose,
        ["projection"] = MatrixProjection,
        ["worldviewtranspose"] = MatrixModelViewTranspose,
        ["worldtranspose"] = MatrixModelTranspose,
        ["diffuse"] = Color,
        ["specular"] = Color,
        ["ambient"] = Color,
        ["specularpower"] = Constant,
        ["emissive"] = Color,
        ["view"] = MatrixView,
        ["viewprojection"] = MatrixViewProjection,
        ["viewinverse"] = MatrixViewInv,
        ["viewinversetranspose"] = MatrixViewInvTranspose,
        ["lightinnerangle"] = LightInnerConeAngle,
        ["lightouterangle"] = LightOuterConeAngle,
        ["lightcosinnerangle"] = LightCosInnerConeAngle,
        ["lightcosouterangle"] = LightCosOuterConeAngle,
        ["lightspotangles"] = LightSpotAngles,
        ["globalambientcolor"] = GlobalAmbientColor,
        ["time"] = Time,

        // Falcom's own, past the end of the engine's range. Not in the SDK we have —
        // that one is Cold Steel 3's — but read straight off Cold Steel 1's effects,
        // where ed8.fx declares them as NodeEdgeParameters, NodeMaterialDiffuse and
        // NodeMaterialEmission.
        ["nodeedgeparameters"] = 223,
        ["nodematerialdiffuse"] = 224,
        ["nodematerialemission"] = 225,
    };

    /// <summary>
    /// The parameter a declaration asks for, or null when nothing recognises it and
    /// it is the material's to fill.
    /// </summary>
    public static byte? Of(string? name, string? semantic)
    {
        if (semantic is not null && Mappings.TryGetValue(semantic.ToLowerInvariant(), out var byName))
        {
            return byName;
        }
        if (name is not null && Mappings.TryGetValue(name.ToLowerInvariant(), out var byOwnName))
        {
            return byOwnName;
        }
        return Search(semantic) ?? Search(name);
    }

    /// <summary>
    /// The engine's own search through the string: shadows first, then the ambient
    /// and the clock, then lights, then the eye. Word for word as PhyreSemantic.cpp
    /// does it, including that it looks for "po" so as to catch "pos", "position"
    /// and "point" alike.
    /// </summary>
    private static byte? Search(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var all = text.ToLowerInvariant();

        var shadow = From(all, "shadow");
        if (shadow is not null)
        {
            if (Has(shadow, "distance")) return ShadowSplitDistances;
            if (Has(shadow, "map"))
            {
                return Split(shadow, ShadowMap0, ShadowMap1, ShadowMap2, ShadowMap3, ShadowMap0);
            }
            if (Has(shadow, "transform"))
            {
                if (Has(shadow, "array")) return ShadowTransformArray;
                return Split(shadow, ShadowTransform0, ShadowTransform1, ShadowTransform2,
                    ShadowTransform3, ShadowTransform0);
            }
            return null;
        }

        if (Has(all, "globalambient")) return GlobalAmbientColor;
        if (Has(all, "time")) return Time;

        var light = From(all, "light");
        if (light is not null)
        {
            if (Has(light, "colortimesinten") || Has(light, "colorinten")) return LightColorTimesIntensity;
            if (Has(all, "spotangles")) return LightSpotAngles;
            if (Has(light, "inten")) return LightIntensity;
            if (Has(light, "col")) return LightColor;
            if (Has(light, "po"))
            {
                if (Has(light, "ws") || Has(light, "world")) return LightPositionWorld;
                if (Has(light, "cs") || Has(light, "cam")) return LightPositionCamera;
                return LightPositionObject;
            }
            if (Has(light, "dir"))
            {
                if (Has(light, "ws") || Has(light, "world")) return LightDirectionWorld;
                if (Has(light, "cs") || Has(light, "cam")) return LightDirectionCamera;
                return LightDirectionObject;
            }
            if (Has(all, "att")) return LightAttenuation;
            if (Has(all, "cone") || Has(all, "angle"))
            {
                var cosine = Has(all, "cos");
                if (Has(all, "inner")) return cosine ? LightCosInnerConeAngle : LightInnerConeAngle;
                if (Has(all, "outer")) return cosine ? LightCosOuterConeAngle : LightOuterConeAngle;
            }
            return null;
        }

        var eye = From(all, "eye") ?? From(all, "cam");
        if (eye is null) return null;
        if (Has(eye, "po"))
        {
            return Has(eye, "ws") || Has(eye, "world") ? EyePositionWorld : EyePositionObject;
        }
        if (Has(eye, "dir"))
        {
            return Has(eye, "ws") || Has(eye, "world") ? EyeDirectionWorld : EyeDirectionObject;
        }
        return null;
    }

    /// <summary>Which split a shadow parameter names, or the unnumbered one.</summary>
    private static byte Split(string text, byte zero, byte one, byte two, byte three, byte plain) =>
        Has(text, "split0") ? zero
        : Has(text, "split1") ? one
        : Has(text, "split2") ? two
        : Has(text, "split3") ? three
        : plain;

    /// <summary>The rest of the string from a word, as strstr gives it.</summary>
    private static string? From(string text, string word)
    {
        var at = text.IndexOf(word, StringComparison.Ordinal);
        return at < 0 ? null : text[at..];
    }

    private static bool Has(string text, string word) =>
        text.Contains(word, StringComparison.Ordinal);

    /// <summary>
    /// The hash a vertex stream carries, by which the engine matches a shader's
    /// input to a mesh's data. PhyreEngine's <c>PHashTableTree::Hash</c>, seeded at
    /// 1973, keeping only the low five bits of each character — and it is that
    /// masking which defeats every attempt to recognise the function from its
    /// outputs. Kept to sixteen bits, as PShaderStreamDefinition stores it.
    /// </summary>
    public static ushort Hash(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var hash = 1973u;
        foreach (var one in name)
        {
            hash = unchecked(hash * 33 + (one & 0x1Fu));
        }
        return (ushort)hash;
    }
}
