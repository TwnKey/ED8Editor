using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Tables;

public sealed class Cs1TableDocument
{
    private readonly List<Cs1TableEntry> entries;

    internal Cs1TableDocument(string? sourcePath, IEnumerable<Cs1TableEntry> entries)
    {
        SourcePath = sourcePath;
        this.entries = entries.ToList();
    }

    public string? SourcePath { get; internal set; }
    public IList<Cs1TableEntry> Entries => entries;

    public static Cs1TableDocument Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    public static Cs1TableDocument Read(Stream stream, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var count = ReadUInt16(stream);
        var entries = new List<Cs1TableEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var category = ReadNullTerminatedUtf8(stream);
            var declaredLength = ReadUInt16(stream);
            var payloadLength = category switch
            {
                "item" => MeasureItemPayload(stream),
                "magic" => MeasureMagicPayload(stream),
                "QSText" => MeasureQuestTextPayload(stream),
                _ => declaredLength,
            };
            var data = ReadExactly(stream, payloadLength);
            entries.Add(new Cs1TableEntry(category, data, declaredLength, data.ToArray()));
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException($"The TBL contains {stream.Length - stream.Position} trailing byte(s).");
        return new Cs1TableDocument(sourcePath, entries);
    }

    public void Write(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        using var stream = File.Create(path);
        Write(stream);
        SourcePath = path;
    }

    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (entries.Count > ushort.MaxValue) throw new InvalidDataException("A CS1 TBL cannot contain more than 65535 entries.");
        WriteUInt16(stream, (ushort)entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Data.Length > ushort.MaxValue)
                throw new InvalidDataException($"Entry '{entry.Category}' is larger than 65535 bytes.");
            WriteNullTerminatedUtf8(stream, entry.Category);
            WriteUInt16(stream, entry.SerializedLength);
            stream.Write(entry.Data);
        }
    }

    private static int MeasureItemPayload(Stream stream) => MeasureVariablePayload(stream, reader =>
    {
        reader.Skip(4);
        reader.ReadString();
        reader.Skip(46);
        reader.ReadString();
        reader.ReadString();
    });

    private static int MeasureMagicPayload(Stream stream) => MeasureVariablePayload(stream, reader =>
    {
        reader.Skip(4);
        reader.ReadString();
        reader.Skip(24);
        reader.ReadString();
        reader.ReadString();
        reader.ReadString();
    });

    // XSeed's localized t_main.tbl contains stale serialized lengths for some QSText
    // records. The CS1 record schema is u16 + u8 + NUL-terminated UTF-8 + u8.
    private static int MeasureQuestTextPayload(Stream stream) => MeasureVariablePayload(stream, reader =>
    {
        reader.Skip(3);
        reader.ReadString();
        reader.Skip(1);
    });

    private static int MeasureVariablePayload(Stream stream, Action<PayloadProbe> measure)
    {
        var start = stream.Position;
        var probe = new PayloadProbe(stream);
        measure(probe);
        var length = checked((int)(stream.Position - start));
        stream.Position = start;
        return length;
    }

    private sealed class PayloadProbe
    {
        private readonly Stream stream;
        public PayloadProbe(Stream stream) => this.stream = stream;
        public void Skip(int count)
        {
            if (count < 0 || stream.Position + count > stream.Length) throw new EndOfStreamException();
            stream.Position += count;
        }
        public void ReadString() => ReadNullTerminatedUtf8(stream);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string ReadNullTerminatedUtf8(Stream stream)
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            if (value == 0) break;
            bytes.WriteByte((byte)value);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    private static void WriteNullTerminatedUtf8(Stream stream, string value)
    {
        if (value.IndexOf('\0') >= 0) throw new InvalidDataException("TBL category names cannot contain NUL.");
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

public sealed class Cs1TableEntry
{
    private readonly ushort? originalDeclaredLength;
    private readonly byte[]? originalData;

    public Cs1TableEntry(string category, byte[] data)
        : this(category, data, null, null)
    {
    }

    internal Cs1TableEntry(string category, byte[] data, ushort? originalDeclaredLength, byte[]? originalData)
    {
        Category = string.IsNullOrEmpty(category) ? throw new ArgumentException("A category is required.", nameof(category)) : category;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        this.originalDeclaredLength = originalDeclaredLength;
        this.originalData = originalData;
    }

    public string Category { get; set; }
    public byte[] Data { get; set; }

    internal ushort SerializedLength => originalDeclaredLength is { } declared
        && originalData is not null
        && originalData.AsSpan().SequenceEqual(Data)
            ? declared
            : checked((ushort)Data.Length);
}

/// <summary>Explicit construction API used by importers without exposing parser internals.</summary>
public sealed class Cs1TableDocumentBuilder
{
    private readonly List<Cs1TableEntry> entries = new();
    public Cs1TableDocumentBuilder WithEntry(string category, byte[] data)
    {
        entries.Add(new Cs1TableEntry(category, data));
        return this;
    }
    public Cs1TableDocument Build() => new(null, entries);
}
