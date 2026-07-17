using ED8Editor.Core;

namespace ED8Editor.Assets;

public sealed class GameAssetResolver : IAssetPackageResolver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AssetPackage>> packagesById;

    public GameAssetResolver(string assetRootPath)
    {
        if (string.IsNullOrWhiteSpace(assetRootPath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(assetRootPath));
        }

        AssetRootPath = Path.GetFullPath(assetRootPath);
        if (!Directory.Exists(AssetRootPath))
        {
            throw new DirectoryNotFoundException($"Asset directory '{AssetRootPath}' does not exist.");
        }

        packagesById = BuildIndex(AssetRootPath);
        Packages = packagesById.Values.SelectMany(packages => packages).ToArray();
        PackageCount = Packages.Count;
    }

    public string AssetRootPath { get; }

    public int PackageCount { get; }

    public IReadOnlyList<AssetPackage> Packages { get; }

    public int UniqueAssetCount => packagesById.Count;

    public AssetResolution Resolve(string assetId, AssetVariantPreference preference)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(assetId));
        }

        if (!packagesById.TryGetValue(assetId, out var candidates))
        {
            return new AssetResolution(
                assetId,
                AssetResolutionStatus.Missing,
                null,
                Array.Empty<AssetPackage>());
        }

        var order = preference == AssetVariantPreference.English
            ? new[] { AssetVariant.English, AssetVariant.Base, AssetVariant.Other }
            : new[] { AssetVariant.Base, AssetVariant.English, AssetVariant.Other };

        foreach (var variant in order)
        {
            var matches = candidates.Where(candidate => candidate.Variant == variant).ToArray();
            if (matches.Length == 1)
            {
                return new AssetResolution(
                    assetId,
                    AssetResolutionStatus.Resolved,
                    matches[0],
                    candidates);
            }

            if (matches.Length > 1)
            {
                return new AssetResolution(
                    assetId,
                    AssetResolutionStatus.Ambiguous,
                    null,
                    matches);
            }
        }

        return new AssetResolution(assetId, AssetResolutionStatus.Missing, null, candidates);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<AssetPackage>> BuildIndex(string root)
    {
        var mutable = new Dictionary<string, List<AssetPackage>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(root, "*.pkg", SearchOption.AllDirectories))
        {
            var assetId = Path.GetFileNameWithoutExtension(path);
            var variant = ClassifyVariant(root, path);
            var package = new AssetPackage(assetId, Path.GetFullPath(path), variant, new FileInfo(path).Length);

            if (!mutable.TryGetValue(assetId, out var packages))
            {
                packages = new List<AssetPackage>();
                mutable.Add(assetId, packages);
            }

            packages.Add(package);
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AssetPackage>)pair.Value
                .OrderBy(package => package.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static AssetVariant ClassifyVariant(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var firstSeparator = relativePath.IndexOf(Path.DirectorySeparatorChar);
        var directory = firstSeparator < 0 ? string.Empty : relativePath[..firstSeparator];

        if (directory.Equals("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            return AssetVariant.Base;
        }

        if (directory.Equals("D3D11_us", StringComparison.OrdinalIgnoreCase))
        {
            return AssetVariant.English;
        }

        return AssetVariant.Other;
    }
}
