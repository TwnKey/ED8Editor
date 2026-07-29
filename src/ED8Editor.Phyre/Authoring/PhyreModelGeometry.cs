using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One run of the GPU payload, and the field that says where it sits.</summary>
/// <param name="Kind">Indices or vertices.</param>
/// <param name="ObjectOffset">Where in the cluster the offset field itself is.</param>
public sealed record PhyreGeometryRange(
    string Kind,
    int GroupIndex,
    uint ObjectId,
    long Offset,
    long Size,
    long ObjectOffset,
    long SizeFieldOffset);

/// <summary>
/// Finds every buffer a model's GPU payload holds, and where the fields that
/// describe them live.
///
/// A segment says where its indices are and how long they run; a data block says
/// the same for its vertices. Replacing geometry means writing those runs again
/// and correcting those fields, which is why they are located here rather than
/// read into a mesh: what matters is the address of the number, not its value.
/// </summary>
public static class PhyreModelGeometry
{
    /// <summary>PMeshSegment: how many indices, where they start, how long they run.</summary>
    private const int SegmentIndexOffsetField = 0x40;
    private const int SegmentIndexSizeField = 0x48;
    private const int SegmentVertexPointerField = 0x18;

    /// <summary>PDataBlockD3D11: where the vertices start and how long they run.</summary>
    private const int BlockVertexOffsetField = 0x28;
    private const int BlockVertexSizeField = 0x30;

    public static IReadOnlyList<PhyreGeometryRange> Ranges(PhyreClusterData cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var ranges = new List<PhyreGeometryRange>();
        var segments = FindGroup(cluster, "PMeshSegment");
        var blocks = FindGroup(cluster, "PDataBlockD3D11");
        if (segments < 0) return ranges;

        // The index buffers come first in the payload, so a vertex offset is
        // counted from the end of them.
        var indexBufferSize = BinaryPrimitives.ReadUInt32LittleEndian(
            cluster.Data.Span[(int)PhyreTextureSchema.MemberOffset(
                "PClusterHeaderD3D11", "m_indexBufferSize")..]);

        var group = cluster.Metadata.InstanceGroups[segments];
        for (uint id = 0; id < group.Count; id++)
        {
            var segment = cluster.GetObject(segments, id).Span;
            var offset = ObjectOffset(cluster, segments, id);
            ranges.Add(new PhyreGeometryRange(
                "indices",
                segments,
                id,
                BinaryPrimitives.ReadUInt32LittleEndian(segment[SegmentIndexOffsetField..]),
                BinaryPrimitives.ReadUInt32LittleEndian(segment[SegmentIndexSizeField..]),
                offset + SegmentIndexOffsetField,
                offset + SegmentIndexSizeField));
        }

        if (blocks < 0) return ranges;
        var blockGroup = cluster.Metadata.InstanceGroups[blocks];
        for (uint id = 0; id < blockGroup.Count; id++)
        {
            var block = cluster.GetObject(blocks, id).Span;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(block[BlockVertexSizeField..]);
            if (size == 0) continue;
            var offset = ObjectOffset(cluster, blocks, id);
            ranges.Add(new PhyreGeometryRange(
                "vertices",
                blocks,
                id,
                indexBufferSize
                    + BinaryPrimitives.ReadUInt32LittleEndian(block[BlockVertexOffsetField..]),
                size,
                offset + BlockVertexOffsetField,
                offset + BlockVertexSizeField));
        }
        return ranges;
    }

    /// <summary>
    /// What the payload holds that no buffer claims. A model whose ranges tile it
    /// can have its geometry written again; one with unexplained bytes cannot,
    /// not yet.
    /// </summary>
    public static long Unclaimed(PhyreClusterData cluster, long payloadSize)
    {
        var claimed = 0L;
        var end = 0L;
        foreach (var range in Ranges(cluster).OrderBy(value => value.Offset))
        {
            // Overlapping runs would be counted once, which is what a shared
            // buffer looks like.
            var from = Math.Max(range.Offset, end);
            if (range.Offset + range.Size <= from) continue;
            claimed += range.Offset + range.Size - from;
            end = range.Offset + range.Size;
        }
        return payloadSize - claimed;
    }

    private static int FindGroup(PhyreClusterData cluster, string className)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            if (cluster.Metadata.InstanceGroups[index].ClassName == className) return index;
        }
        return -1;
    }

    private static long ObjectOffset(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var offset = cluster.Metadata.Header.ObjectDataOffset;
        for (var index = 0; index < groupIndex; index++)
        {
            offset += cluster.Metadata.InstanceGroups[index].Size;
        }
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        var objectSize = group.Count == 0 ? 0 : group.ObjectsSize / group.Count;
        return offset + objectId * objectSize;
    }
}
