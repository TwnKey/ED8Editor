using ED8Editor.Core;

namespace ED8Editor.Application;

public enum AssetAnimationLoadStatus
{
    Loaded,
    Missing,
    Invalid,
}

public sealed record AssetAnimationLoad(
    string AssetId,
    string ClipName,
    AssetAnimationLoadStatus Status,
    CpuAnimationClip? Clip,
    string? Error);
