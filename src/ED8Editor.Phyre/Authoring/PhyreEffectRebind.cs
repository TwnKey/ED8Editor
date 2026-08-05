using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>What changing one material's shader would do to the cluster.</summary>
/// <param name="Current">The shader the material binds now.</param>
/// <param name="SharedWith">
/// The other materials that bind the same one. A cluster names a shader once and
/// every material that uses it points at that one name, so changing the name changes
/// all of them — which is worth saying out loud rather than discovering afterwards.
/// </param>
/// <param name="Problems">
/// What stops the change, if anything does. Empty means it can be made.
/// </param>
public sealed record PhyreEffectRebindPlan(
    string Current,
    IReadOnlyList<string> SharedWith,
    IReadOnlyList<string> Problems);

/// <summary>
/// Points a material of a model the game ships at a different shader, without
/// rewriting the model.
///
/// A cluster is a graph of fixed-size regions addressed by offset, so almost
/// nothing in it can be changed without moving everything after it. Two things can:
/// a name of exactly the same length, and a value that fits where it already sits.
/// A shader is named <c>shaders/&lt;source&gt;.fx#&lt;32 hex&gt;</c> — every variant
/// of a source has a name the same length as every other — so swapping one for
/// another is a change of that kind, and only that kind.
///
/// The block the material hands the shader is not rewritten, so the new shader has
/// to want the same one: the same size, the same parameters, at the same places.
/// That is a real restriction and it is checked rather than hoped for — a material
/// filled for one shader and read by another supplies plausible numbers to the
/// wrong parameters, which draws something, which is the worst way for this to go
/// wrong. A shader that does not fit is refused here, and the model can be written
/// again from an import instead, which has no such restriction.
/// </summary>
public static class PhyreEffectRebind
{
    /// <summary>What the change would involve, and what would stop it.</summary>
    public static PhyreEffectRebindPlan Plan(
        ReadOnlyMemory<byte> cluster,
        string materialName,
        string newShaderAsset,
        ReadOnlyMemory<byte> newEffect)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(newShaderAsset);

        var materials = PhyreMaterialTableReader.ReadAll(cluster);
        if (!materials.TryGetValue(materialName, out var bound))
        {
            return new PhyreEffectRebindPlan(
                string.Empty,
                Array.Empty<string>(),
                new[] { $"The model declares no material called '{materialName}'." });
        }

        var shared = materials
            .Where(pair => pair.Key != materialName
                && string.Equals(pair.Value.ShaderAsset, bound.ShaderAsset, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var problems = new List<string>();
        if (string.Equals(bound.ShaderAsset, newShaderAsset, StringComparison.Ordinal))
        {
            problems.Add($"'{materialName}' already binds {newShaderAsset}.");
        }
        if (newShaderAsset.Length != bound.ShaderAsset.Length)
        {
            problems.Add(
                $"'{newShaderAsset}' is {newShaderAsset.Length} characters and"
                + $" '{bound.ShaderAsset}' is {bound.ShaderAsset.Length}. A name can only be"
                + " replaced by one of the same length in a model that is not rewritten;"
                + " import the model to bind a shader named differently.");
        }

        PhyreMaterialTable wanted;
        try
        {
            wanted = PhyreMaterialTableReader.FromEffect(
                newShaderAsset, newEffect, "shaders/placeholder.dds");
        }
        catch (InvalidDataException failure)
        {
            problems.Add(failure.Message);
            return new PhyreEffectRebindPlan(bound.ShaderAsset, shared, problems);
        }

        problems.AddRange(Differences(bound, wanted));
        return new PhyreEffectRebindPlan(bound.ShaderAsset, shared, problems);
    }

    /// <summary>
    /// The cluster with the material's shader changed. Refuses on anything
    /// <see cref="Plan"/> would have reported.
    /// </summary>
    public static byte[] Repoint(
        ReadOnlyMemory<byte> cluster,
        string materialName,
        string newShaderAsset,
        ReadOnlyMemory<byte> newEffect)
    {
        var plan = Plan(cluster, materialName, newShaderAsset, newEffect);
        if (plan.Problems.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", plan.Problems));
        }

        var written = cluster.ToArray();
        var at = ImportNameOffset(written, plan.Current);
        Encoding.ASCII.GetBytes(newShaderAsset).CopyTo(written.AsSpan(at));
        return written;
    }

    /// <summary>
    /// Where the imported shader's name is written, in the file itself. The name
    /// lives in the import group's array data; the group's own start is the object
    /// data offset plus every group before it, which is how the reader reaches it.
    /// </summary>
    private static int ImportNameOffset(ReadOnlyMemory<byte> cluster, string asset)
    {
        var data = new PhyreClusterReader().Read(cluster);
        var groups = data.Metadata.InstanceGroups;
        var group = groups.FirstOrDefault(value => value.ClassName == "PAssetReferenceImport")
            ?? throw new InvalidDataException("The model imports nothing, so it names no shader.");

        var identifier = data.Metadata.Classes
            .First(value => value.Name == group.ClassName).Members
            .First(value => value.Name == "m_id");

        for (var id = 0u; id < group.Count; id++)
        {
            var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index && value.SourceObjectId == id
                && (value.SourceOffsetOrMember == (uint)identifier.Index
                    || value.SourceOffset == identifier.ValueOffset));
            if (fixup is null) continue;
            var bytes = data.GetArrayData(
                group.Index, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
            var zero = bytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(zero >= 0 ? bytes[..zero] : bytes);
            if (!string.Equals(name, asset, StringComparison.Ordinal)) continue;

            var start = data.Metadata.Header.ObjectDataOffset;
            foreach (var before in groups)
            {
                if (before.Index == group.Index) break;
                start = checked(start + before.Size);
            }
            return checked((int)(start + group.ObjectsSize + fixup.Offset));
        }

        throw new InvalidDataException(
            $"The model does not import '{asset}', so there is no name to change.");
    }

    /// <summary>
    /// Every way the new shader's block differs from the one the material already
    /// carries. Compared by what a material actually supplies — where each
    /// parameter sits, how big it is, and what it is called — rather than by the
    /// bytes, which differ between two builds of the same interface.
    /// </summary>
    private static IEnumerable<string> Differences(
        PhyreMaterialTable bound,
        PhyreMaterialTable wanted)
    {
        if (bound.ParameterBufferSize != wanted.ParameterBufferSize)
        {
            yield return $"Its material block is {bound.ParameterBufferSize} bytes and the"
                + $" shader wants {wanted.ParameterBufferSize}.";
            yield break;
        }

        var have = PhyreMaterialValues.Parameters(bound);
        var want = PhyreMaterialValues.Parameters(wanted);
        if (have.Count != want.Count)
        {
            yield return $"Its material supplies {have.Count} parameter(s) and the shader"
                + $" declares {want.Count}.";
            yield break;
        }

        var byName = have.ToDictionary(value => value.Name, StringComparer.Ordinal);
        foreach (var one in want)
        {
            if (!byName.TryGetValue(one.Name, out var mine))
            {
                yield return $"The shader declares '{one.Name}', which its material does not supply.";
                continue;
            }
            if (mine.Offset != one.Offset || mine.Count != one.Count
                || !string.Equals(mine.TypeName, one.TypeName, StringComparison.Ordinal))
            {
                yield return $"'{one.Name}' sits at {mine.Offset} as {mine.Count}"
                    + $" {mine.TypeName} and the shader wants it at {one.Offset} as"
                    + $" {one.Count} {one.TypeName}.";
            }
        }
    }
}
