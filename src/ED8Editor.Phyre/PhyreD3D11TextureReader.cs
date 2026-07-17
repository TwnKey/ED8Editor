using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreD3D11TextureReader : IPhyreTextureReader
{
    private const int MipCountOffset = 0x0c;
    private const int WidthOffset = 0x1c;
    private const int HeightOffset = 0x20;

    public CpuTexture Read(string name, ReadOnlyMemory<byte> phyreData)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Texture name is required.", nameof(name));

        var cluster = new PhyreClusterReader().Read(phyreData);
        var textureGroupIndex = FindTextureGroup(cluster);
        var textureGroup = cluster.Metadata.InstanceGroups[textureGroupIndex];
        if (textureGroup.Count != 1)
        {
            throw new InvalidPhyreException($"Expected one PTexture2D object, found {textureGroup.Count}.");
        }

        var texture = cluster.GetObject(textureGroupIndex, 0).Span;
        var width = ReadPositiveInt(texture, WidthOffset, cluster.Metadata.IsBigEndian, "width");
        var height = ReadPositiveInt(texture, HeightOffset, cluster.Metadata.IsBigEndian, "height");
        var additionalMipCount = ReadNonNegativeInt(texture, MipCountOffset, cluster.Metadata.IsBigEndian, "mipmap count");
        var mipCount = checked(additionalMipCount + 1);
        var format = ReadFormat(cluster, textureGroupIndex);

        var dataOffset = cluster.Fixups.VramDataOffset;
        var dataSize = CalculateDataSize(width, height, mipCount, format);
        if (dataOffset < 0 || dataOffset > phyreData.Length || dataSize > phyreData.Length - dataOffset)
        {
            throw new InvalidPhyreException("The texture GPU payload is truncated.");
        }

        return new CpuTexture(
            name,
            width,
            height,
            mipCount,
            format,
            phyreData.Slice(checked((int)dataOffset), dataSize).ToArray());
    }

    private static int FindTextureGroup(PhyreClusterData cluster)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            if (cluster.Metadata.InstanceGroups[index].ClassName == "PTexture2D")
            {
                return index;
            }
        }

        throw new InvalidPhyreException("Phyre cluster has no PTexture2D instance group.");
    }

    private static string ReadFormat(PhyreClusterData cluster, int textureGroupIndex)
    {
        var formatMember = cluster.Metadata.Classes
            .SelectMany(value => value.Members)
            .SingleOrDefault(value => value.Name == "m_format")
            ?? throw new InvalidPhyreException("PTexture2D metadata has no m_format member.");
        var pointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == textureGroupIndex
            && value.SourceObjectId == 0
            && value.IsClassDataMember
            && value.SourceMemberId == (uint)formatMember.Index)
            ?? throw new InvalidPhyreException("PTexture2D has no format fixup.");
        if (pointer.UserFixupId is not { } userFixupId || userFixupId >= cluster.Fixups.UserFixups.Count)
        {
            throw new InvalidPhyreException("PTexture2D format does not reference a valid user fixup.");
        }

        var format = cluster.Fixups.UserFixups[checked((int)userFixupId)].Text;
        return string.IsNullOrWhiteSpace(format)
            ? throw new InvalidPhyreException("PTexture2D format fixup has no name.")
            : format;
    }

    private static int ReadPositiveInt(ReadOnlySpan<byte> data, int offset, bool bigEndian, string field)
    {
        if ((uint)offset > data.Length - sizeof(uint))
        {
            throw new InvalidPhyreException($"PTexture2D {field} lies outside its object.");
        }

        var source = data.Slice(offset, sizeof(uint));
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(source)
            : BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (value == 0 || value > int.MaxValue)
        {
            throw new InvalidPhyreException($"PTexture2D has an invalid {field} ({value}).");
        }

        return checked((int)value);
    }

    private static int ReadNonNegativeInt(ReadOnlySpan<byte> data, int offset, bool bigEndian, string field)
    {
        if ((uint)offset > data.Length - sizeof(uint))
        {
            throw new InvalidPhyreException($"PTexture2D {field} lies outside its object.");
        }

        var source = data.Slice(offset, sizeof(uint));
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(source)
            : BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (value >= int.MaxValue)
        {
            throw new InvalidPhyreException($"PTexture2D has an invalid {field} ({value}).");
        }

        return checked((int)value);
    }

    private static int CalculateDataSize(int width, int height, int mipCount, string format)
    {
        var blockBytes = format switch
        {
            "DXT1" or "BC4" => 8,
            "DXT3" or "DXT5" or "BC5" or "BC6" or "BC7" => 16,
            _ => 0,
        };
        var bitsPerPixel = format switch
        {
            "L8" or "A8" => 8,
            "LA8" or "RG8" or "L16" or "A16" or "R16F" or "L16F" or "DEPTH16" => 16,
            "LA16" or "RG16" or "RGBA8" or "ARGB8" or "A2RGB10" or "R32F" or "L32F"
                or "RG16F" or "LA16F" or "R32" or "DEPTH24" or "DEPTH24S8" or "DEPTH32" => 32,
            "RGBA16" or "RGBA16F" or "RG32F" or "LA32F" => 64,
            "RGBA32F" => 128,
            _ when blockBytes != 0 => 0,
            _ => throw new InvalidPhyreException($"Unsupported D3D11 texture format '{format}'."),
        };

        long total = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var mip = 0; mip < mipCount; mip++)
        {
            total = checked(total + (blockBytes != 0
                ? (long)Math.Max(1, (mipWidth + 3) / 4) * Math.Max(1, (mipHeight + 3) / 4) * blockBytes
                : (long)mipWidth * mipHeight * bitsPerPixel / 8));
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        if (total > int.MaxValue)
        {
            throw new InvalidPhyreException("Texture GPU payload is too large.");
        }

        return checked((int)total);
    }
}
