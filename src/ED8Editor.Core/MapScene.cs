namespace ED8Editor.Core;

public sealed record MapScene(
    string SourcePath,
    IReadOnlyList<MapProp> Props,
    IReadOnlyList<byte> OriginalBytes,
    IReadOnlyList<MapVolume> Volumes,
    IReadOnlyList<MapPoint> Points,
    IReadOnlyList<MapCameraMarker> Cameras,
    IReadOnlyList<MapSoundMarker> Sounds,
    IReadOnlyList<MapLightMarker> Lights,
    MapEnvironment? DefaultEnvironment = null);
