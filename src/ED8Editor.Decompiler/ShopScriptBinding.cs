namespace ED8Editor.Decompiler;

/// <summary>
/// Exact link between an OPS shop function and the OP114 instruction that opens
/// a shop definition. Local scenario calls are CALL_EXT (opcode 4) with selector
/// 11; other selectors deliberately are not followed.
/// </summary>
public sealed record ShopScriptBinding(
    int FunctionIndex,
    string FunctionName,
    int InstructionIndex,
    int ShopId,
    IReadOnlyList<string> CallPath)
{
    public const int ShopOpcode = 114;
    public const int LocalCallOpcode = 4;
    public const int LocalCallSelector = 11;

    public string Label =>
        $"{string.Join(" → ", CallPath)} · #{InstructionIndex} · shop {ShopId}";

    public static IReadOnlyList<ShopScriptBinding> Read(
        DecompiledScript script,
        string entryFunctionName)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (string.IsNullOrWhiteSpace(entryFunctionName))
            return Array.Empty<ShopScriptBinding>();

        var functions = script.Functions
            .Where(value => value.IsCode)
            .GroupBy(value => value.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var result = new List<ShopScriptBinding>();
        Visit(entryFunctionName, Array.Empty<string>(), new HashSet<int>());
        return result;

        void Visit(
            string functionName,
            IReadOnlyList<string> parentPath,
            HashSet<int> activeFunctions)
        {
            if (!functions.TryGetValue(functionName, out var matches)) return;
            foreach (var function in matches)
            {
                if (!activeFunctions.Add(function.Index)) continue;
                var path = parentPath.Append(function.Name).ToArray();
                foreach (var instruction in function.Instructions)
                {
                    if (instruction.Opcode == ShopOpcode
                        && instruction.Arguments.FirstOrDefault(value =>
                            value.Kind == "scalar") is { } shopArgument)
                    {
                        result.Add(new ShopScriptBinding(
                            function.Index,
                            function.Name,
                            instruction.Index,
                            shopArgument.IntValue,
                            path));
                    }

                    if (!TryReadLocalCall(instruction, out var target)) continue;
                    Visit(target, path, activeFunctions);
                }
                activeFunctions.Remove(function.Index);
            }
        }
    }

    private static bool TryReadLocalCall(
        DecompiledInstruction instruction,
        out string functionName)
    {
        functionName = string.Empty;
        if (instruction.Opcode != LocalCallOpcode
            || instruction.Arguments.Count < 2
            || instruction.Arguments[0].IntValue != LocalCallSelector)
        {
            return false;
        }

        var argument = instruction.Arguments[1];
        if (argument.Kind != "string") return false;
        functionName = DecodeNullTerminated(argument.Raw);
        return !string.IsNullOrWhiteSpace(functionName);
    }

    private static string DecodeNullTerminated(byte[] raw)
    {
        var length = Array.IndexOf(raw, (byte)0);
        if (length < 0) length = raw.Length;
        return System.Text.Encoding.UTF8.GetString(raw, 0, length);
    }
}
