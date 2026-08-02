using System.Text;

namespace ED8Editor.Shaders.Compilation;

/// <summary>
/// Generates a minimal-but-compatible HLSL shader that matches an existing
/// Phyre shader signature. The generated code declares all required parameters
/// at their correct offsets (so the engine doesn't crash) but performs the
/// absolute minimum rendering logic.
/// </summary>
public sealed class PhyreCompatibilityShaderGenerator
{
    public string GenerateVertexShader(PhyreShaderSignature sig)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ============================================================");
        sb.AppendLine("// Minimal Compatible Vertex Shader for Cold Steel 1");
        sb.AppendLine("// Matches the game's constant buffer layout to prevent crashes.");
        sb.AppendLine("// ============================================================");
        sb.AppendLine();

        // CB declaration
        var extractor = new PhyreShaderSignatureExtractor();
        sb.Append(extractor.GenerateCbDeclaration(sig));

        // IO structures
        sb.AppendLine("struct VSInput");
        sb.AppendLine("{");
        sb.AppendLine("    float3 Position : POSITION;");
        sb.AppendLine("    float3 Normal : NORMAL;");
        sb.AppendLine("    float2 TexCoord0 : TEXCOORD0;");
        sb.AppendLine("    float2 TexCoord1 : TEXCOORD1;");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("struct PSInput");
        sb.AppendLine("{");
        sb.AppendLine("    float4 Position : SV_Position;");
        sb.AppendLine("    float3 WorldPos : TEXCOORD0;");
        sb.AppendLine("    float3 Normal : TEXCOORD1;");
        sb.AppendLine("    float2 TexCoord0 : TEXCOORD2;");
        sb.AppendLine("    float2 TexCoord1 : TEXCOORD3;");
        sb.AppendLine("};");
        sb.AppendLine();

        // Main VS
        sb.AppendLine("PSInput VSMain(VSInput input)");
        sb.AppendLine("{");
        sb.AppendLine("    PSInput output;");
        sb.AppendLine("    float4 worldPos = mul(float4(input.Position, 1.0f), World);");
        sb.AppendLine("    output.Position = mul(worldPos, scene_ViewProjection);");
        sb.AppendLine("    output.WorldPos = worldPos.xyz;");
        sb.AppendLine("    output.Normal = normalize(mul(input.Normal, (float3x3)World));");
        sb.AppendLine("    output.TexCoord0 = input.TexCoord0;");
        sb.AppendLine("    output.TexCoord1 = input.TexCoord1;");
        sb.AppendLine("    return output;");
        sb.AppendLine("}");
        sb.AppendLine();

        return sb.ToString();
    }

    public string GenerateFragmentShader(PhyreShaderSignature sig, CompatibilityLevel level = CompatibilityLevel.Basic)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ============================================================");
        sb.AppendLine("// Minimal Compatible Fragment Shader for Cold Steel 1");
        sb.AppendLine($"// Level: {level}");
        sb.AppendLine("// ============================================================");
        sb.AppendLine();

        var extractor = new PhyreShaderSignatureExtractor();
        sb.Append(extractor.GenerateCbDeclaration(sig));
        if (level >= CompatibilityLevel.SimpleTextured)
            sb.Append(extractor.GenerateSamplerDeclarations(sig));

        // IO (minimal for solid color test)
        sb.AppendLine("struct PSInput");
        sb.AppendLine("{");
        sb.AppendLine("    float4 Position : SV_Position;");
        if (level >= CompatibilityLevel.SimpleTextured)
        {
            sb.AppendLine("    float3 WorldPos : TEXCOORD0;");
            sb.AppendLine("    float3 Normal : TEXCOORD1;");
            sb.AppendLine("    float2 TexCoord0 : TEXCOORD2;");
        }
        sb.AppendLine("};");
        sb.AppendLine();

        // Main PS
        sb.AppendLine("float4 PSMain(PSInput input) : SV_Target");
        sb.AppendLine("{");

        switch (level)
        {
            case CompatibilityLevel.Basic:
                GenerateBasicPS(sb, sig);
                break;
            case CompatibilityLevel.SimpleTextured:
                GenerateSimpleTexturedPS(sb, sig);
                break;
            case CompatibilityLevel.FullLighting:
                GenerateFullLightingPS(sb, sig);
                break;
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void GenerateBasicPS(StringBuilder sb, PhyreShaderSignature sig)
    {
        // Absolute minimum: solid magenta — no textures, no samplers
        // This is JUST to test if the engine accepts the cbuffer layout
        sb.AppendLine("    // Minimal test shader — magenta = signature OK");
        sb.AppendLine("    return float4(1.0f, 0.0f, 1.0f, 1.0f); // magenta");
    }

    private static void GenerateSimpleTexturedPS(StringBuilder sb, PhyreShaderSignature sig)
    {
        sb.AppendLine("    // Simple textured with fog");
        sb.AppendLine("    float2 uv = input.TexCoord0;");
        sb.AppendLine();

        if (sig.UniqueParameters.Any(p => p.Name == "GameMaterialTexcoord"))
        {
            sb.AppendLine("    uv = uv * GameMaterialTexcoord.xy + GameMaterialTexcoord.zw;");
            sb.AppendLine();
        }

        sb.AppendLine("    float4 color = DiffuseMapSampler.Sample(DiffuseMapSamplerS, uv);");
        sb.AppendLine();

        // Apply alpha test
        if (sig.UniqueParameters.Any(p => p.Name == "AlphaThreshold"))
        {
            sb.AppendLine("    // Alpha test (required for transparent edges)");
            sb.AppendLine("    if (color.a < AlphaThreshold.x) discard;");
            sb.AppendLine();
        }

        // Apply fog
        if (sig.UniqueParameters.Any(p => p.Name == "FogRatio"))
        {
            sb.AppendLine("    // Fog");
            sb.AppendLine("    float fogFactor = saturate((scene_FogRangeParameters.z - length(input.WorldPos - scene_EyePosition)) / (scene_FogRangeParameters.z - scene_FogRangeParameters.x));");
            sb.AppendLine("    color.rgb = lerp(scene_FogColor.rgb, color.rgb, fogFactor * FogRatio.x);");
            sb.AppendLine();
        }

        sb.AppendLine("    return color;");
    }

    private static void GenerateFullLightingPS(StringBuilder sb, PhyreShaderSignature sig)
    {
        sb.AppendLine("    // Full lighting with diffuse, specular, fog, and alpha");
        sb.AppendLine("    float2 uv = input.TexCoord0;");
        sb.AppendLine("    float3 N = normalize(input.Normal);");
        sb.AppendLine("    float3 V = normalize(scene_EyePosition - input.WorldPos);");
        sb.AppendLine();

        if (sig.UniqueParameters.Any(p => p.Name == "GameMaterialTexcoord"))
        {
            sb.AppendLine("    uv = uv * GameMaterialTexcoord.xy + GameMaterialTexcoord.zw;");
            sb.AppendLine();
        }

        sb.AppendLine("    // Sample textures");
        sb.AppendLine("    float4 diffuse = DiffuseMapSampler.Sample(DiffuseMapSamplerS, uv);");
        sb.AppendLine();

        if (sig.UniqueParameters.Any(p => p.Name == "AlphaThreshold"))
        {
            sb.AppendLine("    if (diffuse.a < AlphaThreshold.x) discard;");
            sb.AppendLine();
        }

        sb.AppendLine("    // Ambient");
        sb.AppendLine("    float3 ambient = scene_GlobalAmbientColor.rgb * diffuse.rgb;");
        sb.AppendLine();

        sb.AppendLine("    // Directional light");
        sb.AppendLine("    float3 L = normalize(-scene_FakeRimLightDir.xyz);");
        sb.AppendLine("    float3 H = normalize(L + V);");
        sb.AppendLine("    float NdotL = saturate(dot(N, L));");
        sb.AppendLine("    float3 lightColor = scene_light1_colorIntensity.rgb;");
        sb.AppendLine();

        sb.AppendLine("    // Diffuse");
        sb.AppendLine("    float3 lighting = ambient;");
        sb.AppendLine("    lighting += lightColor * NdotL * diffuse.rgb;");
        sb.AppendLine();

        sb.AppendLine("    // Specular (if Shininess defined)");
        if (sig.UniqueParameters.Any(p => p.Name == "Shininess"))
        {
        sb.AppendLine("    float NdotH = saturate(dot(N, H));");
        sb.AppendLine("    lighting += lightColor * pow(NdotH, SpecularPower.x) * Shininess.x;");
        sb.AppendLine();
        }

        sb.AppendLine("    // Fog");
        if (sig.UniqueParameters.Any(p => p.Name == "FogRatio"))
        {
        sb.AppendLine("    float fogFactor = saturate((scene_FogRangeParameters.z - length(input.WorldPos - scene_EyePosition)) / (scene_FogRangeParameters.z - scene_FogRangeParameters.x));");
        sb.AppendLine("    lighting = lerp(scene_FogColor.rgb, lighting, fogFactor * FogRatio.x);");
        sb.AppendLine();
        }

        sb.AppendLine("    return float4(lighting, diffuse.a);");
    }

    /// <summary>
    /// Generates a complete combined HLSL source file with both VS and PS.
    /// </summary>
    public string GenerateComplete(PhyreShaderSignature sig, CompatibilityLevel level = CompatibilityLevel.Basic)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ============================================================");
        sb.AppendLine("// Cold Steel 1 Compatibility Shader");
        sb.AppendLine($"// Level: {level}");
        sb.AppendLine($"// Parameters: {sig.UniqueParameters.Length}");
        sb.AppendLine($"// Samplers: {sig.UniqueSamplers.Length}");
        sb.AppendLine($"// Context switches: {string.Join(", ", sig.ContextSwitches)}");
        sb.AppendLine("// ============================================================");
        sb.AppendLine();

        // Common declarations (CB, samplers) will be in each shader
        // but we need to be careful about double declarations
        sb.AppendLine("// === VERTEX SHADER ===");
        sb.AppendLine("#ifdef VERTEX_SHADER");
        sb.Append(GenerateVertexShader(sig));
        sb.AppendLine("#endif");
        sb.AppendLine();

        sb.AppendLine("// === FRAGMENT SHADER ===");
        sb.AppendLine("#ifdef FRAGMENT_SHADER");
        sb.Append(GenerateFragmentShader(sig, level));
        sb.AppendLine("#endif");

        return sb.ToString();
    }
}

public enum CompatibilityLevel
{
    /// <summary>Absolute minimum — diffuse texture or solid color</summary>
    Basic,
    /// <summary>Diffuse + alpha test + fog</summary>
    SimpleTextured,
    /// <summary>Full Phong lighting with diffuse, specular, fog</summary>
    FullLighting,
}
