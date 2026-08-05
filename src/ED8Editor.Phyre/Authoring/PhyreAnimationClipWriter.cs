using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes an animation clip whose channels are entirely the author's — any bones,
/// any number of keys — into a cluster that already describes the right skeleton.
///
/// What is taken from the donor is only the skeleton's description: the nodes, the
/// animation set, its targets and its slot table. That is not a shortcut. Those
/// tables say what a character IS, not what it does, and a clip that drove a
/// different set of them would not drive this character at all — so they have to
/// be the target's own whatever the motion is.
///
/// Everything that is the motion is written here: the channels, their names, their
/// times and values, which are constant and which are animated, and the binding —
/// the one part of a clip that is derived rather than authored, and the reason a
/// clip could not be given a different channel count until
/// <see cref="PhyreAnimationBinding"/> could produce it.
///
/// A channel names the bone it drives by name, not by pointer, which is what makes
/// this possible without touching the node hierarchy at all.
/// </summary>
public static class PhyreAnimationClipWriter
{
    /// <summary>
    /// Writes <paramref name="clip"/> into <paramref name="donor"/>'s skeleton.
    ///
    /// A channel whose every key holds the same value is written as a constant
    /// channel, which is how the game stores one: it costs one value instead of a
    /// curve, and the engine treats the two differently.
    /// </summary>
    public static byte[] Write(ReadOnlyMemory<byte> donor, CpuAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var cut = PhyreClusterSectionReader.Read(donor);
        var data = new PhyreClusterReader().Read(donor);
        var fixups = new PhyreFixupReader().Read(donor, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        var channelGroup = Index(cut, "PAnimationChannel");
        var timesGroup = Index(cut, "PAnimationChannelTimes");
        var constantGroup = Index(cut, "PAnimationConstantChannel");
        var clipGroup = Index(cut, "PAnimationClip");
        var bindingGroup = Index(cut, "PAnimationClipBinding");
        var slotGroup = Index(cut, "PAnimationSlotListIndex");
        var targetGroup = Index(cut, "PAnimationChannelTarget");
        if (clipGroup < 0 || bindingGroup < 0 || slotGroup < 0 || targetGroup < 0)
        {
            throw new InvalidPhyreException(
                "This cluster is not an animation clip with a skeleton to write against.");
        }

        // Only the key types this clip actually drives: a donor that animates no
        // scale names no scale type, and asking for one it never had would refuse a
        // perfectly writable clip.
        var nodeType = UserFixup(fixups, "PNode");
        var keyTypes = new Dictionary<CpuAnimationPath, uint>();
        foreach (var path in clip.Channels.Select(value => value.Path).Distinct())
        {
            keyTypes[path] = UserFixup(fixups, path switch
            {
                CpuAnimationPath.Rotation => "Rotation",
                CpuAnimationPath.Translation => "Translation",
                _ => "Scale",
            });
        }

        // The skeleton's own targets, by the name each drives, so a channel the
        // author brings can be given the target fields the set already knows.
        var targets = Targets(data, fixups, classes, targetGroup);
        var slots = Slots(data, fixups, classes, slotGroup, targets);

        // Which of the author's channels are curves and which are one value held.
        var animated = new List<CpuAnimationChannel>();
        var constants = new List<CpuAnimationChannel>();
        foreach (var channel in clip.Channels)
        {
            if (!targets.ContainsKey(channel.TargetName)) continue;
            if (IsConstant(channel)) constants.Add(channel); else animated.Add(channel);
        }
        if (animated.Count == 0 && constants.Count == 0)
        {
            throw new InvalidOperationException(
                "None of the clip's channels names a bone this skeleton has.");
        }

        var bindings = animated
            .Select(channel => Binding(channel, targets, KeyIndex(channel.Path), channel.Times.Count))
            .ToList();
        var constantBindings = constants
            .Select(channel => Binding(channel, targets, KeyIndex(channel.Path), 0))
            .ToList();
        var bindingBytes = PhyreAnimationBinding.Build(
            bindings, constantBindings,
            (key, target) => slots.GetValueOrDefault((key, target), -1));

        var groups = new List<PhyreGroupContents>();
        var arrayFixups = fixups.Arrays
            .Where(value => !Rebuilt(value.SourceListIndex)).ToList();
        var pointerFixups = fixups.Pointers
            .Where(value => !Rebuilt(value.SourceListIndex)
                && !(value.SourceListIndex == clipGroup && ClipPointer(value, classes)))
            .ToList();
        var pointerArrays = fixups.PointerArrays
            .Where(value => !(value.SourceListIndex == clipGroup)).ToList();

        bool Rebuilt(int groupIndex) =>
            groupIndex == channelGroup || groupIndex == timesGroup
            || groupIndex == constantGroup || groupIndex == bindingGroup;

        foreach (var group in cut.Metadata.InstanceGroups)
        {
            if (group.Index == channelGroup)
            {
                groups.Add(Channels(classes, animated, targets, keyTypes, nodeType,
                    group.Index, timesGroup, arrayFixups, pointerFixups));
            }
            else if (group.Index == timesGroup)
            {
                groups.Add(Times(classes, animated, group.Index, arrayFixups));
            }
            else if (group.Index == constantGroup)
            {
                groups.Add(Constants(classes, constants, targets, keyTypes, nodeType,
                    group.Index, arrayFixups, pointerFixups));
            }
            else if (group.Index == bindingGroup)
            {
                groups.Add(new PhyreGroupContents(
                    group.ClassName ?? "",
                    new[]
                    {
                        new PhyreObjectContents(
                            group.ClassName ?? "",
                            new Dictionary<string, byte[]>(StringComparer.Ordinal),
                            bindingBytes.AsMemory(DeclaredSize(classes, "PAnimationClipBinding"))),
                    },
                    ReadOnlyMemory<byte>.Empty));
            }
            else if (group.Index == clipGroup)
            {
                groups.Add(Clip(data, classes, group, clip, animated.Count, constants.Count,
                    channelGroup, constantGroup, bindingGroup, arrayFixups, pointerFixups,
                    pointerArrays, fixups));
            }
            else
            {
                var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
                var stored = data.GetGroupObjectsData(group.Index).ToArray();
                var objects = new List<PhyreObjectContents>();
                for (var id = 0u; id < group.Count; id++)
                {
                    objects.Add(PhyreObjectWriter.ReadObject(
                        stored.AsSpan((int)(id * size), size), group.ClassName ?? "", classes));
                }
                groups.Add(new PhyreGroupContents(
                    group.ClassName ?? "",
                    objects,
                    group.ArraysSize == 0
                        ? ReadOnlyMemory<byte>.Empty
                        : data.GetArrayData(group.Index, 0, group.ArraysSize)));
            }
        }

        return PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            cut.Metadata.Types,
            groups,
            new PhyreFixupSet(pointerArrays, pointerFixups, arrayFixups,
                fixups.UserFixups, fixups.VramDataOffset),
            fixups.UserFixups,
            cut.HeaderClasses,
            cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..],
            PhyreSchemaProfile.Cs1Native,
            classes.Select(value => value.Name).ToArray()));
    }

    /// <summary>A channel holding one value throughout is stored as a constant.</summary>
    private static bool IsConstant(CpuAnimationChannel channel)
    {
        if (channel.Values.Count <= 1) return true;
        var first = channel.Values[0];
        foreach (var value in channel.Values)
        {
            if (System.Numerics.Vector4.Distance(value, first) > 1e-7f) return false;
        }
        return true;
    }

    private static int KeyIndex(CpuAnimationPath path) => path switch
    {
        CpuAnimationPath.Rotation => 0,
        CpuAnimationPath.Translation => 1,
        _ => 2,
    };

    private static PhyreAnimationChannelBinding Binding(
        CpuAnimationChannel channel,
        IReadOnlyDictionary<string, (int Index, byte[] Body)> targets,
        int keyIndex,
        int keyCount)
        => new(
            channel.Interpolation == CpuAnimationInterpolation.Step ? 2 : 1,
            keyIndex,
            targets[channel.TargetName].Index,
            keyCount,
            keyIndex == 0 ? 4 : 3);

    /// <summary>Each bone the set targets, with the member bytes that name it.</summary>
    private static Dictionary<string, (int Index, byte[] Body)> Targets(
        PhyreClusterData data, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes, int targetGroup)
    {
        var found = new Dictionary<string, (int, byte[])>(StringComparer.Ordinal);
        var group = data.Metadata.InstanceGroups[targetGroup];
        var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
        var objects = data.GetGroupObjectsData(targetGroup).Span;
        var nameAt = Member(classes, "PAnimationChannelTarget", "m_name").ValueOffset;
        for (var id = 0u; id < group.Count; id++)
        {
            var name = ArrayString(data, fixups, targetGroup, id, nameAt);
            if (name is null) continue;
            found.TryAdd(name, ((int)id, objects.Slice((int)id * size, size).ToArray()));
        }
        return found;
    }

    /// <summary>The slot the set gives each key type and target.</summary>
    private static Dictionary<(int Key, int Target), int> Slots(
        PhyreClusterData data, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes, int slotGroup,
        IReadOnlyDictionary<string, (int Index, byte[] Body)> targets)
    {
        var found = new Dictionary<(int, int), int>();
        var group = data.Metadata.InstanceGroups[slotGroup];
        var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
        var objects = data.GetGroupObjectsData(slotGroup).Span;
        var keyAt = Member(classes, "PAnimationSlotListIndex", "m_animKeyType").ValueOffset;
        var targetAt = Member(classes, "PAnimationSlotListIndex", "m_targetIndex").ValueOffset;
        for (var id = 0u; id < group.Count; id++)
        {
            var named = UserFixupText(fixups, slotGroup, id, keyAt);
            var key = named switch { "Rotation" => 0, "Translation" => 1, "Scale" => 2, _ => -1 };
            if (key < 0) continue;
            var target = (int)BitConverter.ToUInt32(
                objects[((int)id * size + (int)targetAt)..]);
            found.TryAdd((key, target), (int)id);
        }
        return found;
    }

    private static PhyreGroupContents Channels(
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<CpuAnimationChannel> animated,
        IReadOnlyDictionary<string, (int Index, byte[] Body)> targets,
        IReadOnlyDictionary<CpuAnimationPath, uint> keyTypes,
        uint nodeType,
        int groupIndex, int timesGroup,
        List<PhyreArrayFixup> arrays, List<PhyrePointerFixup> pointers)
    {
        var chain = Chain(classes, "PAnimationChannel");
        var region = new MemoryStream();
        var objects = new List<PhyreObjectContents>();
        for (var id = 0; id < animated.Count; id++)
        {
            var channel = animated[id];
            var members = TargetMembers(classes, "PAnimationChannel", targets[channel.TargetName].Body);
            members[Name(chain, "m_interp")] =
                BitConverter.GetBytes(channel.Interpolation == CpuAnimationInterpolation.Step ? 2 : 1);
            members[Name(chain, "m_keyCount")] = BitConverter.GetBytes((uint)channel.Values.Count);
            members[Name(chain, "m_valueKeys")] =
                BitConverter.GetBytes((uint)channel.Values.Count).Concat(new byte[4]).ToArray();
            objects.Add(new PhyreObjectContents(
                "PAnimationChannel", members, ReadOnlyMemory<byte>.Empty));

            // Its name, then its keys, each written where the object points.
            arrays.Add(new PhyreArrayFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_name"), 0, (uint)region.Length));
            region.Write(Encoding.UTF8.GetBytes(channel.TargetName + "\0"));
            while (region.Length % 4 != 0) region.WriteByte(0);

            var values = Values(channel);
            arrays.Add(new PhyreArrayFixup(groupIndex, (uint)id,
                0x80000000u | (Offset(chain, "m_valueKeys") + sizeof(uint)),
                (uint)(values.Length / sizeof(float)), (uint)region.Length));
            region.Write(values);

            pointers.Add(new PhyrePointerFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_instanceObjectType"), 0, 0, 0, 0, nodeType));
            pointers.Add(new PhyrePointerFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_keyType"), 0, 0, 0, 0, keyTypes[channel.Path]));
            pointers.Add(new PhyrePointerFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_times"), (uint)timesGroup, (uint)id, 0, 0, null));
        }
        return new PhyreGroupContents("PAnimationChannel", objects, region.ToArray());
    }

    private static PhyreGroupContents Times(
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<CpuAnimationChannel> animated,
        int groupIndex, List<PhyreArrayFixup> arrays)
    {
        var chain = Chain(classes, "PAnimationChannelTimes");
        var region = new MemoryStream();
        var objects = new List<PhyreObjectContents>();
        for (var id = 0; id < animated.Count; id++)
        {
            var times = animated[id].Times;
            var members = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [Name(chain, "m_keyCount")] = BitConverter.GetBytes((uint)times.Count),
                [Name(chain, "m_timeKeys")] =
                    BitConverter.GetBytes((uint)times.Count).Concat(new byte[4]).ToArray(),
            };
            objects.Add(new PhyreObjectContents(
                "PAnimationChannelTimes", members, ReadOnlyMemory<byte>.Empty));

            arrays.Add(new PhyreArrayFixup(groupIndex, (uint)id,
                0x80000000u | (Offset(chain, "m_timeKeys") + sizeof(uint)),
                (uint)times.Count, (uint)region.Length));
            foreach (var time in times) region.Write(BitConverter.GetBytes(time));
        }
        return new PhyreGroupContents("PAnimationChannelTimes", objects, region.ToArray());
    }

    private static PhyreGroupContents Constants(
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<CpuAnimationChannel> constants,
        IReadOnlyDictionary<string, (int Index, byte[] Body)> targets,
        IReadOnlyDictionary<CpuAnimationPath, uint> keyTypes,
        uint nodeType,
        int groupIndex, List<PhyreArrayFixup> arrays, List<PhyrePointerFixup> pointers)
    {
        var chain = Chain(classes, "PAnimationConstantChannel");
        var region = new MemoryStream();
        var objects = new List<PhyreObjectContents>();
        for (var id = 0; id < constants.Count; id++)
        {
            var channel = constants[id];
            var members = TargetMembers(classes, "PAnimationConstantChannel", targets[channel.TargetName].Body);
            members[Name(chain, "m_interp")] =
                BitConverter.GetBytes(channel.Interpolation == CpuAnimationInterpolation.Step ? 2 : 1);
            var value = channel.Values.Count == 0 ? default : channel.Values[0];
            members[Name(chain, "m_value")] = BitConverter.GetBytes(value.X)
                .Concat(BitConverter.GetBytes(value.Y))
                .Concat(BitConverter.GetBytes(value.Z))
                .Concat(BitConverter.GetBytes(value.W)).ToArray();
            objects.Add(new PhyreObjectContents(
                "PAnimationConstantChannel", members, ReadOnlyMemory<byte>.Empty));

            arrays.Add(new PhyreArrayFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_name"), 0, (uint)region.Length));
            region.Write(Encoding.UTF8.GetBytes(channel.TargetName + "\0"));
            while (region.Length % 4 != 0) region.WriteByte(0);

            pointers.Add(new PhyrePointerFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_instanceObjectType"), 0, 0, 0, 0, nodeType));
            pointers.Add(new PhyrePointerFixup(groupIndex, (uint)id,
                0x80000000u | Offset(chain, "m_keyType"), 0, 0, 0, 0, keyTypes[channel.Path]));
        }
        return new PhyreGroupContents("PAnimationConstantChannel", objects, region.ToArray());
    }

    private static PhyreGroupContents Clip(
        PhyreClusterData data,
        IReadOnlyList<PhyreClassDescriptor> classes,
        PhyreInstanceGroup group,
        CpuAnimationClip clip,
        int channelCount, int constantCount,
        int channelGroup, int constantGroup, int bindingGroup,
        List<PhyreArrayFixup> arrays, List<PhyrePointerFixup> pointers,
        List<PhyreArrayFixup> pointerArrays, PhyreFixupSet original)
    {
        var chain = Chain(classes, "PAnimationClip");
        var size = (int)(group.ObjectsSize / group.Count);
        var stored = data.GetGroupObjectsData(group.Index).ToArray();
        var members = PhyreObjectWriter
            .ReadObject(stored.AsSpan(0, size), "PAnimationClip", classes)
            .Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        members[Name(chain, "m_channels")] =
            BitConverter.GetBytes((uint)channelCount).Concat(new byte[4]).ToArray();
        members[Name(chain, "m_constantChannels")] =
            BitConverter.GetBytes((uint)constantCount).Concat(new byte[4]).ToArray();
        members[Name(chain, "m_constantChannelStartTime")] = BitConverter.GetBytes(clip.StartTime);
        members[Name(chain, "m_constantChannelEndTime")] = BitConverter.GetBytes(clip.EndTime);

        var region = new MemoryStream();
        arrays.Add(new PhyreArrayFixup(group.Index, 0,
            0x80000000u | Offset(chain, "m_name"), 0, 0));
        region.Write(Encoding.UTF8.GetBytes(clip.Name + "\0"));
        while (region.Length % 4 != 0) region.WriteByte(0);

        var channelsAt = 0x80000000u | (Offset(chain, "m_channels") + sizeof(uint));
        for (var id = 0; id < channelCount; id++)
        {
            pointers.Add(new PhyrePointerFixup(
                group.Index, 0, channelsAt, (uint)channelGroup, (uint)id, 0, (uint)id, null));
        }
        if (channelCount != 0)
        {
            pointerArrays.Add(new PhyreArrayFixup(
                group.Index, 0, channelsAt, (uint)channelCount, 0));
        }
        if (constantCount != 0)
        {
            pointers.Add(new PhyrePointerFixup(
                group.Index, 0,
                0x80000000u | (Offset(chain, "m_constantChannels") + sizeof(uint)),
                (uint)constantGroup, 0, 0, 0, null));
        }
        pointers.Add(new PhyrePointerFixup(
            group.Index, 0, 0x80000000u | Offset(chain, "m_binding"),
            (uint)bindingGroup, 0, 0, 0, null));

        return new PhyreGroupContents(
            "PAnimationClip",
            new[] { new PhyreObjectContents("PAnimationClip", members, ReadOnlyMemory<byte>.Empty) },
            region.ToArray());
    }

    /// <summary>Whether a clip pointer is one this rewrites rather than keeps.</summary>
    private static bool ClipPointer(PhyrePointerFixup fixup, IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var chain = Chain(classes, "PAnimationClip");
        var offset = fixup.SourceOffsetOrMember & 0x7fffffffu;
        return offset == Offset(chain, "m_channels") + sizeof(uint)
            || offset == Offset(chain, "m_constantChannels") + sizeof(uint)
            || offset == Offset(chain, "m_binding");
    }

    /// <summary>The target's own naming fields, copied onto a channel.</summary>
    private static Dictionary<string, byte[]> TargetMembers(
        IReadOnlyList<PhyreClassDescriptor> classes, string className, byte[] targetBody)
    {
        var target = Chain(classes, "PAnimationChannelTarget");
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "m_type", "m_index" })
        {
            var member = target.First(value => value.Name == name);
            members[name] = targetBody
                .AsSpan((int)member.ValueOffset, (int)member.Size).ToArray();
        }
        return members;
    }

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

    private static IReadOnlyList<PhyreDataMember> Chain(
        IReadOnlyList<PhyreClassDescriptor> classes, string className)
        => PhyreObjectWriter
            .Chain(classes.First(value => value.Name == className), classes).ToList();

    private static PhyreDataMember Member(
        IReadOnlyList<PhyreClassDescriptor> classes, string className, string member)
        => Chain(classes, className).First(value => value.Name == member);

    private static string Name(IReadOnlyList<PhyreDataMember> chain, string member)
        => chain.First(value => value.Name == member).Name;

    private static uint Offset(IReadOnlyList<PhyreDataMember> chain, string member)
        => chain.First(value => value.Name == member).ValueOffset;

    private static int DeclaredSize(IReadOnlyList<PhyreClassDescriptor> classes, string className)
        => (int)classes.First(value => value.Name == className).Size;

    private static uint UserFixup(PhyreFixupSet fixups, string text)
    {
        for (var index = 0; index < fixups.UserFixups.Count; index++)
        {
            if (string.Equals(fixups.UserFixups[index].Text, text, StringComparison.Ordinal))
            {
                return (uint)fixups.UserFixups[index].Id;
            }
        }
        throw new InvalidPhyreException($"This cluster names no '{text}' type.");
    }

    private static string? UserFixupText(
        PhyreFixupSet fixups, int groupIndex, uint objectId, uint offset)
    {
        var pointer = fixups.Pointers.FirstOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && (value.SourceOffsetOrMember & 0x7fffffffu) == offset && !value.IsClassDataMember);
        if (pointer?.UserFixupId is not { } id) return null;
        return fixups.UserFixups.FirstOrDefault(value => value.Id == id)?.Text;
    }

    private static string? ArrayString(
        PhyreClusterData data, PhyreFixupSet fixups, int groupIndex, uint objectId, uint offset)
    {
        var fixup = fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && (value.SourceOffsetOrMember & 0x7fffffffu) == offset);
        if (fixup is null) return null;
        var group = data.Metadata.InstanceGroups[groupIndex];
        var span = data.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = span.IndexOf((byte)0);
        return zero < 0 ? null : Encoding.UTF8.GetString(span[..zero]);
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
