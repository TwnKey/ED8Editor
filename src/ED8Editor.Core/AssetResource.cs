namespace ED8Editor.Core;

public sealed record AssetResource(
    int Index,
    string Path,
    string ArchiveEntryName,
    string SourceType,
    AssetResourceKind Kind,
    bool IsEmbedded,
    IReadOnlyDictionary<string, string> SourceAttributes);
