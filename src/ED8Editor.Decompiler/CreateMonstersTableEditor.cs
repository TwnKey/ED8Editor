namespace ED8Editor.Decompiler;

/// <summary>
/// Structural authoring operations for the fixed-width encounter records in a
/// CreateMonsters function. Unknown header/trailer data is never synthesized
/// or rewritten: records are inserted immediately before the preserved trailer.
/// </summary>
public static class CreateMonstersTableEditor
{
    public static void AddEncounter(
        ScriptEditorDocument document,
        int functionIndex,
        int position,
        int encounterId,
        IReadOnlyList<string> monsterAssets,
        IReadOnlyList<int> weights)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(monsterAssets);
        ArgumentNullException.ThrowIfNull(weights);
        if (monsterAssets.Count > 8)
            throw new ArgumentException("A CreateMonsters encounter has exactly eight asset slots.", nameof(monsterAssets));
        if (weights.Count > 8)
            throw new ArgumentException("A CreateMonsters encounter has exactly eight weight slots.", nameof(weights));
        foreach (var asset in monsterAssets)
        {
            if (asset is null) throw new ArgumentException("Monster asset names cannot be null.", nameof(monsterAssets));
            if (System.Text.Encoding.ASCII.GetByteCount(asset) > 15 || asset.Any(value => value > 0x7f))
                throw new ArgumentException(
                    $"Monster asset '{asset}' must be an ASCII identifier of at most 15 bytes.",
                    nameof(monsterAssets));
        }
        foreach (var weight in weights)
            if (weight is < byte.MinValue or > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(weights), weight, "Weights must fit in one byte.");

        var before = Read(document, functionIndex);
        if (position < 0 || position > before.Encounters.Count)
            throw new ArgumentOutOfRangeException(nameof(position));

        document.AddCreateMonstersEncounter(functionIndex, position, encounterId);
        var after = Read(document, functionIndex);
        if (after.Encounters.Count != before.Encounters.Count + 1)
            throw new InvalidOperationException("The CreateMonsters record was not inserted.");
        var inserted = after.Encounters[position];
        for (var slot = 0; slot < 8; slot++)
        {
            document.SetTableText(
                functionIndex,
                inserted.SourceFields[1 + slot * 2].Index,
                slot < monsterAssets.Count ? monsterAssets[slot] : string.Empty);
            document.SetTableInteger(
                functionIndex,
                inserted.SourceFields[17 + slot].Index,
                slot < weights.Count ? weights[slot] : 0);
        }
    }

    public static void DuplicateEncounter(
        ScriptEditorDocument document,
        int functionIndex,
        int sourcePosition,
        int destinationPosition,
        int encounterId)
    {
        var table = Read(document, functionIndex);
        if (sourcePosition < 0 || sourcePosition >= table.Encounters.Count)
            throw new ArgumentOutOfRangeException(nameof(sourcePosition));
        var source = table.Encounters[sourcePosition];
        if (source.AuxiliaryAsset is not null)
            throw new InvalidOperationException(
                "This encounter carries an auxiliary asset. Exact structural cloning "
                + "is not implemented yet, so it will not be silently discarded.");
        AddEncounter(
            document,
            functionIndex,
            destinationPosition,
            encounterId,
            source.MonsterAssets,
            source.Weights);

        // The no-auxiliary representation is a fixed eight-byte opaque field.
        // Preserve it as well, even though known files normally contain zeros.
        var refreshed = Read(document, functionIndex);
        var destination = refreshed.Encounters[destinationPosition];
        var sourceAuxiliary = source.SourceFields.Skip(25).ToArray();
        var destinationAuxiliary = destination.SourceFields.Skip(25).ToArray();
        if (sourceAuxiliary.Length == 1 && destinationAuxiliary.Length == 1
            && sourceAuxiliary[0].Raw.Length == destinationAuxiliary[0].Raw.Length)
        {
            document.SetTableBytes(
                functionIndex,
                destinationAuxiliary[0].Index,
                sourceAuxiliary[0].Raw);
        }
    }

    public static void RemoveEncounter(
        ScriptEditorDocument document,
        int functionIndex,
        int position)
    {
        ArgumentNullException.ThrowIfNull(document);
        var table = Read(document, functionIndex);
        if (position < 0 || position >= table.Encounters.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        document.RemoveCreateMonstersEncounter(functionIndex, position);
    }

    private static CreateMonstersTable Read(ScriptEditorDocument document, int functionIndex)
    {
        var function = document.Snapshot.Functions.ElementAtOrDefault(functionIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(functionIndex));
        if (function.Table is null
            || !CreateMonstersTableReader.TryRead(function.Table, out var table)
            || table is null)
        {
            throw new InvalidOperationException(
                $"Function #{functionIndex} is not a structurally valid CreateMonsters table.");
        }
        return table;
    }
}
