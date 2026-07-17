namespace ED8Editor.Core;

public sealed record AssetResolution(
    string AssetId,
    AssetResolutionStatus Status,
    AssetPackage? SelectedPackage,
    IReadOnlyList<AssetPackage> Candidates);
