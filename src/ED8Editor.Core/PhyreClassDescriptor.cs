namespace ED8Editor.Core;

public sealed record PhyreClassDescriptor(
    int Index,
    string Name,
    uint SuperClassId,
    uint Size,
    uint Alignment,
    uint MemberCount,
    int OffsetFromParent,
    int OffsetToBase,
    int OffsetToBaseInAllocatedBlock,
    uint Flags,
    uint DefaultBufferOffset,
    IReadOnlyList<PhyreDataMember> Members);
