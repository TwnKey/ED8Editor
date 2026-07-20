using ED8Editor.Core;
using ED8Editor.Phyre;

namespace ED8Editor.Application;

public sealed class PhyreMaterialRenderPassResolver
{
    private const string OpaquePass = "Opaque";
    private const string ForceTransparentPass = "ForceTransparent";
    private const string DefaultPass = "Default";
    private const string GlareHighPassSwitch = "GLARE_HIGHTPASS_ENABLED";

    public CpuMaterial Resolve(CpuMaterial material, PhyreEffectMetadata effect)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(effect);

        var isGlareHighPass = effect.MaterialSwitches.ContainsKey(GlareHighPassSwitch);
        var passName = material.RenderPassType switch
        {
            null => isGlareHighPass ? ForceTransparentPass : OpaquePass,
            var value when value.Equals(DefaultPass, StringComparison.Ordinal)
                => effect.DefaultRenderPassName ?? (isGlareHighPass ? ForceTransparentPass : OpaquePass),
            var value => value,
        };
        effect.RenderPassStates.TryGetValue(passName, out var state);

        var phase = isGlareHighPass && state?.BlendEnabled == true
            ? CpuMaterialRenderPhase.EffectTransparent
            : state?.BlendEnabled == true
                ? CpuMaterialRenderPhase.Transparent
                : CpuMaterialRenderPhase.Opaque;

        return material with
        {
            RenderPassState = state,
            EffectSwitches = effect.MaterialSwitches,
            RenderPhase = phase,
            ResolvedRenderPassName = passName,
            EffectProgram = effect.Program,
        };
    }
}
