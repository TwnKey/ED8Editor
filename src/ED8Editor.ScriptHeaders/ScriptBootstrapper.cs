using ED8Editor.Core;

namespace ED8Editor.ScriptHeaders;

public sealed class ScriptBootstrapper
{
    private readonly ScriptHeaderReader headerReader;

    public ScriptBootstrapper(ScriptHeaderReader? headerReader = null)
    {
        this.headerReader = headerReader ?? new ScriptHeaderReader();
    }

    public ScriptOpenResult Open(string scriptPath, string? explicitGameDataPath = null)
    {
        var header = headerReader.Read(scriptPath);
        var gameDataPath = explicitGameDataPath is null
            ? GameDataLocator.FromScriptPath(scriptPath)
            : Path.GetFullPath(explicitGameDataPath);

        var mapOpsPath = ResolveMapOps(header, gameDataPath);
        return new ScriptOpenResult(header, gameDataPath, mapOpsPath);
    }

    private static string? ResolveMapOps(ScriptHeader header, string? gameDataPath)
    {
        if (header.TargetKind != ScriptTargetKind.Map || gameDataPath is null)
        {
            return null;
        }

        var candidate = Path.Combine(gameDataPath, "ops", $"{header.Identifier}.ops");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}
