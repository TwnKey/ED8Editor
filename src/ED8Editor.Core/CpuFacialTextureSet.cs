namespace ED8Editor.Core;

/// <summary>
/// A character's authored CS1 facial textures. The channel names are the
/// resource suffixes used by FC_*.pkg: e, m, c, f and h.
/// </summary>
public sealed record CpuFacialTextureSet(
    string AssetId,
    IReadOnlyDictionary<FacialTextureKey, CpuTexture> Textures);

public readonly record struct FacialTextureKey(char Channel, int Frame)
{
    public char NormalizedChannel => char.ToLowerInvariant(Channel);
}
