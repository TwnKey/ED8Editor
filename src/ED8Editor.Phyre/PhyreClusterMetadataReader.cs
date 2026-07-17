using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreClusterMetadataReader : IPhyreClusterMetadataReader
{
    public const uint LittleEndianMarker = 0x50485952;
    public const uint BigEndianMarker = 0x52594850;
    private const int ClusterHeaderFieldCount = 17;
    private const int ClassDescriptorSize = 36;
    private const int DataMemberSize = 24;
    private const uint MaximumReasonableCount = 1_000_000;

    public PhyreClusterMetadata Read(ReadOnlyMemory<byte> data)
    {
        if (data.Length < ClusterHeaderFieldCount * sizeof(uint))
        {
            throw new InvalidPhyreException("Phyre cluster is shorter than its header.");
        }

        var marker = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[..4]);
        var bigEndian = marker switch
        {
            LittleEndianMarker => false,
            BigEndianMarker => true,
            _ => throw new InvalidPhyreException($"Unknown Phyre marker 0x{marker:X8}."),
        };
        var reader = new PhyreBinaryReader(data, bigEndian);
        var headerMarker = reader.ReadUInt32();
        var headerSize = reader.ReadUInt32();
        var packedNamespaceSize = reader.ReadUInt32();
        var platformId = reader.ReadUInt32();
        var instanceListCount = reader.ReadUInt32();
        var arrayFixupSize = reader.ReadUInt32();
        var arrayFixupCount = reader.ReadUInt32();
        var pointerFixupSize = reader.ReadUInt32();
        var pointerFixupCount = reader.ReadUInt32();
        var pointerArrayFixupSize = reader.ReadUInt32();
        var pointerArrayFixupCount = reader.ReadUInt32();
        var pointersInArraysCount = reader.ReadUInt32();
        var userFixupCount = reader.ReadUInt32();
        var userFixupDataSize = reader.ReadUInt32();
        var totalDataSize = reader.ReadUInt32();
        var headerClassInstanceCount = reader.ReadUInt32();
        var headerClassChildCount = reader.ReadUInt32();

        EnsureCount(instanceListCount, "instance-list");
        reader.Seek(headerSize);
        reader.Skip(2 * sizeof(uint));
        var typeCount = reader.ReadUInt32();
        var classCount = reader.ReadUInt32();
        var dataMemberCount = reader.ReadUInt32();
        var stringTableSize = reader.ReadUInt32();
        reader.Skip(2 * sizeof(uint));
        EnsureCount(typeCount, "type");
        EnsureCount(classCount, "class");
        EnsureCount(dataMemberCount, "data-member");

        var typeOffsets = new uint[typeCount];
        for (var index = 0; index < typeOffsets.Length; index++)
        {
            typeOffsets[index] = reader.ReadUInt32();
        }

        var descriptorOffset = reader.Position;
        var labelOffset = checked(descriptorOffset + (long)classCount * ClassDescriptorSize + (long)dataMemberCount * DataMemberSize);
        if (labelOffset > data.Length || labelOffset + stringTableSize > data.Length)
        {
            throw new InvalidPhyreException("Phyre namespace tables lie outside the cluster.");
        }

        var types = typeOffsets.Select(offset => ReadNamespaceString(reader, labelOffset, stringTableSize, offset)).ToArray();
        reader.Seek(descriptorOffset);
        var classes = new PhyreClassDescriptor[classCount];
        var classMemberCounts = new uint[classCount];
        for (var index = 0; index < classes.Length; index++)
        {
            var superClassId = reader.ReadUInt32();
            var sizeAndAlignment = reader.ReadUInt32();
            var nameOffset = reader.ReadUInt32();
            var memberCount = reader.ReadUInt32();
            var offsetFromParent = unchecked((int)reader.ReadUInt32());
            var offsetToBase = unchecked((int)reader.ReadUInt32());
            var offsetToBaseInAllocatedBlock = unchecked((int)reader.ReadUInt32());
            var flags = reader.ReadUInt32();
            var defaultBufferOffset = reader.ReadUInt32();
            classMemberCounts[index] = memberCount;
            classes[index] = new PhyreClassDescriptor(
                index,
                ReadNamespaceString(reader, labelOffset, stringTableSize, nameOffset),
                superClassId,
                sizeAndAlignment & 0x0fffffff,
                1u << checked((int)(sizeAndAlignment >> 28)),
                memberCount,
                offsetFromParent,
                offsetToBase,
                offsetToBaseInAllocatedBlock,
                flags,
                defaultBufferOffset,
                Array.Empty<PhyreDataMember>());
        }

        var globalMemberIndex = 0;
        for (var classIndex = 0; classIndex < classes.Length; classIndex++)
        {
            var members = new PhyreDataMember[classMemberCounts[classIndex]];
            for (var classMemberIndex = 0; classMemberIndex < members.Length; classMemberIndex++, globalMemberIndex++)
            {
                var nameOffset = reader.ReadUInt32();
                var typeId = reader.ReadUInt32();
                var valueOffset = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var flags = reader.ReadUInt32();
                var fixedArraySize = reader.ReadUInt32();
                members[classMemberIndex] = new PhyreDataMember(
                    globalMemberIndex,
                    classMemberIndex,
                    ReadNamespaceString(reader, labelOffset, stringTableSize, nameOffset),
                    typeId,
                    ResolveTypeName(typeId, types, classes),
                    valueOffset,
                    size,
                    flags,
                    fixedArraySize);
            }

            classes[classIndex] = classes[classIndex] with { Members = members };
        }

        var instanceHeadersOffset = checked((long)headerSize + packedNamespaceSize);
        reader.Seek(instanceHeadersOffset);
        var groups = new PhyreInstanceGroup[instanceListCount];
        for (var index = 0; index < groups.Length; index++)
        {
            var classId = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var objectsSize = reader.ReadUInt32();
            var arraysSize = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var arrayFixups = reader.ReadUInt32();
            var pointerFixups = reader.ReadUInt32();
            var pointerArrayFixups = reader.ReadUInt32();
            groups[index] = new PhyreInstanceGroup(
                index,
                classId,
                classId > 0 && classId <= classes.Length ? classes[classId - 1].Name : null,
                count,
                size,
                objectsSize,
                arraysSize,
                arrayFixups,
                pointerFixups,
                pointerArrayFixups);
        }

        var objectDataOffset = checked(instanceHeadersOffset + (long)instanceListCount * 9 * sizeof(uint));
        var header = new PhyreClusterHeader(
            headerSize,
            packedNamespaceSize,
            arrayFixupSize,
            arrayFixupCount,
            pointerFixupSize,
            pointerFixupCount,
            pointerArrayFixupSize,
            pointerArrayFixupCount,
            pointersInArraysCount,
            userFixupCount,
            userFixupDataSize,
            headerClassInstanceCount,
            headerClassChildCount,
            instanceHeadersOffset,
            objectDataOffset);
        return new PhyreClusterMetadata(marker, bigEndian, platformId, totalDataSize, types, classes, groups, header);
    }

    private static string? ResolveTypeName(uint typeId, IReadOnlyList<string> types, IReadOnlyList<PhyreClassDescriptor> classes)
    {
        if (typeId < types.Count)
        {
            return types[checked((int)typeId)];
        }

        // Class IDs in packed namespaces are one-based after the primitive type table.
        var classId = (long)typeId - types.Count - 1L;
        return classId >= 0 && classId < classes.Count ? classes[checked((int)classId)].Name : null;
    }

    private static string ReadNamespaceString(PhyreBinaryReader reader, long labelOffset, uint tableSize, uint offset)
    {
        if (offset >= tableSize)
        {
            throw new InvalidPhyreException("Phyre namespace string offset lies outside its table.");
        }

        return reader.ReadAsciiZ(checked((int)(labelOffset + offset)), checked((int)(tableSize - offset)));
    }

    private static void EnsureCount(uint count, string kind)
    {
        if (count > MaximumReasonableCount)
        {
            throw new InvalidPhyreException($"Phyre {kind} count {count} is unreasonable.");
        }
    }
}
