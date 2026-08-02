using ED8Editor.Phyre.Authoring;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ED8Editor.Shaders.Compilation;

/// <summary>
/// Compiles custom HLSL source into a Phyre .fx.phyre cluster.
///
/// This is the core of the custom shader pipeline: it takes user-authored HLSL
/// and produces a valid fx.phyre that the game engine can load.
///
/// The approach mirrors PhyreMinimalEffectWriter but generalizes to arbitrary
/// HLSL with support for context switches and multiple passes.
/// </summary>
public sealed class PhyreCustomShaderCompiler
{
    // Standard Phyre effect class layout
    private static readonly string[] ClassLayout =
    {
        "PAssetReference", "PEffect", "PEffectVariant", "PSceneRenderPass",
        "PShader", "PShaderPass", "PShaderParameterDefinition",
        "PShaderStreamDefinition", "PShaderVertexProgram",
        "PShaderFragmentProgram", "PStreamInputDescD3D11",
    };

    /// <summary>
    /// Compiles a simple single-permutation shader from HLSL source.
    /// Use this for shaders that don't need context switch permutations.
    /// </summary>
    public byte[] CompileSimple(CustomShaderSpec spec)
    {
        if (spec.Passes.Count == 0)
            throw new ArgumentException("At least one render pass is required.", nameof(spec));

        // Compile vertex and fragment shaders for each pass
        var compiledPasses = new List<CompiledPass>();
        foreach (var pass in spec.Passes)
        {
            var vertexBytecode = CompileHlsl(
                spec.HlslSource,
                pass.VertexEntryPoint,
                "vs_5_0",
                spec.IncludeDirs);

            var fragmentBytecode = CompileHlsl(
                spec.HlslSource,
                pass.FragmentEntryPoint,
                "ps_5_0",
                spec.IncludeDirs);

            // Reflect on the compiled bytecode to extract metadata
            var vsReflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(vertexBytecode);
            var psReflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(fragmentBytecode);

            // Get the constant buffer description
            var vsCbDesc = vsReflection.GetConstantBufferByIndex(0).Description;
            var psCbDesc = psReflection.GetConstantBufferByIndex(0).Description;

            compiledPasses.Add(new CompiledPass(
                pass.Name,
                vertexBytecode,
                fragmentBytecode,
                vsCbDesc.Size,
                psCbDesc.Size,
                pass.InputElements,
                pass.ParameterDefinitions));
        }

        return WriteEffectCluster(spec, compiledPasses);
    }

    /// <summary>
    /// Compiles a multi-permutation shader where context switches
    /// are defined as preprocessor macros.
    /// </summary>
    public byte[] CompileWithSwitches(CustomShaderSpec spec)
    {
        if (spec.ContextSwitches.Count == 0)
            return CompileSimple(spec);

        // For each combination of context switch values, compile a variant
        var allVariants = GenerateSwitchCombinations(spec.ContextSwitches);

        var compiledPasses = new List<CompiledPass>();
        foreach (var pass in spec.Passes)
        {
            var variants = new List<CompiledVariant>();

            foreach (var variantDefines in allVariants)
            {
                var defines = variantDefines
                    .Select(kv => new ShaderMacro(kv.Key, kv.Value.ToString()))
                    .ToArray();

                var vertexBytecode = CompileHlsl(
                    spec.HlslSource,
                    pass.VertexEntryPoint,
                    "vs_5_0",
                    spec.IncludeDirs,
                    defines);

                var fragmentBytecode = CompileHlsl(
                    spec.HlslSource,
                    pass.FragmentEntryPoint,
                    "ps_5_0",
                    spec.IncludeDirs,
                    defines);

                var vsReflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(vertexBytecode);
                var psReflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(fragmentBytecode);
                var vsCbDesc = vsReflection.GetConstantBufferByIndex(0).Description;
                var psCbDesc = psReflection.GetConstantBufferByIndex(0).Description;

                variants.Add(new CompiledVariant(
                    variantDefines,
                    vertexBytecode,
                    fragmentBytecode,
                    vsCbDesc.Size,
                    psCbDesc.Size));
            }

            compiledPasses.Add(new CompiledPass(
                pass.Name,
                variants,
                pass.InputElements,
                pass.ParameterDefinitions));
        }

        return WriteEffectCluster(spec, compiledPasses);
    }

    private static byte[] CompileHlsl(
        string hlslSource,
        string entryPoint,
        string profile,
        string[]? includeDirs = null,
        ShaderMacro[]? defines = null)
    {
        var result = Compiler.Compile(
            shaderSource: hlslSource,
            defines: defines ?? Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: $"{entryPoint}.hlsl",
            profile: profile,
            shaderFlags: ShaderFlags.OptimizationLevel3,
            effectFlags: EffectFlags.None,
            out var blob,
            out var errors);
        using (blob)
        using (errors)
        {
            result.CheckError();
            return blob.AsBytes();
        }
    }

    private static IReadOnlyList<Dictionary<string, uint>> GenerateSwitchCombinations(
        IReadOnlyList<ContextSwitchDefinition> switches)
    {
        var result = new List<Dictionary<string, uint>>();
        GenerateRecursive(switches, 0, new Dictionary<string, uint>(), result);
        return result;
    }

    private static void GenerateRecursive(
        IReadOnlyList<ContextSwitchDefinition> switches,
        int index,
        Dictionary<string, uint> current,
        List<Dictionary<string, uint>> result)
    {
        if (index >= switches.Count)
        {
            result.Add(new Dictionary<string, uint>(current));
            return;
        }

        var sw = switches[index];
        foreach (var value in sw.PossibleValues)
        {
            current[sw.Name] = value;
            GenerateRecursive(switches, index + 1, current, result);
            current.Remove(sw.Name);
        }
    }

    private byte[] WriteEffectCluster(CustomShaderSpec spec, IReadOnlyList<CompiledPass> passes)
    {
        // For now, delegate to the minimal effect writer as a base,
        // then extend for multi-pass / multi-permutation support.
        // This is a placeholder for the full implementation.

        // The full implementation needs to:
        // 1. Build the class schema from PhyreSchemaLibrary
        // 2. Create all Phyre objects (PEffect, PShader, PShaderPass, etc.)
        // 3. Wire up all pointer fixups
        // 4. Pack the namespace, groups, objects, and fixups
        // 5. Write the complete cluster

        // This follows the same pattern as PhyreMinimalEffectWriter.Write()
        // but generalized for multiple passes and permutations.

        throw new NotImplementedException(
            "Full multi-pass/multi-permutation cluster writing is not yet implemented. " +
            "Use PhyreMinimalEffectWriter.Write() for single-pass shaders as a starting point.");
    }
}

/// <summary>
/// Specification for a custom shader to compile.
/// </summary>
public sealed record CustomShaderSpec(
    string HlslSource,
    string ShaderName,
    string AssetId,
    IReadOnlyList<CustomShaderPass> Passes,
    IReadOnlyList<ContextSwitchDefinition> ContextSwitches,
    IReadOnlyDictionary<string, string>? MaterialSwitches = null,
    string[]? IncludeDirs = null);

/// <summary>
/// One render pass in a custom shader.
/// </summary>
public sealed record CustomShaderPass(
    string Name,
    string VertexEntryPoint,
    string FragmentEntryPoint,
    IReadOnlyList<CustomInputElement> InputElements,
    IReadOnlyList<CustomParameterDefinition> ParameterDefinitions);

/// <summary>
/// A context switch that controls permutation selection.
/// </summary>
public sealed record ContextSwitchDefinition(
    string Name,
    uint[] PossibleValues);

/// <summary>
/// A vertex input element declaration.
/// </summary>
public sealed record CustomInputElement(
    string Semantic,
    int SemanticIndex,
    uint RenderType,  // e.g., "Vertex", "Normal", "TexCoord"
    uint D3DFormat,
    int InputSlot);

/// <summary>
/// A shader parameter definition that will appear in the PParameterBuffer.
/// </summary>
public sealed record CustomParameterDefinition(
    string Name,
    uint Type,      // e.g., float4x4, float4, float, int
    uint Offset,    // byte offset in the constant buffer
    uint Size,      // size in bytes
    uint ArraySize = 1);

// Internal types

internal sealed class CompiledPass
{
    public string Name { get; }
    public byte[] VertexBytecode { get; }
    public byte[] FragmentBytecode { get; }
    public int VsCbSize { get; }
    public int PsCbSize { get; }
    public IReadOnlyList<CustomInputElement> InputElements { get; }
    public IReadOnlyList<CustomParameterDefinition> ParameterDefinitions { get; }
    public IReadOnlyList<CompiledVariant>? Variants { get; }

    public CompiledPass(
        string name,
        byte[] vertexBytecode,
        byte[] fragmentBytecode,
        int vsCbSize,
        int psCbSize,
        IReadOnlyList<CustomInputElement> inputElements,
        IReadOnlyList<CustomParameterDefinition> parameterDefinitions)
    {
        Name = name;
        VertexBytecode = vertexBytecode;
        FragmentBytecode = fragmentBytecode;
        VsCbSize = vsCbSize;
        PsCbSize = psCbSize;
        InputElements = inputElements;
        ParameterDefinitions = parameterDefinitions;
    }

    public CompiledPass(
        string name,
        IReadOnlyList<CompiledVariant> variants,
        IReadOnlyList<CustomInputElement> inputElements,
        IReadOnlyList<CustomParameterDefinition> parameterDefinitions)
    {
        Name = name;
        Variants = variants;
        InputElements = inputElements;
        ParameterDefinitions = parameterDefinitions;
        VertexBytecode = variants[0].VertexBytecode;
        FragmentBytecode = variants[0].FragmentBytecode;
        VsCbSize = variants[0].VsCbSize;
        PsCbSize = variants[0].PsCbSize;
    }
}

internal sealed record CompiledVariant(
    IReadOnlyDictionary<string, uint> SwitchValues,
    byte[] VertexBytecode,
    byte[] FragmentBytecode,
    int VsCbSize,
    int PsCbSize);
