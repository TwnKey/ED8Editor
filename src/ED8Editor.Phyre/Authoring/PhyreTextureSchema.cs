using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// The type schema a texture cluster carries, as data rather than as bytes
/// copied from a file the game ships.
///
/// These are facts about the format — the size and alignment of each class, the
/// offset, size and flags of each member — and they are the same in every
/// texture the game ships. They come from what the engine binds:
/// <c>PHYRE_BIND_CLASS_DATA_MEMBER(m_width)</c> and its siblings. Two of them
/// are worth reading twice: <c>PTextureCommonBase.m_mipmapCount</c> at 12 and
/// <c>PTexture2DBase.m_width</c> at 28 are the fields the editor already patches
/// by hand, and <c>PClusterHeaderD3D11.m_maxTextureBufferSize</c> at 80 explains
/// why the top-mip size sits at offset 80 of the file: the header is that class.
///
/// Nothing here is guessed — <see cref="PhyreAuthoringCheck"/> emits a namespace
/// from this table and requires it to be, byte for byte, the one in the game's
/// own textures.
/// </summary>
public static class PhyreTextureSchema
{
    private sealed record MemberRow(
        string Name, uint TypeId, uint ValueOffset, uint Size, uint Flags, uint FixedArraySize);

    private sealed record ClassRow(
        string Name,
        uint SuperClassId,
        uint Size,
        uint Alignment,
        int OffsetFromParent,
        int OffsetToBase,
        int OffsetToBaseInAllocatedBlock,
        uint Flags,
        uint DefaultBufferOffset,
        MemberRow[] Members);

    /// <summary>The primitive types a texture's members refer to.</summary>
    public static IReadOnlyList<string> TypeNames { get; } =
        new[] { "PUInt32", "PChar", "PTextureFormatBase", "PUInt8", "PInt32" };

    /// <summary>
    /// The four words of the namespace header this project has not named. The
    /// first is the byte-order stamp the engine writes; the second is a version.
    /// </summary>
    public static PhyreNamespaceWriter.UnmodelledHeader Header { get; } =
        new(0x1020304, 0x8D7, 0x0, 0x0);

    private static readonly ClassRow[] Rows =
    {
        new("PAssetReference", 2, 40, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
        {
            new("m_id", 13, 24, 4, 0x10, 0),
            new("m_asset", 7, 28, 4, 0x16, 0),
            new("m_assetType", 8, 32, 4, 0x12, 0),
        }),
        new("PBase", 0, 0, 1, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
        new("PClassDescriptor", 2, 144, 16, 24, 24, 24, 0x2, 0, Array.Empty<MemberRow>()),
        new("PClusterHeader", 5, 84, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
        new("PClusterHeaderD3D11", 6, 84, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_indexBufferSize", 0, 72, 4, 0x0, 0),
            new("m_vertexBufferSize", 0, 76, 4, 0x0, 0),
            new("m_maxTextureBufferSize", 0, 80, 4, 0x0, 0),
        }),
        new("PClusterHeaderBase", 2, 72, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_phyreMarker", 0, 0, 4, 0x8, 0),
            new("m_size", 0, 4, 4, 0x8, 0),
            new("m_instanceListCount", 0, 16, 4, 0x8, 0),
            new("m_packedNamespaceSize", 0, 8, 4, 0x8, 0),
            new("m_arrayFixupSize", 0, 20, 4, 0x8, 0),
            new("m_arrayFixupCount", 0, 24, 4, 0x8, 0),
            new("m_pointerFixupSize", 0, 28, 4, 0x8, 0),
            new("m_pointerFixupCount", 0, 32, 4, 0x8, 0),
            new("m_pointerArrayFixupSize", 0, 36, 4, 0x8, 0),
            new("m_pointerArrayFixupCount", 0, 40, 4, 0x8, 0),
            new("m_pointersInArraysCount", 0, 44, 4, 0x8, 0),
            new("m_userFixupCount", 0, 48, 4, 0x8, 0),
            new("m_userFixupDataSize", 0, 52, 4, 0x8, 0),
            new("m_totalDataSize", 0, 56, 4, 0x8, 0),
            new("m_headerClassInstanceCount", 0, 60, 4, 0x8, 0),
            new("m_headerClassChildCount", 0, 64, 4, 0x8, 0),
            new("m_platformID", 0, 12, 4, 0x8, 0),
            new("m_physicsEngineID", 0, 68, 4, 0x8, 0),
        }),
        new("PInstanceListHeader", 2, 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_classID", 0, 0, 4, 0x8, 0),
            new("m_count", 0, 4, 4, 0x8, 0),
            new("m_size", 0, 8, 4, 0x8, 0),
            new("m_objectsSize", 0, 12, 4, 0x8, 0),
            new("m_arraysSize", 0, 16, 4, 0x8, 0),
            new("m_pointersInArraysCount", 0, 20, 4, 0x8, 0),
            new("m_arrayFixupCount", 0, 24, 4, 0x8, 0),
            new("m_pointerFixupCount", 0, 28, 4, 0x8, 0),
            new("m_pointerArrayFixupCount", 0, 32, 4, 0x8, 0),
        }),
        new("PString", 2, 4, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_buffer", 1, 0, 4, 0x10, 0),
        }),
        new("PTexture2DD3D11", 11, 112, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
        new("PTexture2D", 9, 112, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
        new("PTexture2DBase", 12, 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_width", 0, 28, 4, 0x10, 0),
            new("m_height", 0, 32, 4, 0x10, 0),
        }),
        new("PTextureCommonBase", 2, 28, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
        {
            new("m_format", 2, 0, 4, 0x12, 0),
            new("m_memoryType", 3, 5, 1, 0x10, 0),
            new("m_mipmapCount", 0, 12, 4, 0x10, 0),
            new("m_maxMipLevel", 0, 16, 4, 0x10, 0),
            new("m_textureFlags", 4, 20, 4, 0x10, 0),
        }),
    };

    /// <summary>The classes, in the order a cluster lists them.</summary>
    public static IReadOnlyList<PhyreClassDescriptor> Classes { get; } = Build();

    /// <summary>The one-based identifier a class carries in an instance group.</summary>
    public static uint ClassId(string name)
    {
        for (var index = 0; index < Rows.Length; index++)
        {
            if (Rows[index].Name.Equals(name, StringComparison.Ordinal)) return (uint)index + 1;
        }
        throw new ArgumentException($"A texture cluster has no '{name}' class.", nameof(name));
    }

    /// <summary>Where a member sits in its object, by the name the engine binds it under.</summary>
    public static uint MemberOffset(string className, string memberName)
    {
        var descriptor = Classes.FirstOrDefault(value =>
            value.Name.Equals(className, StringComparison.Ordinal))
            ?? throw new ArgumentException($"No '{className}' class.", nameof(className));
        var member = descriptor.Members.FirstOrDefault(value =>
            value.Name.Equals(memberName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"'{className}' binds no '{memberName}'.", nameof(memberName));
        return member.ValueOffset;
    }

    private static PhyreClassDescriptor[] Build()
    {
        var classes = new PhyreClassDescriptor[Rows.Length];
        var memberIndex = 0;
        for (var index = 0; index < Rows.Length; index++)
        {
            var row = Rows[index];
            var members = new PhyreDataMember[row.Members.Length];
            for (var member = 0; member < members.Length; member++, memberIndex++)
            {
                var source = row.Members[member];
                members[member] = new PhyreDataMember(
                    memberIndex,
                    member,
                    source.Name,
                    source.TypeId,
                    null,
                    source.ValueOffset,
                    source.Size,
                    source.Flags,
                    source.FixedArraySize);
            }
            classes[index] = new PhyreClassDescriptor(
                index,
                row.Name,
                row.SuperClassId,
                row.Size,
                row.Alignment,
                (uint)members.Length,
                row.OffsetFromParent,
                row.OffsetToBase,
                row.OffsetToBaseInAllocatedBlock,
                row.Flags,
                row.DefaultBufferOffset,
                members);
        }
        return classes;
    }
}
