using System.Globalization;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed record OpsCreationInput(string Name, string DisplayName);

public sealed record OpsSpatialElementDraft(
    SceneElementSelection Selection,
    SceneTransformCapabilities Capabilities,
    SceneTransform Transform,
    MapVolume? Volume = null,
    MapPoint? Point = null,
    MapCameraMarker? Camera = null,
    MapSoundMarker? Sound = null,
    MapLightMarker? Light = null);

public sealed class OpsSpatialCreationProfile
{
    private readonly Func<int, string, Vector3, IReadOnlyDictionary<string, string>, OpsSpatialElementDraft> factory;
    private readonly Func<IReadOnlyDictionary<string, string>, string> baseName;

    internal OpsSpatialCreationProfile(
        string id,
        string displayName,
        SceneElementKind kind,
        string evidence,
        IReadOnlyList<OpsCreationInput> inputs,
        Func<IReadOnlyDictionary<string, string>, string> baseName,
        Func<int, string, Vector3, IReadOnlyDictionary<string, string>, OpsSpatialElementDraft> factory)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Evidence = evidence;
        Inputs = inputs;
        this.baseName = baseName;
        this.factory = factory;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SceneElementKind Kind { get; }
    public string Evidence { get; }
    public IReadOnlyList<OpsCreationInput> Inputs { get; }

    internal string CreateBaseName(IReadOnlyDictionary<string, string> values)
    {
        ValidateInputs(values);
        return baseName(values);
    }

    internal OpsSpatialElementDraft Create(
        int sourceIndex,
        string name,
        Vector3 position,
        IReadOnlyDictionary<string, string> values)
    {
        ValidateInputs(values);
        return factory(sourceIndex, name, position, values);
    }

    public void ValidateInputs(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var input in Inputs)
        {
            if (!values.TryGetValue(input.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"'{input.DisplayName}' is required for {DisplayName}.", input.Name);
            }
        }
    }
}

public static class OpsSpatialCreationCatalog
{
    public static IReadOnlyList<OpsSpatialCreationProfile> Profiles { get; } = new[]
    {
        EntryTransitionType2(),
        GroupBox(),
        EventLookPoint(),
        MapCamera(),
        PointSound(),
        PointLight(),
    };

    private static OpsSpatialCreationProfile EntryTransitionType2()
        => new(
            "observed.m0010.entry_type_2",
            "TP / EntryBox (type 2)",
            SceneElementKind.EntryVolume,
            "Observed in m0010.ops: go_r0010",
            new[]
            {
                new OpsCreationInput("next", "Destination map"),
                new OpsCreationInput("entry", "Destination entry"),
            },
            values => $"go_{values["next"].Trim()}",
            (id, name, position, values) =>
            {
                var transform = VolumeTransform(position, new Vector3(10f, 2.5f, 2f));
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["next"] = values["next"].Trim(),
                    ["entry"] = values["entry"].Trim(),
                    ["placeid"] = "0",
                    ["flag"] = "0x1",
                    ["pos"] = VolumePosition(transform),
                    ["distance"] = "2",
                    ["cameraDir"] = "-1",
                    ["entryType"] = "2",
                    ["markPos"] = "0, 0, 0",
                };
                var volume = new MapVolume(
                    id, MapVolumeKind.Entry, name, transform,
                    attributes["next"], attributes["entry"], attributes);
                var selection = new SceneElementSelection(SceneElementKind.EntryVolume, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.All, SceneTransform.FromMapTransform(transform), Volume: volume);
            });

    private static OpsSpatialCreationProfile GroupBox()
        => new(
            "observed.c0010.group_box",
            "GroupBox",
            SceneElementKind.GroupVolume,
            "Observed in c0010.ops: 1F",
            Array.Empty<OpsCreationInput>(),
            _ => "group",
            (id, name, position, _) =>
            {
                var transform = VolumeTransform(position, new Vector3(200f, 8f, 200f));
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["flag"] = "0x3",
                    ["pos"] = VolumePosition(transform),
                };
                var volume = new MapVolume(id, MapVolumeKind.Group, name, transform, null, null, attributes);
                var selection = new SceneElementSelection(SceneElementKind.GroupVolume, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.All, SceneTransform.FromMapTransform(transform), Volume: volume);
            });

    private static OpsSpatialCreationProfile EventLookPoint()
        => new(
            "observed.a0006.look_point_type_0",
            "LookPoint event (type 0)",
            SceneElementKind.LookPoint,
            "Observed in a0006.ops: LP_event00",
            Array.Empty<OpsCreationInput>(),
            _ => "LP_event",
            (id, name, position, _) =>
            {
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["flag"] = "0x1",
                    ["type"] = "0",
                    ["pos"] = Position(position),
                    ["markPos"] = "0, 1, 0",
                    ["radius"] = "1.5",
                    ["rotY"] = "0",
                };
                var point = new MapPoint(id, MapPointKind.LookPoint, name, position, 1.5f, attributes);
                var selection = new SceneElementSelection(SceneElementKind.LookPoint, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.Translate,
                    new SceneTransform(position, Quaternion.Identity, Vector3.One), Point: point);
            });

    private static OpsSpatialCreationProfile PointSound()
        => new(
            "observed.a0007.point_sound",
            "Point sound",
            SceneElementKind.Sound,
            "Observed in a0007.ops: POINT SoundObject",
            new[] { new OpsCreationInput("seName", "Sound name") },
            values => values["seName"].Trim(),
            (id, name, position, values) =>
            {
                var soundName = values["seName"].Trim();
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["seName"] = soundName,
                    ["seGroupId"] = "0",
                    ["seType"] = "POINT",
                    ["seVolume"] = "1",
                    ["seRange"] = "10",
                    ["sePosition"] = Position(position),
                    ["seRotation"] = "0",
                    ["seScale"] = "1, 1, 1",
                };
                var sound = new MapSoundMarker(
                    id, soundName, MapSoundKind.Point, "POINT", position, 10f, 0f, Vector3.One, attributes);
                var selection = new SceneElementSelection(SceneElementKind.Sound, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.Translate,
                    new SceneTransform(position, Quaternion.Identity, Vector3.One), Sound: sound);
            });

    private static OpsSpatialCreationProfile MapCamera()
        => new(
            "observed.a1700.map_camera_type_3",
            "Map camera (type 3)",
            SceneElementKind.Camera,
            "Observed in a1700.ops: no 0 / type 3",
            Array.Empty<OpsCreationInput>(),
            _ => "Camera",
            (id, name, position, _) =>
            {
                var lookAt = position + new Vector3(-0.86f, -1f, -1.5f);
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["no"] = "0",
                    ["type"] = "3",
                    ["flag"] = "0x0",
                    ["eye"] = Position(position),
                    ["lookat"] = Position(lookAt),
                    ["offset"] = "0.00,1.30,0.00",
                    ["rot"] = "3.00,335.00,0.00",
                    ["dist"] = "6.00",
                    ["fov"] = "35.00",
                    ["time"] = "2000",
                };
                var camera = new MapCameraMarker(id, "0", position, lookAt, attributes);
                var selection = new SceneElementSelection(SceneElementKind.Camera, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.Translate,
                    new SceneTransform(position, Quaternion.Identity, Vector3.One), Camera: camera);
            });

    private static OpsSpatialCreationProfile PointLight()
        => new(
            "observed.a0004.point_light_0x103",
            "Point light",
            SceneElementKind.Light,
            "Observed twice in a0004.ops: type 1 / flag 0x103",
            Array.Empty<OpsCreationInput>(),
            _ => "Light",
            (id, name, position, _) =>
            {
                var color = new Vector4(0.98f, 0.89f, 0.51f, 1f);
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["group"] = "0",
                    ["type"] = "1",
                    ["flag"] = "0x103",
                    ["pos"] = Position(position),
                    ["color"] = "0.98, 0.89, 0.51, 1",
                    ["colorPower"] = "1",
                    ["innerRange"] = "0",
                    ["outerRange"] = "5",
                };
                var light = new MapLightMarker(id, "0", "1", position, color, 1f, 0f, 5f, attributes);
                var selection = new SceneElementSelection(SceneElementKind.Light, id, name);
                return new OpsSpatialElementDraft(
                    selection, SceneTransformCapabilities.Translate,
                    new SceneTransform(position, Quaternion.Identity, Vector3.One), Light: light);
            });

    private static MapTransform VolumeTransform(Vector3 position, Vector3 scale)
        => new(position, Quaternion.Identity, scale, ToSourcePosition(position), Vector3.Zero);

    private static string Position(Vector3 value)
        => $"{Number(-value.X)}, {Number(value.Y)}, {Number(value.Z)}";

    private static string VolumePosition(MapTransform transform)
        => $"{Position(transform.Position)},  0, 0, 0,  {Vector(transform.Scale)}";

    private static Vector3 ToSourcePosition(Vector3 value) => new(-value.X, value.Y, value.Z);
    private static string Vector(Vector3 value) => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
