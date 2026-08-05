namespace ED8Editor.Application;

/// <summary>
/// Where the game's content is, given that some of it may be loose.
///
/// The game reads a <c>dev</c> folder that mirrors its own layout and takes what it
/// finds there in preference to what it ships. Anything that walks the game's folders
/// has to walk both, in that order, or it lists what the game no longer loads.
///
/// The rule lives here rather than in each caller because it is one rule, and a copy
/// of it in six places is six places to forget it.
/// </summary>
public static class GameContentDirectories
{
    private const string DevelopmentFolder = "dev";

    /// <summary>
    /// The asset folders to search, most-preferred first: the loose one when it
    /// exists, then the game's own. Only folders that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> Assets(string gameDirectory)
        => Under(gameDirectory, Path.Combine("data", "asset", "D3D11"));

    /// <summary>The same, for any path relative to the game's root.</summary>
    public static IReadOnlyList<string> Under(string gameDirectory, string relative)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        ArgumentNullException.ThrowIfNull(relative);
        var root = Path.GetFullPath(gameDirectory);
        return new[]
            {
                Path.Combine(root, DevelopmentFolder, relative),
                Path.Combine(root, relative),
            }
            .Where(Directory.Exists)
            .ToArray();
    }

    /// <summary>
    /// Every file of a kind across those folders, one entry per name: a loose file
    /// stands in for the shipped one it shares a name with.
    /// </summary>
    public static IReadOnlyList<string> Files(IReadOnlyList<string> directories, string pattern)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                // The first folder to claim a name keeps it, and the folders arrive
                // in order of preference.
                found.TryAdd(Path.GetFileName(path), path);
            }
        }
        return found.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Where a file of the game is read from: the loose copy when there is one.
    /// </summary>
    public static string Resolve(string gameDirectory, string relative)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        var root = Path.GetFullPath(gameDirectory);
        var loose = Path.Combine(root, DevelopmentFolder, relative);
        return File.Exists(loose) ? loose : Path.Combine(root, relative);
    }
}
