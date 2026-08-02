using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Tables;

/// <summary>What a character carries, and where on them it sits.</summary>
/// <param name="AttachPoint">
/// The name of a point the character's model offers. A model states these as
/// PLocator objects — ply000 has sixteen, <c>R_arm_point</c>, <c>head_point</c>,
/// <c>Left_SB_point</c> and the rest — and they are animated like bones, so what
/// hangs on one follows the animation rather than sitting still.
/// </param>
/// <param name="Slot">
/// What kind of thing this is. Read off the table: 0 hangs a weapon on an arm
/// point, 5 is an outfit (the model is the character's own), 6 is eyewear on
/// <c>megane_point</c>.
/// </param>
/// <param name="ItemId">
/// Which item this stands for — 1000 for the weapon, 800, 801, 802 for the
/// outfits, 9999 on the default rows. This is a reference into the item tables,
/// so an editor should offer it as a list to pick from rather than a number to
/// type.
/// </param>
/// <param name="Field3">
/// Three more words. 263 appears on one outfit and zero on its neighbours, and
/// the last two hold small counters — 0, 1, 2. Named by position because
/// nothing here has established what they mean, and hiding them would leave a
/// field the user cannot reach.
/// </param>
public sealed record Cs1Attachment(
    int Index,
    int Character,
    int Slot,
    int ItemId,
    int Field3,
    int Field4,
    int Field5,
    string Model,
    string AttachPoint);

/// <summary>
/// Typed view of CS1 AttachTableData records: which equipment model a character
/// carries, and on which point of their model it hangs.
///
/// The schema is <c>character</c> (u16), twenty bytes nothing here claims to
/// understand, then two zero-terminated strings — the model and the point. Those
/// twenty bytes are carried across untouched on every edit, as the other tables
/// in this project do: a record is changed by cloning a real one and replacing
/// only what is named, never by building one from scratch.
/// </summary>
public sealed class Cs1AttachTable
{
    private const string Category = "AttachTableData";
    private const int UnknownSize = 20;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly Cs1TableDocument document;

    private Cs1AttachTable(string path, Cs1TableDocument document)
    {
        Path = path;
        this.document = document;
    }

    public string Path { get; }

    public static Cs1AttachTable Read(string path) => new(path, Cs1TableDocument.Read(path));

    public IReadOnlyList<Cs1Attachment> Attachments => document.Entries
        .Select((entry, index) => (Entry: entry, Index: index))
        .Where(value => value.Entry.Category.Equals(Category, StringComparison.Ordinal))
        .Select(value => Decode(value.Entry.Data, value.Index))
        .ToArray();

    /// <summary>
    /// Hangs <paramref name="model"/> on <paramref name="attachPoint"/> for
    /// <paramref name="character"/>.
    ///
    /// An existing record for that character and point is rewritten; otherwise a
    /// new one is cloned from <paramref name="templateIndex"/> so the twenty
    /// bytes this project has not named come from a record the game itself
    /// wrote. Without a template there is nothing honest to put there, so one is
    /// required rather than invented.
    /// </summary>
    /// <summary>
    /// Writes an attachment. Every field is given; an existing record for the
    /// same character, slot and item is replaced, otherwise one is appended.
    /// </summary>
    public int Set(Cs1Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.Character is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(attachment));
        }
        Check(attachment.Model, nameof(attachment));
        Check(attachment.AttachPoint, nameof(attachment));

        var data = Encode(attachment);
        var existing = Attachments.FirstOrDefault(value =>
            value.Character == attachment.Character
            && value.Slot == attachment.Slot
            && value.ItemId == attachment.ItemId);
        if (existing is not null)
        {
            document.Entries[existing.Index].Data = data;
            return existing.Index;
        }
        document.Entries.Add(new Cs1TableEntry(Category, data));
        return document.Entries.Count - 1;
    }

    /// <summary>Removes an attachment, leaving every other record untouched.</summary>
    public void Remove(int index)
    {
        if (index < 0 || index >= document.Entries.Count
            || !document.Entries[index].Category.Equals(Category, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        document.Entries.RemoveAt(index);
    }

    public void Write() => document.Write(Path);

    private static Cs1Attachment Decode(byte[] data, int index)
    {
        var character = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var words = new int[5];
        for (var word = 0; word < words.Length; word++)
        {
            words[word] = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(sizeof(ushort) + word * sizeof(uint)));
        }
        var at = sizeof(ushort) + UnknownSize;
        var model = ReadString(data, ref at);
        var point = ReadString(data, ref at);
        return new Cs1Attachment(
            index, character, words[0], words[1], words[2], words[3], words[4], model, point);
    }

    private static byte[] Encode(Cs1Attachment attachment)
    {
        var first = Utf8.GetBytes(attachment.Model);
        var second = Utf8.GetBytes(attachment.AttachPoint);
        var data = new byte[sizeof(ushort) + UnknownSize + first.Length + 1 + second.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)attachment.Character);
        var words = new[]
        {
            attachment.Slot, attachment.ItemId,
            attachment.Field3, attachment.Field4, attachment.Field5,
        };
        for (var word = 0; word < words.Length; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(sizeof(ushort) + word * sizeof(uint)), (uint)words[word]);
        }
        var at = sizeof(ushort) + UnknownSize;
        first.CopyTo(data.AsSpan(at));
        at += first.Length + 1;
        second.CopyTo(data.AsSpan(at));
        return data;
    }

    private static string ReadString(byte[] data, ref int at)
    {
        if (at >= data.Length) return string.Empty;
        var zero = Array.IndexOf(data, (byte)0, at);
        if (zero < 0) zero = data.Length;
        var value = Utf8.GetString(data, at, zero - at);
        at = zero + 1;
        return value;
    }

    private static void Check(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A name is required.", name);
        }
        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A name cannot contain NUL.", name);
        }
    }
}
