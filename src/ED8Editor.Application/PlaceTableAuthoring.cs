using System.Globalization;
using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Application;

public sealed record PlaceTableEntry(
    ushort Id,
    short Kind,
    string Map,
    string DisplayName,
    string Marker = "n");

/// <summary>
/// Adds an authored map to both language variants of t_place.tbl. Existing
/// records are updated by map ID and are never duplicated.
/// </summary>
public sealed class PlaceTableAuthoring
{
    private const string Category = "PlaceTableData";
    private readonly ModProject project;

    public PlaceTableAuthoring(ModProject project)
        => this.project = project ?? throw new ArgumentNullException(nameof(project));

    public IReadOnlyList<string> Upsert(string map, string displayName, short kind)
    {
        if (string.IsNullOrWhiteSpace(map))
            throw new ArgumentException("A map ID is required.", nameof(map));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));

        var paths = new[]
        {
            Path.Combine(project.GameDirectory, "data", "text", "dat", "t_place.tbl"),
            Path.Combine(project.GameDirectory, "data", "text", "dat_us", "t_place.tbl"),
        };
        if (paths.Any(path => !File.Exists(path)))
            throw new FileNotFoundException("The game installation has no complete t_place.tbl pair.");

        var documents = paths.Select(Cs1TableDocument.Read).ToArray();
        var id = ExistingId(documents, map) ?? AllocateId(documents);
        var value = new PlaceTableEntry(id, kind, map.Trim(), displayName.Trim());
        var written = new List<string>(paths.Length);

        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            var document = documents[index];
            var codec = new Cs1TableRecordCodec(
                textEncoding: index == 0 ? JapaneseEncoding() : new UTF8Encoding(false, true));
            var replacement = new Cs1TableEntry(Category, Encode(codec, value));
            var existing = document.Entries
                .Select((entry, ordinal) => (entry, ordinal))
                .FirstOrDefault(candidate => IsMap(codec, candidate.entry, value.Map));
            if (existing.entry is null) document.Entries.Add(replacement);
            else document.Entries[existing.ordinal] = replacement;

            project.CaptureOriginal(path);
            document.Write(path);
            project.TrackSave(path);
            written.Add(path);
        }
        return written;
    }

    private static byte[] Encode(Cs1TableRecordCodec codec, PlaceTableEntry entry)
    {
        var fields = Cs1TableSchemaSet.Default.FindAtomicFields(Category)
            ?? throw new InvalidDataException($"No schema exists for {Category}.");
        var values = new[]
        {
            unchecked((short)entry.Id).ToString(CultureInfo.InvariantCulture),
            entry.Kind.ToString(CultureInfo.InvariantCulture),
            entry.Map,
            entry.DisplayName,
            entry.Marker,
        };
        if (fields.Count != values.Length)
            throw new InvalidDataException($"{Category} has an unexpected field layout.");
        return codec.Encode(
            Category,
            fields.Select((field, index) => new Cs1TableFieldValue(field, values[index])).ToArray());
    }

    private static bool IsMap(Cs1TableRecordCodec codec, Cs1TableEntry entry, string map)
    {
        if (!entry.Category.Equals(Category, StringComparison.Ordinal)) return false;
        var values = codec.Decode(entry);
        return values is not null
            && string.Equals(
                values.FirstOrDefault(value => value.Field.Name == "map")?.Value,
                map,
                StringComparison.OrdinalIgnoreCase);
    }

    private static ushort? ExistingId(
        IReadOnlyList<Cs1TableDocument> documents,
        string map)
    {
        foreach (var document in documents)
        {
            var codec = new Cs1TableRecordCodec(
                textEncoding: ReferenceEquals(document, documents[0])
                    ? JapaneseEncoding()
                    : new UTF8Encoding(false, true));
            foreach (var entry in document.Entries)
            {
                if (!IsMap(codec, entry, map)) continue;
                return unchecked((ushort)short.Parse(
                    codec.Decode(entry)![0].Value,
                    CultureInfo.InvariantCulture));
            }
        }
        return null;
    }

    private static ushort AllocateId(IReadOnlyList<Cs1TableDocument> documents)
    {
        var used = new HashSet<ushort>();
        for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
        {
            var codec = new Cs1TableRecordCodec(
                textEncoding: documentIndex == 0
                    ? JapaneseEncoding()
                    : new UTF8Encoding(false, true));
            foreach (var entry in documents[documentIndex].Entries
                         .Where(entry => entry.Category == Category))
            {
                var values = codec.Decode(entry);
                if (values is null || values.Count == 0) continue;
                used.Add(unchecked((ushort)short.Parse(
                    values[0].Value, CultureInfo.InvariantCulture)));
            }
        }

        var largest = used.Count == 0 ? 0u : used.Max(value => (uint)value);
        for (var candidate = largest + 1; candidate <= ushort.MaxValue; candidate++)
            if (!used.Contains((ushort)candidate)) return (ushort)candidate;
        throw new InvalidOperationException("t_place.tbl has no free 16-bit place ID.");
    }

    private static Encoding JapaneseEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
