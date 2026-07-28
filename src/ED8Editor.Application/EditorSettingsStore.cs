using System.Text.Json;

namespace ED8Editor.Application;

public enum EditorKeyboardLayout
{
    Azerty,
    Qwerty,
}

public sealed record EditorUserSettings(
    int Version,
    string? GameDirectory,
    EditorKeyboardLayout KeyboardLayout = EditorKeyboardLayout.Azerty,
    string? InstructionDefinitionsPath = null,
    string? LastProjectPath = null)
{
    public static EditorUserSettings Default { get; } = new(1, null, EditorKeyboardLayout.Azerty, null);
}

public sealed class EditorSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public EditorSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ED8Editor",
            "settings.json");
    }

    public string Path { get; }

    public EditorUserSettings Load()
    {
        if (!File.Exists(Path)) return EditorUserSettings.Default;
        try
        {
            var settings = JsonSerializer.Deserialize<EditorUserSettings>(File.ReadAllBytes(Path), JsonOptions);
            return settings is { Version: 1 }
                && Enum.IsDefined(settings.KeyboardLayout)
                ? settings
                : EditorUserSettings.Default;
        }
        catch (JsonException)
        {
            return EditorUserSettings.Default;
        }
    }

    public void Save(EditorUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
