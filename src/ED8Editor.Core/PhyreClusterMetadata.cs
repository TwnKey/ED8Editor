namespace ED8Editor.Core;

public sealed record PhyreClusterMetadata(
    uint Marker,
    bool IsBigEndian,
    uint PlatformId,
    uint TotalDataSize,
    IReadOnlyList<string> Types,
    IReadOnlyList<PhyreClassDescriptor> Classes,
    IReadOnlyList<PhyreInstanceGroup> InstanceGroups,
    PhyreClusterHeader Header);
