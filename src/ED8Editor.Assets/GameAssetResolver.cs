using ED8Editor.Core;

namespace ED8Editor.Assets;

public sealed class GameAssetResolver : IAssetPackageResolver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AssetPackage>> packagesById;

    /// <param name="overrideRoots">
    /// Asset folders searched before the game's own, in order.
    ///
    /// The game loads loose files from a <c>dev</c> folder that mirrors its layout,
    /// and a package there stands in for the shipped one of the same name. So the
    /// editor has to look there first, or it shows the shipped asset while the game
    /// shows the modded one — which is the whole point of working loosely.
    /// </param>
    public GameAssetResolver(string assetRootPath, IReadOnlyList<string>? overrideRoots = null)
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

        OverrideRoots = (overrideRoots ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .ToArray();
        packagesById = BuildIndex(AssetRootPath, OverrideRoots);
        Packages = packagesById.Values.SelectMany(packages => packages).ToArray();
        PackageCount = Packages.Count;
    }

    public string AssetRootPath { get; }

    /// <summary>The folders searched ahead of the game's own, in order.</summary>
    public IReadOnlyList<string> OverrideRoots { get; } = Array.Empty<string>();

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

    private static IReadOnlyDictionary<string, IReadOnlyList<AssetPackage>> BuildIndex(
        string root,
        IReadOnlyList<string> overrideRoots)
    {
        var mutable = new Dictionary<string, List<AssetPackage>>(StringComparer.OrdinalIgnoreCase);
        // Which root a package came from, so the order below can prefer the earlier
        // ones without depending on where the files happen to sit on disk.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var roots = overrideRoots.Append(root).ToArray();
        for (var index = 0; index < roots.Length; index++)
        {
            foreach (var path in Directory.EnumerateFiles(roots[index], "*.pkg", SearchOption.AllDirectories))
            {
                var assetId = Path.GetFileNameWithoutExtension(path);
                // Classified against the root it was found under: a loose folder
                // mirrors the game's layout, so "D3D11" means the same thing in both.
                var variant = ClassifyVariant(roots[index], path);
                var full = Path.GetFullPath(path);
                var package = new AssetPackage(assetId, full, variant, new FileInfo(path).Length);

                if (!mutable.TryGetValue(assetId, out var packages))
                {
                    packages = new List<AssetPackage>();
                    mutable.Add(assetId, packages);
                }

                // A loose package stands in for the shipped one of the same name and
                // variant. The roots are walked in order of priority, so the first
                // one to claim a variant keeps it; keeping both would leave the
                // choice to whichever sorted first, which is not a choice at all.
                // Only against a root that outranks this one. Two packages of the
                // same name inside one root is an ambiguity in the game's own files
                // and stays reported as such; this is about a loose file standing in
                // for a shipped one, which is not ambiguous at all.
                if (packages.Any(value =>
                        value.Variant == variant && rank[value.Path] < index))
                {
                    continue;
                }
                rank[full] = index;
                packages.Add(package);
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AssetPackage>)pair.Value
                .OrderBy(package => rank[package.Path])
                .ThenBy(package => package.Path, StringComparer.OrdinalIgnoreCase)
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
