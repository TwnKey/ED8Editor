namespace ED8Editor.Application;

public sealed record GameInstallation(string RootPath, string DataPath)
{
    public static bool TryOpen(string? selectedPath, out GameInstallation? installation, out string? reason)
    {
        installation = null;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            reason = "No game directory was selected.";
            return false;
        }
        var fullPath = Path.GetFullPath(selectedPath);
        var dataPath = Directory.Exists(Path.Combine(fullPath, "data"))
            ? Path.Combine(fullPath, "data")
            : string.Equals(Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)), "data", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        if (dataPath is null || !Directory.Exists(dataPath))
        {
            reason = "The selected directory contains no data folder.";
            return false;
        }
        var requiredDirectories = new[] { "scripts", "ops", "asset" };
        var missing = requiredDirectories.Where(name => !Directory.Exists(Path.Combine(dataPath, name))).ToArray();
        if (missing.Length != 0)
        {
            reason = $"The game data folder is missing: {string.Join(", ", missing)}.";
            return false;
        }
        dataPath = Path.GetFullPath(dataPath);
        var rootPath = Directory.GetParent(dataPath)?.FullName ?? dataPath;
        installation = new GameInstallation(rootPath, dataPath);
        reason = null;
        return true;
    }
}
