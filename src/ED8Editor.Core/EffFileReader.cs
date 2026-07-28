using System.Text;

namespace ED8Editor.Core;

/// <summary>
/// Reads the .eff container. The layout is little-endian throughout: fixed-width
/// name fields cut at their first null byte, and every array is a count followed
/// by its records. Which optional blocks a segment carries depends on the file's
/// version and on the segment's own flag word, so nothing here is guessed — the
/// two together decide what is read next.
/// </summary>
public static class EffFileReader
{
    public static EffFile Read(string path)
        => Read(File.ReadAllBytes(path));

    public static EffFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var cursor = new EffCursor(data);
        var version = cursor.ReadUInt32();
        if (!EffGameVersion.IsSupported(version))
        {
            throw new InvalidDataException(
                $"Unsupported .eff version {EffGameVersion.Describe(version)}.");
        }

        var file = new EffFile
        {
            Version = version,
            Unknown1 = cursor.ReadUInt32(),
        };

        // From version 0x6D the name is length-prefixed; before that it sits in a
        // fixed 16-byte field.
        var nameLength = version >= EffGameVersion.ColdSteel4 ? (int)cursor.ReadUInt32() : 16;
        var (effectName, effectNameRaw) = cursor.ReadFixedJapanese(nameLength);
        file.EffectName = effectName;
        file.EffectNameRaw = effectNameRaw;

        var textureCount = (int)cursor.ReadUInt32();
        for (var index = 0; index < textureCount; index++)
        {
            file.Textures.Add(cursor.ReadFixedAscii(20).Text);
        }

        var unknownCount = (int)cursor.ReadUInt32();
        for (var index = 0; index < unknownCount; index++)
        {
            file.UnknownNames.Add(cursor.ReadFixedAscii(36).Text);
        }

        var segmentCount = (int)cursor.ReadUInt32();
        for (var index = 0; index < segmentCount; index++)
        {
            file.Segments.Add(ReadSegment(cursor, version));
        }

        // Whatever follows the last segment (usually eight zero bytes) is kept as
        // is: the file must rewrite byte for byte.
        file.Trailing = cursor.ReadRemaining();
        return file;
    }

    private static EffSegment ReadSegment(EffCursor cursor, uint version)
    {
        var segment = new EffSegment();
        var (name, nameRaw) = cursor.ReadFixedJapanese(16);
        segment.Name = name;
        segment.NameRaw = nameRaw;
        var (textureName, textureNameRaw) = cursor.ReadFixedAscii(16);
        segment.TextureName = textureName;
        segment.TextureNameRaw = textureNameRaw;
        var (modelName, modelNameRaw) = cursor.ReadFixedAscii(16);
        segment.ModelName = modelName;
        segment.ModelNameRaw = modelNameRaw;

        // The console versions store the flag word inside a 16-byte field, at
        // offset 4. The PC layout has no such field at all.
        if (version >= EffGameVersion.PlayStationCs1)
        {
            var flagField = cursor.ReadBytes(16);
            segment.StructFlags = BitConverter.ToUInt32(flagField, 4);
        }

        cursor.ReadUInt32Into(segment.Data02);
        if (version >= EffGameVersion.PlayStationCs2) segment.Data03 = cursor.ReadSingles(2);
        cursor.ReadSinglesInto(segment.Data04);
        if (version < EffGameVersion.PlayStationCs2) segment.Data05 = cursor.ReadSingles(3);
        cursor.ReadSinglesInto(segment.Data06);
        if (version >= EffGameVersion.ColdSteel3) segment.Data07 = cursor.ReadSingles(4);
        cursor.ReadSinglesInto(segment.Data08);

        ReadTrack(cursor, segment.Position);
        ReadTrack(cursor, segment.Rotation);
        ReadTrack(cursor, segment.Scale);
        ReadTrack(cursor, segment.Rotation2);
        ReadTrack(cursor, segment.ColorMultiply);
        ReadTrack(cursor, segment.ColorAdd);

        if ((segment.StructFlags & 0x0100_0000) != 0) ReadTrack(cursor, segment.Data0F);
        if ((segment.StructFlags & 0x0400_0000) != 0) ReadTrack(cursor, segment.Data10);
        if ((segment.StructFlags & 0x0800_0000) != 0) ReadTrack(cursor, segment.Data11);
        if ((segment.StructFlags & 0x2000_0000) != 0) ReadTrack(cursor, segment.Data12);

        if ((segment.StructFlags & 0x0200_0000) != 0)
        {
            var outerCount = (int)cursor.ReadUInt32();
            for (var outer = 0; outer < outerCount; outer++)
            {
                var inner = new List<EffKeyframe>();
                ReadTrack(cursor, inner);
                segment.Data13.Add(inner);
            }
        }

        ReadTrack(cursor, segment.Children);

        if (version <= EffGameVersion.Pc)
        {
            segment.Data15 = cursor.ReadSingles(2);
            // The PC layout carries no flag word of its own: past this point it
            // always lays out blocks 16 and 17, which is exactly flags 0x001|0x002.
            segment.StructFlags = 3;
        }

        if ((segment.StructFlags & 0x002) != 0) segment.Data16 = cursor.ReadSingles(16);
        if ((segment.StructFlags & 0x001) != 0)
        {
            if (version >= EffGameVersion.PlayStationCs2)
            {
                var count = (int)cursor.ReadUInt32();
                for (var index = 0; index < count; index++)
                {
                    segment.Data17.Add(ReadRecord72(cursor));
                }
            }
            else
            {
                segment.Data17PcRaw = cursor.ReadBytes(16);
            }
        }

        if ((segment.StructFlags & 0x010) != 0) segment.Data18 = cursor.ReadUInt32s(4);
        if ((segment.StructFlags & 0x004) != 0) segment.Data19 = cursor.ReadUInt32s(8);
        if ((segment.StructFlags & 0x008) != 0) segment.Data1A = cursor.ReadSingles(24);
        if (version >= EffGameVersion.PlayStationCs1)
        {
            var count = (int)cursor.ReadUInt32();
            for (var index = 0; index < count; index++)
            {
                segment.Data1B.Add(cursor.ReadUInt32s(3));
            }
        }
        if ((segment.StructFlags & 0x020) != 0) segment.Data1C = cursor.ReadSingles(6);
        if ((segment.StructFlags & 0x040) != 0) segment.Data1D = cursor.ReadSingles(4);
        if ((segment.StructFlags & 0x080) != 0) segment.Data1E = cursor.ReadUInt32s(8);
        if ((segment.StructFlags & 0x100) != 0) segment.Data1F = cursor.ReadUInt32s(2);
        if ((segment.StructFlags & 0x200) != 0) segment.Data20 = cursor.ReadSingles(13);
        return segment;
    }

    private static void ReadTrack(EffCursor cursor, List<EffKeyframe> track)
    {
        var count = (int)cursor.ReadUInt32();
        for (var index = 0; index < count; index++)
        {
            var keyframe = new EffKeyframe();
            cursor.ReadSinglesInto(keyframe.Floats);
            cursor.ReadUInt32Into(keyframe.Ints);
            keyframe.Trailing = cursor.ReadSingle();
            track.Add(keyframe);
        }
    }

    private static EffRecord72 ReadRecord72(EffCursor cursor)
    {
        var record = new EffRecord72();
        cursor.ReadUInt32Into(record.Ints0);
        record.F0 = cursor.ReadSingle();
        record.Int1 = cursor.ReadUInt32();
        cursor.ReadSinglesInto(record.Floats);
        cursor.ReadUInt32Into(record.Ints1);
        return record;
    }
}

/// <summary>A little-endian read head over the file's bytes.</summary>
internal sealed class EffCursor
{
    private readonly byte[] data;
    private int position;

    public EffCursor(byte[] data) => this.data = data;

    public uint ReadUInt32()
    {
        Require(4);
        var value = BitConverter.ToUInt32(data, position);
        position += 4;
        return value;
    }

    public float ReadSingle()
    {
        Require(4);
        var value = BitConverter.ToSingle(data, position);
        position += 4;
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        Require(count);
        var slice = new byte[count];
        Array.Copy(data, position, slice, 0, count);
        position += count;
        return slice;
    }

    public byte[] ReadRemaining()
    {
        var slice = new byte[data.Length - position];
        Array.Copy(data, position, slice, 0, slice.Length);
        position = data.Length;
        return slice;
    }

    public float[] ReadSingles(int count)
    {
        var values = new float[count];
        ReadSinglesInto(values);
        return values;
    }

    public void ReadSinglesInto(float[] values)
    {
        for (var index = 0; index < values.Length; index++) values[index] = ReadSingle();
    }

    public uint[] ReadUInt32s(int count)
    {
        var values = new uint[count];
        ReadUInt32Into(values);
        return values;
    }

    public void ReadUInt32Into(uint[] values)
    {
        for (var index = 0; index < values.Length; index++) values[index] = ReadUInt32();
    }

    /// <summary>A fixed-width name field the engine writes in cp932.</summary>
    public (string Text, byte[] Raw) ReadFixedJapanese(int size)
    {
        var raw = ReadBytes(size);
        return (EffText.DecodeJapanese(raw), raw);
    }

    /// <summary>A fixed-width name field the engine writes in plain ASCII.</summary>
    public (string Text, byte[] Raw) ReadFixedAscii(int size)
    {
        var raw = ReadBytes(size);
        return (EffText.DecodeAscii(raw), raw);
    }

    private void Require(int count)
    {
        if (position + count > data.Length)
        {
            throw new InvalidDataException(
                $"The .eff file ends at {data.Length} bytes, before a {count}-byte field at {position}.");
        }
    }
}

/// <summary>
/// The two text encodings the format uses. Names are stored in fixed-width
/// fields terminated by a null byte, and anything past that terminator is
/// authoring leftovers the writer keeps rather than re-encodes.
/// </summary>
public static class EffText
{
    private static Encoding? japanese;

    public static string DecodeJapanese(byte[] field)
        => Japanese.GetString(field, 0, TerminatorIndex(field));

    public static string DecodeAscii(byte[] field)
        => Encoding.UTF8.GetString(field, 0, TerminatorIndex(field));

    public static byte[] EncodeJapanese(string text) => Japanese.GetBytes(text);

    public static byte[] EncodeAscii(string text) => Encoding.UTF8.GetBytes(text);

    private static Encoding Japanese
    {
        get
        {
            if (japanese is not null) return japanese;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            japanese = Encoding.GetEncoding(932);
            return japanese;
        }
    }

    private static int TerminatorIndex(byte[] field)
    {
        var index = Array.IndexOf(field, (byte)0);
        return index < 0 ? field.Length : index;
    }
}
