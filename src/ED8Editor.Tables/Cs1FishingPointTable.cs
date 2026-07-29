using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Tables;

/// <summary>
/// Lossless authoring view of t_fish.tbl fish_pnt records. Only the established
/// leading signed 16-bit ID is interpreted. Every other field is preserved by
/// cloning a user-selected record until its semantics are documented.
/// </summary>
public sealed class Cs1FishingPointTable
{
    public const int RecordSize = 36;
    private readonly Cs1TableDocument document;
    private readonly IReadOnlyDictionary<int, string> fishNames;

    private Cs1FishingPointTable(
        string path,
        Cs1TableDocument document,
        IReadOnlyDictionary<int, string> fishNames)
    {
        Path = path;
        this.document = document;
        this.fishNames = fishNames;
    }

    public string Path { get; }

    /// <summary>
    /// Every fish declared by t_notefish.tbl, including species that are not
    /// currently referenced by an existing fish_pnt record.
    /// </summary>
    public IReadOnlyList<Cs1FishChoice> Fish => fishNames
        .OrderBy(value => value.Key)
        .Select(value => new Cs1FishChoice(value.Key, value.Value))
        .ToArray();

    public static Cs1FishingPointTable Read(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var namesPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(fullPath)!,
            "t_notefish.tbl");
        return new Cs1FishingPointTable(
            fullPath,
            Cs1TableDocument.Read(fullPath),
            File.Exists(namesPath)
                ? ReadFishNames(namesPath)
                : new Dictionary<int, string>());
    }

    public IReadOnlyList<Cs1FishingPoint> Points => document.Entries
        .Select((entry, index) => new { Entry = entry, Index = index })
        .Where(value => value.Entry.Category.Equals(
            "fish_pnt", StringComparison.Ordinal))
        .Select(value =>
        {
            ValidateRecord(value.Entry, value.Index);
            var fishIds = Enumerable.Range(5, 13)
                .Select(field => BinaryPrimitives.ReadInt16LittleEndian(
                    value.Entry.Data.AsSpan(field * sizeof(short), sizeof(short))))
                .Where(id => id >= 0)
                .Select(id => (int)id)
                .ToArray();
            return new Cs1FishingPoint(
                BinaryPrimitives.ReadInt16LittleEndian(value.Entry.Data),
                value.Index,
                fishIds,
                fishIds.Select(id => fishNames.TryGetValue(id, out var name)
                    ? name
                    : $"Fish #{id}").ToArray());
        })
        .OrderBy(value => value.Id)
        .ToArray();

    public void AddPoint(
        int id,
        int availabilitySourcePointId,
        IReadOnlyList<int>? availableFishIds = null)
    {
        if (id is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (Points.Any(value => value.Id == id))
            throw new InvalidOperationException($"fish_pnt {id} already exists.");

        var template = document.Entries
            .Select((entry, index) => new { Entry = entry, Index = index })
            .FirstOrDefault(value =>
                value.Entry.Category.Equals("fish_pnt", StringComparison.Ordinal)
                && ReadId(value.Entry, value.Index) == availabilitySourcePointId)
            ?? throw new InvalidDataException(
                $"Fishing availability source {availabilitySourcePointId} does not exist.");
        var payload = template.Entry.Data.ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(payload, checked((short)id));
        if (availableFishIds is not null)
        {
            if (availableFishIds.Count > 13)
                throw new ArgumentException(
                    "A CS1 fishing spot can contain at most 13 fish species.",
                    nameof(availableFishIds));
            for (var field = 5; field < 18; field++)
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan(field * sizeof(short), sizeof(short)),
                    -1);
            for (var index = 0; index < availableFishIds.Count; index++)
            {
                var fishId = availableFishIds[index];
                if (fishId is < 0 or > short.MaxValue)
                    throw new ArgumentOutOfRangeException(
                        nameof(availableFishIds),
                        $"Fish ID {fishId} cannot be stored in fish_pnt.");
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan((index + 5) * sizeof(short), sizeof(short)),
                    checked((short)fishId));
            }
        }

        var insertionIndex = document.Entries
            .Select((entry, index) => new { Entry = entry, Index = index })
            .Where(value => value.Entry.Category.Equals(
                "fish_pnt", StringComparison.Ordinal))
            .Select(value => value.Index + 1)
            .DefaultIfEmpty(template.Index + 1)
            .Max();
        document.Entries.Insert(
            insertionIndex,
            new Cs1TableEntry("fish_pnt", payload));
    }

    public void Write() => document.Write(Path);

    private static IReadOnlyDictionary<int, string> ReadFishNames(string path)
    {
        var result = new Dictionary<int, string>();
        foreach (var entry in Cs1TableDocument.Read(path).Entries.Where(value =>
                     value.Category.Equals("QSFish", StringComparison.Ordinal)))
        {
            if (entry.Data.Length < 3) continue;
            var id = BinaryPrimitives.ReadInt16LittleEndian(entry.Data);
            if (id < 0) continue;
            var terminator = Array.IndexOf(entry.Data, (byte)0, sizeof(short));
            if (terminator <= sizeof(short)) continue;
            var name = Encoding.UTF8.GetString(
                entry.Data,
                sizeof(short),
                terminator - sizeof(short));
            if (name.Length != 0) result.TryAdd(id, name);
        }
        return result;
    }

    private static int ReadId(Cs1TableEntry entry, int documentIndex)
    {
        ValidateRecord(entry, documentIndex);
        return BinaryPrimitives.ReadInt16LittleEndian(entry.Data);
    }

    private static void ValidateRecord(Cs1TableEntry entry, int documentIndex)
    {
        if (entry.Data.Length != RecordSize)
        {
            throw new InvalidDataException(
                $"fish_pnt entry #{documentIndex} is {entry.Data.Length} bytes"
                + $" instead of the schema's {RecordSize} bytes.");
        }
    }
}

public sealed record Cs1FishingPoint(
    int Id,
    int DocumentIndex,
    IReadOnlyList<int> FishIds,
    IReadOnlyList<string> FishNames)
{
    public string Label => FishNames.Count == 0
        ? $"Spot {Id} — no fish configured"
        : $"Spot {Id} — {string.Join(", ", FishNames)}";
}

public sealed record Cs1FishChoice(int Id, string Name)
{
    public string Label => $"{Name} (ID {Id})";
    public override string ToString() => Label;
}
