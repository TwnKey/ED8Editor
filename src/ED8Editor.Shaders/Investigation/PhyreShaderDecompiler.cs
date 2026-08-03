using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Rendering;
using Vortice.D3DCompiler;

namespace ED8Editor.Shaders.Investigation;

/// <summary>
/// Extracts and reflects on D3D11 shader bytecode from a .fx.phyre cluster.
/// Produces structured metadata: constant buffer layouts, resource bindings,
/// input/output signatures, and instruction counts for each permutation.
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
                var vs = ReflectBytecode(
                    permutation.VertexProgram.Bytecode,
                    $"vs_{passName}_perm{i}",
                    D3D11ShaderStage.Vertex);

                // Decompile fragment shader
                var ps = ReflectBytecode(
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

    /// <summary>
    /// Reflects on D3D11 bytecode to extract structured metadata
    /// (CB layout, resource bindings, I/O signatures, instruction count).
    /// </summary>
    public static string ReflectBytecode(byte[] bytecode, string label, D3D11ShaderStage stage)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"// === {label} ({stage}) ===");

            using var reflection = Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(bytecode);
            var desc = reflection.Description;
            sb.AppendLine($"// Instructions: {desc.InstructionCount}");
            sb.AppendLine($"// Constant buffers: {desc.ConstantBuffers}");
            sb.AppendLine($"// Bound resources: {desc.BoundResources}");
            sb.AppendLine($"// Input params: {desc.InputParameters}");
            sb.AppendLine($"// Output params: {desc.OutputParameters}");
            sb.AppendLine($"// Bytecode: {bytecode.Length} bytes");
            sb.AppendLine($"// Creator: {desc.Creator} v{desc.Version}");
            sb.AppendLine();

            // Constant buffer details
            foreach (var cb in reflection.ConstantBuffers)
            {
                var cbDesc = cb.Description;
                sb.AppendLine($"// cbuffer {cbDesc.Name} ({cbDesc.Size}b)");
                foreach (var variable in cb.Variables)
                {
                    var varDesc = variable.Description;
                    sb.AppendLine($"//   {varDesc.Name} @{varDesc.StartOffset} sz={varDesc.Size}");
                }
            }
            sb.AppendLine();

            // Bound resources
            foreach (var res in reflection.BoundResources)
            {
                sb.AppendLine($"// bind {res.Name}: {res.Type} @[{res.BindPoint},{res.BindCount}]");
            }
            sb.AppendLine();

            // Input signature
            foreach (var p in reflection.InputParameters)
            {
                sb.AppendLine($"// in  {p.SemanticName}{p.SemanticIndex}: {p.ComponentType} r={p.Register}");
            }
            sb.AppendLine();

            // Output signature
            foreach (var p in reflection.OutputParameters)
            {
                sb.AppendLine($"// out {p.SemanticName}{p.SemanticIndex}: {p.ComponentType} r={p.Register}");
            }
            sb.AppendLine();

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"// Failed: {label} — {ex.Message}\n";
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
