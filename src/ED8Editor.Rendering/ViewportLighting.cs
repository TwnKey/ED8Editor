using System.Numerics;

namespace ED8Editor.Rendering;

public sealed record ViewportLighting
{
    public ViewportLighting(Vector3 directionToLight, Vector3 ambientColor, Vector3 directColor)
    {
        DirectionToLight = ValidateDirection(directionToLight);
        AmbientColor = ValidateColor(ambientColor, nameof(ambientColor));
        DirectColor = ValidateColor(directColor, nameof(directColor));
    }

    public static ViewportLighting Neutral { get; } = new(
        new Vector3(0.35f, 0.8f, -0.45f),
        new Vector3(0.38f),
        new Vector3(0.72f));

    public Vector3 DirectionToLight { get; }
    public Vector3 AmbientColor { get; }
    public Vector3 DirectColor { get; }

    private static Vector3 ValidateDirection(Vector3 value)
    {
        ValidateFinite(value, nameof(value));
        if (value.LengthSquared() <= float.Epsilon) throw new ArgumentOutOfRangeException(nameof(value));
        return Vector3.Normalize(value);
    }

    private static Vector3 ValidateColor(Vector3 value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value.X < 0f || value.Y < 0f || value.Z < 0f) throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
