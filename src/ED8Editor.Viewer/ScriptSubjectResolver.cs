using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// The actor a script belongs to. An ANI or craft script is not attached to a
/// map: it drives one character, named by <c>t_name.tbl</c> for a party member
/// or an NPC and by <c>t_mons.tbl</c> for a monster, both of which spell out the
/// model asset next to the script name. Knowing it lets the editor show that
/// actor and bind the script's own "self" reference to it.
/// </summary>
internal sealed record ScriptSubject(string ScriptName, string ModelAssetId, string Source);

internal static class ScriptSubjectResolver
{
    private const string MonsterCategory = "status";

    /// <summary>
    /// Actor driven by <paramref name="scriptPath"/>, or null when the script is
    /// not one actor's own (a scenario, a system script).
    /// </summary>
    public static ScriptSubject? Resolve(
        string scriptPath,
        string? gameDataPath,
        ScriptAnimationLibrary? animationLibrary)
    {
        if (string.IsNullOrWhiteSpace(scriptPath)) return null;
        var name = Path.GetFileNameWithoutExtension(scriptPath);
        if (name.Length == 0) return null;
        if (animationLibrary?.FindModelByAnimationScript(name) is { Length: > 0 } model)
            return new ScriptSubject(name, model, "t_name.tbl");
        return ResolveMonster(name, gameDataPath);
    }

    private static ScriptSubject? ResolveMonster(string scriptName, string? gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) return null;
        var path = new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, "t_mons.tbl"))
            .FirstOrDefault(File.Exists);
        if (path is null) return null;
        try
        {
            foreach (var entry in Cs1TableDocument.Read(path).Entries)
            {
                if (!entry.Category.Equals(MonsterCategory, StringComparison.Ordinal)) continue;
                var fields = ReadLeadingStrings(entry.Data, 3);
                // The record opens with the monster's script name, then its model.
                if (fields.Count < 2
                    || !fields[0].Equals(scriptName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var model = fields.FirstOrDefault(value =>
                    value.StartsWith("C_", StringComparison.OrdinalIgnoreCase));
                if (model is not null) return new ScriptSubject(scriptName, model, "t_mons.tbl");
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read '{path}': {exception.Message}");
        }
        return null;
    }

    private static List<string> ReadLeadingStrings(byte[] data, int count)
    {
        var values = new List<string>(count);
        var start = 0;
        while (values.Count < count && start < data.Length)
        {
            var end = Array.IndexOf(data, (byte)0, start);
            if (end < 0 || end == start) break;
            values.Add(Encoding.ASCII.GetString(data, start, end - start));
            start = end + 1;
        }
        return values;
    }
}
