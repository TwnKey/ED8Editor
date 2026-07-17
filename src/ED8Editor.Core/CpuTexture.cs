namespace ED8Editor.Core;

public sealed record CpuTexture(
    string Name,
    int Width,
    int Height,
    int MipCount,
    string Format,
    byte[] Data);
