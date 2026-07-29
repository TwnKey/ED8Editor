namespace ED8Editor.Decompiler;

public sealed record QuestScriptSource(string Path, DecompiledScript Script);

public sealed record QuestScriptCorpusIndex(
    IReadOnlyList<QuestScriptMutation> Mutations,
    int ScannedScriptCount,
    IReadOnlyList<string> UnreadableScripts);

/// <summary>
/// A script-side mutation of a quest. The analyzer deliberately keys off the
/// opcode and selector bytes, never function-name conventions such as EV_QS*.
/// Unknown OP103 selectors remain visible so later research can name them
/// without changing the quest editor's data model.
/// </summary>
public sealed record QuestScriptMutation(
    string ScriptPath,
    int FunctionIndex,
    string FunctionName,
    int InstructionIndex,
    int InstructionOffset,
    int QuestId,
    int Selector,
    int? Value,
    QuestMutationKind Kind)
{
    public string Location =>
        $"{Path.GetFileName(ScriptPath)} :: {FunctionName} :: #{InstructionIndex}";
}

public enum QuestMutationKind
{
    JournalStage,
    LifecycleFlags,
    UnknownSelector2,
    UnknownSelector4,
    UnknownSelector5,
    UnknownSelector6,
    UnknownSelector,
}

/// <summary>
/// Extracts the quest state operations currently verified in the CS1 script
/// corpus. This is intentionally an inventory, not an execution heuristic:
/// validation conditions and rewards remain normal instructions in the graph.
/// </summary>
public sealed class QuestScriptAnalyzer
{
    private const int QuestOpcode = 103;

    public IReadOnlyList<QuestScriptMutation> Analyze(
        string scriptPath,
        DecompiledScript script)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            throw new ArgumentException("A script path is required.", nameof(scriptPath));
        ArgumentNullException.ThrowIfNull(script);
        var result = new List<QuestScriptMutation>();
        foreach (var function in script.Functions.Where(value => value.IsCode))
        {
            foreach (var instruction in function.Instructions.Where(value =>
                         value.Opcode == QuestOpcode && value.Arguments.Count >= 2))
            {
                var questId = instruction.Arguments[0].IntValue;
                var selector = instruction.Arguments[1].IntValue;
                int? value = instruction.Arguments.Count >= 3
                    ? instruction.Arguments[2].IntValue
                    : null;
                result.Add(new QuestScriptMutation(
                    Path.GetFullPath(scriptPath),
                    function.Index,
                    function.Name,
                    instruction.Index,
                    instruction.Offset,
                    questId,
                    selector,
                    value,
                    Classify(selector)));
            }
        }
        return result;
    }

    private static QuestMutationKind Classify(int selector) => selector switch
    {
        1 => QuestMutationKind.JournalStage,
        2 => QuestMutationKind.UnknownSelector2,
        3 => QuestMutationKind.LifecycleFlags,
        4 => QuestMutationKind.UnknownSelector4,
        5 => QuestMutationKind.UnknownSelector5,
        6 => QuestMutationKind.UnknownSelector6,
        _ => QuestMutationKind.UnknownSelector,
    };
}

/// <summary>
/// Builds a quest-only index for a scenario-script directory. Decompiled
/// scripts are discarded immediately; the compact mutation records are the
/// only retained data.
/// </summary>
public sealed class QuestScriptCorpusIndexer
{
    public QuestScriptCorpusIndex AnalyzeDirectory(
        string directory,
        string? instructionDefinitionsPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            return new QuestScriptCorpusIndex(
                Array.Empty<QuestScriptMutation>(), 0, Array.Empty<string>());
        var analyzer = new QuestScriptAnalyzer();
        var mutations = new List<QuestScriptMutation>();
        var unreadable = new List<string>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dat", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var script = ScriptDecompiler.Decompile(path, instructionDefinitionsPath);
                mutations.AddRange(analyzer.Analyze(path, script));
                scanned++;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or InvalidOperationException
                or ArgumentException)
            {
                unreadable.Add($"{path}: {exception.Message}");
            }
        }
        return new QuestScriptCorpusIndex(mutations, scanned, unreadable);
    }
}
