namespace ED8Editor.Phyre.Authoring;

/// <summary>One channel, as the binding needs to see it.</summary>
/// <param name="Interpolation">The channel's own interpolation mode.</param>
/// <param name="KeyTypeIndex">
/// Which animation key type it drives. The engine registers them in one fixed
/// order, and the index is that order: rotation 0, translation 1, scale 2.
/// </param>
/// <param name="TargetIndex">Its target's position in the animation set's targets.</param>
/// <param name="KeyCount">How many keys it holds; zero for a constant channel.</param>
/// <param name="ValueWidth">Floats per key — four for a rotation, three otherwise.</param>
public sealed record PhyreAnimationChannelBinding(
    int Interpolation, int KeyTypeIndex, int TargetIndex, int KeyCount, int ValueWidth);

/// <summary>
/// Derives an animation clip's binding block — the cache that says, for every
/// channel, which slot of the animation set it drives.
///
/// A clip cannot simply be given more or fewer channels: this block is sized and
/// ordered by the channel set, so changing that set means deriving it again. It
/// is the one part of a clip that is not data an author supplies, which is why it
/// stood in the way of writing a clip whose channels are the author's own.
///
/// Every rule here is PhyreEngine's own, from
/// <c>Core/Animation/PhyreAnimationClipBinding.cpp</c>:
///
/// <list type="bullet">
/// <item>A channel's batch sort key is its interpolation, with its key type index
/// above it: <c>interp | (keyType &lt;&lt; 2)</c>.</item>
/// <item>Channels are written sorted by that key — so the engine can process a run
/// of same-typed channels together — and the run lengths are the histogram of
/// those keys, in ascending key order, padded with zeros to a multiple of four.</item>
/// <item>Constant channels follow, unsorted.</item>
/// <item>Each entry names the slot the animation set gives to its key type and
/// target, and its own interpolation.</item>
/// </list>
///
/// The arithmetic was confirmed against a shipped clip before any of it was
/// written: npc000's idle declares 40 channels, 54 constant channels and 3 run
/// lengths, and this layout gives 8 + 8 + 94×4 + 40×16 = 1032 bytes with an SPU
/// size of align16(392) = 400 — the two numbers the file itself states.
/// </summary>
public static class PhyreAnimationBinding
{
    /// <summary>Bytes one data block cache entry takes, measured on CS1's clips.</summary>
    private const int CacheStride = 16;

    /// <summary>The declared members, ahead of everything derived.</summary>
    private const int HeaderSize = 8;

    /// <summary>
    /// The order channels are written in: sorted by batch sort key, and stable
    /// within a key so a channel keeps its place among its own kind.
    ///
    /// Returned separately because the caller needs it too — the data block cache
    /// records where each written entry came from, and nothing else can say.
    /// </summary>
    public static IReadOnlyList<int> SortedOrder(
        IReadOnlyList<PhyreAnimationChannelBinding> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return Enumerable.Range(0, channels.Count)
            .OrderBy(index => SortKey(channels[index]))
            .ToArray();
    }

    /// <summary>The block itself.</summary>
    /// <param name="slotIndex">
    /// What slot the animation set gives to a key type and target. Reading it from
    /// the set rather than deriving it is deliberate: the set's slot array is
    /// sorted when it is built, and its order is the answer.
    /// </param>
    public static byte[] Build(
        IReadOnlyList<PhyreAnimationChannelBinding> channels,
        IReadOnlyList<PhyreAnimationChannelBinding> constantChannels,
        Func<int, int, int> slotIndex)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(constantChannels);
        ArgumentNullException.ThrowIfNull(slotIndex);

        // The run lengths, one per distinct sort key that appears, in ascending
        // key order — then padded with zeros until the count is a multiple of four.
        var histogram = new SortedDictionary<int, int>();
        foreach (var channel in channels)
        {
            histogram[SortKey(channel)] = histogram.GetValueOrDefault(SortKey(channel)) + 1;
        }
        var runs = histogram.Values.ToList();
        var declaredRuns = runs.Count;
        while (runs.Count % 4 != 0) runs.Add(0);

        var mapsAt = HeaderSize + runs.Count * sizeof(ushort);
        var cacheAt = mapsAt + (channels.Count + constantChannels.Count) * 4;
        var spuBindingSize = (cacheAt + 15) / 16 * 16;
        var bytes = new byte[cacheAt + channels.Count * CacheStride];

        BitConverter.GetBytes((ushort)spuBindingSize).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)channels.Count).CopyTo(bytes, 2);
        BitConverter.GetBytes((ushort)constantChannels.Count).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)declaredRuns).CopyTo(bytes, 6);
        for (var index = 0; index < runs.Count; index++)
        {
            BitConverter.GetBytes((ushort)runs[index]).CopyTo(bytes, HeaderSize + index * 2);
        }

        var order = SortedOrder(channels);
        for (var written = 0; written < order.Count; written++)
        {
            var source = order[written];
            var channel = channels[source];
            BitConverter.GetBytes((short)slotIndex(channel.KeyTypeIndex, channel.TargetIndex))
                .CopyTo(bytes, mapsAt + written * 4);
            BitConverter.GetBytes((ushort)channel.Interpolation)
                .CopyTo(bytes, mapsAt + written * 4 + 2);

            // The cache keeps the two pointers a load fixes up, so they are left
            // as they are found — zero in a file nothing has loaded yet — and the
            // width and key count the engine packs into one word.
            var cache = cacheAt + written * CacheStride;
            BitConverter.GetBytes((uint)((channel.ValueWidth << 24) | (channel.KeyCount & 0xFFFFFF)))
                .CopyTo(bytes, cache + 8);
            BitConverter.GetBytes((ushort)source).CopyTo(bytes, cache + 12);
        }

        var constantsAt = mapsAt + channels.Count * 4;
        for (var index = 0; index < constantChannels.Count; index++)
        {
            var channel = constantChannels[index];
            BitConverter.GetBytes((short)slotIndex(channel.KeyTypeIndex, channel.TargetIndex))
                .CopyTo(bytes, constantsAt + index * 4);
            BitConverter.GetBytes((ushort)channel.Interpolation)
                .CopyTo(bytes, constantsAt + index * 4 + 2);
        }
        return bytes;
    }

    /// <summary>
    /// How big a binding is for a given channel set, without building one — what
    /// the instance list has to give each object.
    /// </summary>
    public static int SizeOf(int channelCount, int constantChannelCount, int runCount)
    {
        var padded = (runCount + 3) / 4 * 4;
        return HeaderSize + padded * sizeof(ushort)
            + (channelCount + constantChannelCount) * 4
            + channelCount * CacheStride;
    }

    /// <summary>
    /// The key channels are batched by. Asserted in the engine to fit the
    /// interpolation in two bits, which is why the key type starts at bit two.
    /// </summary>
    private static int SortKey(PhyreAnimationChannelBinding channel)
        => channel.Interpolation | (channel.KeyTypeIndex << 2);
}
