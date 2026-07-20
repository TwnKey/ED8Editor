namespace ED8Editor.Core;

public sealed record CpuShaderStageProgram(
    byte[] Bytecode,
    int ConstantBufferSize,
    uint GlobalConstantBufferIndex,
    IReadOnlyList<CpuShaderInputLayoutElement>? InputLayout = null);

public sealed record CpuShaderInputLayoutElement(
    string Semantic,
    int SemanticIndex,
    uint RenderType,
    uint D3DFormat,
    int InputSlot);

public sealed record CpuShaderInput(
    string Name,
    int SemanticIndex,
    uint RenderType,
    byte DataType);

public sealed record CpuShaderContext(
    int VariantIndex,
    IReadOnlyDictionary<string, uint> PackedSwitchValues);

public sealed record CpuShaderPermutation(
    CpuShaderStageProgram VertexProgram,
    CpuShaderStageProgram FragmentProgram,
    IReadOnlyList<CpuShaderInput> Inputs,
    CpuShaderContext? Context = null);

public sealed record CpuSceneRenderPassProgram(
    string Name,
    IReadOnlyList<CpuShaderPermutation> Permutations);

public sealed record CpuEffectProgram(
    IReadOnlyDictionary<string, CpuSceneRenderPassProgram> SceneRenderPasses,
    IReadOnlyList<string>? ContextSwitches = null,
    IReadOnlyList<CpuShaderContext>? Contexts = null);
