using System.Text.Json;
using System.Text.Json.Serialization;

namespace ED8Editor.Application;

/// <summary>What a material was pointed at, in a form a file can hold.</summary>
/// <param name="HlslPath">
/// The author's own source, when it was one. The compiled effect is not kept here:
/// it is megabytes, and it is derivable — recompiling the file says what the shader
/// is now, where a stored copy would say what it was.
/// </param>
public sealed record MapShaderRecord(
    string AssetName,
    string? HlslPath,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Everything a map was authored from, kept so it can be opened again.
///
/// A map is written as game files — a package, a scene, two table rows — and none
/// of them remembers which model it came from, which mesh was meant to be a wall,
/// or what the author typed into a shader's parameters. Writing those down is what
/// makes a map editable a second time instead of only creatable a first.
///
/// It is stored beside the mod project rather than in the game folder: it belongs
/// to the person making the map, not to the game.
/// </summary>
public sealed record MapAuthoringRecord(
    string MapName,
    string DisplayName,
    short PlaceKind,
    string Skybox,
    string ModelPath,
    IReadOnlyDictionary<string, string> CollisionNodes,
    IReadOnlyDictionary<string, MapShaderRecord> MaterialShaders)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Where a project keeps them.</summary>
    public static string DirectoryOf(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(
            Path.GetDirectoryName(project.ProjectPath) ?? ".", "map-authoring");
    }

    public static string PathOf(ModProject project, string mapName)
        => Path.Combine(DirectoryOf(project), mapName.ToLowerInvariant() + ".json");

    public void Save(ModProject project)
    {
        var path = PathOf(project, MapName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
    }

    /// <summary>
    /// What a map was authored from, or null when it was not authored here. A map
    /// the game ships has no record, and inventing one would claim knowledge of a
    /// file nothing in this editor produced.
    /// </summary>
    public static MapAuthoringRecord? Load(ModProject project, string mapName)
    {
        var path = PathOf(project, mapName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MapAuthoringRecord>(
                File.ReadAllText(path), Format);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every map this project has authored.</summary>
    public static IReadOnlyList<string> Authored(ModProject project)
    {
        var directory = DirectoryOf(project);
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
