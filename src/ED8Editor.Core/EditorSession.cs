namespace ED8Editor.Core;

public sealed record EditorSession(
    ScriptOpenResult Script,
    MapScene? Map,
    IReadOnlyDictionary<string, AssetResolution> AssetResolutions,
    IReadOnlyDictionary<string, AssetManifestLoad> AssetManifests,
    IReadOnlyDictionary<string, AssetModelLoad> AssetModels);
