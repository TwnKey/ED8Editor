using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes new keys into an animation clip the game ships, keeping everything
/// about the clip that is not key data.
///
/// A clip cluster carries far more than its curves: the node hierarchy it drives
/// (79 nodes on a character's idle), an animation set, a target per driven node,
/// a slot index per channel, and a <c>PAnimationClipBinding</c> — one of only two
/// header classes in the whole game, declaring eight bytes and storing 1032.
///
/// All of that describes the SKELETON, not the motion, and a character's skeleton
/// is exactly what the rest of this pipeline leaves alone. So this rewrites the
/// motion of a clip that already targets the right skeleton, which is what makes
/// it exact: everything it does not touch was already right.
///
/// Changing how many channels a clip has is a different job, and a bigger one —
/// the binding's channel maps and interpolation run lengths are derived from the
/// channel set, and <see cref="PhyreAnimationBinding"/> is where that derivation
/// lives.
///
/// The layout is rebuilt by walking the original array fixups in the order their
/// offsets already have, and writing each entry's new bytes in that same order.
/// Nothing that keeps its size moves, which is what makes the round trip — a clip
/// given its own keys back — come out as the file it was, byte for byte.
/// </summary>
public static class PhyreAnimationWriter
{
    private const uint ChannelNameOffset = 0x14;
    private const uint ChannelValuesOffset = 0x2c;
    private const uint ChannelKeyCountOffset = 0x30;
    private const uint TimesKeysOffset = 0x08;
    private const uint TimesKeyCountOffset = 0x00;
    private const uint ClipStartTimeOffset = 0x14;
    private const uint ClipEndTimeOffset = 0x18;
    private const uint ConstantValueOffset = 0x24;

    /// <summary>
    /// Rewrites <paramref name="cluster"/>'s keys from <paramref name="clip"/>.
    ///
    /// A channel of the file is matched to one of <paramref name="clip"/> by the
    /// name it targets and the path it drives. A channel the clip says nothing
    /// about keeps every key it had — silence is not a reason to flatten a bone.
    /// </summary>
    public static byte[] Rewrite(ReadOnlyMemory<byte> cluster, CpuAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        var byTarget = new Dictionary<(string Name, CpuAnimationPath Path), CpuAnimationChannel>();
        foreach (var channel in clip.Channels) byTarget.TryAdd((channel.TargetName, channel.Path), channel);

        var channelGroup = Index(cut, "PAnimationChannel");
        var timesGroup = Index(cut, "PAnimationChannelTimes");
        var constantGroup = Index(cut, "PAnimationConstantChannel");
        var clipGroup = Index(cut, "PAnimationClip");
        // A clip may hold no animated channel at all — a pose whose every bone is
        // constant is still a clip, and three of the game's own are exactly that.
        if (clipGroup < 0)
        {
            throw new InvalidPhyreException("This cluster holds no animation clip to rewrite.");
        }

        // Which channel object drives what, so a times object can be found from
        // the channel that points at it and given that channel's new keys.
        var wanted = new Dictionary<uint, CpuAnimationChannel>();
        var timesOwner = new Dictionary<uint, CpuAnimationChannel>();
        var channelCount = channelGroup < 0 ? 0 : cut.Metadata.InstanceGroups[channelGroup].Count;
        for (var id = 0u; id < channelCount; id++)
        {
            var name = ArrayString(data, fixups, channelGroup, id, ChannelNameOffset);
            var path = PathOf(data, fixups, channelGroup, id);
            if (name is null || path is null) continue;
            if (!byTarget.TryGetValue((name, path.Value), out var replacement)) continue;
            wanted[id] = replacement;

            var times = fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == channelGroup && value.SourceObjectId == id
                && !value.IsClassDataMember && value.SourceOffset == 0x24
                && value.DestinationListIndex == (uint)timesGroup);
            if (times is not null) timesOwner[times.DestinationObjectId] = replacement;
        }

        var groups = new List<PhyreGroupContents>();
        var moved = fixups.Arrays.ToList();
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            var stored = data.GetGroupObjectsData(group.Index).ToArray();
            var arrays = group.ArraysSize == 0
                ? ReadOnlyMemory<byte>.Empty
                : data.GetArrayData(group.Index, 0, group.ArraysSize).ToArray();

            if (group.Index == channelGroup || group.Index == timesGroup)
            {
                arrays = Relaid(moved, group.Index, arrays, (id, offset, original) =>
                {
                    if (group.Index == timesGroup)
                    {
                        if (offset != TimesKeysOffset || !timesOwner.TryGetValue(id, out var owner))
                        {
                            return original;
                        }
                        return Floats(owner.Times);
                    }
                    if (offset != ChannelValuesOffset || !wanted.TryGetValue(id, out var channel))
                    {
                        return original;
                    }
                    return Values(channel);
                });
            }

            // The counts each object states about its own arrays.
            if (group.Index == timesGroup)
            {
                for (var id = 0u; id < group.Count; id++)
                {
                    if (!timesOwner.TryGetValue(id, out var owner)) continue;
                    BitConverter.GetBytes((uint)owner.Times.Count)
                        .CopyTo(stored, (int)(id * size + TimesKeyCountOffset));
                }
            }
            if (group.Index == channelGroup)
            {
                foreach (var (id, channel) in wanted)
                {
                    BitConverter.GetBytes((uint)channel.Values.Count)
                        .CopyTo(stored, (int)(id * size + ChannelKeyCountOffset));
                }
            }
            if (group.Index == constantGroup)
            {
                for (var id = 0u; id < group.Count; id++)
                {
                    var name = ArrayString(data, fixups, constantGroup, id, ChannelNameOffset);
                    var path = PathOf(data, fixups, constantGroup, id);
                    if (name is null || path is null) continue;
                    if (!byTarget.TryGetValue((name, path.Value), out var replacement)) continue;
                    if (replacement.Values.Count == 0) continue;
                    var value = replacement.Values[0];
                    var at = (int)(id * size + ConstantValueOffset);
                    BitConverter.GetBytes(value.X).CopyTo(stored, at);
                    BitConverter.GetBytes(value.Y).CopyTo(stored, at + 4);
                    BitConverter.GetBytes(value.Z).CopyTo(stored, at + 8);
                    BitConverter.GetBytes(value.W).CopyTo(stored, at + 12);
                }
            }
            if (group.Index == clipGroup && group.Count != 0)
            {
                BitConverter.GetBytes(clip.StartTime).CopyTo(stored, (int)ClipStartTimeOffset);
                BitConverter.GetBytes(clip.EndTime).CopyTo(stored, (int)ClipEndTimeOffset);
            }

            var objects = new List<PhyreObjectContents>();
            for (var id = 0u; id < group.Count; id++)
            {
                objects.Add(PhyreObjectWriter.ReadObject(
                    stored.AsSpan((int)(id * size), size), group.ClassName ?? "", classes));
            }
            groups.Add(new PhyreGroupContents(group.ClassName ?? "", objects, arrays));
        }

        return PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            cut.Metadata.Types,
            groups,
            fixups with { Arrays = moved },
            fixups.UserFixups,
            cut.HeaderClasses,
            cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..],
            PhyreSchemaProfile.Cs1Native,
            classes.Select(value => value.Name).ToArray()));
    }

    /// <summary>
    /// Lays a group's array region out again, entry by entry, in the order the
    /// offsets already had. <paramref name="replace"/> answers with each entry's
    /// new bytes, or the ones it already had.
    /// </summary>
    private static byte[] Relaid(
        List<PhyreArrayFixup> fixups,
        int groupIndex,
        ReadOnlyMemory<byte> original,
        Func<uint, uint, byte[], byte[]> replace)
    {
        var mine = fixups
            .Select((value, at) => (Fixup: value, At: at))
            .Where(pair => pair.Fixup.SourceListIndex == groupIndex)
            .OrderBy(pair => pair.Fixup.Offset)
            .ToArray();
        if (mine.Length == 0) return original.ToArray();

        var laid = new MemoryStream();
        for (var index = 0; index < mine.Length; index++)
        {
            var (fixup, at) = mine[index];
            var end = index + 1 < mine.Length ? mine[index + 1].Fixup.Offset : (uint)original.Length;
            var was = original.Slice((int)fixup.Offset, (int)(end - fixup.Offset)).ToArray();
            var now = replace(fixup.SourceObjectId, fixup.SourceOffsetOrMember & 0x7fffffffu, was);

            // Entries the game aligns keep their padding: the slice above runs to
            // the next entry, so whatever followed a string travels with it.
            var offset = (uint)laid.Length;
            laid.Write(now);
            fixups[at] = fixup with
            {
                Offset = offset,
                Count = fixup.Count == 0 ? 0 : (uint)(now.Length / ElementSize(fixup, was)),
            };
        }
        return laid.ToArray();
    }

    /// <summary>
    /// How many bytes one element of an array fixup takes, from what it held.
    /// A fixup counting elements rather than bytes has to keep counting them.
    /// </summary>
    private static int ElementSize(PhyreArrayFixup fixup, byte[] was)
        => fixup.Count == 0 ? 1 : Math.Max(1, was.Length / (int)fixup.Count);

    private static byte[] Floats(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            BitConverter.GetBytes(values[index]).CopyTo(bytes, index * sizeof(float));
        }
        return bytes;
    }

    /// <summary>A channel's keys, three floats each or four for a rotation.</summary>
    private static byte[] Values(CpuAnimationChannel channel)
    {
        var width = channel.Path == CpuAnimationPath.Rotation ? 4 : 3;
        var bytes = new byte[channel.Values.Count * width * sizeof(float)];
        for (var index = 0; index < channel.Values.Count; index++)
        {
            var value = channel.Values[index];
            var at = index * width * sizeof(float);
            BitConverter.GetBytes(value.X).CopyTo(bytes, at);
            BitConverter.GetBytes(value.Y).CopyTo(bytes, at + 4);
            BitConverter.GetBytes(value.Z).CopyTo(bytes, at + 8);
            if (width == 4) BitConverter.GetBytes(value.W).CopyTo(bytes, at + 12);
        }
        return bytes;
    }

    private static string? ArrayString(
        PhyreClusterData data, PhyreFixupSet fixups, int groupIndex, uint objectId, uint offset)
    {
        var fixup = fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && !value.IsClassDataMember && value.SourceOffset == offset);
        if (fixup is null) return null;
        var group = data.Metadata.InstanceGroups[groupIndex];
        var span = data.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = span.IndexOf((byte)0);
        return zero < 0 ? null : Encoding.UTF8.GetString(span[..zero]);
    }

    private static CpuAnimationPath? PathOf(
        PhyreClusterData data, PhyreFixupSet fixups, int groupIndex, uint objectId)
    {
        var pointer = fixups.Pointers.FirstOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && !value.IsClassDataMember && value.SourceOffset == 0x1c);
        if (pointer?.UserFixupId is not { } id || id >= fixups.UserFixups.Count) return null;
        return fixups.UserFixups[(int)id].Text switch
        {
            "Translation" => CpuAnimationPath.Translation,
            "Rotation" => CpuAnimationPath.Rotation,
            "Scale" => CpuAnimationPath.Scale,
            _ => null,
        };
    }

    private static int Index(PhyreClusterSections cut, string className)
    {
        for (var at = 0; at < cut.Metadata.InstanceGroups.Count; at++)
        {
            if (cut.Metadata.InstanceGroups[at].ClassName == className) return at;
        }
        return -1;
    }
}
