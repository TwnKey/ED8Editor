using System.Numerics;

namespace ED8Editor.Core;

/// <param name="SourceIntParameters">
/// The whole-number constants the material supplies, kept apart from the floating
/// ones rather than squeezed into them. A shader's switch word is four bytes read
/// as a uint — reinterpreting it as a float would make it a value nobody can read
/// and something else might mistake for a colour.
/// </param>
public sealed record CpuMaterial(
    string Name,
    Vector4 BaseColor,
    int? BaseColorTextureIndex,
    IReadOnlyDictionary<string, float[]> SourceParameters,
    IReadOnlyDictionary<string, string> SourceTextureReferences,
    IReadOnlyDictionary<string, int> TextureBindings,
    string? RenderPassType = null,
    string? EffectAssetName = null,
    CpuRenderPassState? RenderPassState = null,
    IReadOnlyDictionary<string, string>? EffectSwitches = null,
    CpuMaterialRenderPhase RenderPhase = CpuMaterialRenderPhase.Opaque,
    string? ResolvedRenderPassName = null,
    CpuEffectProgram? EffectProgram = null,
    IReadOnlyDictionary<string, uint>? SourceIntParameters = null);
