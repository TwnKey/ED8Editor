using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed record EnemyEncounterReference(
    string ScriptPath,
    string SceneName,
    int TableFunctionIndex,
    string TableFunctionName,
    int EncounterId,
    int BattleScenarioId,
    int InstanceCount)
{
    public string Label =>
        $"{SceneName} / {TableFunctionName} / Encounter {EncounterId} "
        + $"— btl{BattleScenarioId:0000}, {InstanceCount} map instance(s)";
}

internal static class EnemyEncounterReferenceCatalog
{
    public static IReadOnlyList<EnemyEncounterReference> Find(
        string gameDataPath,
        string enemyAssetId,
        string? instructionDefinitionsPath)
    {
        var directory = new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(
                gameDataPath, "scripts", "scena", locale))
            .FirstOrDefault(Directory.Exists);
        if (directory is null) return Array.Empty<EnemyEncounterReference>();

        var result = new List<EnemyEncounterReference>();
        foreach (var path in Directory.EnumerateFiles(
                     directory, "*.dat", SearchOption.TopDirectoryOnly))
        {
            DecompiledScript script;
            try
            {
                script = ScriptDecompiler.Decompile(path, instructionDefinitionsPath);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or InvalidOperationException)
            {
                continue;
            }
            var spawns = ScriptMonsterSpawnReader.Read(script);
            foreach (var function in script.Functions.Where(value =>
                         value.Table is { Kind: "CreateMonsters", IsStale: false }))
            {
                if (!CreateMonstersTableReader.TryRead(
                        function.Table!, out var table)
                    || table is null)
                {
                    continue;
                }
                var descriptor = table.HeaderFields.Count > 2
                    ? unchecked((uint)table.HeaderFields[2].IntValue)
                    : 0;
                foreach (var encounter in table.Encounters.Where(value =>
                             value.MonsterAssets.Any(asset =>
                                 asset.Equals(
                                     enemyAssetId,
                                     StringComparison.OrdinalIgnoreCase))))
                {
                    result.Add(new EnemyEncounterReference(
                        path,
                        script.SceneName,
                        function.Index,
                        function.Name,
                        encounter.Id,
                        (int)(descriptor & 0xffff),
                        spawns.Count(value =>
                            value.BattleFunctionIndex == function.Index
                            && value.EncounterIndex == encounter.Id)));
                }
            }
        }
        return result
            .OrderBy(value => value.SceneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.EncounterId)
            .ToArray();
    }
}
