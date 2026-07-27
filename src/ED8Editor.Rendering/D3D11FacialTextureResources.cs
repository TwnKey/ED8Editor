using ED8Editor.Core;
using Vortice.Direct3D11;

namespace ED8Editor.Rendering;

public sealed class D3D11FacialTextureResources : IDisposable
{
    private readonly IReadOnlyDictionary<FacialTextureKey, D3D11TextureResources> textures;

    public D3D11FacialTextureResources(
        CpuFacialTextureSet source,
        D3D11ModelUploader uploader)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(uploader);
        AssetId = source.AssetId;
        var uploaded = new Dictionary<FacialTextureKey, D3D11TextureResources>();
        try
        {
            foreach (var pair in source.Textures)
                uploaded.Add(pair.Key, uploader.UploadTexture(pair.Value));
            textures = uploaded;
        }
        catch
        {
            DisposeTextures(uploaded.Values);
            throw;
        }
    }

    public string AssetId { get; }

    public ID3D11ShaderResourceView? Find(char channel, int frame)
        => textures.TryGetValue(
            new FacialTextureKey(char.ToLowerInvariant(channel), frame),
            out var texture)
                ? texture.ShaderResourceView
                : null;

    public void Dispose() => DisposeTextures(textures.Values);

    private static void DisposeTextures(IEnumerable<D3D11TextureResources> resources)
    {
        foreach (var resource in resources)
        {
            resource.ShaderResourceView.Dispose();
            resource.Texture.Dispose();
        }
    }
}

public sealed record D3D11MaterialTextureOverride(
    ID3D11ShaderResourceView? DiffuseTexture = null,
    ID3D11ShaderResourceView? DiffuseTexture2 = null);
