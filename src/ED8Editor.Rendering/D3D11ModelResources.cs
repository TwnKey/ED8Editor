using ED8Editor.Core;
using System.Numerics;
using Vortice.Direct3D11;

namespace ED8Editor.Rendering;

public sealed record D3D11VertexBufferResource(
    ID3D11Buffer Buffer,
    int Stride,
    int VertexCount,
    IReadOnlyList<CpuVertexAttribute> Attributes);

public sealed record D3D11PrimitiveResources(
    IReadOnlyList<D3D11VertexBufferResource> VertexBuffers,
    ID3D11Buffer IndexBuffer,
    int IndexElementSize,
    int IndexCount,
    int MaterialIndex,
    PrimitiveTopology Topology);

public sealed record D3D11MeshResources(
    string Name,
    Matrix4x4 LocalTransform,
    IReadOnlyList<D3D11PrimitiveResources> Primitives);

public sealed record D3D11SceneInstance(
    int SceneInstanceId,
    D3D11ModelResources Model,
    Matrix4x4 Transform,
    bool IsSelected);

public sealed record D3D11TextureResources(
    CpuTexture Source,
    ID3D11Texture2D Texture,
    ID3D11ShaderResourceView ShaderResourceView);

public sealed record D3D11MaterialResources(
    CpuMaterial Source,
    IReadOnlyDictionary<string, ID3D11ShaderResourceView> TextureBindings);

public sealed class D3D11ModelResources : IDisposable
{
    public D3D11ModelResources(
        string assetId,
        IReadOnlyList<D3D11MeshResources> meshes,
        IReadOnlyList<D3D11TextureResources> textures,
        IReadOnlyList<D3D11MaterialResources> materials,
        long allocatedBytes)
    {
        AssetId = assetId;
        Meshes = meshes;
        Textures = textures;
        Materials = materials;
        AllocatedBytes = allocatedBytes;
    }

    public string AssetId { get; }
    public IReadOnlyList<D3D11MeshResources> Meshes { get; }
    public IReadOnlyList<D3D11TextureResources> Textures { get; }
    public IReadOnlyList<D3D11MaterialResources> Materials { get; }
    public long AllocatedBytes { get; }

    public void Dispose()
    {
        foreach (var mesh in Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                primitive.IndexBuffer.Dispose();
                foreach (var vertexBuffer in primitive.VertexBuffers) vertexBuffer.Buffer.Dispose();
            }
        }

        foreach (var texture in Textures)
        {
            texture.ShaderResourceView.Dispose();
            texture.Texture.Dispose();
        }
    }
}
