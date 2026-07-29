using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// A Phyre cluster cut into the sections it is made of, in file order.
///
/// The sections tile the file exactly — the study measured zero unexplained
/// bytes on both a texture and a character model — so this is the map a writer
/// has to fill. Keeping each section as its own bytes gives two things at once:
/// a rebuild that is byte-identical by construction, and a place to replace one
/// section at a time as each one learns to be written from data instead of
/// copied.
/// </summary>
public sealed record PhyreClusterSections(
    PhyreClusterMetadata Metadata,
    ReadOnlyMemory<byte> Header,
    ReadOnlyMemory<byte> PackedNamespace,
    ReadOnlyMemory<byte> InstanceHeaders,
    ReadOnlyMemory<byte> ObjectData,
    ReadOnlyMemory<byte> UserFixupData,
    ReadOnlyMemory<byte> UserFixupDescriptors,
    ReadOnlyMemory<byte> HeaderClasses,
    ReadOnlyMemory<byte> PointerArrayFixups,
    ReadOnlyMemory<byte> PointerFixups,
    ReadOnlyMemory<byte> ArrayFixups,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>The sections, in the order the file lays them out.</summary>
    public IReadOnlyList<(string Name, ReadOnlyMemory<byte> Bytes)> InOrder => new[]
    {
        ("header", Header),
        ("packed namespace", PackedNamespace),
        ("instance headers", InstanceHeaders),
        ("object data", ObjectData),
        ("user fixup data", UserFixupData),
        ("user fixup descriptors", UserFixupDescriptors),
        ("header classes", HeaderClasses),
        ("pointer-array fixups", PointerArrayFixups),
        ("pointer fixups", PointerFixups),
        ("array fixups", ArrayFixups),
        ("payload", Payload),
    };

    public byte[] Compose()
    {
        var total = InOrder.Sum(section => section.Bytes.Length);
        var output = new byte[total];
        var cursor = 0;
        foreach (var (_, bytes) in InOrder)
        {
            bytes.Span.CopyTo(output.AsSpan(cursor));
            cursor += bytes.Length;
        }
        return output;
    }
}

/// <summary>
/// Cuts a cluster into <see cref="PhyreClusterSections"/> and puts it back
/// together.
/// </summary>
public static class PhyreClusterSectionReader
{
    /// <summary>Nine words describe each instance group.</summary>
    private const int InstanceHeaderSize = 9 * sizeof(uint);

    /// <summary>A user fixup is described by its type, its size and its offset.</summary>
    private const int UserFixupDescriptorSize = 3 * sizeof(uint);

    public static PhyreClusterSections Read(ReadOnlyMemory<byte> cluster)
    {
        var metadata = new PhyreClusterMetadataReader().Read(cluster);
        var header = metadata.Header;
        var namespaceOffset = checked((int)header.Size);
        var instanceHeadersOffset = checked((int)header.InstanceHeadersOffset);
        var objectDataOffset = checked((int)header.ObjectDataOffset);
        var userDataOffset = checked(objectDataOffset + (int)metadata.TotalDataSize);
        var userDescriptorOffset = checked(userDataOffset + (int)header.UserFixupDataSize);
        var headerClassesOffset = checked(
            userDescriptorOffset + (int)header.UserFixupCount * UserFixupDescriptorSize);
        var headerClassesSize = checked(
            (int)header.HeaderClassInstanceCount * sizeof(uint)
            + (int)header.HeaderClassChildCount * 16);
        var pointerArrayOffset = checked(headerClassesOffset + headerClassesSize);
        var pointerOffset = checked(pointerArrayOffset + (int)header.PointerArrayFixupSize);
        var arrayOffset = checked(pointerOffset + (int)header.PointerFixupSize);
        var payloadOffset = checked(arrayOffset + (int)header.ArrayFixupSize);
        if (payloadOffset > cluster.Length)
        {
            throw new InvalidPhyreException(
                $"The cluster's sections run to {payloadOffset}, past its {cluster.Length} bytes.");
        }

        return new PhyreClusterSections(
            metadata,
            cluster[..namespaceOffset],
            cluster[namespaceOffset..instanceHeadersOffset],
            cluster[instanceHeadersOffset..objectDataOffset],
            cluster[objectDataOffset..userDataOffset],
            cluster[userDataOffset..userDescriptorOffset],
            cluster[userDescriptorOffset..headerClassesOffset],
            cluster[headerClassesOffset..pointerArrayOffset],
            cluster[pointerArrayOffset..pointerOffset],
            cluster[pointerOffset..arrayOffset],
            cluster[arrayOffset..payloadOffset],
            cluster[payloadOffset..]);
    }

    /// <summary>
    /// The instance-group headers, written from the groups themselves. The sixth
    /// word is the one the reader does not model, so it is carried over from the
    /// bytes that were read.
    /// </summary>
    public static byte[] WriteInstanceHeaders(
        IReadOnlyList<PhyreInstanceGroup> groups,
        ReadOnlyMemory<byte> original)
    {
        var output = new byte[groups.Count * InstanceHeaderSize];
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var record = output.AsSpan(index * InstanceHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, group.ClassId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], group.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], group.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], group.ObjectsSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], group.ArraysSize);
            var unmodelled = original.Length >= (index + 1) * InstanceHeaderSize
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    original.Span[(index * InstanceHeaderSize + 20)..])
                : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], unmodelled);
            BinaryPrimitives.WriteUInt32LittleEndian(record[24..], group.ArrayFixupCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record[28..], group.PointerFixupCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record[32..], group.PointerArrayFixupCount);
        }
        return output;
    }
}
