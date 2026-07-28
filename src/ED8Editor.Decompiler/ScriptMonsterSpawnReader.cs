using System.Numerics;
using System.Text;

namespace ED8Editor.Decompiler;

/// <summary>
/// A field monster created by opcode 0x13 (OP19). The opcode's battle-function
/// operand points at a structurally validated CreateMonsters table.
/// </summary>
public sealed record ScriptMonsterSpawn(
    int EntityId,
    string AssetId,
    Vector3 Position,
    float HeadingDegrees,
    int BattleFunctionIndex,
    int EncounterIndex,
    int SourceFunctionIndex,
    int SourceInstructionIndex);

public static class ScriptMonsterSpawnReader
{
    private const int CreateEntityOpcode = 0x13;
    private const int EntityIdArgument = 0;
    private const int AssetArgument = 3;
    private const int PositionXArgument = 6;
    private const int PositionYArgument = 7;
    private const int PositionZArgument = 8;
    private const int HeadingArgument = 9;
    private const int BattleFunctionArgument = 15;
    private const int EncounterIndexArgument = 16;

    public static IReadOnlyList<ScriptMonsterSpawn> Read(DecompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var result = new List<ScriptMonsterSpawn>();
        foreach (var function in script.Functions.Where(value => value.IsCode))
        {
            foreach (var instruction in function.Instructions.Where(value => value.Opcode == CreateEntityOpcode))
            {
                if (!HasCreateEntityLayout(instruction.Arguments)) continue;
                var battleFunctionIndex = instruction.Arguments[BattleFunctionArgument].IntValue;
                if (battleFunctionIndex < 0 || battleFunctionIndex >= script.Functions.Count
                    || script.Functions[battleFunctionIndex].Table is not { Kind: "CreateMonsters", IsStale: false })
                {
                    continue;
                }

                var assetId = ReadString(instruction.Arguments[AssetArgument]);
                if (string.IsNullOrEmpty(assetId)) continue;
                result.Add(new ScriptMonsterSpawn(
                    instruction.Arguments[EntityIdArgument].IntValue,
                    assetId,
                    new Vector3(
                        (float)instruction.Arguments[PositionXArgument].FloatValue,
                        (float)instruction.Arguments[PositionYArgument].FloatValue,
                        (float)instruction.Arguments[PositionZArgument].FloatValue),
                    (float)instruction.Arguments[HeadingArgument].FloatValue,
                    battleFunctionIndex,
                    instruction.Arguments[EncounterIndexArgument].IntValue,
                    function.Index,
                    instruction.Index));
            }
        }
        return result;
    }

    private static bool HasCreateEntityLayout(IReadOnlyList<InstructionArgument> arguments)
        => arguments.Count > EncounterIndexArgument
            && arguments[EntityIdArgument].Type == "s16"
            && arguments[AssetArgument].Kind == "string"
            && arguments[PositionXArgument].Type == "f32"
            && arguments[PositionYArgument].Type == "f32"
            && arguments[PositionZArgument].Type == "f32"
            && arguments[HeadingArgument].Type == "f32"
            && arguments[BattleFunctionArgument].Type == "s32"
            && arguments[EncounterIndexArgument].Type == "u8";

    private static string ReadString(InstructionArgument argument)
        => Encoding.Latin1.GetString(argument.Raw).TrimEnd('\0');
}
