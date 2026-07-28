namespace ED8Editor.Core;

/// <summary>
/// The format version stamped at the head of an .eff file. Every version moves
/// fields around inside a segment, so a segment can only be read with its file's
/// version in hand:
/// 0x04 = CS1/CS2 PC ports and Hajimari, 0x6A = CS1 Vita/PS3, 0x6B = CS2
/// Vita/PS3, 0x6C = CS3, 0x6D = CS4.
/// </summary>
public static class EffGameVersion
{
    public const uint PlayStationCs1 = 0x6A;
    public const uint PlayStationCs2 = 0x6B;
    public const uint ColdSteel3 = 0x6C;
    public const uint ColdSteel4 = 0x6D;

    /// <summary>The version the Cold Steel 1 PC release ships.</summary>
    public const uint Pc = 0x04;

    public static bool IsSupported(uint version)
        => version is Pc or PlayStationCs1 or PlayStationCs2 or ColdSteel3 or ColdSteel4;

    public static string Describe(uint version) => version switch
    {
        Pc => "0x04 (CS1/CS2 PC, Hajimari)",
        PlayStationCs1 => "0x6A (CS1 Vita/PS3)",
        PlayStationCs2 => "0x6B (CS2 Vita/PS3)",
        ColdSteel3 => "0x6C (CS3)",
        ColdSteel4 => "0x6D (CS4)",
        _ => $"0x{version:X2} (unknown)",
    };
}

/// <summary>
/// One keyframe of a segment track — 48 bytes: nine floats, two integers and a
/// trailing float. The first four floats are the value, the next four the second
/// bound a random keyframe rolls against, and the ninth is the keyframe's time.
/// The low half of the first integer carries the mode bits read by
/// <see cref="EffTrackEvaluator"/>, its high half the track type.
/// </summary>
public sealed class EffKeyframe
{
    public float[] Floats { get; init; } = new float[9];

    public uint[] Ints { get; init; } = new uint[2];

    public float Trailing { get; set; }

    public ushort Flags
    {
        get => (ushort)(Ints[0] & 0xFFFF);
        set => Ints[0] = (Ints[0] & 0xFFFF0000u) | value;
    }

    public ushort TrackType => (ushort)(Ints[0] >> 16);

    public float Time
    {
        get => Floats[8];
        set => Floats[8] = value;
    }

    public EffKeyframe Clone() => new()
    {
        Floats = (float[])Floats.Clone(),
        Ints = (uint[])Ints.Clone(),
        Trailing = Trailing,
    };
}

/// <summary>
/// A 72-byte record of a segment's <see cref="EffSegment.Data17"/> block, which
/// only the console versions (0x6B and up) lay out in a readable form.
/// </summary>
public sealed class EffRecord72
{
    public uint[] Ints0 { get; init; } = new uint[3];

    public float F0 { get; set; }

    public uint Int1 { get; set; }

    public float[] Floats { get; init; } = new float[11];

    public uint[] Ints1 { get; init; } = new uint[2];
}

/// <summary>
/// A segment of an effect: one emitter, particle or mesh with its own animation
/// tracks. Fields whose meaning is not yet reversed keep the slot number the
/// format gives them, and the raw bytes of the fixed-width name fields are kept
/// so a file that is read and written back is byte for byte the original.
/// </summary>
public sealed class EffSegment
{
    public string Name { get; set; } = string.Empty;

    public byte[] NameRaw { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The base name of the texture asset the segment draws with — an effect
    /// texture package such as I_EFTEX000, which the game preloads through the
    /// file's <see cref="EffFile.Textures"/> list.
    /// </summary>
    public string TextureName { get; set; } = string.Empty;

    public byte[] TextureNameRaw { get; set; } = Array.Empty<byte>();

    /// <summary>The base name of the model a mesh segment draws, when it has one.</summary>
    public string ModelName { get; set; } = string.Empty;

    public byte[] ModelNameRaw { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Which optional blocks the segment carries. Read from a 16-byte field on
    /// the console versions; the PC version has no such field and the format
    /// fixes the value at 3 once the version-specific block 15 has been read.
    /// </summary>
    public uint StructFlags { get; set; }

    public uint[] Data02 { get; init; } = new uint[8];

    /// <summary>Two floats present from version 0x6B on.</summary>
    public float[]? Data03 { get; set; }

    public float[] Data04 { get; init; } = new float[12];

    /// <summary>Three floats present below version 0x6B, the PC layout.</summary>
    public float[]? Data05 { get; set; }

    public float[] Data06 { get; init; } = new float[9];

    /// <summary>Four floats present from version 0x6C on.</summary>
    public float[]? Data07 { get; set; }

    public float[] Data08 { get; init; } = new float[8];

    /// <summary>Track 09: position, in the effect's own space.</summary>
    public List<EffKeyframe> Position { get; init; } = new();

    /// <summary>Track 0A: rotation, Euler degrees.</summary>
    public List<EffKeyframe> Rotation { get; init; } = new();

    /// <summary>Track 0B: scale.</summary>
    public List<EffKeyframe> Scale { get; init; } = new();

    /// <summary>Track 0C: a second rotation, Euler degrees.</summary>
    public List<EffKeyframe> Rotation2 { get; init; } = new();

    /// <summary>Track 0D: the colour the segment multiplies by.</summary>
    public List<EffKeyframe> ColorMultiply { get; init; } = new();

    /// <summary>Track 0E: the colour the segment adds.</summary>
    public List<EffKeyframe> ColorAdd { get; init; } = new();

    /// <summary>Present when <see cref="StructFlags"/> has 0x01000000.</summary>
    public List<EffKeyframe> Data0F { get; init; } = new();

    /// <summary>Present when <see cref="StructFlags"/> has 0x04000000.</summary>
    public List<EffKeyframe> Data10 { get; init; } = new();

    /// <summary>Present when <see cref="StructFlags"/> has 0x08000000.</summary>
    public List<EffKeyframe> Data11 { get; init; } = new();

    /// <summary>Present when <see cref="StructFlags"/> has 0x20000000.</summary>
    public List<EffKeyframe> Data12 { get; init; } = new();

    /// <summary>Nested records, present when <see cref="StructFlags"/> has 0x02000000.</summary>
    public List<List<EffKeyframe>> Data13 { get; init; } = new();

    /// <summary>Block 14: the spawn descriptors of this segment's children.</summary>
    public List<EffKeyframe> Children { get; init; } = new();

    /// <summary>Two floats the PC layout (version 0x04 and below) carries.</summary>
    public float[]? Data15 { get; set; }

    /// <summary>Sixteen floats, flag 0x002.</summary>
    public float[]? Data16 { get; set; }

    /// <summary>Flag 0x001, from version 0x6B on.</summary>
    public List<EffRecord72> Data17 { get; init; } = new();

    /// <summary>
    /// Flag 0x001 below version 0x6B: a 16-byte block whose layout is not
    /// reversed, kept verbatim so the file still rewrites byte for byte.
    /// </summary>
    public byte[] Data17PcRaw { get; set; } = Array.Empty<byte>();

    /// <summary>Four integers, flag 0x010.</summary>
    public uint[]? Data18 { get; set; }

    /// <summary>Eight integers, flag 0x004.</summary>
    public uint[]? Data19 { get; set; }

    /// <summary>Twenty-four floats, flag 0x008.</summary>
    public float[]? Data1A { get; set; }

    /// <summary>Triples of integers, present from version 0x6A on.</summary>
    public List<uint[]> Data1B { get; init; } = new();

    /// <summary>Six floats, flag 0x020.</summary>
    public float[]? Data1C { get; set; }

    /// <summary>Four floats, flag 0x040.</summary>
    public float[]? Data1D { get; set; }

    /// <summary>Eight integers, flag 0x080.</summary>
    public uint[]? Data1E { get; set; }

    /// <summary>Two integers, flag 0x100.</summary>
    public uint[]? Data1F { get; set; }

    /// <summary>Thirteen floats, flag 0x200.</summary>
    public float[]? Data20 { get; set; }
}

/// <summary>
/// A parsed .eff file: the effect's name, the textures its segments draw with,
/// and the segments themselves. Bytes past the last segment
/// are kept in <see cref="Trailing"/> so writing the file back reproduces it
/// exactly.
/// </summary>
public sealed class EffFile
{
    public uint Version { get; set; } = EffGameVersion.Pc;

    /// <summary>The word after the version; its meaning is not reversed.</summary>
    public uint Unknown1 { get; set; }

    public string EffectName { get; set; } = string.Empty;

    public byte[] EffectNameRaw { get; set; } = Array.Empty<byte>();

    /// <summary>Texture names, each stored in a 20-byte field.</summary>
    public List<string> Textures { get; init; } = new();

    /// <summary>
    /// A second list of names, each stored in a 36-byte field. What the engine
    /// looks up with them is not reversed; they are read and written verbatim.
    /// </summary>
    public List<string> UnknownNames { get; init; } = new();

    public List<EffSegment> Segments { get; init; } = new();

    public byte[] Trailing { get; set; } = Array.Empty<byte>();
}
