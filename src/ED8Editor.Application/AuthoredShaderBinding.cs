using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>
/// What one material of an authored model draws with.
/// </summary>
/// <param name="ShaderAsset">
/// The id the material names it by — <c>shaders/ed8.fx#HASH</c>. A material states
/// an asset; a package holds an entry; the two are the same shader written the two
/// ways the format writes it.
/// </param>
/// <param name="Cluster">
/// The compiled effect itself, which travels with the assignment. A package whose
/// material names a shader the package does not carry is a material bound to
/// nothing — the shader may resolve from a neighbouring package today and not
/// tomorrow — so the choice and the file it needs are one thing here.
/// </param>
/// <param name="Custom">
/// Whether the author wrote it. The game's own variants are shipped as they are;
/// a custom one was compiled from HLSL a moment ago.
/// </param>
public sealed record ShaderAssignment(
    string Material,
    string ShaderAsset,
    string EntryName,
    byte[] Cluster,
    IReadOnlyDictionary<string, string> Values,
    bool Custom)
{
    /// <summary>The shader as it reads in a list: the hash, or the author's name for it.</summary>
    public string Label => Custom
        ? Path.GetFileName(ShaderAsset) + "  (le vôtre)"
        : Path.GetFileName(ShaderAsset);
}

/// <summary>
/// Turns what the author assigned, material by material, into what a model writer
/// takes: one parameter block per material slot, and the effects to ship beside it.
///
/// Every editor that writes a model needs this and none of them needs its own copy —
/// a map's surfaces, a piece of equipment, a character and an enemy are the same
/// problem, which is that a material has to hand its shader the constant block that
/// shader declares. The block is built from the effect's own declarations, so a
/// shader nothing has ever seen is bound as correctly as one the game ships.
/// </summary>
public static class AuthoredShaderBinding
{
    /// <summary>An assignment for a shader named the way the catalogue names it.</summary>
    public static ShaderAssignment For(
        string material,
        string assetName,
        byte[] cluster,
        IReadOnlyDictionary<string, string> values,
        bool custom)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return new ShaderAssignment(
            material,
            "shaders/" + assetName,
            assetName + ".phyre",
            cluster,
            values,
            custom);
    }

    /// <summary>
    /// The binding a model writer takes, and every effect the written package has
    /// to carry for it to mean anything.
    /// </summary>
    /// <param name="materialNames">
    /// The model's materials, in the order the writer will number them. The blocks
    /// come back in that same order, which is how a slot finds its own.
    /// </param>
    /// <param name="fallback">
    /// What a material with no assignment keeps. Null leaves it to the writer's own
    /// default, which is a block naming no shader of ours.
    /// </param>
    /// <param name="textureAsset">
    /// What a texture parameter points at when the author named none. The block has
    /// to name something: an effect declaring a texture and a material supplying no
    /// image is a draw with an unbound slot.
    /// </param>
    public static (PhyreShaderBinding Binding, IReadOnlyList<(string Name, byte[] Data)> Shaders)
        Build(
            IReadOnlyList<string> materialNames,
            IReadOnlyDictionary<string, ShaderAssignment> assignments,
            PhyreMaterialTable? fallback,
            string textureAsset)
    {
        ArgumentNullException.ThrowIfNull(materialNames);
        ArgumentNullException.ThrowIfNull(assignments);

        var blocks = new List<PhyreMaterialTable?>(materialNames.Count);
        var shaders = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string? first = null;

        foreach (var material in materialNames)
        {
            if (!assignments.TryGetValue(material, out var assignment))
            {
                blocks.Add(fallback);
                continue;
            }

            var block = PhyreMaterialValues.WithValues(
                PhyreMaterialTableReader.FromEffect(
                    assignment.ShaderAsset, assignment.Cluster, textureAsset),
                assignment.Values);
            blocks.Add(block);
            shaders[assignment.EntryName] = assignment.Cluster;
            first ??= assignment.ShaderAsset;
        }

        // The model's own shader is the one it is bound to when nothing else says
        // otherwise. An assignment answers for it when there is one, so a package
        // written entirely from the author's shaders names one of theirs.
        var bound = first ?? fallback?.ShaderAsset
            ?? throw new InvalidOperationException(
                "No material was assigned a shader and none was kept, so the model"
                + " would name no shader at all.");

        return (
            new PhyreShaderBinding(
                bound,
                fallback ?? blocks.FirstOrDefault(value => value is not null),
                blocks),
            shaders
                .Select(pair => (pair.Key, pair.Value))
                .ToArray());
    }
}
