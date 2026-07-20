namespace ED8Editor.Core;

public enum SceneEnvironmentVariant
{
    Daylight,
    Evening,
    Night,
    Morning,
    Rain,
}

public static class SceneEnvironmentVariantSelector
{
    private static readonly (string Marker, SceneEnvironmentVariant Variant)[] AuthoredMarkers =
    {
        ("_daylight", SceneEnvironmentVariant.Daylight),
        ("_evening", SceneEnvironmentVariant.Evening),
        ("_night", SceneEnvironmentVariant.Night),
        ("_morning", SceneEnvironmentVariant.Morning),
        ("_rain", SceneEnvironmentVariant.Rain),
    };

    public static SceneEnvironmentVariant FromProfileName(string? profileName)
        => profileName switch
        {
            null or "default" or "daylight" => SceneEnvironmentVariant.Daylight,
            "evening" => SceneEnvironmentVariant.Evening,
            "night" => SceneEnvironmentVariant.Night,
            "morning" => SceneEnvironmentVariant.Morning,
            "rain" => SceneEnvironmentVariant.Rain,
            _ => throw new ArgumentException($"Unsupported map environment profile '{profileName}'.", nameof(profileName)),
        };

    public static SceneEnvironmentVariant? GetAuthoredVariant(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (marker, variant) in AuthoredMarkers)
        {
            if (name.Contains(marker, StringComparison.Ordinal)) return variant;
        }
        return null;
    }

    public static bool IsVisible(string name, SceneEnvironmentVariant activeVariant)
        => GetAuthoredVariant(name) is not { } authoredVariant || authoredVariant == activeVariant;
}
