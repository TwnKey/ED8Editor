using ED8Editor.Core;
using ED8Editor.Phyre;
using System.Text;

namespace ED8Editor.Shaders.Compilation;

/// <summary>
/// Extracts the complete shader signature from an existing fx.phyre —
/// every parameter definition, sampler state, and constant buffer layout —
/// as a blueprint that a custom shader must match.
/// </summary>
public sealed class PhyreShaderSignatureExtractor
{
    public PhyreShaderSignature Extract(byte[] fxPhyreData)
    {
        var cluster = new PhyreClusterReader().Read(fxPhyreData);

        var parameters = new List<ShaderParamDef>();
        var samplers = new List<SamplerDef>();
        var textures = new List<TextureDef>();

        for (var gi = 0; gi < cluster.Metadata.InstanceGroups.Count; gi++)
        {
            var g = cluster.Metadata.InstanceGroups[gi];

            if (g.ClassName == "PShaderParameterDefinition" && g.Count > 0)
            {
                for (uint i = 0; i < g.Count; i++)
                {
                    var obj = cluster.GetObject(gi, i).Span;
                    var name = ResolveName(cluster, gi, i);
                    if (name is null) continue;

                    parameters.Add(new ShaderParamDef(
                        name,
                        ArrayCount: (ushort)(obj[0] | (obj[1] << 8)),
                        ParamType: obj[2],
                        DataType: obj[3],
                        BufferLoc: BitConverter.ToUInt32(obj[8..12]),
                        CbLocation: BitConverter.ToUInt32(obj[12..16])));
                }
            }
            else if (g.ClassName == "PSamplerState" && g.Count > 0)
            {
                // PSamplerState names are also resolved via fixup at m_name offset
                for (uint i = 0; i < g.Count; i++)
                {
                    var obj = cluster.GetObject(gi, i).Span;
                    if (obj.Length < 20) continue;
                    var name = ResolveName(cluster, gi, i);
                    if (name is null) continue;

                    samplers.Add(new SamplerDef(
                        name,
                        Filter: obj.Length > 8 ? BitConverter.ToUInt32(obj[4..8]) : 0,
                        AddressU: obj.Length > 12 ? BitConverter.ToUInt32(obj[8..12]) : 0,
                        AddressV: obj.Length > 16 ? BitConverter.ToUInt32(obj[12..16]) : 0,
                        AddressW: obj.Length > 20 ? BitConverter.ToUInt32(obj[16..20]) : 0));
                }
            }
        }

        // Also read the effect metadata for passes and context switches
        var metadata = new PhyreEffectRenderPassReader().ReadMetadata(fxPhyreData);
        var passes = metadata.RenderPassStates.Keys.ToArray();
        var contextSwitches = metadata.Program?.ContextSwitches?.ToArray() ?? Array.Empty<string>();
        var contexts = metadata.Program?.Contexts?.ToArray() ?? Array.Empty<CpuShaderContext>();

        // Deduplicate parameters — same name at different cbLocations are different context variants
        // Keep only one parameter per CbLocation
        var uniqueParams = new List<ShaderParamDef>();
        foreach (var p in parameters
            .Where(p => p.CbLocation != 0xFFFFFFFF)
            .OrderBy(p => p.CbLocation).ThenBy(p => p.Name))
        {
            // Skip if we already have a param at this exact offset
            if (uniqueParams.Any(x => x.CbLocation == p.CbLocation)) continue;

            var finalName = p.Name;
            var dupCount = parameters.Count(x => x.Name == p.Name && x.CbLocation != p.CbLocation);
            if (dupCount > 0)
                finalName = $"{p.Name}_0x{p.CbLocation:X3}";
            
            uniqueParams.Add(p with { Name = finalName });
        }

        var uniqueSamplers = samplers
            .GroupBy(s => s.Name)
            .Select(g => g.First())
            .OrderBy(s => s.Name)
            .ToList();

        // Add known scene parameters (from PS reflection, engine always writes these)
        AddSceneParameters(uniqueParams);
        // Add known sampler names (from earlier dump)
        if (uniqueSamplers.Count == 0)
            AddKnownSamplers(uniqueSamplers);

        // Final sort by cbLocation
        uniqueParams = uniqueParams.OrderBy(p => p.CbLocation).ToList();

        return new PhyreShaderSignature(
            contextSwitches,
            contexts,
            passes,
            uniqueParams.ToArray(),
            uniqueSamplers.ToArray(),
            textures.ToArray(),
            fxPhyreData);
    }

    private static void AddSceneParameters(List<ShaderParamDef> parms)
    {
        var scene = new (string Name, uint Off, string HlslType)[]
        {
            ("scene_EyePosition", 0x000, "float3"),
            ("scene_View", 0x010, "float4x4"),
            ("scene_ViewProjection", 0x050, "float4x4"),
            ("scene_cameraNearFarParameters", 0x090, "float4"),
            ("scene_viewportSizeParameters", 0x0A0, "float4"),
            ("scene_FakeRimLightDir", 0x0B0, "float3"),
            ("scene_GlobalAmbientColor", 0x0C0, "float3"),
            ("scene_FogColor", 0x0D0, "float3"),
            ("scene_FogRangeParameters", 0x0E0, "float4"),
            ("scene_MiscParameters1", 0x0F0, "float3"),
            ("scene_MiscParameters2", 0x100, "float4"),
            ("AdditionalShadowOffset", 0x110, "float"),
            ("scene_light1_position", 0x120, "float4"),
            ("scene_light1_colorIntensity", 0x130, "float3"),
            ("scene_light1_attenuation", 0x140, "float4"),
            ("scene_light2_position", 0x150, "float3"),
            ("scene_light2_colorIntensity", 0x160, "float3"),
            ("scene_light2_attenuation", 0x170, "float4"),
            ("World", 0x190, "float4x4"),
            ("GlobalMainLightClampFactor", 0x1D4, "float"),
            ("GlobalTexcoordFactor", 0x210, "float"),
            ("AlphaTestDirection", 0x214, "float"),
            ("HemiSphereAmbientSkyColor", 0x230, "float3"),
            ("HemiSphereAmbientGndColor", 0x240, "float3"),
            ("HemiSphereAmbientAxis", 0x250, "float3"),
            ("TexCoordOffset", 0x278, "float2"),
            ("TexCoordOffset2", 0x280, "float2"),
            ("TexCoordOffset3", 0x288, "float2"),
            ("SphereMapIntensity", 0x290, "float"),
            ("WindyGrassDirection", 0x2C0, "float2"),
            ("WindyGrassSpeed", 0x2C8, "float"),
            ("WindyGrassHomogenity", 0x2CC, "float"),
            ("WindyGrassScale", 0x2D0, "float"),
            ("DuranteSettings", 0x300, "float4"),
        };
        foreach (var (name, off, type) in scene)
        {
            if (!parms.Any(p => p.CbLocation == off))
                parms.Add(new ShaderParamDef(name, 1, 64, 0, 0, off) { _overrideSize = HlslTypeSize(type), _overrideHlslType = type });
        }
    }

    private static void AddKnownSamplers(List<SamplerDef> samplers)
    {
        var names = new[] {
            "ShadowMapSampler", "DiffuseMapSamplerS", "DiffuseMap2SamplerS", "DiffuseMap3SamplerS",
            "DiffuseMapTrans1SamplerS", "DiffuseMapTrans2SamplerS", "NormalMapSamplerS", "NormalMap2SamplerS",
            "SpecularMapSamplerS", "SpecularMap2SamplerS", "SpecularMap3SamplerS",
            "OcculusionMapSamplerS", "OcculusionMap2SamplerS", "OcculusionMap3SamplerS",
            "ProjectionMapSamplerS", "CartoonMapSamplerS", "HighlightMapSamplerS",
            "GlareMapSamplerS", "EmissionMapSamplerS", "DuDvMapSamplerS", "CubeMapSamplerS",
            "LinearWrapSampler", "PointWrapSampler", "MinimapTextureSamplerS", "DiffuseMapSampler",
        };
        foreach (var name in names)
            samplers.Add(new SamplerDef(name, 0, 0, 0, 0));
    }

    private static int HlslTypeSize(string type) => type switch
    {
        "float4x4" => 64, "float3" => 12, "float2" => 8, "float" => 4, _ => 16
    };

    private static string? ResolveName(PhyreClusterData cluster, int groupIdx, uint objId)
    {
        // Try array fixup at offset 0x04 first (works for PShaderParameterDefinition)
        var fixup = cluster.Fixups.Arrays.FirstOrDefault(f =>
            f.SourceListIndex == groupIdx && f.SourceObjectId == objId
            && f.SourceOffset == 0x04);
        // Also try member fixup (for PSamplerState etc.)
        fixup ??= cluster.Fixups.Arrays.FirstOrDefault(f =>
            f.SourceListIndex == groupIdx && f.SourceObjectId == objId
            && f.IsClassDataMember);
        if (fixup is null) return null;
        try
        {
            var size = fixup.Count == 0 ? 256u : fixup.Count;
            var data = cluster.GetArrayData(groupIdx, fixup.Offset, Math.Min(size, 1024u)).Span;
            var nul = data.IndexOf((byte)0);
            return nul >= 0
                ? Encoding.ASCII.GetString(data[..nul])
                : Encoding.ASCII.GetString(data);
        }
        catch { return null; }
    }

    /// <summary>
    /// Generates the HLSL constant buffer declaration that matches the shader signature.
    /// </summary>
    public string GenerateCbDeclaration(PhyreShaderSignature sig)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated constant buffer matching CS1 shader signature");
        sb.AppendLine($"// {sig.UniqueParameters.Length} parameters, {sig.UniqueSamplers.Length} samplers");
        sb.AppendLine();
        sb.AppendLine("cbuffer Globals : register(b0)");
        sb.AppendLine("{");

        var paramList = sig.UniqueParameters.OrderBy(p => p.CbLocation).ToArray();
        var maxOffset = 0;
        foreach (var p in paramList)
        {
            var end = (int)p.CbLocation + p.SizeInBytes();
            if (end > maxOffset) maxOffset = end;
        }
        maxOffset = Math.Min(maxOffset + 16, 4096); // cap at 4KB
        var covered = new bool[maxOffset];

        foreach (var p in paramList)
        {
            var type = ParamTypeToHlsl(p.ParamType, p.DataType, p);
            var arrayStr = p.ArrayCount > 1 ? $"[{p.ArrayCount}]" : "";
            var packOffset = $" : packoffset(c{p.CbLocation / 16})";
            sb.AppendLine($"    {type}{arrayStr} {p.Name}{packOffset}; // offset=0x{p.CbLocation:X3}, size={p.SizeInBytes()}");

            // Mark as covered
            var end = (int)p.CbLocation + p.SizeInBytes();
            for (var j = (int)p.CbLocation; j < end && j < covered.Length; j++)
                covered[j] = true;
        }

        // Add padding for uncovered ranges (engine writes there)
        var inGap = false;
        var gapStart = 0;
        for (var i = 0; i < covered.Length; i += 4)
        {
            if (!covered[i] && !inGap)
            {
                inGap = true;
                gapStart = i;
            }
            else if ((covered[i] || i >= covered.Length - 4) && inGap)
            {
                var gapEnd = covered[i] ? i : covered.Length;
                var gapSize = gapEnd - gapStart;
                if (gapSize >= 4)
                {
                    sb.AppendLine($"    float _pad_0x{gapStart:X3}[{gapSize / 4}] : packoffset(c{gapStart / 16}); // engine-reserved padding");
                }
                inGap = false;
            }
        }

        sb.AppendLine("};");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Generates sampler declarations matching the shader signature.
    /// </summary>
    public string GenerateSamplerDeclarations(PhyreShaderSignature sig)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Samplers matching CS1 shader signature");
        var samplerNames = new HashSet<string>(sig.UniqueSamplers.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var slot = 0;
        foreach (var s in sig.UniqueSamplers)
        {
            var texName = s.Name.EndsWith("S", StringComparison.Ordinal) && s.Name.Length > 1
                ? s.Name[..^1]
                : s.Name + "Tex";
            // Avoid collision: if texName is also a sampler name, prefix it
            if (samplerNames.Contains(texName))
                texName = "t_" + texName;
            sb.AppendLine($"SamplerState {s.Name} : register(s{slot});");
            sb.AppendLine($"Texture2D {texName} : register(t{slot});");
            slot++;
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string ParamTypeToHlsl(byte paramType, byte dataType, ShaderParamDef? p = null)
    {
        if (p?._overrideHlslType is { } t) return t;
        if (paramType == 71 || paramType == 66) return "int"; // sampler slot reference
        if (dataType == 8 || dataType == 0) return "float4";
        if (dataType == 1) return "float2";
        if (dataType == 2) return "float3";
        if (dataType == 3) return "float4x4";
        if (dataType == 49) return "float4x4";
        if (dataType == 52) return "int"; // sampler index
        return "float4"; // fallback
    }
}

public sealed record PhyreShaderSignature(
    IReadOnlyList<string> ContextSwitches,
    IReadOnlyList<CpuShaderContext> Contexts,
    string[] Passes,
    ShaderParamDef[] UniqueParameters,
    SamplerDef[] UniqueSamplers,
    TextureDef[] UniqueTextures,
    byte[] OriginalFxData);

public sealed record ShaderParamDef(
    string Name,
    ushort ArrayCount,
    byte ParamType,
    byte DataType,
    uint BufferLoc,
    uint CbLocation)
{
    internal int _overrideSize = -1;
    internal string? _overrideHlslType;

    public int SizeInBytes()
    {
        if (_overrideSize >= 0) return _overrideSize;
        if (DataType == 8 || DataType == 0 || DataType == 3) return (ArrayCount > 1 ? ArrayCount : 1) * 16;
        if (DataType == 1) return (ArrayCount > 1 ? ArrayCount : 1) * 8;
        if (DataType == 2) return (ArrayCount > 1 ? ArrayCount : 1) * 12;
        if (DataType == 49) return 64;
        if (DataType == 52) return 4;
        return 16;
    }
}

public sealed record SamplerDef(
    string Name,
    uint Filter,
    uint AddressU,
    uint AddressV,
    uint AddressW);

public sealed record TextureDef(string Name, int Slot);
