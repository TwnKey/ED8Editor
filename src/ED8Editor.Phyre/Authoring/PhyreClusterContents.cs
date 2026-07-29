using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One instance list to produce: a class, and the objects in it.</summary>
/// <param name="ArrayData">
/// What the group keeps after its objects — the bytes arrays point into.
/// </param>
public sealed record PhyreGroupContents(
    string ClassName,
    IReadOnlyList<PhyreObjectContents> Objects,
    ReadOnlyMemory<byte> ArrayData);

/// <summary>
/// A whole cluster, said rather than read: which classes, which objects, and
/// which of them point at which.
///
/// This is what an author fills in. Everything it needs to become a file is
/// already proven elsewhere — the schema comes from
/// <see cref="PhyreSchemaLibrary"/>, the objects from
/// <see cref="PhyreObjectWriter"/>, the tables from the fixup writer — so this
/// type carries no bytes of its own beyond the payload and the pieces that have
/// no structured form yet.
/// </summary>
public sealed record PhyreClusterContents(
    IReadOnlyList<string> TypeNames,
    IReadOnlyList<PhyreGroupContents> Groups,
    PhyreFixupSet Fixups,
    IReadOnlyList<PhyreUserFixup> UserFixups,
    ReadOnlyMemory<byte> HeaderClasses,
    ReadOnlyMemory<byte> Payload,
    PhyreNamespaceWriter.UnmodelledHeader NamespaceHeader,
    ReadOnlyMemory<byte> HeaderTail)
{
    /// <summary>
    /// The classes the cluster has to list: the ones its groups instantiate,
    /// every class those derive from, and every class a member of any of them
    /// refers to. A cluster names nothing it does not use, and leaves out
    /// nothing it does.
    /// </summary>
    public IReadOnlyList<string> ClassNames()
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var group in Groups)
        {
            if (wanted.Add(group.ClassName)) queue.Enqueue(group.ClassName);
        }
        while (queue.Count != 0)
        {
            foreach (var name in PhyreSchemaLibrary.Referenced(queue.Dequeue()))
            {
                if (wanted.Add(name)) queue.Enqueue(name);
            }
        }
        return wanted.ToArray();
    }
}
