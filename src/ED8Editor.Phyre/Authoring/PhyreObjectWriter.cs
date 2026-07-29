using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One object to write: the class it is, and what its members hold.</summary>
/// <param name="ClassName">The class the instance list is of.</param>
/// <param name="Members">Member name to its bytes. A member left out stays zero.</param>
/// <param name="Trailing">
/// What a header class carries past its declared size — the payload the header
/// class section describes. Empty for every other class.
/// </param>
public sealed record PhyreObjectContents(
    string ClassName,
    IReadOnlyDictionary<string, byte[]> Members,
    ReadOnlyMemory<byte> Trailing);

/// <summary>
/// Writes the objects of an instance list from what they hold, rather than
/// copying them out of a file the game ships.
///
/// Two rules, both measured over the whole corpus rather than assumed:
///
/// <list type="number">
/// <item>An object is its declared members over zeros. Members come from the
/// whole inheritance chain, and a fixed array counts once per element. Of the
/// 5 171 709 objects the game ships, the bytes no member covers are zero in all
/// but 155 — and those 155 sit in two camera classes.</item>
/// <item>A class flagged <c>PE_CLASS_DESCRIPTOR_HEADER</c> stores objects larger
/// than itself, and what dangles past the declared size is the payload the
/// header class section describes. Only two classes in the game do this,
/// <c>PParameterBuffer</c> and <c>PAnimationClipBinding</c>; every other class
/// stores its objects at exactly its own size.</item>
/// </list>
///
/// So writing an object is: size it, zero it, place its members, and append the
/// trailing payload if it is a header class.
/// </summary>
public static class PhyreObjectWriter
{
    /// <summary>
    /// The bytes of one object. <paramref name="objectSize"/> is what the
    /// instance list gives each object, which is the class size except for a
    /// header class.
    /// </summary>
    public static byte[] WriteObject(
        PhyreObjectContents contents,
        IReadOnlyList<PhyreClassDescriptor> classes,
        int objectSize)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(classes);

        var descriptor = Find(classes, contents.ClassName);
        var bytes = new byte[objectSize];

        foreach (var member in Chain(descriptor, classes))
        {
            if (!contents.Members.TryGetValue(member.Name, out var value)) continue;
            var span = checked((int)(member.Size * Math.Max(member.FixedArraySize, 1)));
            if (value.Length != span)
            {
                throw new InvalidOperationException(
                    $"'{contents.ClassName}.{member.Name}' holds {span} bytes, was given {value.Length}.");
            }
            if (member.ValueOffset + span > bytes.Length)
            {
                throw new InvalidOperationException(
                    $"'{contents.ClassName}.{member.Name}' does not fit in an object of {objectSize} bytes.");
            }
            value.CopyTo(bytes.AsSpan((int)member.ValueOffset));
        }

        if (contents.Trailing.Length == 0) return bytes;
        var from = (int)descriptor.Size;
        if (from + contents.Trailing.Length > bytes.Length)
        {
            throw new InvalidOperationException(
                $"'{contents.ClassName}' carries {contents.Trailing.Length} bytes past its"
                + $" {from}, which an object of {objectSize} bytes cannot hold.");
        }
        contents.Trailing.Span.CopyTo(bytes.AsSpan(from));
        return bytes;
    }

    /// <summary>
    /// Reads an object back into what it holds, so a shipped one can be written
    /// again and compared. Anything the members do not name is dropped — which
    /// is the point: if the two agree byte for byte, nothing else was there.
    /// </summary>
    public static PhyreObjectContents ReadObject(
        ReadOnlySpan<byte> bytes,
        string className,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        var descriptor = Find(classes, className);
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var member in Chain(descriptor, classes))
        {
            var span = checked((int)(member.Size * Math.Max(member.FixedArraySize, 1)));
            if (member.ValueOffset + span > bytes.Length) continue;
            members[member.Name] = bytes.Slice((int)member.ValueOffset, span).ToArray();
        }

        var from = (int)descriptor.Size;
        var trailing = bytes.Length > from
            ? bytes[from..].ToArray()
            : Array.Empty<byte>();
        return new PhyreObjectContents(className, members, trailing);
    }

    /// <summary>Every member a class has, its own and its ancestors'.</summary>
    public static IEnumerable<PhyreDataMember> Chain(
        PhyreClassDescriptor descriptor,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(classes);

        for (var walk = descriptor; walk is not null;)
        {
            foreach (var member in walk.Members) yield return member;
            walk = walk.SuperClassId == 0 || walk.SuperClassId - 1 >= classes.Count
                ? null
                : classes[(int)walk.SuperClassId - 1];
        }
    }

    private static PhyreClassDescriptor Find(
        IReadOnlyList<PhyreClassDescriptor> classes,
        string className)
    {
        foreach (var descriptor in classes)
        {
            if (descriptor.Name == className) return descriptor;
        }
        throw new InvalidOperationException($"The cluster does not list a class '{className}'.");
    }
}
