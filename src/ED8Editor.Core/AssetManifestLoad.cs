namespace ED8Editor.Core;

public sealed record AssetManifestLoad(
    string AssetId,
    AssetManifestLoadStatus Status,
    AssetManifest? Manifest,
    string? Error);
