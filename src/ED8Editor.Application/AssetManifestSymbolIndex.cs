using ED8Editor.Core;

namespace ED8Editor.Application;

/// <summary>
/// Builds an exact index from authored manifest symbols to the packages that
/// declare them. This is used for resources, such as event animation banks,
/// whose package name cannot be derived from the model or clip name.
/// </summary>
internal sealed class AssetManifestSymbolIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AssetPackage>> packagesBySymbol;

    public AssetManifestSymbolIndex(
        string gameDataPath,
        IPackageArchiveReader packageReader,
        IAssetManifestReader manifestReader)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new ArgumentException("Game data path is required.", nameof(gameDataPath));
        ArgumentNullException.ThrowIfNull(packageReader);
        ArgumentNullException.ThrowIfNull(manifestReader);

        var assetRoot = Path.Combine(Path.GetFullPath(gameDataPath), "asset");
        var mutable = new Dictionary<string, List<AssetPackage>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     assetRoot, "*.pkg", SearchOption.AllDirectories))
        {
            var package = new AssetPackage(
                Path.GetFileNameWithoutExtension(path),
                Path.GetFullPath(path),
                ClassifyVariant(assetRoot, path),
                new FileInfo(path).Length);
            try
            {
                var archive = packageReader.Read(package.Path);
                var manifest = manifestReader.Read(archive, package.AssetId);
                foreach (var asset in manifest.Assets)
                {
                    if (!mutable.TryGetValue(asset.Symbol, out var packages))
                    {
                        packages = new List<AssetPackage>();
                        mutable.Add(asset.Symbol, packages);
                    }
                    packages.Add(package);
                }
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Skipping package '{package.Path}' while indexing manifest symbols: {exception.Message}");
            }
        }
        packagesBySymbol = mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AssetPackage>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public AssetPackage? Resolve(
        string symbol,
        AssetVariantPreference preference)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Manifest symbol is required.", nameof(symbol));
        if (!packagesBySymbol.TryGetValue(symbol, out var packages)) return null;
        var variants = preference == AssetVariantPreference.English
            ? new[] { AssetVariant.English, AssetVariant.Base, AssetVariant.Other }
            : new[] { AssetVariant.Base, AssetVariant.English, AssetVariant.Other };
        return variants.SelectMany(variant =>
                packages.Where(package => package.Variant == variant)
                    .OrderBy(package => package.Path, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static AssetVariant ClassifyVariant(string assetRoot, string path)
    {
        var relativePath = Path.GetRelativePath(assetRoot, path);
        var directory = relativePath.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (directory.Equals("D3D11", StringComparison.OrdinalIgnoreCase))
            return AssetVariant.Base;
        if (directory.Equals("D3D11_us", StringComparison.OrdinalIgnoreCase))
            return AssetVariant.English;
        return AssetVariant.Other;
    }
}
