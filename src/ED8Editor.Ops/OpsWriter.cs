using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ED8Editor.Core;

namespace ED8Editor.Ops;

public sealed class OpsWriter
{
    public byte[] Serialize(MapScene source, MapScene edited)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edited);
        ValidateEditableShape(source, edited);
        if (SpatiallyEqual(source, edited)) return source.OriginalBytes.ToArray();

        XDocument document;
        using (var input = new MemoryStream(source.OriginalBytes.ToArray(), writable: false))
        {
            document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        }

        UpdateProps(document, source, edited);
        UpdateVolumes(document, source, edited, MapVolumeKind.Entry, "Entrys", "EntryBox");
        UpdateVolumes(document, source, edited, MapVolumeKind.Group, "GroupBoxes", "GroupBox");
        UpdatePositions(document, "LookPoints", "LookPoint", source.Points, edited.Points,
            (left, right) => left.Position == right.Position, value => value.Position, "pos");
        UpdateCameras(document, source, edited);
        UpdatePositions(document, "MapSounds", "SoundObject", source.Sounds, edited.Sounds,
            (left, right) => left.Position == right.Position, value => value.Position, "sePosition");
        UpdatePositions(document, "Lights", "Light", source.Lights, edited.Lights,
            (left, right) => left.Position == right.Position, value => value.Position, "pos");

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = false,
        }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }

    public void Write(string path, MapScene source, MapScene edited)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, Serialize(source, edited));
            var validated = new OpsReader().Read(temporaryPath);
            ValidateShape(edited, validated);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void UpdateProps(XDocument document, MapScene source, MapScene edited)
    {
        var elements = Elements(document, "MapObjects", "AssetObject");
        var editedById = edited.Props.ToDictionary(value => value.SourceIndex);
        foreach (var original in source.Props)
        {
            if (!editedById.TryGetValue(original.SourceIndex, out var changed))
            {
                elements[original.SourceIndex].Remove();
                continue;
            }
            var element = elements[original.SourceIndex];
            if (!original.SourceAttributes.OrderBy(value => value.Key).SequenceEqual(
                changed.SourceAttributes.OrderBy(value => value.Key)))
            {
                var changedNames = changed.SourceAttributes.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var attribute in element.Attributes().Where(value => !changedNames.Contains(value.Name.LocalName)).ToArray())
                {
                    attribute.Remove();
                }
                foreach (var attribute in changed.SourceAttributes) element.SetAttributeValue(attribute.Key, attribute.Value);
            }
            if (SameTransform(original.Transform, changed.Transform)) continue;
            SetPropAttributes(element, changed);
        }

        var sourceIds = source.Props.Select(value => value.SourceIndex).ToHashSet();
        var container = document.Root!.Elements().First(element => element.Name.LocalName == "MapObjects");
        foreach (var added in edited.Props.Where(value => !sourceIds.Contains(value.SourceIndex)).OrderBy(value => value.SourceIndex))
        {
            var element = new XElement("AssetObject");
            foreach (var attribute in added.SourceAttributes) element.SetAttributeValue(attribute.Key, attribute.Value);
            SetPropAttributes(element, added);
            container.Add(element);
        }
    }

    private static void SetPropAttributes(XElement element, MapProp prop)
    {
        element.SetAttributeValue("asset", prop.AssetId);
        element.SetAttributeValue("name", prop.Name);
        var converted = OpsCoordinateConverter.ToSourceTransform(prop.Transform, assetObject: true);
        element.SetAttributeValue("pos", Vector(converted.Position));
        element.SetAttributeValue("rot", Vector(converted.EulerRadians));
        element.SetAttributeValue("scl", Vector(converted.Scale));
    }

    private static void UpdateVolumes(
        XDocument document, MapScene source, MapScene edited, MapVolumeKind kind, string section, string elementName)
    {
        if (!source.Volumes.Any(value => value.Kind == kind)) return;
        var elements = Elements(document, section, elementName);
        foreach (var original in source.Volumes.Where(value => value.Kind == kind))
        {
            var changed = edited.Volumes.Single(value => value.Kind == kind && value.SourceIndex == original.SourceIndex);
            if (SameTransform(original.Transform, changed.Transform)) continue;
            var converted = OpsCoordinateConverter.ToSourceTransform(changed.Transform, assetObject: false);
            elements[original.SourceIndex].SetAttributeValue(
                "pos", $"{Vector(converted.Position)},  {Vector(converted.EulerRadians)},  {Vector(converted.Scale)}");
        }
    }

    private static void UpdateCameras(XDocument document, MapScene source, MapScene edited)
    {
        if (source.Cameras.Count == 0) return;
        var elements = Elements(document, "MapCameras", "MapCamera");
        foreach (var original in source.Cameras)
        {
            var changed = edited.Cameras.Single(value => value.SourceIndex == original.SourceIndex);
            if (original.Eye == changed.Eye && original.LookAt == changed.LookAt) continue;
            elements[original.SourceIndex].SetAttributeValue("eye", Vector(ToSourcePosition(changed.Eye)));
            elements[original.SourceIndex].SetAttributeValue("lookat", Vector(ToSourcePosition(changed.LookAt)));
        }
    }

    private static void UpdatePositions<T>(
        XDocument document,
        string section,
        string elementName,
        IReadOnlyList<T> source,
        IReadOnlyList<T> edited,
        Func<T, T, bool> equal,
        Func<T, Vector3> position,
        string attribute)
    {
        if (source.Count == 0) return;
        var elements = Elements(document, section, elementName);
        for (var index = 0; index < source.Count; index++)
        {
            if (equal(source[index], edited[index])) continue;
            elements[index].SetAttributeValue(attribute, Vector(ToSourcePosition(position(edited[index]))));
        }
    }

    private static IReadOnlyList<XElement> Elements(XDocument document, string section, string elementName)
        => document.Root!.Elements().First(element => element.Name.LocalName == section)
            .Elements().Where(element => element.Name.LocalName == elementName).ToArray();

    private static Vector3 ToSourcePosition(Vector3 editorPosition)
        => new(-editorPosition.X, editorPosition.Y, editorPosition.Z);

    private static string Vector(Vector3 value)
        => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool SameTransform(MapTransform left, MapTransform right)
        => left.Position == right.Position && left.Rotation == right.Rotation && left.Scale == right.Scale;

    private static bool SpatiallyEqual(MapScene left, MapScene right)
        => left.Props.Select(value => value.Transform).SequenceEqual(right.Props.Select(value => value.Transform))
            && left.Props.Select(value => value.SourceAttributes.OrderBy(pair => pair.Key).ToArray())
                .Zip(right.Props.Select(value => value.SourceAttributes.OrderBy(pair => pair.Key).ToArray()))
                .All(pair => pair.First.SequenceEqual(pair.Second))
            && left.Volumes.Select(value => value.Transform).SequenceEqual(right.Volumes.Select(value => value.Transform))
            && left.Points.Select(value => value.Position).SequenceEqual(right.Points.Select(value => value.Position))
            && left.Cameras.Select(value => (value.Eye, value.LookAt)).SequenceEqual(right.Cameras.Select(value => (value.Eye, value.LookAt)))
            && left.Sounds.Select(value => value.Position).SequenceEqual(right.Sounds.Select(value => value.Position))
            && left.Lights.Select(value => value.Position).SequenceEqual(right.Lights.Select(value => value.Position));

    private static void ValidateShape(MapScene expected, MapScene actual)
    {
        if (expected.Props.Count != actual.Props.Count || expected.Volumes.Count != actual.Volumes.Count
            || expected.Points.Count != actual.Points.Count || expected.Cameras.Count != actual.Cameras.Count
            || expected.Sounds.Count != actual.Sounds.Count || expected.Lights.Count != actual.Lights.Count)
        {
            throw new InvalidDataException("Edited OPS scene structure does not match its source document.");
        }
    }

    private static void ValidateEditableShape(MapScene source, MapScene edited)
    {
        if (source.Volumes.Count != edited.Volumes.Count || source.Points.Count != edited.Points.Count
            || source.Cameras.Count != edited.Cameras.Count || source.Sounds.Count != edited.Sounds.Count
            || source.Lights.Count != edited.Lights.Count)
        {
            throw new InvalidDataException("Only the prop collection can currently change the OPS document structure.");
        }
    }
}
