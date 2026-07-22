using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreAnimationReader
{
    public CpuAnimationClip Read(string assetId, ReadOnlyMemory<byte> data)
        => ReadCore(assetId, data, requireTransformChannels: true)!;

    public CpuAnimationClip? ReadEmbeddedSceneAnimation(string assetId, ReadOnlyMemory<byte> data)
        => ReadCore(assetId, data, requireTransformChannels: false);

    private static CpuAnimationClip? ReadCore(
        string assetId,
        ReadOnlyMemory<byte> data,
        bool requireTransformChannels)
    {
        if (string.IsNullOrWhiteSpace(assetId)) throw new ArgumentException("Animation asset ID is required.", nameof(assetId));
        var cluster = new PhyreClusterReader().Read(data);
        var clipGroup = FindRequiredGroup(cluster, "PAnimationClip");
        if (clipGroup.Group.Count != 1)
            throw new InvalidPhyreException($"Animation asset '{assetId}' contains {clipGroup.Group.Count} clips instead of one.");

        var clipObject = cluster.GetObject(clipGroup.Index, 0).Span;
        var animatedCount = ReadUInt32(clipObject, 0x04, cluster.Metadata.IsBigEndian);
        var constantCount = ReadUInt32(clipObject, 0x0c, cluster.Metadata.IsBigEndian);
        var clipName = ReadArrayString(cluster, clipGroup.Index, 0, 0x1c) ?? assetId;
        var startTime = ReadSingle(clipObject, 0x14, cluster.Metadata.IsBigEndian);
        var endTime = ReadSingle(clipObject, 0x18, cluster.Metadata.IsBigEndian);
        var channels = new List<CpuAnimationChannel>();

        if (animatedCount > 0)
        {
            var channelGroup = FindRequiredGroup(cluster, "PAnimationChannel");
            var timesGroup = FindRequiredGroup(cluster, "PAnimationChannelTimes");
            var animatedPointers = cluster.Fixups.Pointers
                .Where(value => value.SourceListIndex == clipGroup.Index && value.SourceObjectId == 0
                    && !value.IsClassDataMember && value.SourceOffset == 0x08
                    && value.UserFixupId is null && value.DestinationListIndex == channelGroup.Index)
                .OrderBy(value => value.ArrayIndex)
                .ToArray();
            if (animatedPointers.Length != animatedCount)
                throw new InvalidPhyreException(
                    $"Animation asset '{assetId}' declares {animatedCount} animated channels but references {animatedPointers.Length}.");
            foreach (var pointer in animatedPointers)
            {
                var channel = ReadAnimatedChannel(
                    cluster, channelGroup.Index, pointer.DestinationObjectId, timesGroup.Index);
                if (channel is not null) channels.Add(channel);
            }
        }

        if (constantCount > 0)
        {
            var constantGroup = FindRequiredGroup(cluster, "PAnimationConstantChannel");
            var pointer = RequirePointer(cluster, clipGroup.Index, 0, 0x10);
            if (pointer.UserFixupId is not null || pointer.DestinationListIndex != constantGroup.Index
                || (ulong)pointer.DestinationObjectId + constantCount > constantGroup.Group.Count)
                throw new InvalidPhyreException($"Animation asset '{assetId}' has an invalid constant-channel array.");
            var times = new[] { startTime, endTime };
            for (uint index = 0; index < constantCount; index++)
            {
                var channel = ReadConstantChannel(cluster, constantGroup.Index,
                    checked(pointer.DestinationObjectId + index), times);
                if (channel is not null) channels.Add(channel);
            }
        }

        if (channels.Count == 0)
        {
            if (requireTransformChannels)
                throw new InvalidPhyreException($"Animation asset '{assetId}' has no addressable transform channels.");
            return null;
        }
        var observedStart = channels.SelectMany(value => value.Times).DefaultIfEmpty(startTime).Min();
        var observedEnd = channels.SelectMany(value => value.Times).DefaultIfEmpty(endTime).Max();
        return new CpuAnimationClip(assetId, clipName,
            Math.Min(startTime, observedStart), Math.Max(endTime, observedEnd), channels);
    }

    private static CpuAnimationChannel? ReadAnimatedChannel(
        PhyreClusterData cluster, int channelGroupIndex, uint channelId, int timesGroupIndex)
    {
        var source = cluster.GetObject(channelGroupIndex, channelId).Span;
        var path = ReadPath(cluster, channelGroupIndex, channelId);
        if (path is null) return null;
        var name = ReadArrayString(cluster, channelGroupIndex, channelId, 0x14)
            ;
        if (name is null) return null;
        var interpolation = ReadInt32(source, 0x20, cluster.Metadata.IsBigEndian) == 2
            ? CpuAnimationInterpolation.Step
            : CpuAnimationInterpolation.Linear;
        var timesPointer = RequirePointer(cluster, channelGroupIndex, channelId, 0x24);
        if (timesPointer.UserFixupId is not null || timesPointer.DestinationListIndex != timesGroupIndex)
            throw new InvalidPhyreException($"Animation channel {channelId} has invalid timestamps.");
        var times = ReadTimes(cluster, timesGroupIndex, timesPointer.DestinationObjectId);
        var keyCount = ReadUInt32(source, 0x30, cluster.Metadata.IsBigEndian);
        if (keyCount != times.Count)
            throw new InvalidPhyreException($"Animation channel {channelId} has {keyCount} values for {times.Count} timestamps.");
        var width = path == CpuAnimationPath.Rotation ? 4 : 3;
        var valuesFixup = RequireArrayFixup(cluster, channelGroupIndex, channelId, 0x2c);
        var valueComponentCount = checked(keyCount * (uint)width);
        if (valuesFixup.Count < valueComponentCount)
            throw new InvalidPhyreException($"Animation channel {channelId} has an invalid value array width.");
        var floats = ReadFloatArray(cluster.GetArrayData(channelGroupIndex, valuesFixup.Offset,
            checked(valueComponentCount * sizeof(float))).Span, cluster.Metadata.IsBigEndian);
        var values = new Vector4[keyCount];
        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * width;
            values[index] = width == 4
                ? new Vector4(floats[offset], floats[offset + 1], floats[offset + 2], floats[offset + 3])
                : new Vector4(floats[offset], floats[offset + 1], floats[offset + 2], 0f);
        }
        return new CpuAnimationChannel(name, path.Value, interpolation, times, values);
    }

    private static CpuAnimationChannel? ReadConstantChannel(
        PhyreClusterData cluster, int groupIndex, uint objectId, IReadOnlyList<float> times)
    {
        var source = cluster.GetObject(groupIndex, objectId).Span;
        var path = ReadPath(cluster, groupIndex, objectId);
        if (path is null) return null;
        var name = ReadArrayString(cluster, groupIndex, objectId, 0x14)
            ;
        if (name is null) return null;
        var interpolation = ReadInt32(source, 0x20, cluster.Metadata.IsBigEndian) == 2
            ? CpuAnimationInterpolation.Step
            : CpuAnimationInterpolation.Linear;
        var value = new Vector4(
            ReadSingle(source, 0x24, cluster.Metadata.IsBigEndian),
            ReadSingle(source, 0x28, cluster.Metadata.IsBigEndian),
            ReadSingle(source, 0x2c, cluster.Metadata.IsBigEndian),
            ReadSingle(source, 0x30, cluster.Metadata.IsBigEndian));
        return new CpuAnimationChannel(name, path.Value, interpolation, times, new[] { value, value });
    }

    private static CpuAnimationPath? ReadPath(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var pointer = RequirePointer(cluster, groupIndex, objectId, 0x1c);
        if (pointer.UserFixupId is not { } userId || userId >= cluster.Fixups.UserFixups.Count)
            throw new InvalidPhyreException($"Animation channel {objectId} has no key-data type.");
        return cluster.Fixups.UserFixups[checked((int)userId)].Text switch
        {
            "Translation" => CpuAnimationPath.Translation,
            "Rotation" => CpuAnimationPath.Rotation,
            "Scale" => CpuAnimationPath.Scale,
            _ => null,
        };
    }

    private static IReadOnlyList<float> ReadTimes(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var source = cluster.GetObject(groupIndex, objectId).Span;
        var count = ReadUInt32(source, 0, cluster.Metadata.IsBigEndian);
        var fixup = RequireArrayFixup(cluster, groupIndex, objectId, 0x08);
        if (fixup.Count < count) throw new InvalidPhyreException(
            $"Timestamp array {objectId} has object count {count} and fixup count {fixup.Count}.");
        return ReadFloatArray(cluster.GetArrayData(groupIndex, fixup.Offset,
            checked(count * sizeof(float))).Span, cluster.Metadata.IsBigEndian);
    }

    private static float[] ReadFloatArray(ReadOnlySpan<byte> source, bool bigEndian)
    {
        if (source.Length % sizeof(float) != 0) throw new InvalidPhyreException("Float array is misaligned.");
        var values = new float[source.Length / sizeof(float)];
        for (var index = 0; index < values.Length; index++)
            values[index] = ReadSingle(source, index * sizeof(float), bigEndian);
        return values;
    }

    private static string? ReadArrayString(PhyreClusterData cluster, int groupIndex, uint objectId, uint sourceOffset)
    {
        var fixup = cluster.Fixups.Arrays.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && !value.IsClassDataMember && value.SourceOffset == sourceOffset);
        if (fixup is null) return null;
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        var data = cluster.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = data.IndexOf((byte)0);
        if (zero < 0) throw new InvalidPhyreException("Animation string is not zero terminated.");
        return Encoding.UTF8.GetString(data[..zero]);
    }

    private static PhyrePointerFixup RequirePointer(PhyreClusterData cluster, int groupIndex, uint objectId, uint offset)
        => cluster.Fixups.Pointers.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && SourceMatches(cluster, value, offset))
            ?? throw new InvalidPhyreException($"Missing animation pointer at {groupIndex}:{objectId}+0x{offset:X}.");

    private static PhyreArrayFixup RequireArrayFixup(PhyreClusterData cluster, int groupIndex, uint objectId, uint offset)
        => cluster.Fixups.Arrays.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && !value.IsClassDataMember && value.SourceOffset == offset)
            ?? throw new InvalidPhyreException($"Missing animation array at {groupIndex}:{objectId}+0x{offset:X}.");

    private static (int Index, PhyreInstanceGroup Group) FindRequiredGroup(PhyreClusterData cluster, string name)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
            if (cluster.Metadata.InstanceGroups[index].ClassName == name)
                return (index, cluster.Metadata.InstanceGroups[index]);
        throw new InvalidPhyreException($"Missing {name} instance group.");
    }

    private static bool SourceMatches(PhyreClusterData cluster, PhyreFixup fixup, uint offset)
    {
        if (!fixup.IsClassDataMember) return fixup.SourceOffset == offset;
        var member = cluster.Metadata.Classes.SelectMany(value => value.Members)
            .SingleOrDefault(value => value.Index == fixup.SourceMemberId);
        return member is not null && member.ValueOffset == offset;
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(source[offset..]) : BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(source[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static float ReadSingle(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => BitConverter.Int32BitsToSingle(ReadInt32(source, offset, bigEndian));
}
