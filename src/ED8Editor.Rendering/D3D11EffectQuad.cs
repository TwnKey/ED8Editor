using System.Numerics;
using ED8Editor.Core;
using Vortice.Direct3D11;

namespace ED8Editor.Rendering;

/// <summary>How an effect segment is blended over the scene, from its blend byte.</summary>
public enum EffBlendMode
{
    /// <summary>Premultiplied alpha over what is already drawn.</summary>
    Alpha,

    /// <summary>The segment adds its colour: fire, glows, sparks.</summary>
    Additive,

    /// <summary>The segment subtracts its colour: smoke that darkens.</summary>
    Subtractive,
}

/// <summary>
/// One quad of a playing effect, in world space, with the piece of its texture
/// the segment's crop selects and the colours its tracks give it.
/// </summary>
/// <param name="Priority">
/// The order byte the segment carries: the engine draws lower priorities first.
/// </param>
public sealed record D3D11EffectQuad(
    Vector3 CornerA,
    Vector3 CornerB,
    Vector3 CornerC,
    Vector3 CornerD,
    Vector2 UvMinimum,
    Vector2 UvMaximum,
    Vector4 Color,
    Vector4 Add,
    ID3D11ShaderResourceView? Texture,
    EffBlendMode Blend,
    int Priority)
{
    /// <summary>Where the quad sits, for sorting it against the other ones.</summary>
    public Vector3 Center => (CornerA + CornerB + CornerC + CornerD) / 4f;
}

/// <summary>
/// The effect textures uploaded to the GPU, kept by asset name (I_EFTEX###) so
/// an effect that plays every frame uploads its texture once. An asset that
/// cannot be read is remembered as missing rather than retried.
/// </summary>
public sealed class D3D11EffectTextureResources : IDisposable
{
    private readonly D3D11ModelUploader uploader;
    private readonly Dictionary<string, D3D11TextureResources?> textures =
        new(StringComparer.OrdinalIgnoreCase);

    public D3D11EffectTextureResources(D3D11ModelUploader uploader)
        => this.uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));

    /// <summary>True once the asset has been uploaded or found missing.</summary>
    public bool Knows(string assetId) => textures.ContainsKey(assetId);

    /// <summary>Records an asset, or its absence when <paramref name="texture"/> is null.</summary>
    public void Add(string assetId, CpuTexture? texture)
    {
        if (textures.ContainsKey(assetId)) return;
        textures[assetId] = texture is null ? null : uploader.UploadTexture(texture);
    }

    public ID3D11ShaderResourceView? Find(string assetId)
        => textures.TryGetValue(assetId, out var texture) ? texture?.ShaderResourceView : null;

    public void Dispose()
    {
        foreach (var texture in textures.Values)
        {
            if (texture is null) continue;
            texture.ShaderResourceView.Dispose();
            texture.Texture.Dispose();
        }
        textures.Clear();
    }
}
