using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One stream of a packed mesh: what it holds, and its bytes.</summary>
public sealed record PhyrePackedStream(
    VertexSemantic Semantic,
    int SemanticIndex,
    string Format,
    int Stride,
    byte[] Data);

/// <summary>A mesh laid out the way the game lays its own out.</summary>
public sealed record PhyrePackedGeometry(
    IReadOnlyList<PhyrePackedStream> Streams,
    IReadOnlyList<byte[]> IndexBuffers,
    int VertexCount,
    bool SixteenBitIndices);

/// <summary>
/// Turns a mesh handed in from outside into the buffers a model cluster holds.
///
/// The layout is not invented: read off the 64 character models the game ships,
/// every attribute sits in a stream of its own whose stride is exactly its own
/// size — position, normal, tangent and bitangent as three floats, texture
/// coordinates as two, joint weights as four, joint indices as four bytes.
/// Nothing is interleaved and nothing is packed tightly, so writing geometry is
/// one flat array per attribute.
///
/// A second set of texture coordinates and bitangents appears on most meshes;
/// it is written only when the mesh being brought in has one.
/// </summary>
public static class PhyreModelGeometryPacker
{
    /// <summary>Above this many vertices the game's meshes index on 32 bits.</summary>
    private const int SixteenBitLimit = 0x10000;

    /// <summary>Packs every mesh of a model, in the order it states them.</summary>
    public static IReadOnlyList<PhyrePackedGeometry> Pack(PhyreModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var problems = source.Problems();
        if (problems.Count != 0)
        {
            throw new InvalidOperationException(
                "This model cannot be written: " + string.Join("; ", problems));
        }
        return source.Meshes.Select(mesh => Pack(mesh, source.IsSkinned)).ToArray();
    }

    /// <summary>Packs one mesh: a stream per attribute, and its indices.</summary>
    public static PhyrePackedGeometry Pack(PhyreMeshSource source, bool skinned)
    {
        ArgumentNullException.ThrowIfNull(source);
        var count = source.Vertices.Count;
        var sets = source.Vertices.Count == 0 ? 0 : source.Vertices[0].TexCoords.Count;
        foreach (var vertex in source.Vertices)
        {
            if (vertex.TexCoords.Count == sets) continue;
            throw new InvalidOperationException(
                "Every vertex of a mesh has to carry the same number of texture"
                + $" coordinate sets; one carries {vertex.TexCoords.Count} where the"
                + $" first carries {sets}.");
        }

        // Position and the normal once, then a texture coordinate, a tangent and
        // a bitangent for each set. A set's frame belongs to that set: the game
        // stores Tangent1 next to TextureCoordinate1, not one frame for all.
        var streams = new List<PhyrePackedStream>
        {
            Float3(VertexSemantic.Position, 0, source, vertex => vertex.Position),
            Float3(VertexSemantic.Normal, 0, source, vertex => vertex.Normal),
        };
        for (var set = 0; set < sets; set++)
        {
            var which = set;
            streams.Add(Float2(VertexSemantic.TextureCoordinate, which, source,
                vertex => vertex.TexCoords[which].TexCoord));
            streams.Add(Float3(VertexSemantic.Tangent, which, source,
                vertex => vertex.TexCoords[which].Tangent));
            // Written exactly as given, with no fallback. Standing in a cross
            // product for a bitangent left at zero was wrong twice over: it does
            // not reproduce the frame the game stores, and the game itself
            // writes zeros for a set a mesh does not use — so inventing a value
            // there fills an unused set with noise.
            streams.Add(Float3(VertexSemantic.Bitangent, which, source,
                vertex => vertex.TexCoords[which].Bitangent));
        }

        // A colour per vertex, white. The shader declares COLOR among its vertex
        // inputs — read off the compiled program's input signature, which lists
        // POSITION, NORMAL, TEXCOORD and COLOR — and every shipped map mesh carries a
        // sixteen-byte stream for it. A mesh that leaves it out cannot satisfy the
        // layout the shader was compiled against.
        //
        // White because an imported file rarely carries vertex colours and white is
        // the value that changes nothing: the shader multiplies by it.
        streams.Add(White(source));

        if (skinned)
        {
            streams.Add(JointIndices(source));
            streams.Add(JointWeights(source));
        }

        // Indices are per segment, and their width follows the vertex count of
        // the whole mesh, not of the segment.
        var sixteen = count < SixteenBitLimit;
        var buffers = new List<byte[]>();
        {
            var bytes = new byte[source.Indices.Length * (sixteen ? 2 : 4)];
            for (var index = 0; index < source.Indices.Length; index++)
            {
                var value = (uint)source.Indices[index];
                if (sixteen)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(index * 2), (ushort)value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), value);
                }
            }
            buffers.Add(bytes);
        }

        return new PhyrePackedGeometry(streams, buffers, count, sixteen);
    }

    private static PhyrePackedStream White(PhyreMeshSource source)
    {
        var bytes = new byte[source.Vertices.Count * 16];
        for (var index = 0; index < source.Vertices.Count; index++)
        {
            var at = bytes.AsSpan(index * 16);
            for (var channel = 0; channel < 4; channel++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(at[(channel * 4)..], 1f);
            }
        }
        return new PhyrePackedStream(VertexSemantic.Color, 0, "Float32x4", 16, bytes);
    }

    private static PhyrePackedStream Float3(
        VertexSemantic semantic,
        int semanticIndex,
        PhyreMeshSource source,
        Func<PhyreVertexSource, Vector3> read)
    {
        var bytes = new byte[source.Vertices.Count * 12];
        for (var index = 0; index < source.Vertices.Count; index++)
        {
            var value = read(source.Vertices[index]);
            var at = bytes.AsSpan(index * 12);
            BinaryPrimitives.WriteSingleLittleEndian(at, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[4..], value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at[8..], value.Z);
        }
        return new PhyrePackedStream(semantic, semanticIndex, "Float32x3", 12, bytes);
    }

    private static PhyrePackedStream Float2(
        VertexSemantic semantic,
        int semanticIndex,
        PhyreMeshSource source,
        Func<PhyreVertexSource, Vector2> read)
    {
        var bytes = new byte[source.Vertices.Count * 8];
        for (var index = 0; index < source.Vertices.Count; index++)
        {
            var value = read(source.Vertices[index]);
            var at = bytes.AsSpan(index * 8);
            BinaryPrimitives.WriteSingleLittleEndian(at, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[4..], value.Y);
        }
        return new PhyrePackedStream(semantic, semanticIndex, "Float32x2", 8, bytes);
    }

    private static PhyrePackedStream JointIndices(PhyreMeshSource source)
    {
        var bytes = new byte[source.Vertices.Count * 4];
        for (var index = 0; index < source.Vertices.Count; index++)
        {
            var joints = source.Vertices[index].Joints;
            for (var slot = 0; slot < 4; slot++)
            {
                var joint = slot < joints.Length ? joints[slot] : 0;
                if (joint is < 0 or > 255)
                {
                    throw new InvalidOperationException(
                        $"Vertex {index} follows joint {joint}, which does not fit in a byte."
                        + " A mesh has to be split before it can follow more than 256 joints.");
                }
                bytes[index * 4 + slot] = (byte)joint;
            }
        }
        return new PhyrePackedStream(VertexSemantic.JointIndices, 0, "UInt8x4", 4, bytes);
    }

    private static PhyrePackedStream JointWeights(PhyreMeshSource source)
    {
        var bytes = new byte[source.Vertices.Count * 16];
        for (var index = 0; index < source.Vertices.Count; index++)
        {
            var weights = source.Vertices[index].Weights;
            var at = bytes.AsSpan(index * 16);
            for (var slot = 0; slot < 4; slot++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    at[(slot * 4)..], slot < weights.Length ? weights[slot] : 0f);
            }
        }
        return new PhyrePackedStream(VertexSemantic.JointWeights, 0, "Float32x4", 16, bytes);
    }
}
