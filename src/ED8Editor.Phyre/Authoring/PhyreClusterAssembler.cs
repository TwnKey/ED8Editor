using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Builds a cluster from a description alone — no file is read, nothing is
/// copied from one.
///
/// Everything it leans on is already checked against the game: the class table
/// comes from <see cref="PhyreSchemaLibrary"/> (32 796 namespaces reproduced),
/// the objects from <see cref="PhyreObjectWriter"/> (5 171 709 objects), the
/// tables from the fixup writer, the sections from
/// <see cref="PhyreClusterWriter"/>. What is new here is only the arithmetic in
/// between: which classes to list, how big each group is, and what the instance
/// list headers say.
/// </summary>
public static class PhyreClusterAssembler
{
    private const int InstanceHeaderSize = 36;
    private const int InstanceDataAlignment = 4;

    public static byte[] Assemble(PhyreClusterContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        // The table the caller states, when it states one. Deriving a class list is
        // right when authoring — a cluster names nothing it does not use — but it
        // cannot reproduce a shipped file: a class is identified by WHERE it sits,
        // and the game's own tables were not derived by this rule. A shader lists 65
        // classes where our library holds 125, so rebuilding one meant writing a
        // namespace half again too big and every id shifted.
        var classNames = contents.StatedClasses ?? contents.ClassNames();
        // The canonical class table and its primitive-type table are one ABI.
        // ClassNames() deliberately promotes authored model/effect clusters to
        // that fixed game table; retaining a smaller caller-derived type list
        // would leave canonical descriptors referring to enum types that have no
        // id in the destination namespace.
        IReadOnlyList<string> typeNames;
        if (contents.StatedClasses is not null)
        {
            typeNames = contents.TypeNames;
        }
        else if (contents.SchemaProfile == PhyreSchemaProfile.FalcomAssetProcessor)
        {
            typeNames = PhyreSchemaLibrary.AssetProcessorCanonicalTypes;
        }
        else if (contents.SchemaProfile == PhyreSchemaProfile.Cs1RuntimeAuthoring)
        {
            typeNames = PhyreSchemaLibrary.CanonicalTypes;
        }
        else if (classNames.SequenceEqual(PhyreSchemaLibrary.CanonicalClasses)
            || classNames.SequenceEqual(PhyreSchemaLibrary.CanonicalPhysicsClasses))
        {
            typeNames = PhyreSchemaLibrary.CanonicalTypes;
        }
        else
        {
            // ClassNames() may add mandatory, non-instantiated runtime classes
            // after a writer has prepared its initial type list. Preserve the
            // caller's established numbering and append only types newly required
            // by that final class table.
            var listed = contents.TypeNames.ToHashSet(StringComparer.Ordinal);
            typeNames = contents.TypeNames.Concat(
                    PhyreSchemaLibrary.PrimitiveTypesFor(classNames)
                        .Where(listed.Add))
                .ToArray();
        }
        var descriptors = PhyreSchemaLibrary.Descriptors(
            typeNames, classNames, contents.SchemaProfile);
        var packedNamespace = PhyreNamespaceWriter.Write(
            typeNames, descriptors, contents.NamespaceHeader);

        // A group's objects are laid one after another, then whatever its arrays
        // hold. The class says how big one object is — except for a header
        // class, whose objects carry a payload past it.
        var objectData = new MemoryStream();
        var headers = new byte[contents.Groups.Count * InstanceHeaderSize];
        for (var index = 0; index < contents.Groups.Count; index++)
        {
            var group = contents.Groups[index];
            var classId = Array.FindIndex(classNames.ToArray(), name => name == group.ClassName);
            if (classId < 0)
            {
                throw new InvalidOperationException($"'{group.ClassName}' is not among the classes.");
            }
            var descriptor = descriptors[classId];
            var objectSize = ObjectSize(descriptor, group);

            var before = objectData.Length;
            foreach (var contentsOfObject in group.Objects)
            {
                objectData.Write(
                    PhyreObjectWriter.WriteObject(contentsOfObject, descriptors, objectSize));
            }
            var objectsSize = (uint)(objectData.Length - before);
            objectData.Write(group.ArrayData.Span);
            // Phyre advances from one instance list to the next using m_size,
            // then treats the next address as storage for 32-bit objects and
            // pointers. The shipped DX11 clusters therefore include the
            // trailing alignment bytes in both m_arraysSize and m_size. String
            // arrays are the case that exposes this: their logical byte count
            // is arbitrary, but the following instance list still starts on a
            // four-byte boundary.
            //
            // Except for the last, which nothing follows: a shipped shader ends on a
            // group whose arrays measure 33 bytes and stops there. Padding it made
            // the cluster three bytes long and its declared data size disagree with
            // the game's by the same three.
            if (index + 1 < contents.Groups.Count)
            {
                while ((objectData.Length - before) % InstanceDataAlignment != 0)
                {
                    objectData.WriteByte(0);
                }
            }
            var groupSize = (uint)(objectData.Length - before);
            var arraysSize = groupSize - objectsSize;

            var header = headers.AsSpan(index * InstanceHeaderSize);
            Write(header, 0, (uint)(classId + 1));
            Write(header, 4, (uint)group.Objects.Count);
            Write(header, 8, groupSize);
            Write(header, 12, objectsSize);
            Write(header, 16, arraysSize);
            Write(header, 20, Count(contents.Fixups.PointerArrays, index, value => value.Count));
            Write(header, 24, Count(contents.Fixups.Arrays, index, _ => 1));
            Write(header, 28, Count(contents.Fixups.Pointers, index, _ => 1));
            Write(header, 32, Count(contents.Fixups.PointerArrays, index, _ => 1));
        }

        var groups = new List<PhyreInstanceGroup>();
        for (var index = 0; index < contents.Groups.Count; index++)
        {
            var header = headers.AsSpan(index * InstanceHeaderSize);
            groups.Add(new PhyreInstanceGroup(
                index,
                Read(header, 0),
                contents.Groups[index].ClassName,
                Read(header, 4),
                Read(header, 8),
                Read(header, 12),
                Read(header, 16),
                Read(header, 24),
                Read(header, 28),
                Read(header, 32)));
        }

        // The counts the writer needs; the sizes it computes itself from the
        // tables it produces, so they are left at zero here.
        var header0 = new PhyreClusterHeader(
            HeaderSize,
            (uint)packedNamespace.Length,
            0,
            (uint)contents.Fixups.Arrays.Count,
            0,
            (uint)contents.Fixups.Pointers.Count,
            0,
            (uint)contents.Fixups.PointerArrays.Count,
            (uint)contents.Fixups.PointerArrays.Sum(value => (long)value.Count),
            (uint)contents.UserFixups.Count,
            0,
            HeaderClassInstances(contents, descriptors),
            HeaderClassChildren(contents),
            HeaderSize + packedNamespace.Length,
            HeaderSize + packedNamespace.Length + headers.Length);

        var metadata = new PhyreClusterMetadata(
            Marker,
            false,
            PlatformId,
            0,
            typeNames,
            descriptors,
            groups,
            header0);

        var written = PhyreClusterWriter.Write(
            metadata,
            contents.Fixups,
            objectData.ToArray(),
            contents.HeaderClasses,
            headers,
            packedNamespace,
            contents.Payload);
        // Past the words the writer names, the header keeps what the author
        // states — the buffer sizes a texture or a model carries.
        var named = 17 * sizeof(uint);
        if (contents.HeaderTail.Length != 0)
        {
            contents.HeaderTail.Span.CopyTo(written.AsSpan(named));
        }
        return written;
    }

    private const uint HeaderSize = 84;
    private const uint Marker = 0x50485952;
    /// <summary>
    /// Which platform's cluster this is: the four characters "DX11", which is what
    /// every cluster the game ships carries at that word and what this project's own
    /// texture writer already used.
    ///
    /// It had been 6 here. A cluster is otherwise well-formed with the wrong value —
    /// our reader takes it, the structure checks pass — but the engine reads the
    /// platform before it interprets anything the GPU will touch, so an authored
    /// model never had a chance to be looked at. It is the only word the grafting
    /// path preserved that this path did not.
    /// </summary>
    private const uint PlatformId = 0x44583131;

    private static uint Read(ReadOnlySpan<byte> header, int at)
        => BinaryPrimitives.ReadUInt32LittleEndian(header[at..]);

    /// <summary>How many groups hold a class the engine calls a header class.</summary>
    private static uint HeaderClassInstances(
        PhyreClusterContents contents, IReadOnlyList<PhyreClassDescriptor> descriptors)
    {
        var count = 0u;
        foreach (var group in contents.Groups)
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.Name == group.ClassName && (descriptor.Flags & 4) != 0) count++;
            }
        }
        return count;
    }

    private static uint HeaderClassChildren(PhyreClusterContents contents)
    {
        // The section is a run of counts followed by the children themselves, so
        // its own size says how many children it holds.
        var counts = HeaderClassCounts(contents);
        return counts == 0 ? 0 : (uint)((contents.HeaderClasses.Length - counts * 4) / 16);
    }

    private static int HeaderClassCounts(PhyreClusterContents contents)
    {
        var total = 0;
        foreach (var group in contents.Groups)
        {
            if (PhyreSchemaLibrary.IsHeaderClass(group.ClassName)) total++;
        }
        return total;
    }

    /// <summary>
    /// How much room one object of a group takes. A header class stores more
    /// than its class size — what dangles past it is the payload the header
    /// class section describes — so the objects themselves say how big they are.
    /// </summary>
    private static int ObjectSize(PhyreClassDescriptor descriptor, PhyreGroupContents group)
    {
        var trailing = 0;
        foreach (var contents in group.Objects)
        {
            trailing = Math.Max(trailing, contents.Trailing.Length);
        }
        return (int)descriptor.Size + trailing;
    }

    private static uint Count<T>(
        IReadOnlyList<T> fixups, int groupIndex, Func<T, uint> weigh)
        where T : PhyreFixup
    {
        var total = 0u;
        foreach (var fixup in fixups)
        {
            if (fixup.SourceListIndex == groupIndex) total += weigh(fixup);
        }
        return total;
    }

    private static void Write(Span<byte> header, int at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(header[at..], value);
}
