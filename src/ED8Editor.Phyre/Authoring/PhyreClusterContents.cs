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
    ReadOnlyMemory<byte> HeaderTail,
    PhyreSchemaProfile SchemaProfile = PhyreSchemaProfile.Cs1Native)
{
    /// <summary>
    /// The classes the cluster has to list: the ones its groups instantiate,
    /// every class those derive from, and every class a member of any of them
    /// refers to. A cluster names nothing it does not use, and leaves out
    /// nothing it does.
    /// </summary>
    /// <summary>
    /// The classes every cluster lists whatever it holds.
    ///
    /// Nothing instantiates these, so following what the groups refer to never
    /// reaches them — and yet the game's own clusters all declare them, the same
    /// eleven, in a map as in a prop. Two of them describe the file itself: the
    /// engine reads a cluster's header through <c>PClusterHeader</c> and its instance
    /// lists through <c>PInstanceListHeader</c>. The rest are how a material reaches
    /// a texture, which a parameter buffer names by type without holding one.
    ///
    /// Leaving them out cost 2 222 bytes of namespace in every cluster this project
    /// wrote — the whole difference from the shipped files, to the byte.
    /// </summary>
    private static readonly string[] AlwaysListed =
    {
        "PClusterHeader", "PClusterHeaderBase", "PClusterHeaderD3D11",
        "PInstanceListHeader",
        "PShaderParameterCaptureBufferSampler",
        "PShaderParameterCaptureBufferTexture2D",
        "PShaderParameterCaptureBufferTextureBase",
        "PTexture2D", "PTexture2DBase", "PTexture2DD3D11", "PTextureCommonBase",
    };

    public IReadOnlyList<string> ClassNames()
    {
        if (SchemaProfile == PhyreSchemaProfile.Cs1RuntimeAuthoring)
        {
            if (!Groups.All(group =>
                    PhyreSchemaLibrary.CanonicalClasses.Contains(group.ClassName)))
            {
                throw new InvalidOperationException(
                    "The CS1 runtime authoring profile does not describe every requested group.");
            }
            return PhyreSchemaLibrary.CanonicalClasses;
        }
        if (SchemaProfile == PhyreSchemaProfile.FalcomAssetProcessor)
        {
            if (!Groups.All(group =>
                    PhyreSchemaLibrary.AssetProcessorCanonicalClasses.Contains(group.ClassName)))
            {
                throw new InvalidOperationException(
                    "The Falcom AssetProcessor profile does not describe every requested group.");
            }
            return PhyreSchemaLibrary.AssetProcessorCanonicalClasses;
        }

        // The game's own table when it covers this cluster, in the game's own order.
        // A class is identified by where it sits, so deriving a shorter list — even a
        // correct one — numbers everything differently from every file the engine has
        // ever read.
        if (Groups.All(group => PhyreSchemaLibrary.CanonicalClasses.Contains(group.ClassName)))
        {
            return PhyreSchemaLibrary.CanonicalClasses;
        }
        if (Groups.All(group =>
                PhyreSchemaLibrary.CanonicalPhysicsClasses.Contains(group.ClassName)))
        {
            return PhyreSchemaLibrary.CanonicalPhysicsClasses;
        }

        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var group in Groups)
        {
            if (wanted.Add(group.ClassName)) queue.Enqueue(group.ClassName);
        }
        foreach (var name in AlwaysListed)
        {
            if (wanted.Add(name)) queue.Enqueue(name);
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
