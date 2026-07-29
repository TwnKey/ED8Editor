using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes a model's GPU payload again, with buffers of its own choosing, and
/// corrects everything that describes them.
///
/// The payload is two regions, indices then vertices, and every buffer in it is
/// named by a pair of numbers — a start and a length — held by the segment or
/// the data block that uses it. So replacing geometry is: lay the buffers out
/// again, write each pair back at its own address, and set the two region sizes
/// the header carries. Nothing else in the cluster moves.
///
/// Handing back the buffers a model already has must give that model back, byte
/// for byte; that is what says the layout and the addresses are right.
/// </summary>
public static class PhyreModelGeometryWriter
{
    /// <summary>Every buffer of the payload starts on a four-byte boundary.</summary>
    private const int BufferAlignment = 4;

    private const int HeaderIndexBufferSize = 72;
    private const int HeaderVertexBufferSize = 76;

    /// <summary>
    /// Rewrites the payload. <paramref name="replacement"/> is asked for the
    /// bytes of each buffer and may return them unchanged, or a longer or
    /// shorter run.
    /// </summary>
    public static byte[] Rewrite(
        ReadOnlyMemory<byte> cluster,
        Func<PhyreGeometryRange, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var sections = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var ranges = PhyreModelGeometry.Ranges(data);
        if (ranges.Count == 0) return cluster.ToArray();

        var payload = sections.Payload;
        var output = cluster.ToArray();

        // Buffers keep the order they had, indices before vertices, and a buffer
        // two segments share is written once: they name the same run, and giving
        // them one each would double the payload.
        var placed = new Dictionary<(long Offset, long Size), (long Start, long Length)>();
        var written = new MemoryStream();
        var indexRegionEnd = 0L;
        foreach (var kind in new[] { "indices", "vertices" })
        {
            // Offsets are counted from the start of their own region, and so is
            // the alignment: the vertices begin where the indices stop, however
            // that falls.
            var regionStart = written.Length;
            foreach (var range in ranges
                         .Where(value => value.Kind == kind)
                         .OrderBy(value => value.Offset)
                         .ThenBy(value => value.ObjectId))
            {
                var key = (range.Offset, range.Size);
                if (!placed.ContainsKey(key))
                {
                    // Every buffer starts on a four-byte boundary. The gaps that
                    // leaves are the whole of what looked like unclaimed padding:
                    // three of two bytes on this model, six in all.
                    while ((written.Length - regionStart) % BufferAlignment != 0)
                    {
                        written.WriteByte(0);
                    }
                    var source = range.Offset + range.Size <= payload.Length
                        ? payload.Slice((int)range.Offset, (int)range.Size)
                        : ReadOnlyMemory<byte>.Empty;
                    var bytes = replacement(range, source);
                    placed[key] = (written.Length, bytes.Length);
                    written.Write(bytes.Span);
                }
            }
            if (kind != "indices") continue;
            indexRegionEnd = written.Length;
        }

        foreach (var range in ranges)
        {
            var (start, size) = placed[(range.Offset, range.Size)];
            // A vertex buffer counts from the end of the index region, which is
            // how the cluster stores it.
            var stored = range.Kind == "vertices" ? start - indexRegionEnd : start;
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan((int)range.ObjectOffset), (uint)stored);
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan((int)range.SizeFieldOffset), (uint)size);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(HeaderIndexBufferSize), (uint)indexRegionEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(HeaderVertexBufferSize), (uint)(written.Length - indexRegionEnd));

        var payloadStart = cluster.Length - payload.Length;
        var result = new byte[payloadStart + written.Length];
        output.AsSpan(0, payloadStart).CopyTo(result);
        written.ToArray().CopyTo(result.AsSpan(payloadStart));
        return result;
    }

    /// <summary>Hands every buffer back unchanged, which must rebuild the model.</summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> cluster)
        => Rewrite(cluster, (_, source) => source);

    private static long Claimed(IReadOnlyList<PhyreGeometryRange> ranges)
    {
        var claimed = 0L;
        var end = 0L;
        foreach (var range in ranges.OrderBy(value => value.Offset))
        {
            var from = Math.Max(range.Offset, end);
            if (range.Offset + range.Size <= from) continue;
            claimed += range.Offset + range.Size - from;
            end = range.Offset + range.Size;
        }
        return claimed;
    }

}
