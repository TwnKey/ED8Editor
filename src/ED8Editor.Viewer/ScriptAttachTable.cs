using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// A model a character carries and the skeleton node it hangs from — a weapon on
/// a hand point, a shield on a back point.
/// </summary>
internal sealed record ScriptAttachment(int CharacterId, string ModelAssetId, string AttachPoint);

/// <summary>
/// Equipment declared by <c>t_attach.tbl</c>. A record is the character it
/// belongs to, then the model asset and the name of the node it attaches to:
/// character 0 carries <c>C_EQU300_R</c> on <c>R_arm_point</c>. Records naming
/// the model "null" only reserve an attach point and carry nothing.
///
/// Scripts move the same equipment around at runtime (OP37 attaches a model to a
/// node, OP32_0 shows or hides what hangs there), so this table is the default
/// loadout rather than the last word.
/// </summary>
internal sealed class ScriptAttachTable
{
    private const string Category = "AttachTableData";
    private const string EmptyModel = "null";

    private readonly List<ScriptAttachment> attachments = new();

    private ScriptAttachTable()
    {
    }

    public static ScriptAttachTable Empty { get; } = new();

    public int Count => attachments.Count;

    public static ScriptAttachTable Load(string? gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) return Empty;
        var path = new[] { "dat_us", "dat" }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, "t_attach.tbl"))
            .FirstOrDefault(File.Exists);
        if (path is null) return Empty;
        var table = new ScriptAttachTable();
        try
        {
            foreach (var entry in Cs1TableDocument.Read(path).Entries)
            {
                if (!entry.Category.Equals(Category, StringComparison.Ordinal)) continue;
                if (entry.Data.Length < 24) continue;
                var characterId = (short)(entry.Data[0] | (entry.Data[1] << 8));
                var strings = ReadTrailingStrings(entry.Data, 22);
                if (strings.Count < 2) continue;
                // Rows naming no model, or no node to hang it from, are costume
                // and DLC variants rather than something the character carries.
                if (strings[0].Equals(EmptyModel, StringComparison.OrdinalIgnoreCase)
                    || strings[1].Equals(EmptyModel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                table.attachments.Add(new ScriptAttachment(characterId, strings[0], strings[1]));
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read '{path}': {exception.Message}");
            return Empty;
        }
        return table;
    }

    public IReadOnlyList<ScriptAttachment> FindByCharacter(int characterId)
        => attachments.Where(value => value.CharacterId == characterId).ToArray();

    private static List<string> ReadTrailingStrings(byte[] data, int start)
    {
        var values = new List<string>(2);
        var index = start;
        while (index < data.Length)
        {
            var end = Array.IndexOf(data, (byte)0, index);
            if (end < 0) break;
            if (end > index) values.Add(Encoding.ASCII.GetString(data, index, end - index));
            index = end + 1;
        }
        return values;
    }
}
