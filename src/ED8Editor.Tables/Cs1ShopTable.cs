using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Tables;

/// <summary>
/// Typed, lossless view of the three understood fields in CS1 ShopItem records
/// and the understood ID/name fields in ShopTitle records. All undocumented
/// title bytes and the third ShopItem word are preserved explicitly.
/// </summary>
public sealed class Cs1ShopTable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Cs1TableDocument document;

    private Cs1ShopTable(string path, Cs1TableDocument document)
    {
        Path = path;
        this.document = document;
    }

    public string Path { get; }

    public static Cs1ShopTable Read(string path) =>
        new(path, Cs1TableDocument.Read(path));

    public IReadOnlyList<Cs1ShopTitle> Titles => document.Entries
        .Where(value => value.Category.Equals("ShopTitle", StringComparison.Ordinal))
        .Select(DecodeTitle)
        .OrderBy(value => value.Id)
        .ToArray();

    public IReadOnlyList<Cs1ShopItem> Items(int shopId) => document.Entries
        .Select((entry, index) => new { Entry = entry, Index = index })
        .Where(value => value.Entry.Category.Equals("ShopItem", StringComparison.Ordinal))
        .Select(value => DecodeItem(value.Entry, value.Index))
        .Where(value => value.ShopId == shopId)
        .ToArray();

    public void SetTitleName(int shopId, string name)
    {
        if (name.IndexOf('\0') >= 0)
            throw new ArgumentException("A shop title cannot contain NUL.", nameof(name));
        var title = document.Entries
            .Where(value => value.Category.Equals("ShopTitle", StringComparison.Ordinal))
            .FirstOrDefault(value => ReadInt16(value.Data, 0, "ShopTitle ID") == shopId)
            ?? throw new InvalidDataException($"ShopTitle {shopId} does not exist.");
        var terminator = Array.IndexOf(title.Data, (byte)0, 3);
        if (terminator < 0 || title.Data.Length - terminator - 1 != 8)
            throw new InvalidDataException(
                $"ShopTitle {shopId} does not have the established 8-byte suffix.");
        var encoded = Utf8.GetBytes(name);
        var replacement = new byte[3 + encoded.Length + 1 + 8];
        title.Data.AsSpan(0, 3).CopyTo(replacement);
        encoded.CopyTo(replacement.AsSpan(3));
        title.Data.AsSpan(terminator + 1, 8)
            .CopyTo(replacement.AsSpan(4 + encoded.Length));
        title.Data = replacement;
    }

    public void ReplaceItems(int shopId, IReadOnlyList<Cs1ShopItemValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (shopId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shopId));

        var existing = document.Entries
            .Select((entry, index) => new { Entry = entry, Index = index })
            .Where(value => value.Entry.Category.Equals("ShopItem", StringComparison.Ordinal)
                && DecodeItem(value.Entry, value.Index).ShopId == shopId)
            .Select(value => value.Index)
            .ToArray();
        var insertionIndex = existing.Length > 0
            ? existing[0]
            : document.Entries
                .Select((entry, index) => new { Entry = entry, Index = index })
                .Where(value => value.Entry.Category.Equals("ShopItem", StringComparison.Ordinal))
                .Select(value => value.Index + 1)
                .LastOrDefault();
        for (var index = existing.Length - 1; index >= 0; index--)
            document.Entries.RemoveAt(existing[index]);
        foreach (var item in items)
        {
            var payload = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, checked((ushort)shopId));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), item.ItemId);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), item.UnknownValue);
            document.Entries.Insert(insertionIndex++, new Cs1TableEntry("ShopItem", payload));
        }
    }

    public void Write() => document.Write(Path);

    private static Cs1ShopTitle DecodeTitle(Cs1TableEntry entry)
    {
        if (entry.Data.Length < 12)
            throw new InvalidDataException("A ShopTitle record is shorter than its fixed fields.");
        var terminator = Array.IndexOf(entry.Data, (byte)0, 3);
        if (terminator < 0)
            throw new InvalidDataException("A ShopTitle name has no NUL terminator.");
        if (entry.Data.Length - terminator - 1 != 8)
            throw new InvalidDataException("A ShopTitle record does not have its 8-byte suffix.");
        return new Cs1ShopTitle(
            ReadInt16(entry.Data, 0, "ShopTitle ID"),
            entry.Data[2],
            Utf8.GetString(entry.Data, 3, terminator - 3),
            entry.Data.AsSpan(terminator + 1, 8).ToArray());
    }

    private static Cs1ShopItem DecodeItem(Cs1TableEntry entry, int documentIndex)
    {
        if (entry.Data.Length != 6)
            throw new InvalidDataException(
                $"ShopItem entry #{documentIndex} is {entry.Data.Length} bytes instead of 6.");
        return new Cs1ShopItem(
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Data),
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Data.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Data.AsSpan(4)),
            documentIndex);
    }

    private static short ReadInt16(byte[] data, int offset, string field)
    {
        if (offset < 0 || offset + 2 > data.Length)
            throw new InvalidDataException($"{field} exceeds its record.");
        return BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
    }
}

public sealed record Cs1ShopTitle(
    int Id,
    byte UnknownByte,
    string Name,
    byte[] UnknownSuffix)
{
    public string Label => $"{Id} — {Name}";
}

public sealed record Cs1ShopItem(
    ushort ShopId,
    ushort ItemId,
    ushort UnknownValue,
    int DocumentIndex);

public sealed record Cs1ShopItemValue(ushort ItemId, ushort UnknownValue);
