using System.Text.Json;
using System.Text.Json.Serialization;

namespace ED8Editor.Application;

/// <summary>One instruction of a preset, and the operand values it starts with.</summary>
/// <param name="Values">
/// What to put in its operands, by their position. Absent positions are left as the
/// engine creates them — a preset is a starting point, not a fully-specified edit,
/// and pretending to know every operand of every command would be worse than
/// admitting it knows some.
/// </param>
public sealed record ScriptPresetStep(
    string Instruction,
    IReadOnlyDictionary<string, string>? Values = null);

/// <summary>A run of instructions inserted in one go.</summary>
public sealed record ScriptPreset(
    string Name,
    string Category,
    string? Description,
    IReadOnlyList<ScriptPresetStep> Steps);

/// <summary>
/// Runs of instructions that are always written together, kept in a file rather than
/// in code.
///
/// A camera move is three commands, an effect is a load and a play; typing them one
/// at a time through a picker is where the tedium of scripting lives. Which runs are
/// worth having is knowledge about the game that changes as people learn more about
/// it, so it belongs in a file the author can edit — not in a table that ships with
/// the editor and can only be changed by rebuilding it.
///
/// A file that is missing, malformed or empty simply yields no presets. Presets are
/// a convenience; an editor that refuses to open because a convenience file has a
/// stray comma is not one.
/// </summary>
public static class ScriptInstructionPresets
{
    /// <summary>What the file is called, beside the instruction definitions.</summary>
    public const string FileName = "instructions_presets.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The presets beside the instruction definitions, or none.
    /// </summary>
    /// <param name="instructionDefinitionsPath">
    /// Where the instruction definitions were loaded from. The presets name those
    /// instructions, so they live in the same folder: one place to look, and a set
    /// of presets cannot end up describing a different set of instructions.
    /// </param>
    public static IReadOnlyList<ScriptPreset> Load(string? instructionDefinitionsPath)
    {
        var folder = string.IsNullOrWhiteSpace(instructionDefinitionsPath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(Path.GetFullPath(instructionDefinitionsPath));
        if (string.IsNullOrEmpty(folder)) return Array.Empty<ScriptPreset>();
        return LoadFrom(Path.Combine(folder, FileName));
    }

    /// <summary>The presets in one named file, or none.</summary>
    public static IReadOnlyList<ScriptPreset> LoadFrom(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) return Array.Empty<ScriptPreset>();
        try
        {
            var read = JsonSerializer.Deserialize<ScriptPreset[]>(File.ReadAllText(path), Format);
            if (read is null) return Array.Empty<ScriptPreset>();
            // A preset with no steps inserts nothing, and one with no name cannot be
            // chosen: neither is worth offering.
            return read
                .Where(value => !string.IsNullOrWhiteSpace(value.Name))
                .Where(value => value.Steps is { Count: > 0 })
                .Where(value => value.Steps.All(step => !string.IsNullOrWhiteSpace(step.Instruction)))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return Array.Empty<ScriptPreset>();
        }
    }
}
