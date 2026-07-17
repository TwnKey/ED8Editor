namespace ED8Editor.Core;

public abstract record PhyreFixup(
    int SourceListIndex,
    uint SourceObjectId,
    uint SourceOffsetOrMember)
{
    public bool IsClassDataMember => (SourceOffsetOrMember & 0x80000000u) == 0;
    public uint SourceMemberId => SourceOffsetOrMember;
    public uint SourceOffset => SourceOffsetOrMember & 0x7fffffffu;
}

public sealed record PhyreArrayFixup(
    int SourceListIndex,
    uint SourceObjectId,
    uint SourceOffsetOrMember,
    uint Count,
    uint Offset)
    : PhyreFixup(SourceListIndex, SourceObjectId, SourceOffsetOrMember);

public sealed record PhyrePointerFixup(
    int SourceListIndex,
    uint SourceObjectId,
    uint SourceOffsetOrMember,
    uint DestinationListIndex,
    uint DestinationObjectId,
    uint DestinationOffset,
    uint ArrayIndex,
    uint? UserFixupId)
    : PhyreFixup(SourceListIndex, SourceObjectId, SourceOffsetOrMember);

public sealed record PhyreUserFixup(
    int Id,
    uint TypeId,
    string? TypeName,
    uint DeclaredSize,
    uint DataOffset,
    ReadOnlyMemory<byte> Data,
    string? Text);

public sealed record PhyreFixupSet(
    IReadOnlyList<PhyreArrayFixup> PointerArrays,
    IReadOnlyList<PhyrePointerFixup> Pointers,
    IReadOnlyList<PhyreArrayFixup> Arrays,
    IReadOnlyList<PhyreUserFixup> UserFixups,
    long VramDataOffset);
