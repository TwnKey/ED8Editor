using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes a whole cluster from what it is made of, rather than from the bytes it
/// was read as: the header from the counts, the type schema from the classes,
/// the group headers from the groups, the user fixups from their names, and the
/// fixup tables from the fixups.
///
/// Only two runs of bytes are still carried over as they were — the objects
/// themselves and the header-class section — because writing those means knowing
/// the layout of every class a model uses. Everything around them is generated,
/// which <see cref="PhyreAuthoringCheck"/> proves by rebuilding the game's own
/// clusters byte for byte.
/// </summary>
public static class PhyreClusterWriter
{
    private const int UserFixupDescriptorSize = 3 * sizeof(uint);

    public static byte[] Write(
        PhyreClusterMetadata metadata,
        PhyreFixupSet fixups,
        ReadOnlyMemory<byte> objectData,
        ReadOnlyMemory<byte> headerClasses,
        ReadOnlyMemory<byte> instanceHeaderSource,
        ReadOnlyMemory<byte> namespaceSource,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(fixups);

        var packedNamespace = PhyreNamespaceWriter.Write(
            metadata.Types,
            metadata.Classes,
            PhyreNamespaceWriter.ReadUnmodelledHeader(namespaceSource));
        var instanceHeaders = PhyreClusterSectionReader.WriteInstanceHeaders(
            metadata.InstanceGroups, instanceHeaderSource);
        var (userData, userDescriptors) = WriteUserFixups(fixups.UserFixups);
        var pointerArrays = PhyreFixupWriter.WriteArrays(
            fixups.PointerArrays, metadata.InstanceGroups);
        var pointers = PhyreFixupWriter.WritePointers(fixups.Pointers, metadata.InstanceGroups);
        var arrays = PhyreFixupWriter.WriteArrays(fixups.Arrays, metadata.InstanceGroups);

        var header = WriteHeader(
            metadata,
            packedNamespace.Length,
            objectData.Length,
            userData.Length,
            fixups.UserFixups.Count,
            pointerArrays.Length,
            pointers.Length,
            arrays.Length);

        var output = new MemoryStream();
        output.Write(header);
        output.Write(packedNamespace);
        output.Write(instanceHeaders);
        output.Write(objectData.Span);
        output.Write(userData);
        output.Write(userDescriptors);
        output.Write(headerClasses.Span);
        output.Write(pointerArrays);
        output.Write(pointers);
        output.Write(arrays);
        output.Write(payload.Span);
        return output.ToArray();
    }

    /// <summary>
    /// The names a cluster resolves at load time: their bytes, then a record per
    /// name saying what type reads it, how long it is and where it sits.
    /// </summary>
    private static (byte[] Data, byte[] Descriptors) WriteUserFixups(
        IReadOnlyList<PhyreUserFixup> userFixups)
    {
        var data = new MemoryStream();
        var descriptors = new byte[userFixups.Count * UserFixupDescriptorSize];
        for (var index = 0; index < userFixups.Count; index++)
        {
            var fixup = userFixups[index];
            // The offsets are the ones the cluster recorded: a name may be
            // shared, and rewriting them from the order alone would move it.
            var record = descriptors.AsSpan(index * UserFixupDescriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, fixup.TypeId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], fixup.DeclaredSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], fixup.DataOffset);
            if (fixup.DataOffset != data.Length) data.SetLength(fixup.DataOffset);
            data.Position = fixup.DataOffset;
            data.Write(fixup.Data.Span);
        }
        return (data.ToArray(), descriptors);
    }

    private static byte[] WriteHeader(
        PhyreClusterMetadata metadata,
        int packedNamespaceSize,
        int objectDataSize,
        int userDataSize,
        int userFixupCount,
        int pointerArraySize,
        int pointerSize,
        int arraySize)
    {
        var source = metadata.Header;
        var header = new byte[source.Size];
        var values = new uint[]
        {
            metadata.Marker,
            source.Size,
            (uint)packedNamespaceSize,
            metadata.PlatformId,
            (uint)metadata.InstanceGroups.Count,
            (uint)arraySize,
            source.ArrayFixupCount,
            (uint)pointerSize,
            source.PointerFixupCount,
            (uint)pointerArraySize,
            source.PointerArrayFixupCount,
            source.PointersInArraysCount,
            (uint)userFixupCount,
            (uint)userDataSize,
            (uint)objectDataSize,
            source.HeaderClassInstanceCount,
            source.HeaderClassChildCount,
        };
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(index * sizeof(uint)), values[index]);
        }
        // Past the seventeen words this project names, the header keeps whatever
        // the cluster had — the buffer sizes a texture uses, and the physics
        // engine identifier.
        return header;
    }

    /// <summary>
    /// Rewrites a cluster the strong way: its schema comes from
    /// <see cref="PhyreSchemaLibrary"/> rather than from the file, and its
    /// objects are written by <see cref="PhyreObjectWriter"/> from what they
    /// hold rather than copied.
    ///
    /// What is still carried over: the data of arrays, the header class section,
    /// the instance list headers and the GPU payload. Those are the pieces that
    /// have no structured form yet — so this says exactly how far authoring
    /// reaches, and no further.
    /// </summary>
    public static byte[] RebuildFromContents(ReadOnlyMemory<byte> cluster)
    {
        var sections = PhyreClusterSectionReader.Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, sections.Metadata);
        var data = new PhyreClusterReader().Read(cluster);
        var metadata = sections.Metadata;

        var classNames = metadata.Classes.Select(value => value.Name).ToArray();
        var descriptors = PhyreSchemaLibrary.Descriptors(metadata.Types, classNames);
        var packedNamespace = PhyreNamespaceWriter.Write(
            metadata.Types,
            descriptors,
            PhyreNamespaceWriter.ReadUnmodelledHeader(sections.PackedNamespace));

        // Only the objects are rewritten; whatever a group holds after them —
        // array data, and any padding the group carries — stays where it was.
        var objectData = sections.ObjectData.ToArray();
        var groupOffset = 0L;
        foreach (var group in metadata.InstanceGroups)
        {
            var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            if (group.Count != 0 && size != 0
                && group.ClassId != 0 && group.ClassId <= descriptors.Length)
            {
                var className = descriptors[(int)group.ClassId - 1].Name;
                var stored = data.GetGroupObjectsData(group.Index).Span;
                for (uint id = 0; id < group.Count; id++)
                {
                    var at = (int)(id * size);
                    var contents = PhyreObjectWriter.ReadObject(
                        stored.Slice(at, size), className, descriptors);
                    PhyreObjectWriter.WriteObject(contents, descriptors, size)
                        .CopyTo(objectData.AsSpan((int)groupOffset + at));
                }
            }
            groupOffset += group.Size;
        }

        var written = Write(
            metadata,
            fixups,
            objectData,
            sections.HeaderClasses,
            sections.InstanceHeaders,
            packedNamespace,
            sections.Payload);
        var named = 17 * sizeof(uint);
        sections.Header.Span[named..].CopyTo(written.AsSpan(named));
        return written;
    }

    /// <summary>Rewrites a cluster from what it holds, keeping its trailing words.</summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> cluster)
    {
        var sections = PhyreClusterSectionReader.Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, sections.Metadata);
        var written = Write(
            sections.Metadata,
            fixups,
            sections.ObjectData,
            sections.HeaderClasses,
            sections.InstanceHeaders,
            sections.PackedNamespace,
            sections.Payload);
        // The words after the ones this project names are carried over.
        var named = 17 * sizeof(uint);
        sections.Header.Span[named..].CopyTo(written.AsSpan(named));
        return written;
    }
}
