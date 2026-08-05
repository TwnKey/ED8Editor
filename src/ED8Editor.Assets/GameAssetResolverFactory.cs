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

        // The game's loose-loading folder mirrors its layout one level up:
        // <game>/dev/data/asset beside <game>/data/asset. When it is there, it is
        // what the game loads, so it is what the editor has to show.
        var development = Development(gameDataPath);
        var key = development is null ? assetPath : development + "|" + assetPath;
        return cache.GetOrAdd(
            key,
            _ => new Lazy<GameAssetResolver>(
                () => new GameAssetResolver(
                    assetPath,
                    development is null ? null : new[] { development }),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>The loose-loading asset folder beside this data folder, if it exists.</summary>
    public static string? Development(string gameDataPath)
    {
        var game = Path.GetDirectoryName(Path.GetFullPath(gameDataPath));
        if (game is null) return null;
        var candidate = Path.Combine(
            game, "dev", Path.GetFileName(Path.GetFullPath(gameDataPath)), "asset");
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}
