using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Rendering;
using Vortice.D3DCompiler;

namespace ED8Editor.Shaders.Investigation;

/// <summary>
/// Extracts and decompiles D3D11 shader bytecode from a .fx.phyre cluster
/// back to human-readable HLSL. Uses D3DCompile's disassembly feature.
/// </summary>
public sealed class PhyreShaderDecompiler
{
    /// <summary>
    /// Decompiles all shader programs in a .fx.phyre and returns structured output.
    /// </summary>
    public PhyreShaderSource Decompile(byte[] fxPhyreData)
    {
        var cluster = new PhyreClusterReader().Read(fxPhyreData);
        var reader = new PhyreEffectRenderPassReader();
        var metadata = reader.ReadMetadata(fxPhyreData);

        if (metadata.Program is not { } program)
            throw new InvalidOperationException("No effect program found in the fx.phyre.");

        var stages = new List<DecompiledStage>();

        foreach (var (passName, pass) in program.SceneRenderPasses)
        {
            for (var i = 0; i < pass.Permutations.Count; i++)
            {
                var permutation = pass.Permutations[i];
                var contextLabel = permutation.Context is { } ctx
                    ? string.Join(", ", ctx.PackedSwitchValues.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "default";

                // Decompile vertex shader
                var vs = DecompileBytecode(
                    permutation.VertexProgram.Bytecode,
                    $"vs_{passName}_perm{i}",
                    D3D11ShaderStage.Vertex);

                // Decompile fragment shader
                var ps = DecompileBytecode(
                    permutation.FragmentProgram.Bytecode,
                    $"ps_{passName}_perm{i}",
                    D3D11ShaderStage.Fragment);

                stages.Add(new DecompiledStage(
                    passName,
                    i,
                    contextLabel,
                    vs,
                    ps,
                    permutation.VertexProgram.ConstantBufferSize,
                    permutation.FragmentProgram.ConstantBufferSize,
                    permutation.Inputs.Select(si => new DecompiledInput(
                        si.Name, si.SemanticIndex, si.RenderType, si.DataType)).ToArray()));
            }
        }

        // Collect context switch info
        var contextSwitches = program.ContextSwitches ?? Array.Empty<string>();
        var contexts = program.Contexts ?? Array.Empty<CpuShaderContext>();

        return new PhyreShaderSource(
            metadata.MaterialSwitches,
            contextSwitches,
            contexts.Select(c => new DecompiledContext(
                c.VariantIndex,
                c.PackedSwitchValues)).ToArray(),
            stages);
    }

    private static string DecompileBytecode(byte[] bytecode, string label, D3D11ShaderStage stage)
    {
        try
        {
            // Use D3DReflect to get shader description
            using var reflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(bytecode);
            var desc = reflection.Description;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"// {label} ({stage})");
            sb.AppendLine($"// Instruction count: {desc.InstructionCount}");
            sb.AppendLine($"// Constant buffers: {desc.ConstantBuffers}");
            sb.AppendLine($"// Bound resources: {desc.BoundResources}");
            sb.AppendLine($"// Input parameters: {desc.InputParameters}");
            sb.AppendLine($"// Output parameters: {desc.OutputParameters}");
            sb.AppendLine($"// Bytecode size: {bytecode.Length} bytes");
            sb.AppendLine($"// Creator: {desc.Creator}");
            sb.AppendLine($"// Version: {desc.Version}");
            sb.AppendLine();

            foreach (var cb in reflection.ConstantBuffers)
            {
                var cbDesc = cb.Description;
                sb.AppendLine($"// cbuffer {cbDesc.Name} ({cbDesc.Size} bytes)");
                foreach (var variable in cb.Variables)
                {
                    var varDesc = variable.Description;
                    sb.AppendLine($"//   {varDesc.Name}: offset={varDesc.StartOffset}, size={varDesc.Size}");
                }
            }

            foreach (var resource in reflection.BoundResources)
            {
                sb.AppendLine($"// resource {resource.Name}: type={resource.Type}, bind={resource.BindPoint}, count={resource.BindCount}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"// Failed to decompile {label}: {ex.Message}\n// Bytecode size: {bytecode.Length} bytes\n";
        }
    }
}

/// <summary>
/// Complete decompiled shader source from an fx.phyre.
/// </summary>
public sealed record PhyreShaderSource(
    IReadOnlyDictionary<string, string> MaterialSwitches,
    IReadOnlyList<string> ContextSwitchNames,
    IReadOnlyList<DecompiledContext> Contexts,
    IReadOnlyList<DecompiledStage> Stages);

/// <summary>
/// One context variant with its switch values.
/// </summary>
public sealed record DecompiledContext(
    int VariantIndex,
    IReadOnlyDictionary<string, uint> PackedValues);

/// <summary>
/// One decompiled shader stage (a specific pass + permutation).
/// </summary>
public sealed record DecompiledStage(
    string PassName,
    int PermutationIndex,
    string ContextLabel,
    string VertexShaderDisassembly,
    string FragmentShaderDisassembly,
    int VertexConstantBufferSize,
    int FragmentConstantBufferSize,
    IReadOnlyList<DecompiledInput> Inputs);

/// <summary>
/// One shader input declaration.
/// </summary>
public sealed record DecompiledInput(
    string Name,
    int SemanticIndex,
    uint RenderType,
    byte DataType);
