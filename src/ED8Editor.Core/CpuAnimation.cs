using System.Numerics;

namespace ED8Editor.Core;

public enum CpuAnimationPath
{
    Translation,
    Rotation,
    Scale,
}

public enum CpuAnimationInterpolation
{
    Linear,
    Step,
}

public sealed record CpuAnimationChannel(
    string TargetName,
    CpuAnimationPath Path,
    CpuAnimationInterpolation Interpolation,
    IReadOnlyList<float> Times,
    IReadOnlyList<Vector4> Values);

public sealed record CpuAnimationClip(
    string AssetId,
    string Name,
    float StartTime,
    float EndTime,
    IReadOnlyList<CpuAnimationChannel> Channels)
{
    public float Duration => Math.Max(0f, EndTime - StartTime);
}
