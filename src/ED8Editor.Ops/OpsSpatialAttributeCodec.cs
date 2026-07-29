using System.Globalization;
using System.Numerics;
using System.Xml;
using ED8Editor.Core;

namespace ED8Editor.Ops;

public sealed class OpsSpatialAttributeCodec
{
    public IReadOnlyDictionary<string, string> GetEditableAttributes(MapVolume source)
    {
        var converted = OpsCoordinateConverter.ToSourceTransform(source.Transform);
        return WithAttributes(
            source.SourceAttributes,
            ("pos", $"{Vector(converted.Position)},  {Vector(converted.EulerRadians)},  {Vector(converted.Scale)}"));
    }

    public IReadOnlyDictionary<string, string> GetEditableAttributes(MapPoint source)
        => WithAttributes(source.SourceAttributes, ("pos", Vector(source.Position)));

    public IReadOnlyDictionary<string, string> GetEditableAttributes(MapCameraMarker source)
        => WithAttributes(
            source.SourceAttributes,
            ("eye", Vector(source.Eye)),
            ("lookat", Vector(source.LookAt)));

    public IReadOnlyDictionary<string, string> GetEditableAttributes(MapSoundMarker source)
        => WithAttributes(source.SourceAttributes, ("sePosition", Vector(source.Position)));

    public IReadOnlyDictionary<string, string> GetEditableAttributes(MapLightMarker source)
        => WithAttributes(source.SourceAttributes, ("pos", Vector(source.Position)));

    public MapVolume Apply(MapVolume source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes);
        var positionComponents = ParseComponents(Required(updated, "pos"), "pos", 9);
        var transform = OpsCoordinateConverter.ToEditorVolumeTransform(
            new Vector3(positionComponents[0], positionComponents[1], positionComponents[2]),
            new Vector3(positionComponents[3], positionComponents[4], positionComponents[5]),
            new Vector3(positionComponents[6], positionComponents[7], positionComponents[8]));
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
            Name = Required(updated, "name"),
            Transform = transform,
            DestinationMap = Optional(updated, "next"),
            DestinationEntry = Optional(updated, "entry"),
            SourceAttributes = updated,
        };
    }

    public MapPoint Apply(MapPoint source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes);
        ValidateOptionalUInt32(updated, "flag");
        ValidateOptionalVector3(updated, "markPos");
        ValidateOptionalFloat(updated, "rotY");
        ValidateOptionalInteger(updated, "type");
        var radius = Optional(updated, "radius");
        return source with
        {
            Name = Required(updated, "name"),
            Position = OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(Required(updated, "pos"), "pos")),
            Radius = radius is null ? null : ParseFloat(radius, "radius"),
            SourceAttributes = updated,
        };
    }

    public MapCameraMarker Apply(MapCameraMarker source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes, "no");
        ValidateOptionalUInt32(updated, "flag");
        ValidateOptionalFloat(updated, "dist");
        ValidateOptionalFloat(updated, "fov");
        ValidateOptionalVector3(updated, "offset");
        ValidateOptionalVector3(updated, "rot");
        ValidateOptionalFloat(updated, "time");
        ValidateOptionalInteger(updated, "type");
        return source with
        {
            Eye = OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(Required(updated, "eye"), "eye")),
            LookAt = OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(Required(updated, "lookat"), "lookat")),
            SourceAttributes = updated,
        };
    }

    public MapSoundMarker Apply(MapSoundMarker source, IReadOnlyDictionary<string, string> attributes)
    {
        var updated = Prepare(attributes, source.SourceAttributes);
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
            SoundName = Required(updated, "seName"),
            Kind = kind,
            SourceKind = sourceKind,
            Position = OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(Required(updated, "sePosition"), "sePosition")),
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
        var updated = Prepare(attributes, source.SourceAttributes);
        ValidateOptionalUInt32(updated, "flag");
        return source with
        {
            Group = Required(updated, "group"),
            Type = Required(updated, "type"),
            Position = OpsCoordinateConverter.ToEditorPosition(
                ParseVector3(Required(updated, "pos"), "pos")),
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

    private static IReadOnlyDictionary<string, string> WithAttributes(
        IReadOnlyDictionary<string, string> source,
        params (string Name, string Value)[] replacements)
    {
        var attributes = new Dictionary<string, string>(source, StringComparer.Ordinal);
        foreach (var replacement in replacements) attributes[replacement.Name] = replacement.Value;
        return attributes;
    }

    private static string Vector(Vector3 value)
        => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";

    private static string Number(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);
}
