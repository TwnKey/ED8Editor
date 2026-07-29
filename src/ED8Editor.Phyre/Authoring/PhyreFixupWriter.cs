using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes the fixup tables — the lists that say which pointer of which object
/// points at what, once the file is loaded and the objects have real addresses.
///
/// A table is a run of blocks, one per group of fixups that share a source. The
/// first byte of a block carries the packing on its low three bits and, above
/// them, a mask of what the block leaves out because it is the same for every
/// fixup or simply absent. Numbers are variable-length, seven bits at a time.
///
/// Four of the game's shapes are written — every object, a bitmask, all but a
/// listed few, and an explicit list — and the shortest one wins. The fifth,
/// grouped targets, is not written yet; it is what still separates a model's
/// table from Falcom's byte for byte. Nothing is assumed:
/// <see cref="PhyreAuthoringCheck"/> compares the bytes.
/// </summary>
public static class PhyreFixupWriter
{
    public static byte[] WritePointers(
        IReadOnlyList<PhyrePointerFixup> fixups,
        IReadOnlyList<PhyreInstanceGroup> groups)
        => Write(fixups, groups, pointer: true);

    public static byte[] WriteArrays(
        IReadOnlyList<PhyreArrayFixup> fixups,
        IReadOnlyList<PhyreInstanceGroup> groups)
        => Write(fixups, groups, pointer: false);

    /// <summary>
    /// A table: the groups in order, and within a group one block per source,
    /// each block covering every fixup that starts from that source.
    /// </summary>
    private static byte[] Write<TFixup>(
        IReadOnlyList<TFixup> fixups,
        IReadOnlyList<PhyreInstanceGroup> groups,
        bool pointer)
        where TFixup : PhyreFixup
    {
        ArgumentNullException.ThrowIfNull(fixups);
        ArgumentNullException.ThrowIfNull(groups);
        var output = new MemoryStream();
        foreach (var group in groups)
        {
            // The engine sorts a list's fixups before packing them — by source,
            // then by what they point at, then by which object they belong to —
            // and a block is then a run of neighbours that share a source. The
            // sort is what puts fixups with the same target side by side, which
            // is what makes a shared destination list and grouped targets worth
            // anything.
            var sorted = fixups
                .Where(value => value.SourceListIndex == group.Index)
                .OrderBy(value => value, PhyreFixupOrder.Instance)
                .ToArray();
            for (var start = 0; start < sorted.Length;)
            {
                var end = start + 1;
                while (end < sorted.Length
                       && SameSource(sorted[start], sorted[end]))
                {
                    end++;
                }
                PhyreFixupPacker.WriteBlock(
                    output, sorted[start..end], group.Count, pointer);
                start = end;
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// The blocks the last table written was made of, when the packer was asked
    /// to record them.
    /// </summary>
    public static IReadOnlyList<(long Offset, byte Packing, uint Mask, uint Source, int Count)>
        LastBlocks => PhyreFixupPacker.Trace
            ?? (IReadOnlyList<(long, byte, uint, uint, int)>)Array.Empty<(long, byte, uint, uint, int)>();

    /// <summary>Records the blocks of the next table written.</summary>
    public static void BeginTrace() => PhyreFixupPacker.Trace = new();

    public static void EndTrace() => PhyreFixupPacker.Trace = null;

    /// <summary>
    /// Two fixups share a source when both name a member and name the same one,
    /// or when both name an offset and name the same one. A member and an offset
    /// never share.
    /// </summary>
    private static bool SameSource(PhyreFixup left, PhyreFixup right)
        => left.IsClassDataMember == right.IsClassDataMember
            && left.SourceOffsetOrMember == right.SourceOffsetOrMember;

    /// <summary>
    /// How many blocks a group would carry, which the instance-group header has
    /// to declare: one per fixup, the way this writer packs them.
    /// </summary>
    public static uint CountFor(IEnumerable<PhyreFixup> fixups, int groupIndex)
        => (uint)fixups.Count(value => value.SourceListIndex == groupIndex);
}
