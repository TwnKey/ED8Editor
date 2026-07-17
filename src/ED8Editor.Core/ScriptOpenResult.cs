namespace ED8Editor.Core;

public sealed record ScriptOpenResult(
    ScriptHeader Header,
    string? GameDataPath,
    string? MapOpsPath);
