using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

internal sealed record EnemyBattleProfile(
    int DocumentIndex,
    string TablePath,
    string AssetId,
    string DisplayName,
    string ModelAssetId,
    string ModelName,
    string AiScriptName,
    string AnimationScriptName,
    IReadOnlyList<Cs1TableFieldValue> Fields)
{
    public string Label => $"{DisplayName} — {AssetId}";
}

internal sealed record EnemyBattleAction(
    int Index,
    int ActionId,
    string AnimationFunction,
    string DisplayLabel,
    byte[] Parameters);

internal sealed record EnemyBattleRule(
    int Index,
    int ActionId,
    int ConditionSelector,
    int Probability,
    int Enabled,
    int TargetSelector,
    int Threshold,
    int ParameterA,
    int ParameterB,
    byte[] AdditionalParameters);

internal sealed record EnemySupplementalTable(
    string Kind,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

internal sealed record EnemyBattleAnalysis(
    string? AiScriptPath,
    string? AnimationScriptPath,
    IReadOnlyList<EnemyBattleAction> Actions,
    IReadOnlyList<EnemyBattleRule> Rules,
    IReadOnlyList<EnemySupplementalTable> SupplementalTables,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Exact, read-only projection of the native enemy resources. Unknown binary
/// fields retain stable names and hexadecimal values so later reverse
/// engineering can enrich the catalog without changing the editor model.
/// </summary>
internal static class EnemyBattleCatalog
{
    public static IReadOnlyList<EnemyBattleProfile> LoadProfiles(string gameDataPath)
    {
        var tablePath = ResolveLocalizedFile(gameDataPath, "text", "t_mons.tbl");
        if (tablePath is null) return Array.Empty<EnemyBattleProfile>();
        var codec = new Cs1TableRecordCodec(textEncoding: EncodingFor(tablePath));
        var result = new List<EnemyBattleProfile>();
        foreach (var indexed in Cs1TableDocument.Read(tablePath).Entries
                     .Select((entry, index) => (Entry: entry, Index: index))
                     .Where(value => value.Entry.Category == "status"))
        {
            var fields = codec.Decode(indexed.Entry);
            if (fields is null) continue;
            var values = fields.ToDictionary(
                value => value.Field.Name,
                value => value.Value,
                StringComparer.Ordinal);
            var ai = Value(values, "script");
            var modelAsset = Value(values, "texture");
            var modelName = Value(values, "model");
            if (string.IsNullOrWhiteSpace(ai) || string.IsNullOrWhiteSpace(modelAsset))
                continue;
            result.Add(new EnemyBattleProfile(
                indexed.Index,
                tablePath,
                ai,
                Value(values, "name", ai),
                modelAsset,
                modelName,
                ai,
                string.IsNullOrWhiteSpace(modelName) ? ai : modelName,
                fields));
        }
        return result
            .OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static EnemyBattleAnalysis Analyze(
        string gameDataPath,
        EnemyBattleProfile profile,
        string? instructionDefinitionsPath)
    {
        var aiPath = ResolveBattleScript(gameDataPath, "al" + profile.AiScriptName);
        var aniPath = ResolveAniScript(gameDataPath, profile.AnimationScriptName);
        var actions = new List<EnemyBattleAction>();
        var rules = new List<EnemyBattleRule>();
        var supplemental = new List<EnemySupplementalTable>();
        var diagnostics = new List<string>();
        if (aiPath is null)
        {
            diagnostics.Add($"Missing AI script: al{profile.AiScriptName}.dat");
        }
        else
        {
            try
            {
                var script = ScriptDecompiler.Decompile(aiPath, instructionDefinitionsPath);
                foreach (var function in script.Functions.Where(value =>
                             value.Table is { IsStale: false }))
                {
                    switch (function.Table!.Kind)
                    {
                        case "ActionTable":
                            actions.AddRange(ReadActions(function.Table));
                            break;
                        case "AlgoTable":
                            rules.AddRange(ReadRules(function.Table));
                            break;
                        case "SummonTable":
                        case "PartTable":
                        case "ReactionTable":
                        case "AddCollision":
                            supplemental.Add(ReadSupplemental(function.Table));
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or InvalidOperationException)
            {
                diagnostics.Add($"AI script could not be decoded: {exception.Message}");
            }
        }

        if (aniPath is null)
            diagnostics.Add($"Missing ANI program: {profile.AnimationScriptName}.dat");
        var localActions = actions.Select(value => value.ActionId).ToHashSet();
        foreach (var rule in rules.Where(value =>
                     value.ActionId >= 1000 && !localActions.Contains(value.ActionId)))
        {
            diagnostics.Add(
                $"AI rule {rule.Index} references missing local action {rule.ActionId}.");
        }
        return new EnemyBattleAnalysis(
            aiPath,
            aniPath,
            actions,
            rules,
            supplemental,
            diagnostics);
    }

    public static string? ResolveBattleScript(string gameDataPath, string scriptName)
        => ResolveLocalizedFile(
            gameDataPath,
            Path.Combine("scripts", "battle"),
            NormalizeDatName(scriptName));

    public static string? ResolveAniScript(string gameDataPath, string scriptName)
        => ResolveLocalizedFile(
            gameDataPath,
            Path.Combine("scripts", "ani"),
            NormalizeDatName(scriptName));

    public static void SaveProfile(
        EnemyBattleProfile profile,
        IReadOnlyDictionary<string, string> values,
        Action<string, bool> onSaving)
    {
        var document = Cs1TableDocument.Read(profile.TablePath);
        if (profile.DocumentIndex < 0 || profile.DocumentIndex >= document.Entries.Count)
            throw new InvalidOperationException("The selected t_mons row no longer exists.");
        var entry = document.Entries[profile.DocumentIndex];
        if (entry.Category != "status")
            throw new InvalidOperationException("The selected t_mons row changed category.");
        var codec = new Cs1TableRecordCodec(textEncoding: EncodingFor(profile.TablePath));
        var fields = codec.Decode(entry)
            ?? throw new InvalidDataException("The selected status row has no schema.");
        var edited = fields.Select(field =>
            new Cs1TableFieldValue(
                field.Field,
                values.TryGetValue(field.Field.Name, out var value)
                    ? value
                    : field.Value)).ToArray();
        entry.Data = codec.Encode(entry.Category, edited);
        onSaving(profile.TablePath, true);
        document.Write(profile.TablePath);
        onSaving(profile.TablePath, false);
    }

    private static IReadOnlyList<EnemyBattleAction> ReadActions(DecompiledTable table)
    {
        if (table.Fields.Count == 0 || table.Fields[0].Type != "u8")
            return Array.Empty<EnemyBattleAction>();
        var count = checked((int)table.Fields[0].IntValue);
        var result = new List<EnemyBattleAction>(count);
        var cursor = 1;
        for (var index = 0; index < count && cursor + 4 < table.Fields.Count; index++)
        {
            var parameters = table.Fields[cursor++].Raw;
            var animation = table.Fields[cursor++].Text ?? string.Empty;
            cursor++; // fixed string fill
            var label = table.Fields[cursor++].Text ?? string.Empty;
            cursor++; // fixed string fill
            if (parameters.Length < 2) break;
            result.Add(new EnemyBattleAction(
                index,
                BinaryPrimitives.ReadInt16LittleEndian(parameters),
                animation,
                label,
                parameters));
        }
        return result;
    }

    private static IReadOnlyList<EnemyBattleRule> ReadRules(DecompiledTable table)
    {
        if (table.Fields.Count == 0 || table.Fields[0].Type != "u8")
            return Array.Empty<EnemyBattleRule>();
        var count = checked((int)table.Fields[0].IntValue);
        var result = new List<EnemyBattleRule>(count);
        var cursor = 1;
        for (var index = 0; index < count && cursor + 6 < table.Fields.Count; index++)
        {
            var action = checked((int)table.Fields[cursor++].IntValue);
            var conditionChance = unchecked((ushort)table.Fields[cursor++].IntValue);
            var targetGate = unchecked((ushort)table.Fields[cursor++].IntValue);
            var conditionParameters = table.Fields[cursor++].Raw;
            var parameterA = checked((int)table.Fields[cursor++].IntValue);
            var parameterB = checked((int)table.Fields[cursor++].IntValue);
            var trailing = table.Fields[cursor++].Raw;
            var additional = conditionParameters.Concat(trailing).ToArray();
            var threshold = conditionParameters.Length >= 2
                ? BinaryPrimitives.ReadInt16LittleEndian(conditionParameters)
                : 0;
            result.Add(new EnemyBattleRule(
                index,
                action,
                conditionChance & 0xff,
                conditionChance >> 8,
                targetGate & 0xff,
                targetGate >> 8,
                threshold,
                parameterA,
                parameterB,
                additional));
        }
        return result;
    }

    private static EnemySupplementalTable ReadSupplemental(DecompiledTable table)
    {
        var rows = table.Kind switch
        {
            "SummonTable" => ReadSummons(table),
            "PartTable" => ReadParts(table),
            "ReactionTable" => ReadReactions(table),
            "AddCollision" => ReadCollisions(table),
            _ => Array.Empty<IReadOnlyDictionary<string, string>>(),
        };
        return new EnemySupplementalTable(table.Kind, rows);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadSummons(
        DecompiledTable table)
    {
        var count = Count(table);
        var result = new List<IReadOnlyDictionary<string, string>>();
        var cursor = 1;
        for (var index = 0; index < count && cursor + 4 < table.Fields.Count; index++)
        {
            var row = new Dictionary<string, string>
            {
                ["Index"] = index.ToString(CultureInfo.InvariantCulture),
                ["Slot mask / ID"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
                ["Unknown byte 1"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
                ["Unknown byte 2"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
                ["Enemy script"] = table.Fields[cursor++].Text ?? string.Empty,
            };
            cursor++; // fixed string fill
            result.Add(row);
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadParts(
        DecompiledTable table)
    {
        var count = Count(table);
        var result = new List<IReadOnlyDictionary<string, string>>();
        var cursor = 1;
        for (var index = 0; index < count && cursor + 4 < table.Fields.Count; index++)
        {
            result.Add(new Dictionary<string, string>
            {
                ["Index"] = index.ToString(CultureInfo.InvariantCulture),
                ["Part ID / flags"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
                ["Model asset"] = table.Fields[cursor++].Text ?? string.Empty,
                ["Attachment node"] = table.Fields[cursor + 1].Text ?? string.Empty,
            });
            cursor += 3; // first fill, node string and node fill
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadReactions(
        DecompiledTable table)
    {
        var count = Count(table);
        var result = new List<IReadOnlyDictionary<string, string>>();
        var cursor = 1;
        for (var index = 0; index < count && cursor + 5 < table.Fields.Count; index++)
        {
            var row = new Dictionary<string, string>
            {
                ["Index"] = index.ToString(CultureInfo.InvariantCulture),
                ["Action ID"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
            };
            for (var field = 1; field <= 5; field++)
                row[$"Unknown {field}"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture);
            result.Add(row);
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadCollisions(
        DecompiledTable table)
    {
        var count = Count(table);
        var result = new List<IReadOnlyDictionary<string, string>>();
        var cursor = 1;
        for (var index = 0; index < count && cursor + 5 < table.Fields.Count; index++)
        {
            var row = new Dictionary<string, string>
            {
                ["Index"] = index.ToString(CultureInfo.InvariantCulture),
                ["Primitive type"] = table.Fields[cursor++].IntValue.ToString(CultureInfo.InvariantCulture),
            };
            for (var field = 1; field <= 5; field++)
                row[$"Float {field}"] = table.Fields[cursor++].FloatValue.ToString("G9", CultureInfo.InvariantCulture);
            result.Add(row);
        }
        return result;
    }

    private static int Count(DecompiledTable table)
        => table.Fields.Count == 0 ? 0 : checked((int)table.Fields[0].IntValue);

    private static string NormalizeDatName(string value)
        => value.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            ? value
            : value + ".dat";

    private static string? ResolveLocalizedFile(
        string gameDataPath,
        string relativeDirectory,
        string fileName)
        => new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(
                gameDataPath, relativeDirectory, locale, fileName))
            .FirstOrDefault(File.Exists);

    private static Encoding EncodingFor(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                "dat",
                StringComparison.OrdinalIgnoreCase)
                ? Encoding.GetEncoding(932)
                : new UTF8Encoding(false, true);
    }

    private static string Value(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback = "")
        => values.TryGetValue(key, out var value) ? value : fallback;
}
