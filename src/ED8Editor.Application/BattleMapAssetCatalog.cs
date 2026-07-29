using System.Text;

namespace ED8Editor.Application;

/// <summary>
/// One battle-map metadata directory under data/map/battle. Geometry is stored
/// elsewhere; the INF file binds authored node parameters and material UV
/// animations to the battle-map asset identifier.
/// </summary>
public sealed record BattleMapAssetEntry(
    string AssetId,
    string DirectoryPath,
    string InfPath,
    bool HasInf,
    IReadOnlyList<string> UvAnimationFiles)
{
    public string Label
    {
        get
        {
            var inf = HasInf ? ".inf" : "no .inf";
            var uv = UvAnimationFiles.Count == 1
                ? "1 UV animation"
                : $"{UvAnimationFiles.Count} UV animations";
            return $"{AssetId} — {inf}, {uv}";
        }
    }
}

/// <summary>
/// Exact filesystem catalog for battle-map metadata. Creating an INF writes
/// only the minimal node_infomation document already used by shipped maps; it
/// never fabricates geometry or UV animation data.
/// </summary>
public sealed class BattleMapAssetCatalog
{
    private const int MaximumAssetIdBytes = 15;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string battleRoot;

    public BattleMapAssetCatalog(string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new ArgumentException("The game data directory is required.", nameof(gameDataPath));
        battleRoot = Path.GetFullPath(Path.Combine(gameDataPath, "map", "battle"));
    }

    public string BattleRoot => battleRoot;

    public IReadOnlyList<BattleMapAssetEntry> Entries
        => !Directory.Exists(battleRoot)
            ? Array.Empty<BattleMapAssetEntry>()
            : Directory.EnumerateDirectories(battleRoot)
                .Select(ReadEntry)
                .OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public BattleMapAssetEntry CreateMinimalInf(string assetId)
    {
        ValidateAssetId(assetId);
        var directory = Path.GetFullPath(Path.Combine(battleRoot, assetId));
        var expectedPrefix = battleRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The battle-map identifier escapes data/map/battle.", nameof(assetId));

        var infPath = Path.Combine(directory, $"{assetId}.inf");
        if (File.Exists(infPath))
            throw new IOException($"'{infPath}' already exists.");

        Directory.CreateDirectory(directory);
        const string document =
            "<!-- dae node information file -->\r\n"
            + "<node_infomation>\r\n"
            + "\r\n"
            + "</node_infomation>\r\n";
        File.WriteAllText(infPath, document, Utf8WithoutBom);
        return ReadEntry(directory);
    }

    private static BattleMapAssetEntry ReadEntry(string directory)
    {
        var assetId = Path.GetFileName(directory);
        var infPath = Path.Combine(directory, $"{assetId}.inf");
        var uvAnimations = Directory.EnumerateFiles(directory, "*.uvb", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(value => value is not null)
            .Cast<string>()
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BattleMapAssetEntry(
            assetId,
            directory,
            infPath,
            File.Exists(infPath),
            uvAnimations);
    }

    private static void ValidateAssetId(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("A battle-map identifier is required.", nameof(assetId));
        if (Encoding.ASCII.GetByteCount(assetId) > MaximumAssetIdBytes
            || assetId.Any(value => value > 0x7f
                || !(char.IsLetterOrDigit(value) || value == '_')))
        {
            throw new ArgumentException(
                "A battle-map identifier must contain only ASCII letters, digits or '_' "
                + $"and fit in {MaximumAssetIdBytes} bytes.",
                nameof(assetId));
        }
    }
}
