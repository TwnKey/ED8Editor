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

        var entryVolumes = ReadElements(document, "Entrys", "EntryBox")
            .Select((element, index) => ReadVolume(element, index, MapVolumeKind.Entry))
            .ToArray();
        var groupVolumes = ReadElements(document, "GroupBoxes", "GroupBox")
            .Select((element, index) => ReadVolume(element, index, MapVolumeKind.Group))
            .ToArray();
        var points = ReadElements(document, "LookPoints", "LookPoint")
            .Select(ReadPoint)
            .ToArray();
        var cameras = ReadElements(document, "MapCameras", "MapCamera")
            .Select(ReadCamera)
            .ToArray();
        var sounds = ReadElements(document, "MapSounds", "SoundObject")
            .Select(ReadSound)
            .ToArray();
        var lights = ReadElements(document, "Lights", "Light")
            .Select(ReadLight)
            .ToArray();
        var defaultEnvironment = ReadDefaultEnvironment(document);

        return new MapScene(
            fullPath,
            props,
            originalBytes,
            entryVolumes.Concat(groupVolumes).ToArray(),
            points,
            cameras,
            sounds,
            lights,
            defaultEnvironment);
    }

    private static MapEnvironment? ReadDefaultEnvironment(XDocument document)
    {
        var mapSetting = document.Root!.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "MapSetting");
        var mapColor = mapSetting?.Elements()
            .Where(element => element.Name.LocalName == "MapColor")
            .FirstOrDefault(element => element.Elements()
                .Any(child => child.Name.LocalName == "Type"
                    && child.Attribute("type")?.Value == "default"));
        var fog = mapColor?.Elements().FirstOrDefault(element => element.Name.LocalName == "Fog");
        if (fog is null) return null;

        var location = ElementLocation(fog);
        return new MapEnvironment(
            "default",
            ParseVector3(RequiredAttribute(fog, "color", location), "color", location),
            ParseFloat(RequiredAttribute(fog, "near", location), "near", location),
            ParseFloat(RequiredAttribute(fog, "far", location), "far", location),
            ReadAttributes(fog));
    }

    private static IEnumerable<XElement> ReadElements(XDocument document, string sectionName, string elementName)
        => document.Root!.Elements()
            .FirstOrDefault(element => element.Name.LocalName == sectionName)?
            .Elements()
            .Where(element => element.Name.LocalName == elementName)
            ?? Enumerable.Empty<XElement>();

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
        var attributes = ReadAttributes(element);

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

    private static MapVolume ReadVolume(XElement element, int index, MapVolumeKind kind)
    {
        var location = ElementLocation(element);
        var components = ParseComponents(RequiredAttribute(element, "pos", location), 9, "pos", location);
        var sourcePosition = new Vector3(components[0], components[1], components[2]);
        var sourceEuler = new Vector3(components[3], components[4], components[5]);
        var scale = new Vector3(components[6], components[7], components[8]);
        return new MapVolume(
            index,
            kind,
            RequiredAttribute(element, "name", location),
            OpsCoordinateConverter.ToEditorVolumeTransform(sourcePosition, sourceEuler, scale),
            element.Attribute("next")?.Value,
            element.Attribute("entry")?.Value,
            ReadAttributes(element));
    }

    private static MapPoint ReadPoint(XElement element, int index)
    {
        var location = ElementLocation(element);
        var position = OpsCoordinateConverter.ToEditorPosition(
            ParseVector3(RequiredAttribute(element, "pos", location), "pos", location));
        var radiusValue = element.Attribute("radius")?.Value;
        float? radius = radiusValue is null ? null : ParseFloat(radiusValue, "radius", location);
        return new MapPoint(
            index,
            MapPointKind.LookPoint,
            RequiredAttribute(element, "name", location),
            position,
            radius,
            ReadAttributes(element));
    }

    private static MapCameraMarker ReadCamera(XElement element, int index)
    {
        var location = ElementLocation(element);
        var eye = OpsCoordinateConverter.ToEditorPosition(
            ParseVector3(RequiredAttribute(element, "eye", location), "eye", location));
        var lookAt = OpsCoordinateConverter.ToEditorPosition(
            ParseVector3(RequiredAttribute(element, "lookat", location), "lookat", location));
        var name = element.Attribute("no")?.Value ?? index.ToString(CultureInfo.InvariantCulture);
        return new MapCameraMarker(index, name, eye, lookAt, ReadAttributes(element));
    }

    private static MapSoundMarker ReadSound(XElement element, int index)
    {
        var location = ElementLocation(element);
        var sourceKind = RequiredAttribute(element, "seType", location);
        var kind = sourceKind.ToUpperInvariant() switch
        {
            "POINT" => MapSoundKind.Point,
            "LINE" => MapSoundKind.Line,
            "BOX" => MapSoundKind.Box,
            _ => MapSoundKind.Unknown,
        };
        return new MapSoundMarker(
            index,
            RequiredAttribute(element, "seName", location),
            kind,
            sourceKind,
            OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(RequiredAttribute(element, "sePosition", location), "sePosition", location)),
            ParseFloat(RequiredAttribute(element, "seRange", location), "seRange", location),
            ParseFloat(RequiredAttribute(element, "seRotation", location), "seRotation", location),
            ParseVector3(RequiredAttribute(element, "seScale", location), "seScale", location),
            ReadAttributes(element));
    }

    private static MapLightMarker ReadLight(XElement element, int index)
    {
        var location = ElementLocation(element);
        return new MapLightMarker(
            index,
            RequiredAttribute(element, "group", location),
            RequiredAttribute(element, "type", location),
            OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(RequiredAttribute(element, "pos", location), "pos", location)),
            ParseVector4(RequiredAttribute(element, "color", location), "color", location),
            ParseFloat(RequiredAttribute(element, "colorPower", location), "colorPower", location),
            ParseFloat(RequiredAttribute(element, "innerRange", location), "innerRange", location),
            ParseFloat(RequiredAttribute(element, "outerRange", location), "outerRange", location),
            ReadAttributes(element));
    }

    private static IReadOnlyDictionary<string, string> ReadAttributes(XElement element)
        => element.Attributes().ToDictionary(
            attribute => attribute.Name.LocalName,
            attribute => attribute.Value,
            StringComparer.Ordinal);

    private static string ElementLocation(XElement element)
    {
        var lineInfo = (IXmlLineInfo)element;
        return lineInfo.HasLineInfo() ? $" at line {lineInfo.LineNumber}" : string.Empty;
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

    private static float ParseFloat(string value, string name, string location)
    {
        value = value.Trim();
        if (value.EndsWith('f') || value.EndsWith('F')) value = value[..^1];
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || !float.IsFinite(result))
        {
            throw new InvalidOpsException($"Attribute '{name}'{location} contains invalid number '{value}'.");
        }
        return result;
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
            components[index] = ParseFloat(parts[index], name, location);
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
