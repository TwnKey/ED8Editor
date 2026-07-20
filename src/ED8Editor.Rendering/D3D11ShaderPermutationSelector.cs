using ED8Editor.Core;

namespace ED8Editor.Rendering;

public sealed record D3D11ShaderContextPolicy(
    IReadOnlyDictionary<string, uint> PackedSwitchValues)
{
    public static D3D11ShaderContextPolicy ViewerWithoutDynamicLights { get; } = new(
        new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["NUM_LIGHTS"] = 0,
            ["INSTANCING_ENABLED"] = 0,
            ["SHADER_LOD_LEVEL"] = 0,
        });
}

public sealed record D3D11ShaderPermutationSelection(
    CpuShaderPermutation? Permutation,
    string? UnsupportedReason)
{
    public bool IsSupported => Permutation is not null;
}

/// <summary>
/// Selects an authored Phyre permutation from its declared context switches.
/// Unknown switches are rejected explicitly instead of being assigned guessed values.
/// </summary>
public sealed class D3D11ShaderPermutationSelector
{
    public D3D11ShaderPermutationSelection Select(
        CpuMaterial material,
        D3D11ShaderContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(policy);
        if (material.EffectProgram is not { } program)
            return Unsupported("The material has no loaded Phyre effect program.");
        if (string.IsNullOrEmpty(material.ResolvedRenderPassName))
            return Unsupported("The material has no resolved scene-render-pass name.");
        if (!program.SceneRenderPasses.TryGetValue(material.ResolvedRenderPassName, out var pass))
            return Unsupported($"Effect has no '{material.ResolvedRenderPassName}' program.");

        var unknownSwitches = (program.ContextSwitches ?? Array.Empty<string>())
            .Where(value => !policy.PackedSwitchValues.ContainsKey(value))
            .ToArray();
        if (unknownSwitches.Length != 0)
            return Unsupported($"No viewer value is registered for context switch(es): {string.Join(", ", unknownSwitches)}.");

        var matches = pass.Permutations.Where(permutation =>
            permutation.Context is { } context
            && context.PackedSwitchValues.All(pair =>
                policy.PackedSwitchValues.TryGetValue(pair.Key, out var requested)
                && requested == pair.Value)).ToArray();
        if (matches.Length == 1) return new D3D11ShaderPermutationSelection(matches[0], null);
        if (matches.Length == 0 && pass.Permutations.Count == 1 && pass.Permutations[0].Context is null)
            return new D3D11ShaderPermutationSelection(pass.Permutations[0], null);
        return matches.Length == 0
            ? Unsupported($"Effect pass '{pass.Name}' has no permutation for the requested viewer context.")
            : Unsupported($"Effect pass '{pass.Name}' has {matches.Length} ambiguous permutations for the requested viewer context.");
    }

    private static D3D11ShaderPermutationSelection Unsupported(string reason)
        => new(null, reason);
}
