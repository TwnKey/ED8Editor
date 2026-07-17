using ED8Editor.Core;

namespace ED8Editor.ScriptHeaders;

internal static class ScriptClassifier
{
    private static readonly IReadOnlyDictionary<string, ScriptKind> DirectoryKinds =
        new Dictionary<string, ScriptKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["scena"] = ScriptKind.Scenario,
            ["talk"] = ScriptKind.Talk,
            ["ani"] = ScriptKind.Animation,
            ["battle"] = ScriptKind.Battle,
            ["book"] = ScriptKind.Book,
            ["ui"] = ScriptKind.UserInterface,
        };

    public static ScriptKind FromPath(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        while (directory is not null)
        {
            var name = Path.GetFileName(directory);
            if (DirectoryKinds.TryGetValue(name, out var kind))
            {
                return kind;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return ScriptKind.Unknown;
    }

    public static ScriptTargetKind TargetFor(ScriptKind kind) => kind switch
    {
        ScriptKind.Scenario => ScriptTargetKind.Map,
        ScriptKind.Talk => ScriptTargetKind.Character,
        _ => ScriptTargetKind.Unknown,
    };
}
