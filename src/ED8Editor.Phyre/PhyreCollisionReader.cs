using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Phyre;

/// <summary>One collision shape: the triangles the game stops the player with.</summary>
public sealed record PhyreCollisionShape(
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<int> Indices);

/// <summary>
/// Reads the collision out of a model cluster.
///
/// A cluster's collision is not among the meshes it draws — it lives in PShape, as
/// raw positions and indices — so nothing that walks the render meshes ever sees
/// it. That makes a collision impossible to judge by looking at the model, which
/// is exactly when one needs to.
/// </summary>
public static class PhyreCollisionReader
{
    /// <summary>Sixteen-bit indices; every shape the game ships uses this.</summary>
    private const uint NarrowIndices = 12;

    public static IReadOnlyList<PhyreCollisionShape> Read(ReadOnlyMemory<byte> cluster)
    {
        var data = new PhyreClusterReader().Read(cluster);
        var group = data.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PShape" && value.Count != 0);
        if (group is null) return Array.Empty<PhyreCollisionShape>();

        var descriptor = data.Metadata.Classes
            .FirstOrDefault(value => value.Name == "PShape");
        if (descriptor is null) return Array.Empty<PhyreCollisionShape>();
        var members = PhyreObjectWriter.Chain(descriptor, data.Metadata.Classes).ToList();
        uint Field(ReadOnlySpan<byte> shape, string name, int width)
        {
            var member = members.FirstOrDefault(value => value.Name == name);
            if (member is null || member.ValueOffset + width > shape.Length) return 0;
            return width == 1
                ? shape[(int)member.ValueOffset]
                : BitConverter.ToUInt32(shape[(int)member.ValueOffset..]);
        }

        var fixups = new PhyreFixupReader().Read(cluster, data.Metadata);
        uint Offset(int id, string name)
        {
            var member = members.FirstOrDefault(value => value.Name == name);
            if (member is null) return 0;
            var found = fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index
                && value.SourceObjectId == (uint)id
                && (value.SourceOffsetOrMember == member.Index
                    || value.SourceOffsetOrMember == member.ValueOffset
                    || value.SourceOffsetOrMember == (0x80000000u | member.ValueOffset)
                    || value.SourceOffsetOrMember == (0x80000000u | (member.ValueOffset + 4))));
            return found?.Offset ?? 0;
        }

        var objects = data.GetGroupObjectsData(group.Index).Span;
        var each = (int)(group.ObjectsSize / group.Count);
        var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize).Span;
        var shapes = new List<PhyreCollisionShape>();
        for (var id = 0; id < group.Count; id++)
        {
            var shape = objects.Slice(id * each, each);
            var vertexCount = (int)Field(shape, "m_vertexCount", 4);
            var indexCount = (int)Field(shape, "m_indexCount", 4);
            var format = Field(shape, "m_indexFormat", 1);
            // Where each run starts is NOT in the object: the member holds its size
            // and then a pointer left at zero, and an array fixup says where the
            // pointer lands. Reading the size as an offset gives a shape whose
            // triangles point at nothing, which is how this was wrong at first.
            var vertexAt = (int)Offset(id, "m_vertexData");
            var indexAt = (int)Offset(id, "m_indices");
            var narrow = format == NarrowIndices;
            var indexWidth = narrow ? 2 : 4;
            if (vertexCount <= 0 || indexCount <= 0) continue;
            if (vertexAt + vertexCount * 12 > arrays.Length) continue;
            if (indexAt + indexCount * indexWidth > arrays.Length) continue;

            var vertices = new Vector3[vertexCount];
            for (var at = 0; at < vertexCount; at++)
            {
                var point = arrays.Slice(vertexAt + at * 12, 12);
                vertices[at] = new Vector3(
                    BitConverter.ToSingle(point),
                    BitConverter.ToSingle(point[4..]),
                    BitConverter.ToSingle(point[8..]));
            }
            var indices = new int[indexCount];
            for (var at = 0; at < indexCount; at++)
            {
                var span = arrays.Slice(indexAt + at * indexWidth, indexWidth);
                indices[at] = narrow ? BitConverter.ToUInt16(span) : (int)BitConverter.ToUInt32(span);
            }
            shapes.Add(new PhyreCollisionShape(vertices, indices));
        }
        return shapes;
    }

    /// <summary>
    /// The shapes as the line segments that draw them: one per triangle edge, with
    /// each edge drawn once however many triangles share it.
    /// </summary>
    public static IReadOnlyList<(Vector3 From, Vector3 To)> Edges(
        IReadOnlyList<PhyreCollisionShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        var lines = new List<(Vector3, Vector3)>();
        var seen = new HashSet<(int, int)>();
        foreach (var shape in shapes)
        {
            seen.Clear();
            for (var at = 0; at + 2 < shape.Indices.Count; at += 3)
            {
                var triangle = new[] { shape.Indices[at], shape.Indices[at + 1], shape.Indices[at + 2] };
                for (var side = 0; side < 3; side++)
                {
                    var a = triangle[side];
                    var b = triangle[(side + 1) % 3];
                    if (a < 0 || b < 0 || a >= shape.Vertices.Count || b >= shape.Vertices.Count) continue;
                    var key = a < b ? (a, b) : (b, a);
                    if (!seen.Add(key)) continue;
                    lines.Add((shape.Vertices[a], shape.Vertices[b]));
                }
            }
        }
        return lines;
    }
}
