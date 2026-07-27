using System.IO.Compression;
using System.Text.Json;

namespace ED8Editor.Application;

public sealed record ModProjectFile(
    string RelativePath,
    bool HasOriginal,
    bool HasModCopy,
    DateTimeOffset LastSaved)
{
    /// <summary>Backslash-free path segments, for a tree view.</summary>
    public IReadOnlyList<string> Segments
        => RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A mod is a set of game files the editor has written, plus a pristine copy of
/// each one taken the first time it was touched. Both copies live next to the
/// project file, never inside the game folder, so the game install can always be
/// put back exactly as it was and the mod can be handed to someone else.
///
/// Layout: <c>&lt;project&gt;.ed8mod</c> and, beside it,
/// <c>&lt;project&gt;.files/original/&lt;game relative path&gt;</c> and
/// <c>.../current/&lt;game relative path&gt;</c>. A file that the game does not
/// ship (a brand new asset) simply has no original copy, and restoring deletes it.
/// </summary>
public sealed class ModProject
{
    private const string OriginalFolder = "original";
    private const string CurrentFolder = "current";

    private readonly Dictionary<string, ModProjectFile> files =
        new(StringComparer.OrdinalIgnoreCase);

    private ModProject(string projectPath, string gameDirectory, string name)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        GameDirectory = Path.GetFullPath(gameDirectory);
        Name = name;
    }

    public string ProjectPath { get; }
    public string GameDirectory { get; }
    public string Name { get; private set; }

    public string StoreDirectory => Path.Combine(
        Path.GetDirectoryName(ProjectPath)!,
        Path.GetFileNameWithoutExtension(ProjectPath) + ".files");

    public IReadOnlyCollection<ModProjectFile> Files
        => files.Values.OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();

    public static ModProject Create(string projectPath, string gameDirectory, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("A project path is required.", nameof(projectPath));
        if (!Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException($"The game directory '{gameDirectory}' does not exist.");
        var project = new ModProject(
            projectPath,
            gameDirectory,
            string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(projectPath) : name.Trim());
        project.Save();
        return project;
    }

    public static ModProject Open(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var document = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException($"'{fullPath}' is not a readable mod project.");
        if (string.IsNullOrWhiteSpace(document.GameDirectory))
            throw new InvalidDataException("The mod project has no game directory.");
        var project = new ModProject(fullPath, document.GameDirectory, document.Name ?? "mod");
        foreach (var entry in document.Files ?? new List<ProjectFileDocument>())
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            var relative = Normalize(entry.Path);
            project.files[relative] = new ModProjectFile(
                relative,
                File.Exists(project.StorePath(OriginalFolder, relative)),
                File.Exists(project.StorePath(CurrentFolder, relative)),
                entry.LastSaved);
        }
        return project;
    }

    public void Save()
    {
        var document = new ProjectDocument
        {
            Name = Name,
            GameDirectory = GameDirectory,
            Files = Files
                .Select(value => new ProjectFileDocument
                {
                    Path = value.RelativePath,
                    LastSaved = value.LastSaved,
                })
                .ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);
        File.WriteAllText(
            ProjectPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Call before writing <paramref name="gameFilePath"/>: it captures the
    /// pristine file once, so the very first save is still reversible.
    /// </summary>
    public void CaptureOriginal(string gameFilePath)
    {
        var relative = RequireRelative(gameFilePath);
        var backup = StorePath(OriginalFolder, relative);
        if (File.Exists(backup) || !File.Exists(gameFilePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(gameFilePath, backup);
    }

    /// <summary>
    /// Call after writing <paramref name="gameFilePath"/>: it keeps the mod's own
    /// copy of the file so the project can be re-applied or shipped.
    /// </summary>
    public void TrackSave(string gameFilePath)
    {
        var relative = RequireRelative(gameFilePath);
        if (!File.Exists(gameFilePath))
            throw new FileNotFoundException($"'{gameFilePath}' was not written.", gameFilePath);
        var copy = StorePath(CurrentFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(gameFilePath, copy, overwrite: true);
        files[relative] = new ModProjectFile(
            relative,
            File.Exists(StorePath(OriginalFolder, relative)),
            true,
            DateTimeOffset.Now);
        Save();
    }

    /// <summary>Puts the game files back exactly as they were before the mod.</summary>
    public int RestoreOriginals(IEnumerable<string>? relativePaths = null)
    {
        var restored = 0;
        foreach (var relative in Select(relativePaths))
        {
            var backup = StorePath(OriginalFolder, relative);
            var target = Path.Combine(GameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
                restored++;
            }
            else if (File.Exists(target))
            {
                // The mod added this file; the game never had it.
                File.Delete(target);
                restored++;
            }
        }
        return restored;
    }

    /// <summary>Writes the mod's files back into the game folder.</summary>
    public int ApplyMod(IEnumerable<string>? relativePaths = null)
    {
        var applied = 0;
        foreach (var relative in Select(relativePaths))
        {
            var copy = StorePath(CurrentFolder, relative);
            if (!File.Exists(copy)) continue;
            var target = Path.Combine(GameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            CaptureOriginal(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(copy, target, overwrite: true);
            applied++;
        }
        return applied;
    }

    /// <summary>
    /// Zips the mod's files with the game-relative paths at the archive root, so
    /// a player only has to extract it over their installation.
    /// </summary>
    public int ExportArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("An archive path is required.", nameof(archivePath));
        var fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        var written = 0;
        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        foreach (var file in Files)
        {
            var copy = StorePath(CurrentFolder, file.RelativePath);
            if (!File.Exists(copy)) continue;
            archive.CreateEntryFromFile(copy, file.RelativePath, CompressionLevel.Optimal);
            written++;
        }
        return written;
    }

    public bool Contains(string gameFilePath)
        => TryGetRelative(gameFilePath, out var relative) && files.ContainsKey(relative);

    public string? OriginalCopyPath(string relativePath)
    {
        var path = StorePath(OriginalFolder, Normalize(relativePath));
        return File.Exists(path) ? path : null;
    }

    public string GameFilePath(string relativePath)
        => Path.Combine(GameDirectory, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Registers a file the mod ships without the editor having written it. The
    /// file on disk is already the mod's version, so no pristine copy can be
    /// taken from it: unless one was captured earlier, restoring treats the file
    /// as belonging to the mod and removes it.
    /// </summary>
    public void Include(string gameFilePath) => TrackSave(gameFilePath);

    public void Remove(string relativePath)
    {
        var relative = Normalize(relativePath);
        if (!files.Remove(relative)) return;
        foreach (var folder in new[] { OriginalFolder, CurrentFolder })
        {
            var path = StorePath(folder, relative);
            if (File.Exists(path)) File.Delete(path);
        }
        Save();
    }

    private IEnumerable<string> Select(IEnumerable<string>? relativePaths)
        => relativePaths is null
            ? files.Keys.ToArray()
            : relativePaths.Select(Normalize).Where(files.ContainsKey).ToArray();

    private string StorePath(string folder, string relativePath)
        => Path.Combine(
            StoreDirectory, folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string RequireRelative(string gameFilePath)
        => TryGetRelative(gameFilePath, out var relative)
            ? relative
            : throw new ArgumentException(
                $"'{gameFilePath}' is outside the game directory '{GameDirectory}'.", nameof(gameFilePath));

    private bool TryGetRelative(string gameFilePath, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(gameFilePath)) return false;
        var full = Path.GetFullPath(gameFilePath);
        var root = GameDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? GameDirectory
            : GameDirectory + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        relative = Normalize(full[root.Length..]);
        return relative.Length != 0;
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').Trim('/');

    private sealed class ProjectDocument
    {
        public string? Name { get; set; }
        public string? GameDirectory { get; set; }
        public List<ProjectFileDocument>? Files { get; set; }
    }

    private sealed class ProjectFileDocument
    {
        public string? Path { get; set; }
        public DateTimeOffset LastSaved { get; set; }
    }
}
