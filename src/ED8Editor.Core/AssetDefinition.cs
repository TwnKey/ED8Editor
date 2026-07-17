namespace ED8Editor.Core;

public sealed record AssetDefinition(
    string Symbol,
    IReadOnlyList<AssetResource> Resources,
    IReadOnlyDictionary<string, string> SourceAttributes);
