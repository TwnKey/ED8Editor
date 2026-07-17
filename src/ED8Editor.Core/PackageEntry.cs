namespace ED8Editor.Core;

public sealed record PackageEntry(
    int Index,
    string Name,
    uint UncompressedSize,
    uint StoredSize,
    uint Offset,
    PackageCompressionType CompressionType);
