namespace ED8Editor.Models;

public interface IModelFormatImporter
{
    IReadOnlyCollection<string> SupportedExtensions { get; }

    ImportedModelScene Import(string modelPath);
}

public sealed class ModelImportService
{
    private readonly IReadOnlyList<IModelFormatImporter> importers;

    public ModelImportService(IEnumerable<IModelFormatImporter>? importers = null)
    {
        this.importers = (importers ?? new IModelFormatImporter[]
        {
            new AssimpModelImporter(),
        }).ToArray();
    }

    public IReadOnlyList<ModelImportCandidate> FindCandidates(string path)
        => ModelImportCatalog.Find(path);

    public ImportedModelScene Import(string modelPath, string? packageRoot = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("A model path is required.", nameof(modelPath));
        var fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Model source was not found.", fullPath);
        var extension = Path.GetExtension(fullPath);
        var importer = importers.SingleOrDefault(value =>
            value.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (importer is null)
            throw new NotSupportedException($"No model importer handles '{extension}'.");
        var imported = importer.Import(fullPath);
        var root = packageRoot is null
            ? Path.GetDirectoryName(fullPath)!
            : Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Model package root was not found: {root}");
        imported = ResolvePackageTextures(imported, root);
        imported = AddUnboundPackageTextures(imported, root);
        var validation = ImportedModelValidator.Validate(imported);
        return imported with
        {
            Diagnostics = imported.Diagnostics.Concat(validation).ToArray(),
        };
    }

    private static ImportedModelScene ResolvePackageTextures(
        ImportedModelScene scene,
        string packageRoot)
    {
        var allFiles = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToArray();
        var byName = allFiles
            .GroupBy(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var textures = scene.Textures.ToArray();
        var diagnostics = scene.Diagnostics.ToList();
        for (var index = 0; index < textures.Length; index++)
        {
            var texture = textures[index];
            if (texture.SourcePath is not null
                || texture.Embedded
                || string.IsNullOrWhiteSpace(texture.SourceReference))
            {
                continue;
            }
            var reference = texture.SourceReference!;
            var normalized = reference.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            string? resolved = null;
            if (!Path.IsPathRooted(normalized))
            {
                var relative = Path.GetFullPath(Path.Combine(packageRoot, normalized));
                if (relative.StartsWith(
                        Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    && File.Exists(relative))
                {
                    resolved = relative;
                }
            }
            if (resolved is null
                && byName.TryGetValue(Path.GetFileName(normalized), out var matches)
                && matches.Length == 1)
            {
                resolved = matches[0];
            }
            if (resolved is null) continue;
            textures[index] = texture with
            {
                Name = Path.GetFileName(resolved),
                SourcePath = resolved,
                MediaType = MediaType(Path.GetExtension(resolved)),
                EncodedData = File.ReadAllBytes(resolved),
            };
            diagnostics.RemoveAll(value =>
                value.Code == "missing-texture"
                && value.Message.Equals(
                    $"Texture '{reference}' referenced by the model was not found.",
                    StringComparison.Ordinal));
            diagnostics.Add(new ImportedModelDiagnostic(
                ImportedDiagnosticSeverity.Information,
                "resolved-package-texture",
                $"Texture reference '{reference}' was resolved to "
                + $"'{Path.GetRelativePath(packageRoot, resolved)}'."));
        }
        return scene with { Textures = textures, Diagnostics = diagnostics };
    }

    private static ImportedModelScene AddUnboundPackageTextures(
        ImportedModelScene scene,
        string packageRoot)
    {
        var supported = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds" },
            StringComparer.OrdinalIgnoreCase);
        var knownPaths = scene.Textures
            .Where(texture => texture.SourcePath is not null)
            .Select(texture => Path.GetFullPath(texture.SourcePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = scene.Textures.ToList();
        var diagnostics = scene.Diagnostics.ToList();
        foreach (var path in Directory.EnumerateFiles(
                     packageRoot, "*", SearchOption.AllDirectories)
                 .Where(path => supported.Contains(Path.GetExtension(path)))
                 .Select(Path.GetFullPath)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!knownPaths.Add(path)) continue;
            textures.Add(new ImportedTexture(
                Path.GetFileName(path),
                path,
                MediaType(Path.GetExtension(path)),
                File.ReadAllBytes(path),
                false));
            diagnostics.Add(new ImportedModelDiagnostic(
                ImportedDiagnosticSeverity.Information,
                "unbound-package-texture",
                $"Package texture '{Path.GetRelativePath(packageRoot, path)}'"
                + " is preserved but not assigned to a material."));
        }
        return scene with
        {
            Textures = textures,
            Diagnostics = diagnostics,
        };
    }

    private static string MediaType(string extension)
        => extension.TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "bmp" => "image/bmp",
            "tga" => "image/x-tga",
            "dds" => "image/vnd-ms.dds",
            _ => "application/octet-stream",
        };
}
