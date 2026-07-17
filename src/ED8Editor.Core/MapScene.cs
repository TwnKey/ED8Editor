namespace ED8Editor.Core;

public sealed record MapScene(
    string SourcePath,
    IReadOnlyList<MapProp> Props,
    IReadOnlyList<byte> OriginalBytes);
