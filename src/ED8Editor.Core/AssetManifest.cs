namespace ED8Editor.Core;

public sealed record AssetManifest(
    string SourcePackagePath,
    IReadOnlyList<AssetDefinition> Assets,
    AssetDefinition? PrimaryAsset,
    bool UsedSingleAssetFallback,
    IReadOnlyList<byte> OriginalBytes);
