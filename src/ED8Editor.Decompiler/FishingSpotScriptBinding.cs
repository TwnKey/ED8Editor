using System.Buffers.Binary;
using System.Numerics;

namespace ED8Editor.Decompiler;

/// <summary>
/// The established OP73 selector-1 payload used by CS1 fishing interactions.
/// A physical OPS LookPoint can have several bindings because scenario branches
/// select different fish_pnt records while retaining the same world location.
/// </summary>
public sealed record FishingSpotScriptBinding(
    int FunctionIndex,
    string FunctionName,
    int InstructionIndex,
    int PayloadArgumentIndex,
    int FishingPointId,
    Vector3 PlayerPosition,
    float HeadingDegrees,
    Vector3 WaterTarget)
{
    public const int Opcode = 73;
    public const int Selector = 1;
    public const int PayloadSize = 32;

    public string Label =>
        $"#{InstructionIndex} — fish_pnt {FishingPointId}";

    public byte[] EncodePayload()
    {
        Validate();
        var payload = new byte[PayloadSize];
        BinaryPrimitives.WriteInt32LittleEndian(payload, FishingPointId);
        WriteSingle(payload, 4, PlayerPosition.X);
        WriteSingle(payload, 8, PlayerPosition.Y);
        WriteSingle(payload, 12, PlayerPosition.Z);
        WriteSingle(payload, 16, HeadingDegrees);
        WriteSingle(payload, 20, WaterTarget.X);
        WriteSingle(payload, 24, WaterTarget.Y);
        WriteSingle(payload, 28, WaterTarget.Z);
        return payload;
    }

    public static IReadOnlyList<FishingSpotScriptBinding> Read(
        DecompiledScript script,
        string functionName)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (string.IsNullOrWhiteSpace(functionName))
            return Array.Empty<FishingSpotScriptBinding>();

        var result = new List<FishingSpotScriptBinding>();
        foreach (var function in script.Functions.Where(value =>
                     value.IsCode
                     && value.Name.Equals(functionName, StringComparison.Ordinal)))
        {
            foreach (var instruction in function.Instructions.Where(value =>
                         value.Opcode == Opcode
                         && value.Name.Equals("OP73_1", StringComparison.Ordinal)
                         && value.Arguments.Count >= 1))
            {
                var payloadArgument = instruction.Arguments.FirstOrDefault(value =>
                    value.Type == "bytes" && value.Raw.Length == PayloadSize);
                if (payloadArgument is null) continue;
                var payload = payloadArgument.Raw;
                var binding = new FishingSpotScriptBinding(
                    function.Index,
                    function.Name,
                    instruction.Index,
                    payloadArgument.Index,
                    BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)),
                    new Vector3(
                        ReadSingle(payload, 4),
                        ReadSingle(payload, 8),
                        ReadSingle(payload, 12)),
                    ReadSingle(payload, 16),
                    new Vector3(
                        ReadSingle(payload, 20),
                        ReadSingle(payload, 24),
                        ReadSingle(payload, 28)));
                if (binding.IsFinite()) result.Add(binding);
            }
        }
        return result;
    }

    private void Validate()
    {
        if (!IsFinite())
            throw new ArgumentException("Fishing spot coordinates must be finite.");
    }

    private bool IsFinite() =>
        IsFinite(PlayerPosition)
        && float.IsFinite(HeadingDegrees)
        && IsFinite(WaterTarget);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float ReadSingle(byte[] source, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, 4)));

    private static void WriteSingle(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));
}
