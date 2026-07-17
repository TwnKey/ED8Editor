namespace ED8Editor.ScriptHeaders;

public static class GameDataLocator
{
    public static string? FromScriptPath(string scriptPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!);

        while (directory is not null)
        {
            if (directory.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
