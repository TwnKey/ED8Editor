using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Viewer;

/// <summary>What one material of an asset draws with.</summary>
internal sealed record MaterialBinding(string Material, string ShaderAsset)
{
    public string ShaderName => Path.GetFileName(ShaderAsset);
}

/// <summary>What changing an asset's shaders did.</summary>
/// <param name="Refused">
/// The assignments that could not be made, each with its reason. A shader wanting a
/// different material block than the one the model carries is one of these.
/// </param>
/// <param name="AlsoChanged">
/// Materials that were not assigned anything but followed one that was, because a
/// cluster names a shader once and every material binding it points at that name.
/// </param>
internal sealed record MaterialShaderResult(
    string? PackagePath,
    IReadOnlyList<string> Refused,
    IReadOnlyList<string> AlsoChanged);

/// <summary>
/// Reading and changing what an asset's materials draw with, in one place.
///
/// Every window that shows a model shows the same thing here — a map's surfaces, a
/// prop, a piece of equipment, a character — so the reading and the writing live
/// once rather than four times. The window decides what to offer; this decides what
/// it means.
/// </summary>
internal static class MaterialShaderEditing
{
    /// <summary>The asset's model, resolved through the manifest as the game does.</summary>
    public static CharacterRetargetPackage.ResolvedModel? Resolve(
        string gameDataPath,
        string assetId)
    {
        try
        {
            return CharacterRetargetPackage.ResolveModel(
                new GameAssetResolverFactory(),
                new PkgArchiveReader(),
                new AssetManifestReader(),
                gameDataPath,
                assetId);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Each material of the model, and the shader it binds.</summary>
    public static IReadOnlyList<MaterialBinding> Bindings(ReadOnlyMemory<byte> cluster)
    {
        try
        {
            return PhyreMaterialTableReader.ReadAll(cluster)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MaterialBinding(pair.Key, pair.Value.ShaderAsset))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            return Array.Empty<MaterialBinding>();
        }
    }

    /// <summary>
    /// Puts the chosen shaders into the asset's own package, in one write.
    ///
    /// The model is not rewritten: a shader is changed by the name it is bound
    /// under, replaced in place, which leaves the geometry, the skinning and every
    /// other material exactly as they were. A shader whose interface does not fit
    /// the block the material carries is refused with its reason — the alternative
    /// is a material handing plausible numbers to the wrong parameters, which draws
    /// something and is therefore the hardest kind of mistake to see.
    /// </summary>
    public static MaterialShaderResult Apply(
        ModProject project,
        CharacterRetargetPackage.ResolvedModel resolved,
        IReadOnlyDictionary<string, ShaderAssignment> assignments,
        Action<string, bool>? onSaving = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(assignments);

        var cluster = resolved.Cluster;
        var refused = new List<string>();
        var alsoChanged = new List<string>();
        foreach (var (material, assignment) in assignments)
        {
            var plan = PhyreEffectRebind.Plan(
                cluster, material, assignment.ShaderAsset, assignment.Cluster);
            if (plan.Problems.Count != 0)
            {
                refused.Add($"{material} → {Path.GetFileName(assignment.ShaderAsset)}: "
                    + string.Join(" ", plan.Problems));
                continue;
            }
            cluster = PhyreEffectRebind.Repoint(
                cluster, material, assignment.ShaderAsset, assignment.Cluster);
            alsoChanged.AddRange(plan.SharedWith);
        }

        if (refused.Count == assignments.Count)
        {
            return new MaterialShaderResult(null, refused, Array.Empty<string>());
        }

        var written = AuthoredShaderPackage.With(
            resolved.PackagePath,
            assignments.Values
                .Select(value => (value.EntryName, value.Cluster))
                .DistinctBy(value => value.EntryName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            replaceModel: name => name.Equals(
                resolved.EntryName, StringComparison.OrdinalIgnoreCase)
                    ? cluster
                    : throw new InvalidOperationException(
                        $"{resolved.PackagePath} holds a second model, {name}."));

        var archive = new PkgArchiveReader().Read(resolved.PackagePath);
        if (onSaving is null) project.CaptureOriginal(resolved.PackagePath);
        else onSaving(resolved.PackagePath, true);
        new PkgArchiveWriter().Write(resolved.PackagePath, archive.Magic, written.ToArray());
        if (onSaving is null) project.TrackSave(resolved.PackagePath);
        else onSaving(resolved.PackagePath, false);

        return new MaterialShaderResult(
            resolved.PackagePath,
            refused,
            alsoChanged.Distinct(StringComparer.Ordinal).ToArray());
    }
}
