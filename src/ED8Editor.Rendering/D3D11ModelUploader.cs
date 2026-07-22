using ED8Editor.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ED8Editor.Rendering;

public sealed class D3D11ModelUploader : IModelGpuUploader<D3D11ModelResources>
{
    private readonly ID3D11Device device;

    public D3D11ModelUploader(ID3D11Device device)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public D3D11ModelResources Upload(CpuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var meshes = new List<D3D11MeshResources>(model.Meshes.Count);
        var textures = new List<D3D11TextureResources>(model.Textures.Count);
        var buffers = new List<ID3D11Buffer>();
        long allocatedBytes = 0;
        try
        {
            foreach (var sourceTexture in model.Textures)
            {
                textures.Add(UploadTexture(sourceTexture));
                allocatedBytes = checked(allocatedBytes + sourceTexture.Data.Length);
            }

            foreach (var sourceMesh in model.Meshes.Where(value => value.Purpose == CpuMeshPurpose.Render))
            {
                var primitives = new List<D3D11PrimitiveResources>(sourceMesh.Primitives.Count);
                foreach (var sourcePrimitive in sourceMesh.Primitives)
                {
                    var vertexBuffers = sourcePrimitive.VertexBuffers.Select(source =>
                    {
                        var buffer = device.CreateBuffer(source.Data, BindFlags.VertexBuffer);
                        buffers.Add(buffer);
                        allocatedBytes = checked(allocatedBytes + source.Data.Length);
                        return new D3D11VertexBufferResource(buffer, source.Stride, source.VertexCount, source.Attributes);
                    }).ToArray();
                    var indexBuffer = device.CreateBuffer(sourcePrimitive.Indices.Data, BindFlags.IndexBuffer);
                    buffers.Add(indexBuffer);
                    allocatedBytes = checked(allocatedBytes + sourcePrimitive.Indices.Data.Length);
                    primitives.Add(new D3D11PrimitiveResources(
                        vertexBuffers,
                        indexBuffer,
                        sourcePrimitive.Indices.IndexElementSize,
                        sourcePrimitive.Indices.IndexCount,
                        sourcePrimitive.MaterialIndex,
                        sourcePrimitive.Topology,
                        sourcePrimitive.SkinBones));
                }
                meshes.Add(new D3D11MeshResources(
                    sourceMesh.Name, sourceMesh.LocalTransform, primitives, sourceMesh.SceneNodeIndex));
            }

            var materials = model.Materials.Select(material => new D3D11MaterialResources(
                material,
                material.TextureBindings.ToDictionary(
                    value => value.Key,
                    value => textures[value.Value].ShaderResourceView,
                    StringComparer.Ordinal))).ToArray();
            return new D3D11ModelResources(model.AssetId, meshes, textures, materials, allocatedBytes);
        }
        catch
        {
            foreach (var buffer in buffers) buffer.Dispose();
            foreach (var texture in textures)
            {
                texture.ShaderResourceView.Dispose();
                texture.Texture.Dispose();
            }
            throw;
        }
    }

    private unsafe D3D11TextureResources UploadTexture(CpuTexture source)
    {
        var format = MapFormat(source.Format);
        var description = new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = source.MipCount,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var subresources = new SubresourceData[source.MipCount];
        fixed (byte* data = source.Data)
        {
            var offset = 0;
            var width = source.Width;
            var height = source.Height;
            for (var mip = 0; mip < source.MipCount; mip++)
            {
                var (rowPitch, slicePitch) = CalculateMipLayout(width, height, source.Format);
                if (offset + slicePitch > source.Data.Length)
                {
                    throw new InvalidDataException($"Texture '{source.Name}' mip data is truncated.");
                }
                subresources[mip] = new SubresourceData((IntPtr)(data + offset), rowPitch, slicePitch);
                offset += slicePitch;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            var texture = device.CreateTexture2D(description, subresources);
            try
            {
                return new D3D11TextureResources(source, texture, device.CreateShaderResourceView(texture));
            }
            catch
            {
                texture.Dispose();
                throw;
            }
        }
    }

    private static (int RowPitch, int SlicePitch) CalculateMipLayout(int width, int height, string format)
    {
        var blockBytes = format switch
        {
            "DXT1" or "BC4" => 8,
            "DXT3" or "DXT5" or "BC5" or "BC6" or "BC7" => 16,
            _ => 0,
        };
        if (blockBytes != 0)
        {
            var rowPitch = Math.Max(1, (width + 3) / 4) * blockBytes;
            return (rowPitch, rowPitch * Math.Max(1, (height + 3) / 4));
        }

        var bitsPerPixel = format switch
        {
            "L8" or "A8" => 8,
            "LA8" or "RG8" or "L16" or "A16" or "R16F" or "L16F" or "DEPTH16" => 16,
            "LA16" or "RG16" or "RGBA8" or "ARGB8" or "A2RGB10" or "R32F" or "L32F"
                or "RG16F" or "LA16F" or "R32" or "DEPTH24" or "DEPTH24S8" or "DEPTH32" => 32,
            "RGBA16" or "RGBA16F" or "RG32F" or "LA32F" => 64,
            "RGBA32F" => 128,
            _ => throw new NotSupportedException($"Unsupported D3D11 texture format '{format}'."),
        };
        var pitch = checked(width * bitsPerPixel / 8);
        return (pitch, checked(pitch * height));
    }

    private static Format MapFormat(string format) => format switch
    {
        "L8" or "A8" => Format.R8_UNorm,
        "LA8" or "RG8" => Format.R8G8_UNorm,
        "L16" or "A16" => Format.R16_UInt,
        "LA16" or "RG16" => Format.R16G16_UInt,
        "RGBA8" => Format.R8G8B8A8_UNorm,
        "ARGB8" => Format.B8G8R8A8_UNorm,
        "A2RGB10" => Format.R10G10B10A2_UNorm,
        "RGBA16" => Format.R16G16B16A16_UInt,
        "DXT1" => Format.BC1_UNorm,
        "DXT3" => Format.BC2_UNorm,
        "DXT5" => Format.BC3_UNorm,
        "BC4" => Format.BC4_UNorm,
        "BC5" => Format.BC5_UNorm,
        "BC6" => Format.BC6H_Uf16,
        "BC7" => Format.BC7_UNorm,
        "RGBA16F" => Format.R16G16B16A16_Float,
        "RGBA32F" => Format.R32G32B32A32_Float,
        "R16F" or "L16F" => Format.R16_Float,
        "R32F" or "L32F" => Format.R32_Float,
        "RG16F" or "LA16F" => Format.R16G16_Float,
        "RG32F" or "LA32F" => Format.R32G32_Float,
        "R32" => Format.R32_UInt,
        "DEPTH16" => Format.R16_UNorm,
        "DEPTH24" or "DEPTH24S8" => Format.R24_UNorm_X8_Typeless,
        "DEPTH32" => Format.R32_Float,
        _ => throw new NotSupportedException($"Unsupported D3D11 texture format '{format}'."),
    };
}
