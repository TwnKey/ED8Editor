namespace ED8Editor.Core;

public enum AssetModelLoadStatus
{
    Loaded,
    Missing,
    Invalid,
}

public sealed record AssetModelLoad(
    string AssetId,
    AssetModelLoadStatus Status,
    CpuModel? Model,
    string? Error);
