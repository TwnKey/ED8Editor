namespace ED8Editor.Core;

public sealed record PhyreClusterHeader(
    uint Size,
    uint PackedNamespaceSize,
    uint ArrayFixupSize,
    uint ArrayFixupCount,
    uint PointerFixupSize,
    uint PointerFixupCount,
    uint PointerArrayFixupSize,
    uint PointerArrayFixupCount,
    uint PointersInArraysCount,
    uint UserFixupCount,
    uint UserFixupDataSize,
    uint HeaderClassInstanceCount,
    uint HeaderClassChildCount,
    long InstanceHeadersOffset,
    long ObjectDataOffset);
