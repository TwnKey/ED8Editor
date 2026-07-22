using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ED8Editor.Tables;

public sealed class Cs1TableSchemaSet
{
    private readonly Dictionary<string, Cs1TableSchema> entries;
    private readonly Dictionary<string, Cs1TableSchema> common;
    private readonly Dictionary<string, IReadOnlyList<Cs1TableAtomicField>> flattened = new(StringComparer.Ordinal);

    private Cs1TableSchemaSet(
        Dictionary<string, Cs1TableSchema> entries,
        Dictionary<string, Cs1TableSchema> common)
    {
        this.entries = entries;
        this.common = common;
    }

    public static Cs1TableSchemaSet Default { get; } = LoadDefault();
    public IReadOnlyDictionary<string, Cs1TableSchema> Entries => entries;

    public static Cs1TableSchemaSet Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static Cs1TableSchemaSet Load(Stream stream)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var file = JsonSerializer.Deserialize<Cs1TableSchemaFile>(stream, options)
            ?? throw new InvalidDataException("The TBL schema file is empty.");
        if (file.Version != 1) throw new InvalidDataException($"Unsupported TBL schema version {file.Version}.");
        return new Cs1TableSchemaSet(
            new Dictionary<string, Cs1TableSchema>(file.Entries, StringComparer.Ordinal),
            new Dictionary<string, Cs1TableSchema>(file.Common, StringComparer.Ordinal));
    }

    public Cs1TableSchema? Find(string category) => entries.GetValueOrDefault(category);

    public IReadOnlyList<Cs1TableAtomicField>? FindAtomicFields(string category)
    {
        if (!entries.ContainsKey(category)) return null;
        if (flattened.TryGetValue(category, out var fields)) return fields;
        var result = new List<Cs1TableAtomicField>();
        Flatten(category, string.Empty, result, new HashSet<string>(StringComparer.Ordinal));
        flattened[category] = result;
        return result;
    }

    private void Flatten(
        string schemaName,
        string prefix,
        List<Cs1TableAtomicField> output,
        HashSet<string> stack)
    {
        if (!stack.Add(schemaName)) throw new InvalidDataException($"Recursive TBL schema reference '{schemaName}'.");
        if (!entries.TryGetValue(schemaName, out var schema) && !common.TryGetValue(schemaName, out schema))
            throw new InvalidDataException($"Unknown TBL schema reference '{schemaName}'.");
        foreach (var field in schema.Fields)
        {
            var count = Math.Max(1, field.Count);
            for (var index = 0; index < count; index++)
            {
                var repeatedName = count == 1 ? field.Name : $"{field.Name}[{index + 1}]";
                var name = string.IsNullOrEmpty(prefix) ? repeatedName : $"{prefix} {repeatedName}";
                if (field.Type == "ref")
                {
                    if (string.IsNullOrEmpty(field.Ref))
                        throw new InvalidDataException($"TBL field '{name}' has no referenced schema.");
                    Flatten(field.Ref, name, output, stack);
                }
                else
                {
                    output.Add(new Cs1TableAtomicField(name, field.Type, field.Size));
                }
            }
        }
        stack.Remove(schemaName);
    }

    private static Cs1TableSchemaSet LoadDefault()
    {
        var external = Path.Combine(AppContext.BaseDirectory, "cs1_tbl_schemas.json");
        if (File.Exists(external)) return Load(external);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ED8Editor.Tables.cs1_tbl_schemas.json")
            ?? throw new InvalidOperationException("Embedded CS1 TBL schemas are missing.");
        return Load(stream);
    }

    private sealed class Cs1TableSchemaFile
    {
        public int Version { get; set; }
        public Dictionary<string, Cs1TableSchema> Entries { get; set; } = new();
        public Dictionary<string, Cs1TableSchema> Common { get; set; } = new();
    }
}

public sealed class Cs1TableSchema
{
    public List<Cs1TableSchemaField> Fields { get; set; } = new();
    public string? Key { get; set; }
    public string? Label { get; set; }
}

public sealed class Cs1TableSchemaField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    public int Size { get; set; }
    public string? Ref { get; set; }
}

public sealed record Cs1TableAtomicField(string Name, string Type, int Size);
public sealed record Cs1TableFieldValue(Cs1TableAtomicField Field, string Value);

public sealed class Cs1TableRecordCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Cs1TableSchemaSet schemas;
    public Cs1TableRecordCodec(Cs1TableSchemaSet? schemas = null) => this.schemas = schemas ?? Cs1TableSchemaSet.Default;

    public IReadOnlyList<Cs1TableFieldValue>? Decode(Cs1TableEntry entry)
    {
        var fields = schemas.FindAtomicFields(entry.Category);
        if (fields is null) return null;
        var values = new List<Cs1TableFieldValue>(fields.Count);
        var offset = 0;
        foreach (var field in fields)
            values.Add(new Cs1TableFieldValue(field, ReadValue(entry.Data, ref offset, field)));
        if (offset != entry.Data.Length)
            throw new InvalidDataException($"Schema '{entry.Category}' consumed {offset} of {entry.Data.Length} bytes.");
        return values;
    }

    public byte[] Encode(string category, IReadOnlyList<Cs1TableFieldValue> values)
    {
        var fields = schemas.FindAtomicFields(category)
            ?? throw new InvalidDataException($"No schema exists for '{category}'.");
        if (fields.Count != values.Count) throw new InvalidDataException("The edited row does not match its schema.");
        using var output = new MemoryStream();
        for (var index = 0; index < fields.Count; index++) WriteValue(output, fields[index], values[index].Value);
        return output.ToArray();
    }

    private static string ReadValue(byte[] data, ref int offset, Cs1TableAtomicField field)
    {
        return field.Type switch
        {
            "i8" => unchecked((sbyte)data[Require(data, ref offset, 1, field.Name)]).ToString(CultureInfo.InvariantCulture),
            "u8" => data[Require(data, ref offset, 1, field.Name)].ToString(CultureInfo.InvariantCulture),
            "i16" => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(Require(data, ref offset, 2, field.Name), 2)).ToString(CultureInfo.InvariantCulture),
            "u16" => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Require(data, ref offset, 2, field.Name), 2)).ToString(CultureInfo.InvariantCulture),
            "i32" => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Require(data, ref offset, 4, field.Name), 4)).ToString(CultureInfo.InvariantCulture),
            "u32" => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Require(data, ref offset, 4, field.Name), 4)).ToString(CultureInfo.InvariantCulture),
            "f32" => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Require(data, ref offset, 4, field.Name), 4)))
                .ToString("R", CultureInfo.InvariantCulture),
            "cutf8" => ReadString(data, ref offset, field.Name),
            "bytes" => Convert.ToHexString(data.AsSpan(Require(data, ref offset, field.Size, field.Name), field.Size)),
            _ => throw new InvalidDataException($"Unsupported TBL field type '{field.Type}'."),
        };
    }

    private static int Require(byte[] data, ref int offset, int count, string fieldName)
    {
        if (count < 0 || offset + count > data.Length)
            throw new EndOfStreamException($"Field '{fieldName}' exceeds its TBL entry.");
        var result = offset;
        offset += count;
        return result;
    }

    private static string ReadString(byte[] data, ref int offset, string name)
    {
        var end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) throw new InvalidDataException($"Field '{name}' has no NUL terminator.");
        var value = Utf8.GetString(data, offset, end - offset);
        offset = end + 1;
        return value;
    }

    private static void WriteValue(Stream output, Cs1TableAtomicField field, string text)
    {
        Span<byte> bytes = stackalloc byte[4];
        switch (field.Type)
        {
            case "i8": output.WriteByte(unchecked((byte)sbyte.Parse(text, CultureInfo.InvariantCulture))); break;
            case "u8": output.WriteByte(byte.Parse(text, CultureInfo.InvariantCulture)); break;
            case "i16": BinaryPrimitives.WriteInt16LittleEndian(bytes, short.Parse(text, CultureInfo.InvariantCulture)); output.Write(bytes[..2]); break;
            case "u16": BinaryPrimitives.WriteUInt16LittleEndian(bytes, ushort.Parse(text, CultureInfo.InvariantCulture)); output.Write(bytes[..2]); break;
            case "i32": BinaryPrimitives.WriteInt32LittleEndian(bytes, int.Parse(text, CultureInfo.InvariantCulture)); output.Write(bytes); break;
            case "u32": BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.Parse(text, CultureInfo.InvariantCulture)); output.Write(bytes); break;
            case "f32": BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(float.Parse(text, CultureInfo.InvariantCulture))); output.Write(bytes); break;
            case "cutf8":
                if (text.IndexOf('\0') >= 0) throw new FormatException($"Field '{field.Name}' cannot contain NUL.");
                output.Write(Utf8.GetBytes(text)); output.WriteByte(0); break;
            case "bytes":
                var raw = Convert.FromHexString(new string(text.Where(value => !char.IsWhiteSpace(value)).ToArray()));
                if (raw.Length != field.Size) throw new FormatException($"Field '{field.Name}' requires exactly {field.Size} bytes.");
                output.Write(raw); break;
            default: throw new InvalidDataException($"Unsupported TBL field type '{field.Type}'.");
        }
    }
}
