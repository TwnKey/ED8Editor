namespace ED8Editor.Tables;

public sealed record Cs1TableReference(string TableName, string Category)
{
    public static bool TryParse(string? semanticArgument, out Cs1TableReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(semanticArgument)) return false;
        var separator = semanticArgument.LastIndexOf(':');
        if (separator <= 0 || separator == semanticArgument.Length - 1) return false;
        var table = semanticArgument[..separator].Trim();
        var category = semanticArgument[(separator + 1)..].Trim();
        if (!table.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase)) table += ".tbl";
        reference = new Cs1TableReference(table, category);
        return true;
    }
}

public sealed record Cs1TableChoice(int Value, string Label, Cs1TableEntry Entry);

/// <summary>
/// Loads CS1 tables on demand. How a category exposes its numeric key and display label is
/// registered explicitly; undocumented categories remain available by their stable ordinal.
/// </summary>
public sealed class Cs1TableCatalog
{
    private readonly string directory;
    private readonly Dictionary<string, Cs1TableDocument> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Cs1TableSchemaSet schemas;
    private readonly Cs1TableRecordCodec codec;

    public Cs1TableCatalog(string directory, Cs1TableSchemaSet? schemas = null)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.schemas = schemas ?? Cs1TableSchemaSet.Default;
        codec = new Cs1TableRecordCodec(this.schemas);
    }

    public string DirectoryPath => directory;

    public IReadOnlyList<Cs1TableChoice> GetChoices(Cs1TableReference reference)
    {
        var path = Path.Combine(directory, reference.TableName);
        if (!cache.TryGetValue(path, out var document))
        {
            if (!File.Exists(path)) return Array.Empty<Cs1TableChoice>();
            document = Cs1TableDocument.Read(path);
            cache[path] = document;
        }

        var entries = document.Entries.Where(entry =>
            entry.Category.Equals(reference.Category, StringComparison.Ordinal)).ToArray();
        var schema = schemas.Find(reference.Category);
        return entries.Select((entry, ordinal) =>
        {
            var decoded = codec.Decode(entry);
            var byName = decoded?.ToDictionary(value => value.Field.Name, value => value.Value, StringComparer.Ordinal);
            var value = schema?.Key is { } key && byName is not null && byName.TryGetValue(key, out var keyText)
                ? int.Parse(keyText, System.Globalization.CultureInfo.InvariantCulture)
                : ordinal;
            var description = schema?.Label is { } labelField && byName is not null && byName.TryGetValue(labelField, out var labelText)
                ? labelText
                : null;
            var displayLabel = string.IsNullOrEmpty(description)
                ? $"{value} — {reference.Category}"
                : $"{value} — {description}";
            return new Cs1TableChoice(value, displayLabel, entry);
        }).ToArray();
    }

    public void Invalidate(string? path = null)
    {
        if (path is null) cache.Clear();
        else cache.Remove(path);
    }
}
