using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Reads a mesh already in the game's own cluster back into the neutral form
/// an importer fills in — the same direction <see cref="PhyreModelReplacement"/>
/// writes, run backwards.
///
/// This exists for a narrow reason: replacing one segment of a multi-segment
/// model — one material out of the sixteen a character's body is split into —
/// still has to hand <see cref="PhyreModelReplacement.Replace"/> a full mesh
/// list, because that writer places buffers by position and does not add or
/// remove any. The segments not being touched are read back with this and
/// passed straight through, unchanged in every value that matters; this is
/// also what a round trip through <see cref="PhyreModelGeometryPacker"/> is
/// checked against, so a mistake here is caught the same way one there is.
/// </summary>
public static class PhyreMeshSourceReader
{
    /// <summary>
    /// The mesh a primitive holds, in local — not hierarchy — joint indices:
    /// exactly what its own vertex stream stores, unless the streams the game
    /// always writes are missing, which no primitive this reads should be.
    /// </summary>
    public static PhyreMeshSource? ReadVerbatim(CpuMeshPrimitive primitive, string materialName)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        var found = new Dictionary<string, CpuVertexBuffer>(StringComparer.Ordinal);
        var count = 0;
        foreach (var buffer in primitive.VertexBuffers)
        {
            foreach (var attribute in buffer.Attributes)
            {
                found[Key(attribute.Semantic, attribute.SemanticIndex)] = buffer;
                count = Math.Max(count, buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride);
            }
        }
        if (!found.ContainsKey(Key(VertexSemantic.Position, 0))
            || !found.ContainsKey(Key(VertexSemantic.Normal, 0))
            || !found.ContainsKey(Key(VertexSemantic.Tangent, 0))
            || !found.ContainsKey(Key(VertexSemantic.TextureCoordinate, 0))
            || !found.ContainsKey(Key(VertexSemantic.Bitangent, 0)))
        {
            return null;
        }

        var skinned = found.ContainsKey(Key(VertexSemantic.JointIndices, 0))
            && found.ContainsKey(Key(VertexSemantic.JointWeights, 0));
        var vertices = new List<PhyreVertexSource>(count);
        for (var at = 0; at < count; at++)
        {
            var joints = new int[4];
            var weights = new float[4];
            if (skinned)
            {
                var indexBytes = found[Key(VertexSemantic.JointIndices, 0)].Data;
                var weightBytes = found[Key(VertexSemantic.JointWeights, 0)].Data;
                for (var slot = 0; slot < 4; slot++)
                {
                    joints[slot] = indexBytes[at * 4 + slot];
                    weights[slot] = BitConverter.ToSingle(weightBytes.AsSpan(at * 16 + slot * 4));
                }
            }

            var sets = new List<PhyreTexCoordSet>();
            for (var set = 0; set < 4; set++)
            {
                var suffix = set;
                if (!found.ContainsKey(Key(VertexSemantic.TextureCoordinate, suffix))) break;
                var span = found[Key(VertexSemantic.TextureCoordinate, suffix)].Data.AsSpan(at * 8);
                sets.Add(new PhyreTexCoordSet(
                    new Vector2(BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..])),
                    found.TryGetValue(Key(VertexSemantic.Tangent, suffix), out var tangent)
                        ? V3(tangent.Data, at) : default,
                    found.TryGetValue(Key(VertexSemantic.Bitangent, suffix), out var bitangent)
                        ? V3(bitangent.Data, at) : default));
            }

            vertices.Add(new PhyreVertexSource(
                V3(found[Key(VertexSemantic.Position, 0)].Data, at),
                V3(found[Key(VertexSemantic.Normal, 0)].Data, at),
                sets, joints, weights));
        }

        var stride = vertices.Count < 0x10000 ? 2 : 4;
        var indices = new int[primitive.Indices.Data.Length / stride];
        for (var at = 0; at < indices.Length; at++)
        {
            indices[at] = stride == 2
                ? BitConverter.ToUInt16(primitive.Indices.Data.AsSpan(at * 2))
                : (int)BitConverter.ToUInt32(primitive.Indices.Data.AsSpan(at * 4));
        }
        return new PhyreMeshSource(materialName, vertices, indices);
    }

    private static string Key(VertexSemantic semantic, int index) =>
        semantic + (index == 0 ? "" : index.ToString());

    private static Vector3 V3(byte[] data, int at)
    {
        var span = data.AsSpan(at * 12);
        return new Vector3(
            BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..]),
            BitConverter.ToSingle(span[8..]));
    }
}
