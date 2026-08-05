using ED8Editor.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace ED8Editor.Rendering;

public enum D3D11ShaderStage
{
    Vertex,
    Fragment,
}

public sealed record D3D11ShaderSignatureParameter(
    string SemanticName,
    int SemanticIndex,
    int Register,
    RegisterComponentType ComponentType,
    RegisterComponentMaskFlags UsageMask);

/// <param name="Used">
/// Whether the stage actually reads it. A vertex and a pixel program can share one
/// constant buffer, so its variable list is not the list of what either of them
/// looks at.
/// </param>
public sealed record D3D11ShaderVariable(
    string Name,
    int Offset,
    int Size,
    bool Used = true);

public sealed record D3D11ShaderConstantBuffer(
    string Name,
    int BindPoint,
    int Size,
    IReadOnlyList<D3D11ShaderVariable> Variables);

public sealed record D3D11ShaderResource(
    string Name,
    ShaderInputType Type,
    int BindPoint,
    int BindCount,
    ShaderResourceViewDimension Dimension);

public sealed record D3D11ShaderProgramDescription(
    D3D11ShaderStage Stage,
    IReadOnlyList<D3D11ShaderSignatureParameter> Inputs,
    IReadOnlyList<D3D11ShaderConstantBuffer> ConstantBuffers,
    IReadOnlyList<D3D11ShaderResource> Resources);

/// <summary>
/// Reads the authoritative D3D11 signatures and binding registers from the compiled
/// programs embedded by Phyre. No binding is inferred from asset or material names.
/// </summary>
public sealed class D3D11ShaderProgramInspector
{
    public D3D11ShaderProgramDescription Inspect(
        CpuShaderStageProgram program,
        D3D11ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Bytecode.Length == 0) throw new ArgumentException("Shader bytecode is empty.", nameof(program));

        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(program.Bytecode);
        var resources = reflection.BoundResources
            .Select(value => new D3D11ShaderResource(
                value.Name,
                value.Type,
                value.BindPoint,
                value.BindCount,
                value.Dimension))
            .ToArray();
        var constantBufferBindPoints = resources
            .Where(value => value.Type == ShaderInputType.ConstantBuffer)
            .ToDictionary(value => value.Name, value => value.BindPoint, StringComparer.Ordinal);
        var constantBuffers = reflection.ConstantBuffers
            .Select(buffer =>
            {
                var description = buffer.Description;
                if (!constantBufferBindPoints.TryGetValue(description.Name, out var bindPoint))
                {
                    throw new InvalidDataException(
                        $"Compiled shader constant buffer '{description.Name}' has no resource binding.");
                }
                return new D3D11ShaderConstantBuffer(
                    description.Name,
                    bindPoint,
                    description.Size,
                    buffer.Variables.Select(variable =>
                    {
                        var variableDescription = variable.Description;
                        return new D3D11ShaderVariable(
                            variableDescription.Name,
                            variableDescription.StartOffset,
                            variableDescription.Size,
                            variableDescription.Flags.HasFlag(ShaderVariableFlags.Used));
                    }).ToArray());
            })
            .ToArray();
        var inputs = reflection.InputParameters
            .Select(value => new D3D11ShaderSignatureParameter(
                value.SemanticName,
                value.SemanticIndex,
                value.Register,
                value.ComponentType,
                value.UsageMask))
            .ToArray();
        return new D3D11ShaderProgramDescription(stage, inputs, constantBuffers, resources);
    }
}
