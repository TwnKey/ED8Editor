namespace ED8Editor.Core;

public sealed record PhyreDataMember(
    int Index,
    int ClassMemberIndex,
    string Name,
    uint TypeId,
    string? TypeName,
    uint ValueOffset,
    uint Size,
    uint Flags,
    uint FixedArraySize)
{
    public bool IsDynamicArrayPointer => (Flags & 0x80000000u) != 0;
}
