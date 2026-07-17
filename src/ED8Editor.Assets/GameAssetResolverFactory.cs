using System.Collections.Concurrent;
using ED8Editor.Core;

namespace ED8Editor.Assets;

public sealed class GameAssetResolverFactory : IAssetPackageResolverFactory
{
    private readonly ConcurrentDictionary<string, Lazy<GameAssetResolver>> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public IAssetPackageResolver Create(string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataPath));
        }

        var assetPath = Path.GetFullPath(Path.Combine(gameDataPath, "asset"));
        return cache.GetOrAdd(
            assetPath,
            path => new Lazy<GameAssetResolver>(
                () => new GameAssetResolver(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
