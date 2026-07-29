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
    // SET_SYS slot 5 (Environment_SetProfile) uses these exact values in the
    // shipped scenario scripts. Keep this conversion centralized: the renderer
    // enum is deliberately not coupled to the VM's numeric representation.
    private static readonly IReadOnlyDictionary<int, SceneEnvironmentVariant>
        ScriptProfiles = new Dictionary<int, SceneEnvironmentVariant>
        {
            [0] = SceneEnvironmentVariant.Daylight,
            [1] = SceneEnvironmentVariant.Evening,
            [2] = SceneEnvironmentVariant.Night,
            [3] = SceneEnvironmentVariant.Morning,
            [4] = SceneEnvironmentVariant.Rain,
        };

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

    public static bool TryFromScriptProfile(
        int profile,
        out SceneEnvironmentVariant variant)
        => ScriptProfiles.TryGetValue(profile, out variant);

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
