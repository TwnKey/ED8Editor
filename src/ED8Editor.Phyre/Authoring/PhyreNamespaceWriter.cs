using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes the packed namespace — the type schema a cluster carries, which is
/// most of a texture's container.
///
/// Its layout is the one the reader walks: six words of counts, one offset per
/// type name, then a 36-byte descriptor per class, a 24-byte record per data
/// member, and a table of null-terminated names the offsets point into. Names
/// are written in the order they are first used, each one once — a rule this
/// class does not assume but checks, since re-emitting a shipped namespace has
/// to come out byte for byte.
/// </summary>
public static class PhyreNamespaceWriter
{
    private const int ClassDescriptorSize = 36;
    private const int DataMemberSize = 24;

    /// <summary>The four words of the namespace header the reader steps over.</summary>
    public sealed record UnmodelledHeader(uint First, uint Second, uint Third, uint Fourth);

    /// <summary>Reads back the four words this writer has to carry over.</summary>
    public static UnmodelledHeader ReadUnmodelledHeader(ReadOnlyMemory<byte> packedNamespace)
    {
        var span = packedNamespace.Span;
        return new UnmodelledHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(span),
            BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[28..]));
    }

    /// <summary>
    /// The packed namespace for a schema. <paramref name="carried"/> holds the
    /// words this project has not given a meaning to yet; they come from the
    /// cluster being rewritten, or are zero for one being created.
    /// </summary>
    public static byte[] Write(
        IReadOnlyList<string> types,
        IReadOnlyList<PhyreClassDescriptor> classes,
        UnmodelledHeader carried)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(classes);

        var strings = new PhyreStringTable();
        // The table is written in three runs: every type name, then every class
        // name, then the member names in the order the classes declare them.
        // Reading a shipped namespace back is what says so — its class names sit
        // in one block right after the type names, before any member name.
        // Within the first two runs the names are sorted, while the entries that
        // point at them keep the order the file lists them in — so the table is
        // filled sorted, and every entry then looks its own name up.
        foreach (var name in types.OrderBy(value => value, StringComparer.Ordinal))
        {
            strings.Add(name);
        }
        foreach (var name in classes.Select(value => value.Name)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            strings.Add(name);
        }
        var typeOffsets = types.Select(strings.Add).ToArray();
        var classNameOffsets = classes.Select(value => strings.Add(value.Name)).ToArray();
        var memberNameOffsets = classes
            .SelectMany(value => value.Members)
            .Select(member => strings.Add(member.Name))
            .ToArray();

        var memberCount = classes.Sum(value => value.Members.Count);
        var table = strings.ToArray();
        // Eight words: four counts, and four this project has not named yet.
        var size = 8 * sizeof(uint)
            + types.Count * sizeof(uint)
            + classes.Count * ClassDescriptorSize
            + memberCount * DataMemberSize
            + table.Length;
        var output = new byte[size];
        var cursor = 0;

        Write(output, ref cursor, carried.First);
        Write(output, ref cursor, carried.Second);
        Write(output, ref cursor, (uint)types.Count);
        Write(output, ref cursor, (uint)classes.Count);
        Write(output, ref cursor, (uint)memberCount);
        Write(output, ref cursor, (uint)table.Length);
        Write(output, ref cursor, carried.Third);
        Write(output, ref cursor, carried.Fourth);
        foreach (var offset in typeOffsets) Write(output, ref cursor, offset);

        for (var index = 0; index < classes.Count; index++)
        {
            var descriptor = classes[index];
            Write(output, ref cursor, descriptor.SuperClassId);
            // The size and the alignment share a word: the alignment is kept as
            // the power of two it is, in the top nibble.
            Write(output, ref cursor, descriptor.Size | (AlignmentBits(descriptor.Alignment) << 28));
            Write(output, ref cursor, classNameOffsets[index]);
            Write(output, ref cursor, (uint)descriptor.Members.Count);
            Write(output, ref cursor, unchecked((uint)descriptor.OffsetFromParent));
            Write(output, ref cursor, unchecked((uint)descriptor.OffsetToBase));
            Write(output, ref cursor, unchecked((uint)descriptor.OffsetToBaseInAllocatedBlock));
            Write(output, ref cursor, descriptor.Flags);
            Write(output, ref cursor, descriptor.DefaultBufferOffset);
        }

        var memberName = 0;
        foreach (var member in classes.SelectMany(value => value.Members))
        {
            Write(output, ref cursor, memberNameOffsets[memberName++]);
            Write(output, ref cursor, member.TypeId);
            Write(output, ref cursor, member.ValueOffset);
            Write(output, ref cursor, member.Size);
            Write(output, ref cursor, member.Flags);
            Write(output, ref cursor, member.FixedArraySize);
        }

        table.CopyTo(output.AsSpan(cursor));
        return output;
    }

    /// <summary>The exponent an alignment is stored as.</summary>
    private static uint AlignmentBits(uint alignment)
    {
        var bits = 0u;
        while (alignment > 1)
        {
            alignment >>= 1;
            bits++;
        }
        return bits;
    }

    private static void Write(byte[] output, ref int cursor, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor), value);
        cursor += sizeof(uint);
    }

    /// <summary>
    /// The names a namespace refers to, each stored once and pointed at by its
    /// offset.
    /// </summary>
    private sealed class PhyreStringTable
    {
        private readonly Dictionary<string, uint> offsets = new(StringComparer.Ordinal);
        private readonly MemoryStream bytes = new();

        public uint Add(string? value)
        {
            var text = value ?? string.Empty;
            if (offsets.TryGetValue(text, out var known)) return known;
            var offset = (uint)bytes.Length;
            bytes.Write(Encoding.ASCII.GetBytes(text));
            bytes.WriteByte(0);
            offsets.Add(text, offset);
            return offset;
        }

        public byte[] ToArray() => bytes.ToArray();
    }
}
