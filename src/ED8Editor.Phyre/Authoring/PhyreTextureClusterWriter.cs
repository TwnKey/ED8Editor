using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Writes a whole texture cluster from an image, with nothing borrowed from the
/// game: the schema comes from <see cref="PhyreTextureSchema"/>, the namespace
/// from <see cref="PhyreNamespaceWriter"/>, the fixup tables from
/// <see cref="PhyreFixupWriter"/>, and the rest is laid out here.
///
/// A texture holds two objects. A PAssetReference says which image the texture
/// was built from — its three members are pointers, so they are zero on disk and
/// the fixups say what they point at — followed by the path itself as its array.
/// Then a PTexture2D carries the size, the mip count and a pointer to the name
/// of its pixel format.
/// </summary>
public static class PhyreTextureClusterWriter
{
    private const uint Marker = 0x50485952;
    private const uint HeaderSize = 84;

    /// <summary>The platform stamp of the D3D11 build, "11XD" on disk.</summary>
    private const uint PlatformId = 0x44583131;

    /// <summary>Where the path an asset reference was built from is written.</summary>
    private const string AssetPathPrefix = "effects/images/";

    private const string AssetReference = "PAssetReference";
    private const string Texture = "PTexture2D";
    private const string TextureCommon = "PTextureCommonBase";
    private const string TextureBase = "PTexture2DBase";

    /// <summary>The texture does not carry every mip its size allows.</summary>
    private const uint IncompleteMipChain = 2;

    /// <param name="pixels">The whole mip chain, largest first.</param>
    public static byte[] Write(
        string assetPath,
        int width,
        int height,
        string format,
        int mipCount,
        byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var classes = PhyreTextureSchema.Classes;
        var packedNamespace = PhyreNamespaceWriter.Write(
            PhyreTextureSchema.TypeNames, classes, PhyreTextureSchema.Header);

        // The path is stored with room for its terminator, rounded up so what
        // follows it stays aligned.
        var path = Encoding.ASCII.GetBytes(assetPath);
        var pathSize = Align(path.Length + 1, 4);
        var referenceSize = ClassSize(AssetReference);
        var textureSize = ClassSize(Texture);

        var objects = new byte[referenceSize + pathSize + textureSize];
        path.CopyTo(objects.AsSpan(referenceSize));
        var texture = objects.AsSpan(referenceSize + pathSize);
        // The two mip fields say different things, which only shows on a texture
        // that does not carry a full chain: m_mipmapCount counts the mips that
        // are actually stored, below the largest, while m_maxMipLevel is the
        // depth the size itself allows — a 1024-pixel texture says ten even when
        // it stores a single level.
        WriteMember(texture, TextureCommon, "m_mipmapCount", (uint)Math.Max(0, mipCount - 1));
        var deepest = DeepestMipLevel(width, height);
        WriteMember(texture, TextureCommon, "m_maxMipLevel", deepest);
        // And a texture that stops short of that depth says so: the flag is set
        // on every shipped texture whose stored chain is shorter than its size
        // allows, and clear on every one that carries the chain in full.
        WriteMember(
            texture,
            TextureCommon,
            "m_textureFlags",
            (uint)mipCount - 1 < deepest ? IncompleteMipChain : 0u);
        WriteMember(texture, TextureBase, "m_width", (uint)width);
        WriteMember(texture, TextureBase, "m_height", (uint)height);

        // The two names the cluster needs at load time: the class of the asset it
        // stands for, and the name of its pixel format.
        var userData = new MemoryStream();
        var typeNameOffset = (uint)userData.Length;
        userData.Write(Encoding.ASCII.GetBytes(Texture));
        userData.WriteByte(0);
        var formatOffset = (uint)userData.Length;
        userData.Write(Encoding.ASCII.GetBytes(format));
        userData.WriteByte(0);
        var userBytes = userData.ToArray();
        var userDescriptors = new byte[2 * 12];
        WriteUserDescriptor(
            userDescriptors.AsSpan(0),
            TypeId("PClassDescriptor"),
            (uint)Texture.Length + 1,
            typeNameOffset);
        WriteUserDescriptor(
            userDescriptors.AsSpan(12),
            TypeId("PTextureFormatBase"),
            (uint)format.Length + 1,
            formatOffset);

        var groups = new[]
        {
            new PhyreInstanceGroup(
                0,
                PhyreTextureSchema.ClassId(AssetReference),
                AssetReference,
                1,
                (uint)(referenceSize + pathSize),
                (uint)referenceSize,
                (uint)pathSize,
                1,
                2,
                0),
            new PhyreInstanceGroup(
                1,
                PhyreTextureSchema.ClassId(Texture),
                Texture,
                1,
                (uint)textureSize,
                (uint)textureSize,
                0,
                0,
                1,
                0),
        };

        // The asset reference points at the texture, says what class the asset
        // is, and holds its path; the texture points at the name of its format.
        var pointers = new[]
        {
            new PhyrePointerFixup(0, 0, MemberId(AssetReference, "m_asset"), 1, 0, 0, 0, null),
            new PhyrePointerFixup(0, 0, MemberId(AssetReference, "m_assetType"), 0, 0, 0, 0, 0),
            new PhyrePointerFixup(1, 0, MemberId(TextureCommon, "m_format"), 0, 0, 0, 0, 1),
        };
        var arrays = new[]
        {
            new PhyreArrayFixup(
                0,
                0,
                MemberOffsetSource(AssetReference, "m_id"),
                0,
                0),
        };
        var pointerBytes = PhyreFixupWriter.WritePointers(pointers, groups);
        var arrayBytes = PhyreFixupWriter.WriteArrays(arrays, groups);

        var header = new byte[HeaderSize];
        WriteHeader(header, "m_phyreMarker", Marker);
        WriteHeader(header, "m_size", HeaderSize);
        WriteHeader(header, "m_packedNamespaceSize", (uint)packedNamespace.Length);
        WriteHeader(header, "m_platformID", PlatformId);
        WriteHeader(header, "m_instanceListCount", (uint)groups.Length);
        WriteHeader(header, "m_arrayFixupSize", (uint)arrayBytes.Length);
        WriteHeader(header, "m_arrayFixupCount", (uint)arrays.Length);
        WriteHeader(header, "m_pointerFixupSize", (uint)pointerBytes.Length);
        WriteHeader(header, "m_pointerFixupCount", (uint)pointers.Length);
        WriteHeader(header, "m_userFixupCount", 2);
        WriteHeader(header, "m_userFixupDataSize", (uint)userBytes.Length);
        WriteHeader(header, "m_totalDataSize", (uint)objects.Length);
        WriteHeader(header, "m_maxTextureBufferSize", (uint)TopMipSize(format, width, height));

        var sections = new PhyreClusterSections(
            null!,
            header,
            packedNamespace,
            PhyreClusterSectionReader.WriteInstanceHeaders(groups, ReadOnlyMemory<byte>.Empty),
            objects,
            userBytes,
            userDescriptors,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            pointerBytes,
            arrayBytes,
            pixels);
        return sections.Compose();
    }

    /// <summary>The path a texture of this name records for its image.</summary>
    public static string AssetPathFor(string assetName) => $"{AssetPrefix()}{assetName}.dds";

    private static string AssetPrefix() => AssetPathPrefix;

    private static int TopMipSize(string format, int width, int height) => format switch
    {
        "ARGB8" or "RGBA8" => width * height * 4,
        "DXT1" => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
        "DXT3" or "DXT5" => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16,
        "L8" or "A8" => width * height,
        _ => throw new NotSupportedException($"Unknown texture format '{format}'."),
    };

    /// <summary>How many times a texture of this size can be halved.</summary>
    private static uint DeepestMipLevel(int width, int height)
    {
        var level = 0u;
        var size = Math.Max(width, height);
        while (size > 1)
        {
            size /= 2;
            level++;
        }
        return level;
    }

    private static int Align(int value, int alignment)
        => (value + alignment - 1) / alignment * alignment;

    private static int ClassSize(string name) => (int)PhyreTextureSchema.Classes
        .First(value => value.Name.Equals(name, StringComparison.Ordinal)).Size;

    /// <summary>The identifier a member carries in a fixup, counted across all classes.</summary>
    private static uint MemberId(string className, string memberName)
    {
        var index = 0u;
        foreach (var descriptor in PhyreTextureSchema.Classes)
        {
            foreach (var member in descriptor.Members)
            {
                if (descriptor.Name.Equals(className, StringComparison.Ordinal)
                    && member.Name.Equals(memberName, StringComparison.Ordinal))
                {
                    return index;
                }
                index++;
            }
        }
        throw new ArgumentException($"'{className}' binds no '{memberName}'.", nameof(memberName));
    }

    /// <summary>A fixup that names an offset rather than a member sets the top bit.</summary>
    private static uint MemberOffsetSource(string className, string memberName)
        => PhyreTextureSchema.MemberOffset(className, memberName) | 0x80000000u;

    private static uint TypeId(string name)
    {
        var types = PhyreTextureSchema.TypeNames;
        for (var index = 0; index < types.Count; index++)
        {
            if (types[index].Equals(name, StringComparison.Ordinal)) return (uint)index;
        }
        // Past the primitive types, identifiers count the classes, one-based.
        return (uint)types.Count + PhyreTextureSchema.ClassId(name);
    }

    private static void WriteMember(Span<byte> target, string className, string memberName, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            target[(int)PhyreTextureSchema.MemberOffset(className, memberName)..], value);

    private static void WriteHeader(Span<byte> header, string memberName, uint value)
    {
        var className = memberName is "m_indexBufferSize" or "m_vertexBufferSize"
            or "m_maxTextureBufferSize"
            ? "PClusterHeaderD3D11"
            : "PClusterHeaderBase";
        WriteMember(header, className, memberName, value);
    }

    private static void WriteUserDescriptor(Span<byte> target, uint typeId, uint size, uint offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, typeId);
        BinaryPrimitives.WriteUInt32LittleEndian(target[4..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(target[8..], offset);
    }
}
