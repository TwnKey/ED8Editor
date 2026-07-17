namespace ED8Editor.Scene;

public sealed class SceneSnapSettings
{
    public SceneSnapSettings(float translationStep, float rotationStepRadians, float scaleStep)
    {
        TranslationStep = ValidateStep(translationStep, nameof(translationStep));
        RotationStepRadians = ValidateStep(rotationStepRadians, nameof(rotationStepRadians));
        ScaleStep = ValidateStep(scaleStep, nameof(scaleStep));
    }

    public float TranslationStep { get; }
    public float RotationStepRadians { get; }
    public float ScaleStep { get; }

    public float SnapTranslation(float value) => Snap(value, TranslationStep);
    public float SnapRotation(float radians) => Snap(radians, RotationStepRadians);

    public float SnapScale(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var sign = (float)MathF.Sign(value);
        if (sign == 0f) sign = 1f;
        return sign * MathF.Max(ScaleStep, Snap(MathF.Abs(value), ScaleStep));
    }

    private static float Snap(float value, float step)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return MathF.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    private static float ValidateStep(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}
