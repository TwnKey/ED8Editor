namespace ED8Editor.Core;

public sealed record AssetCatalogEntry(
    string AssetId,
    IReadOnlyList<AssetPackage> Packages);
