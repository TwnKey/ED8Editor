using System.Numerics;

namespace ED8Editor.Core;

public enum MapVolumeKind
{
    Entry,
    Group,
}

public sealed record MapVolume(
    int SourceIndex,
    MapVolumeKind Kind,
    string Name,
    MapTransform Transform,
    string? DestinationMap,
    string? DestinationEntry,
    IReadOnlyDictionary<string, string> SourceAttributes);

public enum MapPointKind
{
    LookPoint,
}

public sealed record MapPoint(
    int SourceIndex,
    MapPointKind Kind,
    string Name,
    Vector3 Position,
    float? Radius,
    IReadOnlyDictionary<string, string> SourceAttributes);

public sealed record MapCameraMarker(
    int SourceIndex,
    string Name,
    Vector3 Eye,
    Vector3 LookAt,
    IReadOnlyDictionary<string, string> SourceAttributes);

public enum MapSoundKind
{
    Unknown,
    Point,
    Line,
    Box,
}

public sealed record MapSoundMarker(
    int SourceIndex,
    string SoundName,
    MapSoundKind Kind,
    string SourceKind,
    Vector3 Position,
    float Range,
    float SourceRotation,
    Vector3 SourceScale,
    IReadOnlyDictionary<string, string> SourceAttributes,
    int GroupId = 0,
    float Volume = 1f);

public sealed record MapLightMarker(
    int SourceIndex,
    string Group,
    string Type,
    Vector3 Position,
    Vector4 Color,
    float ColorPower,
    float InnerRange,
    float OuterRange,
    IReadOnlyDictionary<string, string> SourceAttributes);
