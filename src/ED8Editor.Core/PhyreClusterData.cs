namespace ED8Editor.Core;

public sealed class PhyreClusterData
{
    private readonly long[] _groupOffsets;

    public PhyreClusterData(ReadOnlyMemory<byte> data, PhyreClusterMetadata metadata, PhyreFixupSet fixups)
    {
        Data = data;
        Metadata = metadata;
        Fixups = fixups;
        _groupOffsets = new long[metadata.InstanceGroups.Count];
        var offset = metadata.Header.ObjectDataOffset;
        for (var index = 0; index < _groupOffsets.Length; index++)
        {
            _groupOffsets[index] = offset;
            offset = checked(offset + metadata.InstanceGroups[index].Size);
        }

        if (offset != metadata.Header.ObjectDataOffset + metadata.TotalDataSize)
        {
            throw new InvalidDataException("Phyre instance group sizes do not match the object data size.");
        }
    }

    public ReadOnlyMemory<byte> Data { get; }
    public PhyreClusterMetadata Metadata { get; }
    public PhyreFixupSet Fixups { get; }

    public ReadOnlyMemory<byte> GetObject(int groupIndex, uint objectId)
    {
        var group = GetGroup(groupIndex);
        if (objectId >= group.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        if (group.ClassId == 0 || group.ClassId > Metadata.Classes.Count)
        {
            throw new InvalidDataException($"Phyre instance group {groupIndex} has no local class descriptor.");
        }

        var objectSize = Metadata.Classes[checked((int)group.ClassId - 1)].Size;
        var relativeOffset = checked((long)objectId * objectSize);
        if (relativeOffset + objectSize > group.ObjectsSize)
        {
            throw new InvalidDataException($"Phyre object {objectId} exceeds instance group {groupIndex}.");
        }

        return Slice(checked(_groupOffsets[groupIndex] + relativeOffset), objectSize, "object");
    }

    public ReadOnlyMemory<byte> GetArrayData(int groupIndex, uint relativeOffset, uint size)
    {
        var group = GetGroup(groupIndex);
        if ((ulong)relativeOffset + size > group.ArraysSize)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeOffset));
        }

        return Slice(checked(_groupOffsets[groupIndex] + group.ObjectsSize + relativeOffset), size, "array data");
    }

    public ReadOnlyMemory<byte> GetVramData(uint relativeOffset, uint size)
    {
        return Slice(checked(Fixups.VramDataOffset + relativeOffset), size, "VRAM data");
    }

    public PhyrePointerFixup? FindPointer(int sourceListIndex, uint sourceObjectId, uint sourceOffset)
    {
        return Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == sourceListIndex
            && value.SourceObjectId == sourceObjectId
            && !value.IsClassDataMember
            && value.SourceOffset == sourceOffset);
    }

    private PhyreInstanceGroup GetGroup(int index)
    {
        if ((uint)index >= Metadata.InstanceGroups.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Metadata.InstanceGroups[index];
    }

    private ReadOnlyMemory<byte> Slice(long offset, uint size, string kind)
    {
        if (offset < 0 || offset > Data.Length || size > Data.Length - offset)
        {
            throw new InvalidDataException($"Phyre {kind} lies outside the cluster.");
        }

        return Data.Slice(checked((int)offset), checked((int)size));
    }
}
