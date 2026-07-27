using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;

namespace ED8Editor.Application;

public sealed record GameObjectAnimationAction(
    string Name,
    int StartFrame,
    int EndFrame,
    bool Loop,
    bool Reverse,
    IReadOnlyDictionary<string, string> Attributes);

public sealed class GameObjectAnimationInfoReader
{
    public IReadOnlyDictionary<string, GameObjectAnimationAction> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Object information path is required.", nameof(path));

        using var reader = File.OpenText(path);
        return Read(reader, path);
    }

    public IReadOnlyDictionary<string, GameObjectAnimationAction> Read(
        TextReader reader,
        string sourceName = "<object animation info>")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var actions = new Dictionary<string, GameObjectAnimationAction>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Descendants().Where(value =>
                     value.Name.LocalName.Equals("Animation", StringComparison.OrdinalIgnoreCase)))
        {
            var attributes = element.Attributes().ToDictionary(
                value => value.Name.LocalName,
                value => value.Value,
                StringComparer.OrdinalIgnoreCase);
            var name = RequireAttribute(attributes, "animName", sourceName);
            var start = ParseFrame(
                RequireAttribute(attributes, "start", sourceName), "start", name, sourceName);
            var end = ParseFrame(
                RequireAttribute(attributes, "end", sourceName), "end", name, sourceName);
            if (start < 0 || end < start)
            {
                throw new InvalidDataException(
                    $"Animation action '{name}' in '{sourceName}' has invalid frame range {start}..{end}.");
            }
            if (!actions.TryAdd(
                    name,
                    new GameObjectAnimationAction(
                        name,
                        start,
                        end,
                        ParseBoolean(attributes.GetValueOrDefault("loop")),
                        ParseBoolean(attributes.GetValueOrDefault("reverse")),
                        new ReadOnlyDictionary<string, string>(attributes))))
            {
                throw new InvalidDataException(
                    $"Object information file '{sourceName}' declares animation action '{name}' more than once.");
            }
        }
        return new ReadOnlyDictionary<string, GameObjectAnimationAction>(actions);
    }

    private static string RequireAttribute(
        IReadOnlyDictionary<string, string> attributes,
        string name,
        string path)
        => attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"An Animation entry in '{path}' has no '{name}' attribute.");

    private static int ParseFrame(string value, string attribute, string action, string path)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException(
                $"Animation action '{action}' in '{path}' has invalid {attribute} frame '{value}'.");

    private static bool ParseBoolean(string? value)
        => value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

public sealed class GameObjectAnimationInfoResolver
{
    public string? Resolve(string gameDataPath, string assetId)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new ArgumentException("Game data path is required.", nameof(gameDataPath));
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("Asset ID is required.", nameof(assetId));

        const string objectAssetPrefix = "O_";
        if (!assetId.StartsWith(objectAssetPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var objectName = assetId[objectAssetPrefix.Length..];
        var directory = Path.Combine(gameDataPath, "map", "objects", objectName);
        if (!Directory.Exists(directory)) return null;

        var exactPath = Path.Combine(directory, objectName + ".inf");
        if (File.Exists(exactPath)) return exactPath;

        return Directory.EnumerateFiles(directory, "*.inf", SearchOption.TopDirectoryOnly)
            .SingleOrDefault();
    }
}
