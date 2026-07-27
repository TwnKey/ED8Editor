namespace ED8Editor.Core;

public static class CpuAnimationClipSegment
{
    public static CpuAnimationClip FromFrames(
        CpuAnimationClip source,
        string name,
        int startFrame,
        int endFrame,
        float framesPerSecond = 30f)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Animation segment name is required.", nameof(name));
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (endFrame < startFrame) throw new ArgumentOutOfRangeException(nameof(endFrame));
        if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0f)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

        var startTime = source.StartTime + startFrame / framesPerSecond;
        var endTime = source.StartTime + endFrame / framesPerSecond;
        const float timeTolerance = 1f / 1000f;
        if (endTime > source.EndTime + timeTolerance)
        {
            throw new InvalidDataException(
                $"Animation action '{name}' ends at frame {endFrame} ({endTime:F6}s),"
                + $" beyond clip '{source.Name}' ending at {source.EndTime:F6}s.");
        }

        return source with
        {
            Name = name,
            StartTime = startTime,
            EndTime = Math.Min(endTime, source.EndTime),
        };
    }
}
