using System.Xml;
using System.Xml.Linq;
using ED8Editor.Core;

namespace ED8Editor.Assets;

public sealed class AssetManifestReader : IAssetManifestReader
{
    public const string ManifestEntryName = "asset_D3D11.xml";

    public AssetManifest Read(IPackageArchive archive, string expectedAssetId)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (string.IsNullOrWhiteSpace(expectedAssetId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(expectedAssetId));
        }

        var manifestEntry = archive.Entries.FirstOrDefault(
            entry => entry.Name.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            throw new FileNotFoundException(
                $"Package '{archive.SourcePath}' has no {ManifestEntryName} entry.",
                ManifestEntryName);
        }

        var originalBytes = archive.ReadEntry(manifestEntry);
        XDocument document;
        try
        {
            using var stream = new MemoryStream(originalBytes, writable: false);
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new InvalidAssetManifestException(
                $"Manifest in package '{archive.SourcePath}' is not valid XML.",
                exception);
        }

        if (document.Root?.Name.LocalName != "fassets")
        {
            throw new InvalidAssetManifestException(
                $"Manifest in package '{archive.SourcePath}' has no <fassets> root.");
        }

        var archiveEntries = archive.Entries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = document.Root.Elements()
            .Where(element => element.Name.LocalName == "asset")
            .Select(element => ReadAsset(element, archiveEntries, archive.SourcePath))
            .ToArray();

        if (assets.Length == 0)
        {
            throw new InvalidAssetManifestException(
                $"Manifest in package '{archive.SourcePath}' declares no assets.");
        }

        var primaryAsset = assets.FirstOrDefault(
            asset => asset.Symbol.Equals(expectedAssetId, StringComparison.OrdinalIgnoreCase));
        var usedFallback = primaryAsset is null && assets.Length == 1;
        primaryAsset ??= usedFallback ? assets[0] : null;

        return new AssetManifest(
            archive.SourcePath,
            assets,
            primaryAsset,
            usedFallback,
            originalBytes);
    }

    private static AssetDefinition ReadAsset(
        XElement element,
        IReadOnlySet<string> archiveEntries,
        string packagePath)
    {
        var symbol = RequiredAttribute(element, "symbol", packagePath);
        var resources = element.Elements()
            .Where(child => child.Name.LocalName == "cluster")
            .Select((child, index) => ReadResource(child, index, archiveEntries, packagePath))
            .ToArray();
        var attributes = element.Attributes().ToDictionary(
            attribute => attribute.Name.LocalName,
            attribute => attribute.Value,
            StringComparer.Ordinal);

        return new AssetDefinition(symbol, resources, attributes);
    }

    private static AssetResource ReadResource(
        XElement element,
        int index,
        IReadOnlySet<string> archiveEntries,
        string packagePath)
    {
        var path = RequiredAttribute(element, "path", packagePath);
        var sourceType = RequiredAttribute(element, "type", packagePath);
        var entryName = GetPortableFileName(path);
        if (entryName.Length == 0)
        {
            throw new InvalidAssetManifestException(
                $"Manifest in package '{packagePath}' contains empty cluster path '{path}'.");
        }

        var attributes = element.Attributes().ToDictionary(
            attribute => attribute.Name.LocalName,
            attribute => attribute.Value,
            StringComparer.Ordinal);

        return new AssetResource(
            index,
            path,
            entryName,
            sourceType,
            Classify(sourceType),
            archiveEntries.Contains(entryName),
            attributes);
    }

    private static AssetResourceKind Classify(string sourceType) => sourceType switch
    {
        "p_collada" => AssetResourceKind.Model,
        "p_texture" => AssetResourceKind.Texture,
        "binary" => AssetResourceKind.Binary,
        _ => AssetResourceKind.Unknown,
    };

    private static string RequiredAttribute(XElement element, string name, string packagePath)
    {
        return element.Attribute(name)?.Value
            ?? throw new InvalidAssetManifestException(
                $"Manifest in package '{packagePath}' has <{element.Name.LocalName}> without '{name}'.");
    }

    private static string GetPortableFileName(string path)
    {
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
