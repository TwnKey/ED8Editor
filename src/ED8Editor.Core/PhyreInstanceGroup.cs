namespace ED8Editor.Core;

public sealed record PhyreInstanceGroup(
    int Index,
    uint ClassId,
    string? ClassName,
    uint Count,
    uint Size,
    uint ObjectsSize,
    uint ArraysSize,
    uint ArrayFixupCount,
    uint PointerFixupCount,
    uint PointerArrayFixupCount);
