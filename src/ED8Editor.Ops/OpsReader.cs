using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using ED8Editor.Core;

namespace ED8Editor.Ops;

public sealed class OpsReader : IMapSceneReader
{
    private static readonly Vector4 DefaultDiffuse = Vector4.One;
    private static readonly Vector3 DefaultEmission = Vector3.Zero;

    public MapScene Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var originalBytes = File.ReadAllBytes(fullPath);

        XDocument document;
        try
        {
            using var stream = new MemoryStream(originalBytes, writable: false);
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new InvalidOpsException($"'{fullPath}' is not a valid XML OPS document.", exception);
        }

        if (document.Root?.Name.LocalName != "Ops")
        {
            throw new InvalidOpsException("The OPS document does not have an <Ops> root element.");
        }

        var mapObjects = document.Root.Elements().FirstOrDefault(
            element => element.Name.LocalName == "MapObjects");
        var props = mapObjects is null
            ? Array.Empty<MapProp>()
            : mapObjects.Elements()
                .Where(element => element.Name.LocalName == "AssetObject")
                .Select(ReadProp)
                .ToArray();

        return new MapScene(fullPath, props, originalBytes);
    }

    private static MapProp ReadProp(XElement element, int index)
    {
        var lineInfo = (IXmlLineInfo)element;
        var location = lineInfo.HasLineInfo() ? $" at line {lineInfo.LineNumber}" : string.Empty;
        var assetId = RequiredAttribute(element, "asset", location);
        var name = RequiredAttribute(element, "name", location);
        var sourcePosition = ParseVector3(RequiredAttribute(element, "pos", location), "pos", location);
        var sourceEuler = ParseVector3(RequiredAttribute(element, "rot", location), "rot", location);
        var scale = ParseVector3(RequiredAttribute(element, "scl", location), "scl", location);
        var diffuse = OptionalVector4(element, "materialDiffuse", DefaultDiffuse, location);
        var emission = OptionalVector3(element, "materialEmission", DefaultEmission, location);
        var flags = ParseOptionalUInt32(element.Attribute("flag")?.Value, "flag", location);
        var attributes = element.Attributes().ToDictionary(
            attribute => attribute.Name.LocalName,
            attribute => attribute.Value,
            StringComparer.Ordinal);

        return new MapProp(
            index,
            assetId,
            name,
            OpsCoordinateConverter.ToEditorTransform(sourcePosition, sourceEuler, scale),
            flags,
            diffuse,
            emission,
            attributes);
    }

    private static string RequiredAttribute(XElement element, string name, string location)
    {
        return element.Attribute(name)?.Value
            ?? throw new InvalidOpsException($"AssetObject{location} is missing required '{name}' attribute.");
    }

    private static Vector3 OptionalVector3(
        XElement element,
        string name,
        Vector3 defaultValue,
        string location)
    {
        var value = element.Attribute(name)?.Value;
        return value is null ? defaultValue : ParseVector3(value, name, location);
    }

    private static Vector4 OptionalVector4(
        XElement element,
        string name,
        Vector4 defaultValue,
        string location)
    {
        var value = element.Attribute(name)?.Value;
        return value is null ? defaultValue : ParseVector4(value, name, location);
    }

    private static Vector3 ParseVector3(string value, string name, string location)
    {
        var components = ParseComponents(value, 3, name, location);
        return new Vector3(components[0], components[1], components[2]);
    }

    private static Vector4 ParseVector4(string value, string name, string location)
    {
        var components = ParseComponents(value, 4, name, location);
        return new Vector4(components[0], components[1], components[2], components[3]);
    }

    private static float[] ParseComponents(string value, int expectedCount, string name, string location)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != expectedCount)
        {
            throw new InvalidOpsException(
                $"Attribute '{name}'{location} has {parts.Length} components; expected {expectedCount}.");
        }

        var components = new float[expectedCount];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out components[index])
                || !float.IsFinite(components[index]))
            {
                throw new InvalidOpsException(
                    $"Attribute '{name}'{location} contains invalid number '{parts[index]}'.");
            }
        }

        return components;
    }

    private static uint? ParseOptionalUInt32(string? value, string name, string location)
    {
        if (value is null)
        {
            return null;
        }

        var style = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }

        if (!uint.TryParse(value, style, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOpsException($"Attribute '{name}'{location} contains invalid integer '{value}'.");
        }

        return result;
    }
}
