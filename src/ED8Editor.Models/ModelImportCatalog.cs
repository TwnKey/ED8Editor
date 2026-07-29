namespace ED8Editor.Models;

public sealed record ModelImportCandidate(
    string Path,
    string Format,
    long Length)
{
    public string DisplayName
        => $"{System.IO.Path.GetFileName(Path)} — {Format} ({Length / 1024d / 1024d:0.##} MB)";
}

/// <summary>
/// Finds model sources without guessing which one the user meant. A directory
/// with several model files is an explicit choice to present in the UI.
/// </summary>
public static class ModelImportCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Formats =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".fbx"] = "Autodesk FBX",
            [".glb"] = "glTF Binary",
            [".gltf"] = "glTF",
            [".obj"] = "Wavefront OBJ",
            [".dae"] = "COLLADA",
        };

    public static IReadOnlyCollection<string> SupportedExtensions
        => Formats.Keys.ToArray();

    public static string FileDialogFilter
        => "Supported 3D models|"
            + string.Join(";", Formats.Keys.Select(value => $"*{value}"))
            + "|All files|*.*";

    public static IReadOnlyList<ModelImportCandidate> Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A model path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return TryCreate(fullPath, out var candidate)
                ? new[] { candidate }
                : Array.Empty<ModelImportCandidate>();
        }
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Model import path was not found: {fullPath}");

        return Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
            .Select(file => TryCreate(file, out var candidate) ? candidate : null)
            .Where(candidate => candidate is not null)
            .Cast<ModelImportCandidate>()
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryCreate(string path, out ModelImportCandidate candidate)
    {
        var extension = Path.GetExtension(path);
        if (!Formats.TryGetValue(extension, out var format))
        {
            candidate = null!;
            return false;
        }
        var info = new FileInfo(path);
        candidate = new ModelImportCandidate(info.FullName, format, info.Length);
        return true;
    }
}
