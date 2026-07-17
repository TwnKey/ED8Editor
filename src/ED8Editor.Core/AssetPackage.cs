namespace ED8Editor.Core;

public sealed record AssetPackage(
    string AssetId,
    string Path,
    AssetVariant Variant,
    long FileSize);
