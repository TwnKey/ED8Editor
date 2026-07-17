using ED8Editor.Core;

namespace ED8Editor.Assets;

public sealed class GameAssetCatalog
{
    public GameAssetCatalog(string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataPath));
        var resolver = new GameAssetResolver(Path.Combine(Path.GetFullPath(gameDataPath), "asset"));
        Entries = resolver.Packages
            .GroupBy(package => package.AssetId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AssetCatalogEntry(
                group.Key,
                group.OrderBy(package => package.Path, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(entry => entry.AssetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<AssetCatalogEntry> Entries { get; }
}
