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
    string? BattleAiScript,
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
/// NameTableData and enemy rows from decoded t_mons fields. For enemies the
/// loadable PKG/Phyre asset is "texture" (C_MONxxx); "model" is the model name
/// inside that asset (monxxx).
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
                    null,
                    character.FacialAssetId,
                    "t_name.tbl / NameTableData")))
            .ToArray();

    public static IReadOnlyList<CharacterAuthoringEntry> LoadEnemies(string gameDataPath)
    {
        return EnemyBattleCatalog.LoadProfiles(gameDataPath)
            .Select(profile => new CharacterAuthoringEntry(
                CharacterAuthoringKind.Enemy,
                profile.DocumentIndex,
                profile.DisplayName,
                profile.ModelAssetId,
                profile.AnimationScriptName,
                profile.AiScriptName,
                string.Empty,
                $"t_mons.tbl / status row {profile.DocumentIndex}"))
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
