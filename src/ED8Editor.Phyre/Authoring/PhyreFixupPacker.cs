using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Packs the fixups of one source into a block, the way the game's own writer
/// does.
///
/// Every block shares a source — the member or the offset the fixups start from
/// — and then says which objects of the group carry one, in one of several
/// shapes: all of them, the ones a bitmask marks, all but a listed few, or an
/// explicit list. Which shape a block takes is chosen by size, so the reader
/// meets the shortest of them.
///
/// The two shapes the game never uses (an inclusive list, and a fixed stride)
/// are not written: a census over twenty character packages found 0 blocks of
/// either, against 10 549 of "all objects" and 1 582 of grouped targets.
/// </summary>
public static class PhyreFixupPacker
{
    /// <summary>
    /// Where each block was written, how it was packed and what it covered. Set
    /// to a list to record it, which is what lets the blocks this writer forms
    /// be compared with the ones the game's file holds.
    /// </summary>
    public static List<(long Offset, byte Packing, uint Mask, uint Source, int Count)>? Trace
    { get; set; }


    public const uint ExcludeSourceObject = 2;
    public const uint ExcludeArrayValue = 8;
    public const uint ExcludeUserFixup = 16;
    public const uint ExcludeDestinationList = 32;
    public const uint ExcludeDestinationOffset = 64;

    private const byte PackAll = 0;

    /// <summary>
    /// Fixups grouped by what they point at: one run per target, each saying
    /// which objects share it. The block covers every object of the group.
    /// </summary>
    private const byte PackGroupedTargets = 1;

    private const byte PackInclusive = 2;
    private const byte PackExclusive = 3;
    private const byte PackBitmask = 4;
    private const byte PackRaw = 5;

    /// <summary>Objects at a fixed step from one another: first, step, how many.</summary>
    private const byte PackStrided = 6;

    /// <summary>
    /// Writes every fixup of one group that starts from the same source, as one
    /// block, choosing the shortest shape that fits them.
    /// </summary>
    public static void WriteBlock(
        Stream output,
        IReadOnlyList<PhyreFixup> fixups,
        uint objectCount,
        bool pointer)
    {
        var mask = CommonMask(fixups, objectCount, pointer);
        var shapes = new Dictionary<byte, byte[]>();
        foreach (var packing in new[]
                 {
                     PackAll, PackGroupedTargets, PackBitmask, PackInclusive, PackExclusive,
                     PackStrided, PackRaw,
                 })
        {
            if (TryPack(packing, fixups, objectCount, pointer, mask) is { } bytes)
            {
                shapes[packing] = bytes;
            }
        }

        // A block that covers every object once is written for all of them, and
        // only grouped by target when that comes out shorter. Any other block
        // starts from the raw shape and gives it up only for something strictly
        // shorter — so a tie leaves the raw shape in place. That is the engine's
        // own rule, and a tie is common enough that taking the shortest by name
        // picks the wrong one.
        byte chosen;
        if (shapes.ContainsKey(PackAll))
        {
            chosen = shapes.TryGetValue(PackGroupedTargets, out var grouped)
                && grouped.Length < shapes[PackAll].Length
                    ? PackGroupedTargets
                    : PackAll;
        }
        else
        {
            // The engine's own order: raw holds the seat, then the bitmask, the
            // inclusive list and the exclusive list each take it only by being
            // strictly shorter — and the strided shape is weighed last, against
            // whichever of them is sitting there (PhyreFixupCompression.cpp,
            // selectPackType).
            chosen = PackRaw;
            foreach (var candidate in new[] { PackBitmask, PackInclusive, PackExclusive, PackStrided })
            {
                if (shapes.TryGetValue(candidate, out var bytes)
                    && bytes.Length < shapes[chosen].Length)
                {
                    chosen = candidate;
                }
            }
        }
        var best = (Packing: chosen, Bytes: shapes[chosen]);
        if (Trace is { } trace)
        {
            trace.Add((output.Position, best.Packing, mask, fixups[0].SourceOffsetOrMember, fixups.Count));
        }
        output.Write(best.Bytes);
    }

    /// <summary>
    /// What every fixup of the block has in common, and so what the block can
    /// leave out. A bit may only be set when it holds for all of them.
    /// </summary>
    private static uint CommonMask(IReadOnlyList<PhyreFixup> fixups, uint objectCount, bool pointer)
    {
        var mask = 0u;
        if (!pointer)
        {
            if (fixups.Cast<PhyreArrayFixup>().All(value => value.Count == 0))
            {
                mask |= ExcludeArrayValue;
            }
            return mask;
        }

        var pointers = fixups.Cast<PhyrePointerFixup>().ToArray();
        if (pointers.All(value => value.ArrayIndex == 0)) mask |= ExcludeArrayValue;
        if (pointers.All(value => value.UserFixupId is null)) mask |= ExcludeUserFixup;
        if (pointers.All(value => value.DestinationOffset == 0)) mask |= ExcludeDestinationOffset;
        // The destination list is hoisted out of the payloads when every fixup of
        // the block names the same one — a fixup that names a user fixup instead
        // carries a list of zero, and counts like any other. Hoisting a single
        // fixup's list saves nothing, so the engine does not.
        var destinations = pointers
            .Select(value => value.UserFixupId is null ? value.DestinationListIndex : 0u)
            .Distinct()
            .ToArray();
        if (pointers.Length > 1 && destinations.Length == 1)
        {
            mask |= ExcludeDestinationList;
        }
        return mask;
    }

    /// <summary>
    /// The run of evenly spaced object ids a block covers, or nothing when they
    /// are not evenly spaced.
    ///
    /// The engine walks the ids in order, takes the step between the first two,
    /// and counts how far that step keeps holding (PhyreFixupCompression.cpp,
    /// selectPackType). The shape is only usable when the run reaches every id
    /// the block has — a series that stops short describes only part of them.
    /// </summary>
    private static (uint First, uint Stride, uint Length)? Series(IReadOnlyList<uint> ids)
    {
        var ordered = ids.Distinct().Order().ToArray();
        if (ordered.Length < 2) return null;
        var stride = ordered[1] - ordered[0];
        var length = 2u;
        var last = ordered[1];
        for (var index = 2; index < ordered.Length; index++)
        {
            if (ordered[index] - last != stride) break;
            length++;
            last = ordered[index];
        }
        return length == ordered.Length ? (ordered[0], stride, length) : null;
    }

    private static byte[]? TryPack(
        byte packing,
        IReadOnlyList<PhyreFixup> fixups,
        uint objectCount,
        bool pointer,
        uint mask)
    {
        var ids = fixups.Select(value => value.SourceObjectId).ToArray();
        // The engine asks only that no object appears twice — the fixups are
        // sorted by target, so their ids are not in order, and every shape but
        // the raw one walks them by id instead.
        var noDuplicates = ids.Distinct().Count() == ids.Length;
        var coversEveryObject = (uint)fixups.Count == objectCount && noDuplicates;
        switch (packing)
        {
            case PackAll when !coversEveryObject:
            case PackGroupedTargets when !coversEveryObject:
            case PackBitmask when !noDuplicates:
            case PackInclusive when !noDuplicates:
            case PackExclusive when !noDuplicates:
            // The engine asks that the run reach as far as there are fixups —
            // "the number of matching fixups, which could share objects". Two
            // fixups on one object make the run of distinct ids fall short of
            // that count, so the shape is out.
            case PackStrided when !noDuplicates:
                return null;
        }

        // The block is re-sorted by object before being packed — every shape,
        // the raw one included. It is what makes the object list and the
        // payloads that follow it line up, and a shipped raw block reads back
        // with its object ids ascending, so the raw shape wants it too.
        //
        // The sort has to be stable: within one source object the fixups keep
        // the order the engine's own sort gave them, and that order is not
        // recoverable from the object id alone.
        var ordered = fixups.OrderBy(value => value.SourceObjectId).ToArray();

        var output = new MemoryStream();
        output.WriteByte((byte)(packing | mask));
        PhyreVariableLength.WriteSource(output, fixups[0].SourceOffsetOrMember);
        if (pointer && (mask & ExcludeDestinationList) != 0)
        {
            PhyreVariableLength.Write(
                output, ((PhyrePointerFixup)fixups[0]).DestinationListIndex);
        }

        if (packing == PackGroupedTargets)
        {
            WriteGroupedTargets(output, fixups, objectCount, pointer, mask);
            return output.ToArray();
        }

        // How the block says which objects it covers.
        switch (packing)
        {
            case PackAll:
                break;
            case PackBitmask:
                var bitmask = new byte[(objectCount + 7) / 8];
                foreach (var id in ids) bitmask[id / 8] |= (byte)(1 << (int)(id & 7));
                output.Write(bitmask);
                break;
            case PackInclusive:
                PhyreVariableLength.Write(output, (uint)ordered.Length);
                foreach (var fixup in ordered)
                {
                    WriteObjectId(output, fixup.SourceObjectId, objectCount);
                }
                break;
            case PackExclusive:
                var excluded = Enumerable.Range(0, (int)objectCount)
                    .Select(value => (uint)value)
                    .Where(value => !ids.Contains(value))
                    .ToArray();
                PhyreVariableLength.Write(output, (uint)excluded.Length);
                foreach (var id in excluded) WriteObjectId(output, id, objectCount);
                break;
            case PackStrided:
                // Objects at a fixed step: where the series starts, its step and
                // how long it runs, in place of a list.
                var series = Series(ids);
                if (series is null) return null;
                PhyreVariableLength.Write(output, series.Value.First);
                PhyreVariableLength.Write(output, series.Value.Stride);
                PhyreVariableLength.Write(output, series.Value.Length);
                break;
            case PackRaw:
                PhyreVariableLength.Write(output, (uint)fixups.Count);
                break;
        }

        foreach (var fixup in ordered)
        {
            // Only the raw shape spells out which object each fixup belongs to —
            // and even it stays quiet when the group holds a single object,
            // since there is then nothing to say. That exclusion is not part of
            // the block's mask: the reader adds it from the object count, and so
            // must the writer.
            if (packing == PackRaw && objectCount > 1)
            {
                PhyreVariableLength.Write(output, fixup.SourceObjectId);
            }
            WritePayload(output, fixup, pointer, mask);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Writes the block as runs of objects that share a target: each run gives
    /// the payload once, then says which objects take it, in whichever of the
    /// shapes is shortest for that run.
    /// </summary>
    private static void WriteGroupedTargets(
        Stream output,
        IReadOnlyList<PhyreFixup> fixups,
        uint objectCount,
        bool pointer,
        uint mask)
    {
        // Runs keep the order their target first appears in, so a table read and
        // written back keeps its own.
        foreach (var run in fixups
                     .GroupBy(fixup => PayloadKey(fixup, pointer, mask))
                     .Select(group => group.ToArray()))
        {
            var ids = run.Select(fixup => fixup.SourceObjectId).ToArray();
            var payload = new MemoryStream();
            WritePayload(payload, run[0], pointer, mask);
            var payloadBytes = payload.ToArray();

            // A run inside a grouped block never takes the "all objects" shape —
            // the engine's own switch has no case for it — but it may take a
            // fixed stride, which never appears as a block on its own.
            var selection = SelectPackType(ids, objectCount);
            output.WriteByte(selection);
            output.Write(payloadBytes);
            output.Write(TrySelect(selection, ids, objectCount)!);
        }
    }

    /// <summary>
    /// Which shape a run of objects takes, chosen the way the engine chooses it:
    /// the sizes are compared in the order bitmask, inclusive list, exclusive
    /// list, then stride, and each has to be STRICTLY smaller to win. So a tie
    /// goes to the shape that was weighed first — which is why the order matters
    /// as much as the sizes.
    /// </summary>
    private static byte SelectPackType(uint[] ids, uint objectCount)
    {
        var sorted = ids.OrderBy(value => value).ToArray();
        var bitmaskSize = (objectCount + 7) / 8;
        var inclusiveSize = PackedSize((uint)sorted.Length)
            + (uint)sorted.Sum(id => objectCount < 256 ? 1 : PackedSize(id));
        var excluded = Enumerable.Range(0, (int)objectCount)
            .Select(value => (uint)value)
            .Where(value => !sorted.Contains(value))
            .ToArray();
        var exclusiveSize = PackedSize((uint)excluded.Length)
            + (uint)excluded.Sum(id => objectCount < 256 ? 1 : PackedSize(id));

        var selection = PackRaw;
        var smallest = uint.MaxValue;
        if (bitmaskSize < smallest)
        {
            smallest = bitmaskSize;
            selection = PackBitmask;
        }
        if (inclusiveSize < smallest)
        {
            smallest = inclusiveSize;
            selection = PackInclusive;
        }
        if (exclusiveSize < smallest)
        {
            smallest = exclusiveSize;
            selection = PackExclusive;
        }

        // A stride is only weighed when the run really is one series from end to
        // end — a run of a single object has no stride at all.
        if (SeriesLength(sorted) is { } series && series == sorted.Length)
        {
            var strideSize = PackedSize(sorted[0])
                + PackedSize(sorted[1] - sorted[0])
                + PackedSize((uint)series);
            if (strideSize < smallest) selection = PackStrided;
        }
        return selection;
    }

    /// <summary>How far the run keeps a constant step, or nothing if it has none.</summary>
    private static int? SeriesLength(uint[] sorted)
    {
        if (sorted.Length < 2) return null;
        var stride = sorted[1] - sorted[0];
        var length = 2;
        for (var index = 2; index < sorted.Length; index++)
        {
            if (sorted[index] - sorted[index - 1] != stride) break;
            length++;
        }
        return length;
    }

    /// <summary>How many bytes a number takes, seven bits at a time.</summary>
    private static uint PackedSize(uint value)
    {
        var size = 1u;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    /// <summary>How a run of objects says which ones it covers.</summary>
    private static byte[]? TrySelect(byte selection, uint[] ids, uint objectCount)
    {
        var output = new MemoryStream();
        switch (selection)
        {
            case PackAll:
                if ((uint)ids.Length != objectCount) return null;
                break;
            case PackInclusive:
                PhyreVariableLength.Write(output, (uint)ids.Length);
                foreach (var id in ids) WriteObjectId(output, id, objectCount);
                break;
            case PackExclusive:
                var excluded = Enumerable.Range(0, (int)objectCount)
                    .Select(value => (uint)value)
                    .Where(value => !ids.Contains(value))
                    .ToArray();
                PhyreVariableLength.Write(output, (uint)excluded.Length);
                foreach (var id in excluded) WriteObjectId(output, id, objectCount);
                break;
            case PackBitmask:
                var bitmask = new byte[(objectCount + 7) / 8];
                foreach (var id in ids) bitmask[id / 8] |= (byte)(1 << (int)(id & 7));
                output.Write(bitmask);
                break;
            case PackStrided:
                var ordered = ids.OrderBy(value => value).ToArray();
                if (ordered.Length < 2) return null;
                PhyreVariableLength.Write(output, ordered[0]);
                PhyreVariableLength.Write(output, ordered[1] - ordered[0]);
                PhyreVariableLength.Write(output, (uint)ordered.Length);
                break;
            default:
                return null;
        }
        return output.ToArray();
    }

    /// <summary>What makes two fixups share a run: the bytes their payload writes.</summary>
    private static string PayloadKey(PhyreFixup fixup, bool pointer, uint mask)
    {
        var payload = new MemoryStream();
        WritePayload(payload, fixup, pointer, mask);
        return Convert.ToHexString(payload.ToArray());
    }

    private static void WritePayload(Stream output, PhyreFixup fixup, bool pointer, uint mask)
    {
        if (!pointer)
        {
            var array = (PhyreArrayFixup)fixup;
            if ((mask & ExcludeArrayValue) == 0) PhyreVariableLength.Write(output, array.Count);
            PhyreVariableLength.Write(output, array.Offset);
            return;
        }

        var value = (PhyrePointerFixup)fixup;
        if ((mask & ExcludeUserFixup) == 0)
        {
            // A user fixup is stored one past zero, which is how the block says
            // there is none.
            PhyreVariableLength.Write(output, value.UserFixupId is { } id ? id + 1 : 0);
        }
        if (value.UserFixupId is null)
        {
            PhyreVariableLength.Write(output, value.DestinationObjectId);
            if ((mask & ExcludeDestinationList) == 0)
            {
                PhyreVariableLength.Write(output, value.DestinationListIndex);
            }
            if ((mask & ExcludeDestinationOffset) == 0)
            {
                PhyreVariableLength.Write(output, value.DestinationOffset);
            }
        }
        if ((mask & ExcludeArrayValue) == 0) PhyreVariableLength.Write(output, value.ArrayIndex);
    }

    /// <summary>A group of fewer than 256 objects names them in a single byte.</summary>
    private static void WriteObjectId(Stream output, uint id, uint objectCount)
    {
        if (objectCount < 256) output.WriteByte((byte)id);
        else PhyreVariableLength.Write(output, id);
    }
}

/// <summary>Numbers in a fixup table are written seven bits at a time.</summary>
internal static class PhyreVariableLength
{
    public static void Write(Stream output, uint value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    /// <summary>
    /// The source of a fixup: a member index, or an offset when its top bit is
    /// set. That bit rides in the low bit of the number so both stay short.
    /// </summary>
    public static void WriteSource(Stream output, uint source)
    {
        var offsetRatherThanMember = (source & 0x80000000u) != 0;
        Write(output, ((source & 0x7fffffffu) << 1) | (offsetRatherThanMember ? 1u : 0u));
    }
}
