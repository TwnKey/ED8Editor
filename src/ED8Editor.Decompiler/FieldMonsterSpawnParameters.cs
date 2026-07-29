using System.Numerics;

namespace ED8Editor.Decompiler;

/// <summary>
/// Typed OP19 layout used by field encounters. Its argument slots are the
/// regular Entity_Spawn payload, but the game assigns encounter-specific
/// meanings to slots 3 and 15-19.
/// </summary>
public sealed record FieldMonsterSpawnParameters(
    int EntityId,
    string ModelAsset,
    string DisplayName,
    string MonsterAsset,
    int EntityType,
    int Flags,
    Vector3 Position,
    float HeadingDegrees,
    float Scale,
    float CollisionHeight,
    float CollisionRadius,
    string ScriptFile,
    string InitFunction,
    int BattleFunctionIndex,
    int EncounterIndex,
    int UnknownParameter1,
    int UnknownParameter2,
    int UnknownParameter3)
{
    // These two raw values are 6.0f and 20.0f respectively in the retail
    // field-monster corpus. Their engine-level names remain unresolved.
    public const int RetailUnknownParameter1 = 0x40C00000;
    public const int RetailUnknownParameter2 = 0x41A00000;

    public static FieldMonsterSpawnParameters CreateDefault(
        int entityId,
        string monsterAsset,
        int battleFunctionIndex,
        int encounterIndex)
        => new(
            entityId,
            string.Empty,
            string.Empty,
            monsterAsset,
            EntityType: 2,
            Flags: 0,
            Position: Vector3.Zero,
            HeadingDegrees: 0f,
            Scale: -1f,
            CollisionHeight: 0f,
            CollisionRadius: 0f,
            ScriptFile: string.Empty,
            InitFunction: string.Empty,
            battleFunctionIndex,
            encounterIndex,
            RetailUnknownParameter1,
            RetailUnknownParameter2,
            UnknownParameter3: 0);
}
