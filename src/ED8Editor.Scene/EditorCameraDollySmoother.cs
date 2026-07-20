namespace ED8Editor.Scene;

public sealed class EditorCameraDollySmoother
{
    private const float DefaultSharpness = 18f;

    public float RemainingDistance { get; private set; }

    public void Add(float distance)
    {
        if (!float.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
        RemainingDistance += distance;
    }

    public float Advance(float elapsedSeconds, float sharpness = DefaultSharpness)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!float.IsFinite(sharpness) || sharpness <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sharpness));
        if (elapsedSeconds == 0f || RemainingDistance == 0f) return 0f;

        var distance = RemainingDistance * (1f - MathF.Exp(-sharpness * elapsedSeconds));
        RemainingDistance -= distance;
        if (MathF.Abs(RemainingDistance) < 0.0001f) RemainingDistance = 0f;
        return distance;
    }

    public void Reset() => RemainingDistance = 0f;
}
