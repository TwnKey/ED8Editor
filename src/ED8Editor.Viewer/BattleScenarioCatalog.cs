using System.Text.RegularExpressions;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

public sealed record BattleScenarioEntry(
    int Id,
    string Path)
{
    public string Label => $"btl{Id:0000}";
}

internal sealed record BattleLifecycleFunction(
    int FunctionIndex,
    string Name,
    int InstructionCount,
    IReadOnlyList<string> Operations);

internal sealed record BattleScenarioAnalysis(
    BattleScenarioEntry Entry,
    IReadOnlyList<BattleLifecycleFunction> Lifecycle,
    IReadOnlyList<int> ReferencedEnemySlots,
    IReadOnlyList<string> SharedCalls,
    IReadOnlyList<string> Diagnostics);

internal static partial class BattleScenarioCatalog
{
    private static readonly HashSet<string> LifecycleNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BattleInit",
            "BattleStart",
            "BattleTurn",
            "BattleRelease",
            "BattleRetry",
            "BattleCamera",
        };

    public static IReadOnlyList<BattleScenarioEntry> Load(string gameDataPath)
    {
        var directory = ResolveDirectory(gameDataPath);
        if (directory is null) return Array.Empty<BattleScenarioEntry>();
        return Directory.EnumerateFiles(directory, "btl*.dat")
            .Select(path => (Path: path, Match: BattleName().Match(Path.GetFileName(path))))
            .Where(value => value.Match.Success)
            .Select(value => new BattleScenarioEntry(
                int.Parse(value.Match.Groups[1].Value),
                value.Path))
            .OrderBy(value => value.Id)
            .ToArray();
    }

    public static BattleScenarioAnalysis Analyze(
        BattleScenarioEntry entry,
        string? instructionDefinitionsPath)
    {
        var diagnostics = new List<string>();
        DecompiledScript script;
        try
        {
            script = ScriptDecompiler.Decompile(entry.Path, instructionDefinitionsPath);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException)
        {
            return new BattleScenarioAnalysis(
                entry,
                Array.Empty<BattleLifecycleFunction>(),
                Array.Empty<int>(),
                Array.Empty<string>(),
                new[] { exception.Message });
        }

        var code = script.Functions.Where(value => value.IsCode).ToArray();
        var lifecycle = code
            .Where(function => LifecycleNames.Contains(function.Name)
                || function.Name.StartsWith("Battle", StringComparison.OrdinalIgnoreCase))
            .Select(function => new BattleLifecycleFunction(
                function.Index,
                function.Name,
                function.Instructions.Count,
                function.Instructions
                    .Select(value => value.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        var enemySlots = code
            .SelectMany(function => function.Instructions)
            .SelectMany(instruction => instruction.Arguments)
            .Where(argument => argument.Kind == "scalar"
                && argument.IntValue is >= 2000 and <= 2007)
            .Select(argument => argument.IntValue)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        var sharedCalls = code
            .SelectMany(function => function.Instructions)
            .Where(instruction => instruction.Opcode is 2 or 10 or 11 or 12)
            .Where(instruction => instruction.Name.Contains(
                "Call", StringComparison.OrdinalIgnoreCase))
            .Select(instruction =>
                $"{instruction.Name}: {string.Join(", ", instruction.Arguments.Select(FormatArgument))}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new BattleScenarioAnalysis(
            entry, lifecycle, enemySlots, sharedCalls, diagnostics);
    }

    public static BattleScenarioEntry? Find(
        IReadOnlyList<BattleScenarioEntry> entries,
        int id)
        => entries.FirstOrDefault(value => value.Id == id);

    private static string FormatArgument(InstructionArgument argument)
        => argument.Kind == "string"
            ? System.Text.Encoding.Latin1.GetString(argument.Raw).TrimEnd('\0')
            : argument.IntValue.ToString();

    private static string? ResolveDirectory(string gameDataPath)
        => new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(
                gameDataPath, "scripts", "battle", locale))
            .FirstOrDefault(Directory.Exists);

    [GeneratedRegex("^btl(\\d{4})\\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex BattleName();
}
