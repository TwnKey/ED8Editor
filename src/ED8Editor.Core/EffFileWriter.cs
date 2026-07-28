namespace ED8Editor.Core;

/// <summary>
/// Writes the .eff container back, mirroring <see cref="EffFileReader"/> field
/// for field. A name field is written from its original bytes whenever the name
/// still decodes to what is stored — fixed-width fields keep authoring leftovers
/// past their null terminator, and re-encoding would drop them — so a file that
/// is read and written untouched comes out byte for byte the original.
/// </summary>
public static class EffFileWriter
{
    public static void Write(EffFile file, string path)
        => File.WriteAllBytes(path, Write(file));

    public static byte[] Write(EffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var writer = new EffBuffer();
        writer.WriteUInt32(file.Version);
        writer.WriteUInt32(file.Unknown1);

        if (file.Version >= EffGameVersion.ColdSteel4)
        {
            var bytes = EffText.EncodeJapanese(file.EffectName);
            writer.WriteUInt32((uint)bytes.Length);
            writer.WriteBytes(bytes);
        }
        else
        {
            writer.WriteFixedJapanese(file.EffectName, file.EffectNameRaw, 16);
        }

        writer.WriteUInt32((uint)file.Textures.Count);
        foreach (var texture in file.Textures) writer.WriteFixedAscii(texture, 20);

        writer.WriteUInt32((uint)file.UnknownNames.Count);
        foreach (var name in file.UnknownNames) writer.WriteFixedAscii(name, 36);

        writer.WriteUInt32((uint)file.Segments.Count);
        foreach (var segment in file.Segments) WriteSegment(writer, segment, file.Version);

        writer.WriteBytes(file.Trailing);
        return writer.ToArray();
    }

    /// <summary>The bytes of one segment, as the editor's clipboard needs them.</summary>
    public static byte[] WriteSegment(EffSegment segment, uint version)
    {
        var writer = new EffBuffer();
        WriteSegment(writer, segment, version);
        return writer.ToArray();
    }

    private static void WriteSegment(EffBuffer writer, EffSegment segment, uint version)
    {
        writer.WriteFixedJapanese(segment.Name, segment.NameRaw, 16);
        writer.WriteFixedAscii(segment.TextureName, segment.TextureNameRaw, 16);
        writer.WriteFixedAscii(segment.ModelName, segment.ModelNameRaw, 16);

        if (version >= EffGameVersion.PlayStationCs1)
        {
            var field = new byte[16];
            BitConverter.TryWriteBytes(field.AsSpan(4), segment.StructFlags);
            writer.WriteBytes(field);
        }

        // Which of these blocks exist is decided by the version, not by whether
        // the segment happens to carry them: a segment built from scratch has
        // none of them, and dropping one would shift everything that follows.
        writer.WriteUInt32s(segment.Data02);
        if (version >= EffGameVersion.PlayStationCs2) WriteBlock(writer, segment.Data03, 2);
        writer.WriteSingles(segment.Data04);
        if (version < EffGameVersion.PlayStationCs2) WriteBlock(writer, segment.Data05, 3);
        writer.WriteSingles(segment.Data06);
        if (version >= EffGameVersion.ColdSteel3) WriteBlock(writer, segment.Data07, 4);
        writer.WriteSingles(segment.Data08);

        WriteTrack(writer, segment.Position);
        WriteTrack(writer, segment.Rotation);
        WriteTrack(writer, segment.Scale);
        WriteTrack(writer, segment.Rotation2);
        WriteTrack(writer, segment.ColorMultiply);
        WriteTrack(writer, segment.ColorAdd);

        if ((segment.StructFlags & 0x0100_0000) != 0) WriteTrack(writer, segment.Data0F);
        if ((segment.StructFlags & 0x0400_0000) != 0) WriteTrack(writer, segment.Data10);
        if ((segment.StructFlags & 0x0800_0000) != 0) WriteTrack(writer, segment.Data11);
        if ((segment.StructFlags & 0x2000_0000) != 0) WriteTrack(writer, segment.Data12);

        if ((segment.StructFlags & 0x0200_0000) != 0)
        {
            writer.WriteUInt32((uint)segment.Data13.Count);
            foreach (var inner in segment.Data13) WriteTrack(writer, inner);
        }

        WriteTrack(writer, segment.Children);

        if (version <= EffGameVersion.Pc) WriteBlock(writer, segment.Data15, 2);
        if ((segment.StructFlags & 0x002) != 0) WriteBlock(writer, segment.Data16, 16);

        if ((segment.StructFlags & 0x001) != 0)
        {
            if (version >= EffGameVersion.PlayStationCs2)
            {
                writer.WriteUInt32((uint)segment.Data17.Count);
                foreach (var record in segment.Data17) WriteRecord72(writer, record);
            }
            else
            {
                // The block the PC layout leaves unparsed: written back verbatim.
                writer.WriteBytes(segment.Data17PcRaw.Length == 16
                    ? segment.Data17PcRaw
                    : new byte[16]);
            }
        }

        if ((segment.StructFlags & 0x010) != 0) WriteBlock(writer, segment.Data18, 4);
        if ((segment.StructFlags & 0x004) != 0) WriteBlock(writer, segment.Data19, 8);
        if ((segment.StructFlags & 0x008) != 0) WriteBlock(writer, segment.Data1A, 24);
        if (version >= EffGameVersion.PlayStationCs1)
        {
            writer.WriteUInt32((uint)segment.Data1B.Count);
            foreach (var triple in segment.Data1B) writer.WriteUInt32s(triple);
        }
        if ((segment.StructFlags & 0x020) != 0) WriteBlock(writer, segment.Data1C, 6);
        if ((segment.StructFlags & 0x040) != 0) WriteBlock(writer, segment.Data1D, 4);
        if ((segment.StructFlags & 0x080) != 0) WriteBlock(writer, segment.Data1E, 8);
        if ((segment.StructFlags & 0x100) != 0) WriteBlock(writer, segment.Data1F, 2);
        if ((segment.StructFlags & 0x200) != 0) WriteBlock(writer, segment.Data20, 13);
    }

    /// <summary>
    /// A block the layout calls for. A segment that does not carry it — one the
    /// editor built rather than read — gets it as zeroes rather than nothing.
    /// </summary>
    private static void WriteBlock(EffBuffer writer, float[]? values, int count)
        => writer.WriteSingles(values is { } present && present.Length == count
            ? present
            : new float[count]);

    private static void WriteBlock(EffBuffer writer, uint[]? values, int count)
        => writer.WriteUInt32s(values is { } present && present.Length == count
            ? present
            : new uint[count]);

    private static void WriteTrack(EffBuffer writer, List<EffKeyframe> track)
    {
        writer.WriteUInt32((uint)track.Count);
        foreach (var keyframe in track)
        {
            writer.WriteSingles(keyframe.Floats);
            writer.WriteUInt32s(keyframe.Ints);
            writer.WriteSingle(keyframe.Trailing);
        }
    }

    private static void WriteRecord72(EffBuffer writer, EffRecord72 record)
    {
        writer.WriteUInt32s(record.Ints0);
        writer.WriteSingle(record.F0);
        writer.WriteUInt32(record.Int1);
        writer.WriteSingles(record.Floats);
        writer.WriteUInt32s(record.Ints1);
    }
}

/// <summary>A little-endian write head that grows as the file is built.</summary>
internal sealed class EffBuffer
{
    private readonly MemoryStream stream = new();

    public byte[] ToArray() => stream.ToArray();

    public void WriteUInt32(uint value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BitConverter.TryWriteBytes(scratch, value);
        stream.Write(scratch);
    }

    public void WriteSingle(float value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BitConverter.TryWriteBytes(scratch, value);
        stream.Write(scratch);
    }

    public void WriteBytes(byte[] values) => stream.Write(values, 0, values.Length);

    public void WriteSingles(float[] values)
    {
        foreach (var value in values) WriteSingle(value);
    }

    public void WriteUInt32s(uint[] values)
    {
        foreach (var value in values) WriteUInt32(value);
    }

    public void WriteFixedJapanese(string text, byte[] raw, int size)
    {
        if (raw.Length == size && EffText.DecodeJapanese(raw) == text)
        {
            WriteBytes(raw);
            return;
        }
        WriteFixed(EffText.EncodeJapanese(text), size);
    }

    public void WriteFixedAscii(string text, byte[] raw, int size)
    {
        if (raw.Length == size && EffText.DecodeAscii(raw) == text)
        {
            WriteBytes(raw);
            return;
        }
        WriteFixedAscii(text, size);
    }

    public void WriteFixedAscii(string text, int size) => WriteFixed(EffText.EncodeAscii(text), size);

    private void WriteFixed(byte[] bytes, int size)
    {
        var length = Math.Min(bytes.Length, size);
        stream.Write(bytes, 0, length);
        for (var index = length; index < size; index++) stream.WriteByte(0);
    }
}
