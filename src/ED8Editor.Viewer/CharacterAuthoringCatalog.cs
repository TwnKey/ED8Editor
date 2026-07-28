using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

internal enum CharacterAuthoringKind
{
    Character,
    Enemy,
}

internal sealed record CharacterAuthoringEntry(
    CharacterAuthoringKind Kind,
    int? TableId,
    string DisplayName,
    string ModelAssetId,
    string AnimationScript,
    string FacialAssetId,
    string SourceTable)
{
    public string Label => TableId is { } id
        ? $"{id}: {DisplayName} — {ModelAssetId}"
        : $"{DisplayName} — {ModelAssetId}";
}

/// <summary>
/// Exact game-table bindings used by the character/enemy studio. This catalog
/// does not infer models from filenames: character rows come from decoded
/// NameTableData and enemy rows from the verified leading strings in t_mons.
/// </summary>
internal static class CharacterAuthoringCatalog
{
    public static IReadOnlyList<CharacterAuthoringEntry> LoadCharacters(
        ScriptAnimationLibrary library)
        => library.Characters
            .SelectMany(character => DistinctModels(character).Select(pair =>
                new CharacterAuthoringEntry(
                    CharacterAuthoringKind.Character,
                    character.CharacterId,
                    pair.Label.Length == 0
                        ? character.DisplayName
                        : $"{character.DisplayName} ({pair.Label})",
                    pair.Model,
                    character.AnimationScript,
                    character.FacialAssetId,
                    "t_name.tbl / NameTableData")))
            .ToArray();

    public static IReadOnlyList<CharacterAuthoringEntry> LoadEnemies(string gameDataPath)
    {
        var path = new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, "t_mons.tbl"))
            .FirstOrDefault(File.Exists);
        if (path is null) return Array.Empty<CharacterAuthoringEntry>();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var locale = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        var encoding = locale.Equals("dat", StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false, true);
        var codec = new Cs1TableRecordCodec(textEncoding: encoding);
        var result = new List<CharacterAuthoringEntry>();
        foreach (var pair in Cs1TableDocument.Read(path).Entries
                     .Select((entry, index) => (entry, index))
                     .Where(value => value.entry.Category.Equals("status", StringComparison.Ordinal)))
        {
            var fields = codec.Decode(pair.entry);
            if (fields is null) continue;
            var values = fields.ToDictionary(
                value => value.Field.Name,
                value => value.Value,
                StringComparer.Ordinal);
            if (!values.TryGetValue("script", out var script)
                || !values.TryGetValue("model", out var model)
                || !values.TryGetValue("name", out var name)
                || string.IsNullOrWhiteSpace(script)
                || string.IsNullOrWhiteSpace(model))
            {
                continue;
            }
            result.Add(new CharacterAuthoringEntry(
                CharacterAuthoringKind.Enemy,
                null,
                string.IsNullOrWhiteSpace(name) ? script : name,
                model,
                script,
                string.Empty,
                $"t_mons.tbl / status row {pair.index}"));
        }
        return result
            .DistinctBy(value => (value.AnimationScript, value.ModelAssetId))
            .OrderBy(value => value.AnimationScript, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string Label, string Model)> DistinctModels(
        ScriptCharacterDefinition character)
    {
        if (!string.IsNullOrWhiteSpace(character.ModelAssetId))
            yield return ("battle", character.ModelAssetId);
        if (!string.IsNullOrWhiteSpace(character.FieldAnimationAssetId)
            && !character.FieldAnimationAssetId.Equals(
                character.ModelAssetId, StringComparison.OrdinalIgnoreCase))
        {
            yield return ("field", character.FieldAnimationAssetId);
        }
    }

}
