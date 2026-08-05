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

    /// <summary>The folder the game loads loose files from, when it has one.</summary>
    private const string DevelopmentFolder = "dev";

    private ModProject(string projectPath, string gameDirectory, string name)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        GameDirectory = Path.GetFullPath(gameDirectory);
        Name = name;
    }

    public string ProjectPath { get; }
    public string GameDirectory { get; }
    public string Name { get; private set; }

    /// <summary>Who made the mod, and what it is. Shipped with it, not with the game.</summary>
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Where this project's files are written.
    ///
    /// The game loads loose files from a <c>dev</c> folder that mirrors its own
    /// layout, and it loads them without being restarted — which is the difference
    /// between checking a change in ten seconds and in two minutes. So when that
    /// folder exists, it is where edits go; the game's own folder is left alone,
    /// which is also the safest place for it to be.
    ///
    /// Read at each use rather than cached: the folder can be made while the editor
    /// is open, and someone who has just made it means it to be used.
    /// </summary>
    public string ContentDirectory
    {
        get
        {
            var development = Path.Combine(GameDirectory, DevelopmentFolder);
            return Directory.Exists(development) ? development : GameDirectory;
        }
    }

    /// <summary>Whether edits are going to the loose-loading folder.</summary>
    public bool UsesDevelopmentFolder => !string.Equals(
        ContentDirectory, GameDirectory, StringComparison.OrdinalIgnoreCase);

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
        var project = new ModProject(fullPath, document.GameDirectory, document.Name ?? "mod")
        {
            Author = document.Author ?? string.Empty,
            Description = document.Description ?? string.Empty,
        };
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
            Author = Author,
            Description = Description,
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

    /// <summary>
    /// How the project names a file of the game folder, or null when the path is
    /// outside it. The file need not be tracked: a file being edited for the first
    /// time has no entry yet, and is still one this project would own.
    /// </summary>
    public string? RelativePathOf(string gameFilePath)
        => TryGetRelative(gameFilePath, out var relative) ? relative : null;

    public string? OriginalCopyPath(string relativePath)
    {
        var path = StorePath(OriginalFolder, Normalize(relativePath));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Where a file of this project is written.</summary>
    public string GameFilePath(string relativePath)
        => Path.Combine(
            ContentDirectory, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Where a file is read from: the loose-loading folder first, the game's own
    /// after. That is the order the game itself resolves them in, so what the editor
    /// shows is what the game would load.
    /// </summary>
    public string ResolveExisting(string relativePath)
    {
        var normalized = Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar);
        var loose = Path.Combine(ContentDirectory, normalized);
        return File.Exists(loose) ? loose : Path.Combine(GameDirectory, normalized);
    }

    /// <summary>
    /// Takes every file already in the loose-loading folder into the project.
    ///
    /// Those files are somebody's mod — often several people's — and they are
    /// already what the game loads. Leaving them outside the project would mean
    /// editing them without the project knowing, so nothing could be reverted and
    /// nothing shipped. No pristine copy is taken: the file on disk is already a
    /// modified one, and pretending otherwise would let a revert write it back as if
    /// it were the game's.
    /// </summary>
    public int ImportDevelopmentFiles()
    {
        var root = Path.Combine(GameDirectory, DevelopmentFolder);
        if (!Directory.Exists(root)) return 0;
        var imported = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!TryGetRelative(path, out var relative)) continue;
            if (files.ContainsKey(relative)) continue;
            TrackSave(path);
            imported++;
        }
        if (imported != 0) Save();
        return imported;
    }

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
        // A file is named the same whichever root it sits under, so the loose folder
        // is tried first: it is the longer path, and under it every file is also
        // under the game folder.
        foreach (var candidate in new[] { ContentDirectory, GameDirectory })
        {
            var root = candidate.EndsWith(Path.DirectorySeparatorChar)
                ? candidate
                : candidate + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            relative = Normalize(full[root.Length..]);
            if (relative.Length != 0) return true;
        }
        return false;
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').Trim('/');

    private sealed class ProjectDocument
    {
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? GameDirectory { get; set; }
        public List<ProjectFileDocument>? Files { get; set; }
    }

    private sealed class ProjectFileDocument
    {
        public string? Path { get; set; }
        public DateTimeOffset LastSaved { get; set; }
    }
}
