using System.Globalization;
using System.Numerics;
using System.Xml;
using ED8Editor.Core;

namespace ED8Editor.Ops;

public sealed class OpsSpatialAttributeCodec
{
    public MapVolume Apply(MapVolume source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "name", "pos");
        ValidateOptionalUInt32(updated, "flag");
        if (source.Kind == MapVolumeKind.Entry)
        {
            ValidateOptionalFloat(updated, "cameraDir");
            ValidateOptionalFloat(updated, "distance");
            ValidateOptionalFloat(updated, "northDir");
            ValidateOptionalInteger(updated, "entryType");
            ValidateOptionalInteger(updated, "placeid");
            ValidateOptionalVector3(updated, "markPos");
        }
        return source with
        {
            DestinationMap = Optional(updated, "next"),
            DestinationEntry = Optional(updated, "entry"),
            SourceAttributes = updated,
        };
    }

    public MapPoint Apply(MapPoint source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "name", "pos");
        ValidateOptionalUInt32(updated, "flag");
        ValidateOptionalVector3(updated, "markPos");
        ValidateOptionalFloat(updated, "rotY");
        ValidateOptionalInteger(updated, "type");
        var radius = Optional(updated, "radius");
        return source with
        {
            Radius = radius is null ? null : ParseFloat(radius, "radius"),
            SourceAttributes = updated,
        };
    }

    public MapCameraMarker Apply(MapCameraMarker source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "no", "eye", "lookat");
        ValidateOptionalUInt32(updated, "flag");
        ValidateOptionalFloat(updated, "dist");
        ValidateOptionalFloat(updated, "fov");
        ValidateOptionalVector3(updated, "offset");
        ValidateOptionalVector3(updated, "rot");
        ValidateOptionalFloat(updated, "time");
        ValidateOptionalInteger(updated, "type");
        return source with { SourceAttributes = updated };
    }

    public MapSoundMarker Apply(MapSoundMarker source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "seName", "sePosition");
        ValidateOptionalInteger(updated, "seGroupId");
        ValidateOptionalFloat(updated, "seVolume");
        var sourceKind = Required(updated, "seType");
        var kind = sourceKind.ToUpperInvariant() switch
        {
            "POINT" => MapSoundKind.Point,
            "LINE" => MapSoundKind.Line,
            "BOX" => MapSoundKind.Box,
            _ => MapSoundKind.Unknown,
        };
        return source with
        {
            Kind = kind,
            SourceKind = sourceKind,
            Range = ParseFloat(Required(updated, "seRange"), "seRange"),
            SourceRotation = ParseFloat(Required(updated, "seRotation"), "seRotation"),
            SourceScale = ParseVector3(Required(updated, "seScale"), "seScale"),
            GroupId = Optional(updated, "seGroupId") is { } groupId
                ? ParseInteger(groupId, "seGroupId")
                : 0,
            Volume = Optional(updated, "seVolume") is { } volume
                ? ParseFloat(volume, "seVolume")
                : 1f,
            SourceAttributes = updated,
        };
    }

    public MapLightMarker Apply(MapLightMarker source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "pos");
        ValidateOptionalUInt32(updated, "flag");
        return source with
        {
            Group = Required(updated, "group"),
            Type = Required(updated, "type"),
            Color = ParseVector4(Required(updated, "color"), "color"),
            ColorPower = ParseFloat(Required(updated, "colorPower"), "colorPower"),
            InnerRange = ParseFloat(Required(updated, "innerRange"), "innerRange"),
            OuterRange = ParseFloat(Required(updated, "outerRange"), "outerRange"),
            SourceAttributes = updated,
        };
    }

    private static IReadOnlyDictionary<string, string> Prepare(
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyDictionary<string, string> source,
        params string[] protectedNames)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ValidateAttributeNames(attributes);
        var updated = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        foreach (var name in protectedNames)
        {
            if (!source.TryGetValue(name, out var value))
            {
                throw new ArgumentException($"Source OPS element is missing protected attribute '{name}'.", nameof(source));
            }
            updated[name] = value;
        }
        return updated;
    }

    public static void ValidateAttributeNames(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        foreach (var name in attributes.Keys)
        {
            try
            {
                XmlConvert.VerifyNCName(name);
            }
            catch (XmlException exception)
            {
                throw new ArgumentException($"'{name}' is not a valid OPS attribute name.", nameof(attributes), exception);
            }
        }
    }

    private static string Required(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Required OPS attribute '{name}' cannot be removed.", nameof(attributes));

    private static string? Optional(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) ? value : null;

    private static float ParseFloat(string value, string name)
    {
        value = value.Trim();
        if (value.EndsWith('f') || value.EndsWith('F')) value = value[..^1];
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || !float.IsFinite(result))
        {
            throw new ArgumentException($"OPS attribute '{name}' contains invalid number '{value}'.", name);
        }
        return result;
    }

    private static int ParseInteger(string value, string name)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"OPS attribute '{name}' contains invalid integer '{value}'.", name);
        }
        return result;
    }

    private static Vector3 ParseVector3(string value, string name)
    {
        var components = ParseComponents(value, name, 3);
        return new Vector3(components[0], components[1], components[2]);
    }

    private static Vector4 ParseVector4(string value, string name)
    {
        var components = ParseComponents(value, name, 4);
        return new Vector4(components[0], components[1], components[2], components[3]);
    }

    private static float[] ParseComponents(string value, string name, int expectedCount)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != expectedCount)
        {
            throw new ArgumentException(
                $"OPS attribute '{name}' has {parts.Length} components; expected {expectedCount}.", name);
        }
        return parts.Select(part => ParseFloat(part, name)).ToArray();
    }

    private static void ValidateOptionalFloat(IReadOnlyDictionary<string, string> attributes, string name)
    {
        if (attributes.TryGetValue(name, out var value)) ParseFloat(value, name);
    }

    private static void ValidateOptionalVector3(IReadOnlyDictionary<string, string> attributes, string name)
    {
        if (attributes.TryGetValue(name, out var value)) ParseVector3(value, name);
    }

    private static void ValidateOptionalInteger(IReadOnlyDictionary<string, string> attributes, string name)
    {
        if (attributes.TryGetValue(name, out var value)
            && !long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException($"OPS attribute '{name}' contains invalid integer '{value}'.", name);
        }
    }

    private static void ValidateOptionalUInt32(IReadOnlyDictionary<string, string> attributes, string name)
    {
        if (!attributes.TryGetValue(name, out var value)) return;
        value = value.Trim();
        var style = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        if (!uint.TryParse(value, style, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException($"OPS attribute '{name}' contains invalid unsigned integer '{value}'.", name);
        }
    }
}
