namespace ED8Editor.Viewer;

public sealed record MonsterTableChoice(
    string AssetId,
    string DisplayName,
    string ModelAssetId)
{
    public string Label => $"{DisplayName} — {AssetId}";
}

/// <summary>
/// Human-facing names for CreateMonsters asset identifiers. The key is the
/// decoded t_mons/status script field because real CreateMonsters records store
/// values such as mon116 and mon000_c01 in that namespace.
/// </summary>
internal static class MonsterTableCatalog
{
    public static IReadOnlyList<MonsterTableChoice> Load(string? gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) return Array.Empty<MonsterTableChoice>();
        return CharacterAuthoringCatalog.LoadEnemies(gameDataPath)
            .Where(value => !string.IsNullOrWhiteSpace(value.BattleAiScript))
            .GroupBy(value => value.BattleAiScript!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entry = group.First();
                return new MonsterTableChoice(
                    group.Key,
                    entry.DisplayName,
                    entry.ModelAssetId);
            })
            .OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
