using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Puts a mesh brought in from outside into a model the game ships, keeping
/// everything that is not geometry — its materials, and so the shaders the
/// engine already compiled for it.
///
/// The two halves this joins are each checked against the game on their own: the
/// packer lays out streams the way the game lays out its own (6 016 of 6 016
/// streams reproduced), and the payload writer places buffers and corrects the
/// fields that describe them (7 of 7 models rebuilt). What is new is the join,
/// and it is held to the same rule: handing a model its own mesh back has to
/// give that model back, byte for byte.
/// </summary>
public static class PhyreModelReplacement
{
    /// <summary>
    /// Which semantic each stream of the packed mesh carries, in the order the
    /// cluster's own vertex buffers are laid out.
    /// </summary>
    public static IReadOnlyList<VertexSemantic> Order(PhyrePackedGeometry packed)
    {
        ArgumentNullException.ThrowIfNull(packed);
        return packed.Streams.Select(stream => stream.Semantic).ToArray();
    }

    private static IReadOnlyList<PhyrePackedGeometry> PackedOf(PhyreModelSource source)
        => PhyreModelGeometryPacker.Pack(source);

    /// <summary>
    /// Writes <paramref name="source"/> into <paramref name="cluster"/>.
    ///
    /// A model's payload is a run of index buffers and a run of vertex buffers,
    /// and every one of them is named by a pair of numbers held by the object
    /// that uses it. So this hands each buffer its new bytes and lets the
    /// payload writer place them and correct the pairs.
    /// </summary>
    public static byte[] Replace(ReadOnlyMemory<byte> cluster, PhyreModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var packed = PhyreModelGeometryPacker.Pack(source);

        // The buffers keep the order the cluster gives them: the indices of each
        // segment, then the vertex streams. A mesh that does not have as many of
        // either as the model it goes into cannot be placed, and saying so is
        // better than filling the difference with silence.
        // Which mesh each buffer belongs to, and what it carries. The cluster
        // says so itself — every vertex buffer declares its semantic — so the
        // streams are matched by what they are, never by the order they happen
        // to come in. A model whose buffers are laid out differently would
        // otherwise be handed normals where it expects texture coordinates, and
        // nothing would complain.
        var wanted = new List<(int Mesh, VertexSemantic Semantic, int Index)>();
        var model = new PhyreD3D11ModelReader().Read("target", cluster);
        var mesh = 0;
        foreach (var group in model.Meshes)
        {
            foreach (var primitive in group.Primitives)
            {
                foreach (var buffer in primitive.VertexBuffers)
                {
                    foreach (var attribute in buffer.Attributes)
                    {
                        wanted.Add((mesh, attribute.Semantic, attribute.SemanticIndex));
                    }
                }
                mesh++;
            }
        }

        var indices = 0;
        var vertices = 0;
        return PhyreModelGeometryWriter.Rewrite(cluster, (range, original) =>
        {
            if (range.Kind == "indices")
            {
                var at = indices++;
                return at < packed.Count ? packed[at].IndexBuffers[0] : original;
            }

            var slot = vertices++;
            if (slot >= wanted.Count) return original;
            var (which, semantic, index) = wanted[slot];
            if (which >= packed.Count) return original;
            foreach (var stream in packed[which].Streams)
            {
                if (stream.Semantic == semantic && stream.SemanticIndex == index)
                {
                    return stream.Data;
                }
            }
            // The mesh has nothing for this buffer. Leaving what was there is
            // the only honest answer: writing zeros would be a silent change to
            // a model that still uses it.
            return original;
        });
    }

    /// <summary>
    /// What would stop this mesh from going into this model. An importer should
    /// ask before it writes, so a mismatch is a sentence rather than a model
    /// that loads wrong.
    /// </summary>
    public static IReadOnlyList<string> Problems(
        PhyreClusterData cluster, PhyreModelSource source)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>(source.Problems());
        var ranges = PhyreModelGeometry.Ranges(cluster);
        var indexBuffers = ranges.Count(range => range.Kind == "indices");
        var vertexBuffers = ranges.Count(range => range.Kind == "vertices");

        if (source.Meshes.Count != indexBuffers)
        {
            problems.Add(
                $"the model has {source.Meshes.Count} meshes and the one it goes into"
                + $" has {indexBuffers}");
        }
        var wanted = source.Meshes.Count * (source.IsSkinned ? 7 : 5);
        if (wanted != vertexBuffers)
        {
            problems.Add(
                $"the model needs {wanted} vertex streams and the one it goes into"
                + $" holds {vertexBuffers}");
        }
        return problems;
    }
}
