namespace ED8Editor.Decompiler;

public sealed record CreateMonstersEncounter(
    int Index,
    int Id,
    IReadOnlyList<string> MonsterAssets,
    IReadOnlyList<int> Weights,
    string? AuxiliaryAsset,
    IReadOnlyList<TableField> SourceFields);

public sealed record CreateMonstersTable(
    string MapAsset,
    IReadOnlyList<TableField> HeaderFields,
    IReadOnlyList<CreateMonstersEncounter> Encounters,
    IReadOnlyList<TableField> TrailerFields);

/// <summary>
/// Gives the flat, byte-preserving native table a record structure. Unknown fields
/// remain available verbatim; only fields established by the binary reader are named.
/// </summary>
public static class CreateMonstersTableReader
{
    public static bool TryRead(DecompiledTable table, out CreateMonstersTable? result)
    {
        ArgumentNullException.ThrowIfNull(table);
        result = null;
        if (table.Kind != "CreateMonsters" || table.IsStale || table.Fields.Count < 9
            || table.Fields[0].Type != "string") return false;

        var fields = table.Fields;
        var header = fields.Take(9).ToArray();
        var encounters = new List<CreateMonstersEncounter>();
        var cursor = 9;
        while (cursor < fields.Count && fields[cursor].Type == "s32")
        {
            var start = cursor;
            var id = checked((int)fields[cursor++].IntValue);
            var assets = new List<string>(8);
            for (var slot = 0; slot < 8; slot++)
            {
                if (cursor + 1 >= fields.Count || fields[cursor].Type != "string") return false;
                assets.Add(fields[cursor].Text ?? string.Empty);
                cursor += 2; // fixed-width string followed by its byte-preserving fill
            }
            var weights = new List<int>(8);
            for (var slot = 0; slot < 8; slot++)
            {
                if (cursor >= fields.Count || fields[cursor].Type != "u8") return false;
                weights.Add(checked((int)fields[cursor++].IntValue));
            }

            string? auxiliaryAsset = null;
            if (cursor >= fields.Count) return false;
            if (fields[cursor].Type == "string")
            {
                auxiliaryAsset = fields[cursor++].Text;
                if (cursor >= fields.Count || fields[cursor].Type is not ("bytes" or "fill")) return false;
                cursor++;
            }
            else if (fields[cursor].Type == "bytes")
            {
                cursor++;
            }
            else
            {
                return false;
            }

            encounters.Add(new CreateMonstersEncounter(
                encounters.Count,
                id,
                assets,
                weights,
                auxiliaryAsset,
                fields.Skip(start).Take(cursor - start).ToArray()));
        }

        result = new CreateMonstersTable(
            fields[0].Text ?? string.Empty,
            header,
            encounters,
            fields.Skip(cursor).ToArray());
        return true;
    }
}
