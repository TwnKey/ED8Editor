using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ED8Editor.Core;

namespace ED8Editor.Ops;

public sealed class OpsWriter
{
    private static readonly string[] CanonicalSectionOrder =
    {
        "MapSetting", "MapCameras", "MapObjects", "Entrys", "LookPoints",
        "Occluders", "GroupBoxes", "Lights", "MapSounds", "MapEffects",
    };

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
            UpdateAttributes(element, original.SourceAttributes, changed.SourceAttributes);
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
        var converted = OpsCoordinateConverter.ToSourceTransform(prop.Transform);
        element.SetAttributeValue("pos", Vector(converted.Position));
        element.SetAttributeValue("rot", Vector(converted.EulerRadians));
        element.SetAttributeValue("scl", Vector(converted.Scale));
    }

    private static void UpdateVolumes(
        XDocument document, MapScene source, MapScene edited, MapVolumeKind kind, string section, string elementName)
    {
        var originals = source.Volumes.Where(value => value.Kind == kind).ToArray();
        var changes = edited.Volumes.Where(value => value.Kind == kind).ToArray();
        SynchronizeElements(
            document, section, elementName, originals, changes,
            value => value.SourceIndex,
            (element, original, changed) =>
            {
                UpdateAttributes(element, original.SourceAttributes, changed.SourceAttributes);
                if (!SameTransform(original.Transform, changed.Transform)) SetVolumeAttributes(element, changed);
            },
            (element, added) =>
            {
                CopyAttributes(element, added.SourceAttributes);
                SetVolumeAttributes(element, added);
            });
    }

    private static void UpdateCameras(XDocument document, MapScene source, MapScene edited)
    {
        SynchronizeElements(
            document, "MapCameras", "MapCamera", source.Cameras, edited.Cameras,
            value => value.SourceIndex,
            (element, original, changed) =>
            {
                UpdateAttributes(element, original.SourceAttributes, changed.SourceAttributes);
                if (original.Eye != changed.Eye || original.LookAt != changed.LookAt) SetCameraAttributes(element, changed);
            },
            (element, added) =>
            {
                CopyAttributes(element, added.SourceAttributes);
                SetCameraAttributes(element, added);
            });
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
        SynchronizeElements(
            document, section, elementName, source, edited,
            SourceIndex,
            (element, original, changed) =>
            {
                UpdateAttributes(element, SourceAttributes(original), SourceAttributes(changed));
                if (!equal(original, changed)) element.SetAttributeValue(attribute, Vector(ToSourcePosition(position(changed))));
            },
            (element, added) =>
            {
                CopyAttributes(element, SourceAttributes(added));
                SetAddedPositionAttributes(element, added);
            });

        static int SourceIndex(T value) => value switch
        {
            MapPoint point => point.SourceIndex,
            MapSoundMarker sound => sound.SourceIndex,
            MapLightMarker light => light.SourceIndex,
            _ => throw new InvalidOperationException($"Unsupported positioned OPS type {typeof(T).Name}."),
        };

        static IReadOnlyDictionary<string, string> SourceAttributes(T value) => value switch
        {
            MapPoint point => point.SourceAttributes,
            MapSoundMarker sound => sound.SourceAttributes,
            MapLightMarker light => light.SourceAttributes,
            _ => throw new InvalidOperationException($"Unsupported positioned OPS type {typeof(T).Name}."),
        };

        static void SetAddedPositionAttributes(XElement element, T value)
        {
            switch (value)
            {
                case MapPoint point:
                    element.SetAttributeValue("name", point.Name);
                    element.SetAttributeValue("pos", Vector(ToSourcePosition(point.Position)));
                    if (point.Radius is not null) element.SetAttributeValue("radius", Number(point.Radius.Value));
                    break;
                case MapSoundMarker sound:
                    element.SetAttributeValue("seName", sound.SoundName);
                    element.SetAttributeValue("seType", sound.SourceKind);
                    element.SetAttributeValue("sePosition", Vector(ToSourcePosition(sound.Position)));
                    element.SetAttributeValue("seRange", Number(sound.Range));
                    element.SetAttributeValue("seRotation", Number(sound.SourceRotation));
                    element.SetAttributeValue("seScale", Vector(sound.SourceScale));
                    break;
                case MapLightMarker light:
                    element.SetAttributeValue("group", light.Group);
                    element.SetAttributeValue("type", light.Type);
                    element.SetAttributeValue("pos", Vector(ToSourcePosition(light.Position)));
                    element.SetAttributeValue("color", Vector(light.Color));
                    element.SetAttributeValue("colorPower", Number(light.ColorPower));
                    element.SetAttributeValue("innerRange", Number(light.InnerRange));
                    element.SetAttributeValue("outerRange", Number(light.OuterRange));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported positioned OPS type {typeof(T).Name}.");
            }
        }
    }

    private static void SynchronizeElements<T>(
        XDocument document,
        string section,
        string elementName,
        IReadOnlyList<T> source,
        IReadOnlyList<T> edited,
        Func<T, int> sourceIndex,
        Action<XElement, T, T> updateExisting,
        Action<XElement, T> initializeAdded)
    {
        if (source.Count == 0 && edited.Count == 0) return;
        var container = GetOrCreateSection(document, section);
        var sourceElements = container.Elements().Where(element => element.Name.LocalName == elementName).ToArray();
        if (sourceElements.Length != source.Count)
        {
            throw new InvalidDataException($"OPS section '{section}' changed while it was being edited.");
        }
        var elementsById = source.Select((value, index) => (Id: sourceIndex(value), Element: sourceElements[index]))
            .ToDictionary(value => value.Id, value => value.Element);
        var editedById = edited.ToDictionary(sourceIndex);
        foreach (var original in source)
        {
            var id = sourceIndex(original);
            if (!editedById.TryGetValue(id, out var changed))
            {
                elementsById[id].Remove();
                continue;
            }
            updateExisting(elementsById[id], original, changed);
        }
        var sourceIds = elementsById.Keys.ToHashSet();
        foreach (var added in edited.Where(value => !sourceIds.Contains(sourceIndex(value))).OrderBy(sourceIndex))
        {
            var element = new XElement(elementName);
            initializeAdded(element, added);
            container.Add(element);
        }
    }

    private static XElement GetOrCreateSection(XDocument document, string sectionName)
    {
        var root = document.Root ?? throw new InvalidDataException("OPS document has no root element.");
        var existing = root.Elements().FirstOrDefault(element => element.Name.LocalName == sectionName);
        if (existing is not null) return existing;
        var section = new XElement(sectionName);
        var requestedIndex = Array.IndexOf(CanonicalSectionOrder, sectionName);
        var following = requestedIndex < 0
            ? null
            : root.Elements().FirstOrDefault(element =>
            {
                var index = Array.IndexOf(CanonicalSectionOrder, element.Name.LocalName);
                return index > requestedIndex;
            });
        if (following is null) root.Add(section);
        else following.AddBeforeSelf(section);
        return section;
    }

    private static void SetVolumeAttributes(XElement element, MapVolume volume)
    {
        element.SetAttributeValue("name", volume.Name);
        var converted = OpsCoordinateConverter.ToSourceTransform(volume.Transform);
        element.SetAttributeValue("pos", $"{Vector(converted.Position)},  {Vector(converted.EulerRadians)},  {Vector(converted.Scale)}");
        element.SetAttributeValue("next", volume.DestinationMap);
        element.SetAttributeValue("entry", volume.DestinationEntry);
    }

    private static void SetCameraAttributes(XElement element, MapCameraMarker camera)
    {
        element.SetAttributeValue("no", camera.Name);
        element.SetAttributeValue("eye", Vector(ToSourcePosition(camera.Eye)));
        element.SetAttributeValue("lookat", Vector(ToSourcePosition(camera.LookAt)));
    }

    private static void CopyAttributes(XElement element, IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var attribute in attributes) element.SetAttributeValue(attribute.Key, attribute.Value);
    }

    private static void UpdateAttributes(
        XElement element,
        IReadOnlyDictionary<string, string> original,
        IReadOnlyDictionary<string, string> changed)
    {
        if (AttributesEqual(original, changed)) return;
        var changedNames = changed.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes().Where(value => !changedNames.Contains(value.Name.LocalName)).ToArray())
        {
            attribute.Remove();
        }
        CopyAttributes(element, changed);
    }

    private static bool AttributesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static IReadOnlyList<XElement> Elements(XDocument document, string section, string elementName)
        => document.Root!.Elements().First(element => element.Name.LocalName == section)
            .Elements().Where(element => element.Name.LocalName == elementName).ToArray();

    private static Vector3 ToSourcePosition(Vector3 editorPosition)
        => editorPosition;

    private static string Vector(Vector3 value)
        => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";

    private static string Vector(Vector4 value)
        => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}, {Number(value.W)}";

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool SameTransform(MapTransform left, MapTransform right)
        => left.Position == right.Position && left.Rotation == right.Rotation && left.Scale == right.Scale;

    private static bool SpatiallyEqual(MapScene left, MapScene right)
        => left.Props.SequenceEqual(right.Props)
            && left.Volumes.SequenceEqual(right.Volumes)
            && left.Points.SequenceEqual(right.Points)
            && left.Cameras.SequenceEqual(right.Cameras)
            && left.Sounds.SequenceEqual(right.Sounds)
            && left.Lights.SequenceEqual(right.Lights);

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
        ValidateUniqueIds(edited.Props.Select(value => value.SourceIndex), "props");
        ValidateUniqueIds(edited.Volumes.Where(value => value.Kind == MapVolumeKind.Entry).Select(value => value.SourceIndex), "entry volumes");
        ValidateUniqueIds(edited.Volumes.Where(value => value.Kind == MapVolumeKind.Group).Select(value => value.SourceIndex), "group volumes");
        ValidateUniqueIds(edited.Points.Select(value => value.SourceIndex), "points");
        ValidateUniqueIds(edited.Cameras.Select(value => value.SourceIndex), "cameras");
        ValidateUniqueIds(edited.Sounds.Select(value => value.SourceIndex), "sounds");
        ValidateUniqueIds(edited.Lights.Select(value => value.SourceIndex), "lights");

        static void ValidateUniqueIds(IEnumerable<int> ids, string collectionName)
        {
            if (ids.GroupBy(value => value).Any(group => group.Count() != 1))
            {
                throw new InvalidDataException($"Edited OPS {collectionName} contain duplicate source IDs.");
            }
        }
    }
}
