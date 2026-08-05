using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using ED8Editor.Tables;

namespace ED8Editor.Application;

/// <summary>
/// Names a newly made character or enemy in the table the game reads it from.
///
/// Copying an asset's packages makes something the game can load; it does not
/// make something the game knows about. That takes a row: <c>t_mons</c> for an
/// enemy, <c>t_name</c> for a character. Until there is one, the new asset is a
/// file nothing asks for.
///
/// The row is cloned from the source's own rather than built from nothing. Every
/// field a row carries has a meaning, most of them unrelated to identity — an
/// enemy's stats, its drops, its resistances — and inventing values for those
/// would be inventing a different creature. Cloning changes only what says which
/// asset it is, which is exactly what a new asset needs to differ by.
/// </summary>
public static class CharacterTableRegistration
{
    /// <summary>What a table calls each of the things that identify an asset.</summary>
    /// <param name="Category">The record category the table stores rows under.</param>
    /// <param name="Identity">
    /// Fields naming the asset, which take the new id. For <c>t_mons</c> the
    /// package and the model inside it are separate fields, and both move.
    /// </param>
    private sealed record Shape(
        string FileName,
        string Category,
        IReadOnlyList<string> Identity,
        string? NameField,
        string? KeyField);

    private static readonly Shape Enemy = new(
        "t_mons.tbl", "status", new[] { "texture", "model" }, "name", null);

    private static readonly Shape Character = new(
        "t_name.tbl", "NameTableData",
        new[] { "unknown_string_1", "unknown_string_2" }, "name", "character");

    /// <summary>What was written, so a caller can say it plainly.</summary>
    public sealed record Result(string TablePath, int RowIndex, string? Key);

    /// <summary>
    /// Adds a row for <paramref name="newAssetId"/>, cloned from the row that
    /// names <paramref name="sourceAssetId"/>.
    ///
    /// <paramref name="modelName"/> is the name the model answers to inside its
    /// package, which <c>t_mons</c> keeps apart from the package's own name;
    /// null leaves whatever the source had.
    /// </summary>
    public static Result Add(
        Action<string, bool> onSaving,
        string gameDataPath,
        bool enemy,
        string sourceAssetId,
        string newAssetId,
        string? displayName = null,
        string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(onSaving);
        ArgumentNullException.ThrowIfNull(gameDataPath);
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(newAssetId);

        var shape = enemy ? Enemy : Character;
        var path = Locate(gameDataPath, shape.FileName)
            ?? throw new FileNotFoundException(
                $"'{shape.FileName}' was not found under '{gameDataPath}'.", shape.FileName);

        var document = Cs1TableDocument.Read(path);
        var codec = new Cs1TableRecordCodec(textEncoding: EncodingFor(path));

        // The row that names the source, and the fields it decodes to.
        Cs1TableEntry? source = null;
        IReadOnlyList<Cs1TableFieldValue>? sourceFields = null;
        foreach (var entry in document.Entries)
        {
            if (!entry.Category.Equals(shape.Category, StringComparison.Ordinal)) continue;
            var fields = codec.Decode(entry);
            if (fields is null) continue;
            var named = fields.Any(field =>
                shape.Identity.Contains(field.Field.Name, StringComparer.Ordinal)
                && field.Value.Equals(sourceAssetId, StringComparison.OrdinalIgnoreCase));
            if (!named) continue;
            source = entry;
            sourceFields = fields;
            break;
        }
        if (source is null || sourceFields is null)
        {
            throw new InvalidOperationException(
                $"No {shape.FileName} row names '{sourceAssetId}', so there is nothing to"
                + " clone its stats and bindings from.");
        }

        // A key no row uses yet, where the table has one.
        string? key = null;
        if (shape.KeyField is not null)
        {
            key = NextKey(document, codec, shape).ToString(CultureInfo.InvariantCulture);
        }

        var edited = sourceFields.Select(field =>
        {
            if (shape.Identity.Contains(field.Field.Name, StringComparer.Ordinal))
            {
                // t_mons names the package and the model inside it separately.
                var value = field.Value.Equals(sourceAssetId, StringComparison.OrdinalIgnoreCase)
                    ? newAssetId
                    : modelName ?? field.Value;
                return new Cs1TableFieldValue(field.Field, value);
            }
            if (shape.KeyField is not null && key is not null
                && field.Field.Name.Equals(shape.KeyField, StringComparison.Ordinal))
            {
                return new Cs1TableFieldValue(field.Field, key);
            }
            if (displayName is not null && shape.NameField is not null
                && field.Field.Name.Equals(shape.NameField, StringComparison.Ordinal))
            {
                return new Cs1TableFieldValue(field.Field, displayName);
            }
            return field;
        }).ToArray();

        var added = new Cs1TableEntry(shape.Category, codec.Encode(shape.Category, edited));
        document.Entries.Add(added);

        onSaving(path, true);
        document.Write(path);
        onSaving(path, false);
        return new Result(path, document.Entries.Count - 1, key);
    }

    /// <summary>
    /// Whether a table already names an asset. Reading a row back is the only way
    /// to know one was written as the thing it claims to be, rather than merely
    /// appended as bytes.
    /// </summary>
    public static bool Lists(string gameDataPath, bool enemy, string assetId)
    {
        ArgumentNullException.ThrowIfNull(gameDataPath);
        ArgumentNullException.ThrowIfNull(assetId);
        var shape = enemy ? Enemy : Character;
        var path = Locate(gameDataPath, shape.FileName);
        if (path is null) return false;
        var codec = new Cs1TableRecordCodec(textEncoding: EncodingFor(path));
        foreach (var entry in Cs1TableDocument.Read(path).Entries)
        {
            if (!entry.Category.Equals(shape.Category, StringComparison.Ordinal)) continue;
            var fields = codec.Decode(entry);
            if (fields is null) continue;
            if (fields.Any(field =>
                    shape.Identity.Contains(field.Field.Name, StringComparer.Ordinal)
                    && field.Value.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The first key the table does not already use.</summary>
    private static int NextKey(
        Cs1TableDocument document, Cs1TableRecordCodec codec, Shape shape)
    {
        var taken = new HashSet<int>();
        foreach (var entry in document.Entries)
        {
            if (!entry.Category.Equals(shape.Category, StringComparison.Ordinal)) continue;
            var fields = codec.Decode(entry);
            var value = fields?.FirstOrDefault(field =>
                field.Field.Name.Equals(shape.KeyField, StringComparison.Ordinal))?.Value;
            if (value is not null
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key))
            {
                taken.Add(key);
            }
        }
        var next = 900;
        while (taken.Contains(next)) next++;
        return next;
    }

    private static string? Locate(string gameDataPath, string fileName)
        => new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, fileName))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// The English tables are UTF-8 and the Japanese ones are Shift-JIS; reading
    /// one as the other turns a name into mojibake the moment it is written back.
    /// Kept identical to how the enemy catalogue already decides, so a row this
    /// adds and a row that edits one cannot disagree about the same file.
    /// </summary>
    private static Encoding EncodingFor(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                "dat",
                StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false, true);
    }
}
