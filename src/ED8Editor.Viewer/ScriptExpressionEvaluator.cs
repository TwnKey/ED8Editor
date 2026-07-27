using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// The script variables a replay can know exactly: the per-entity register file
/// written by SET_REG/SET_REG2 and the scenario flags written by
/// SET_FLAG/RESET_FLAG. The engine zero-initialises a character's register file
/// when its model is loaded, so a register this replay never wrote reads 0.
/// Save-game state the replay cannot observe (work/sys variables, flags set by
/// earlier scenes) stays unknown and leaves the branch policy untouched.
/// </summary>
internal sealed class ScriptVariableState
{
    private const int ScenarioOwner = int.MinValue;

    private readonly Dictionary<int, Dictionary<int, int>> registersByOwner = new();
    private readonly Dictionary<int, bool> flags = new();
    private readonly Dictionary<int, int> entityStatus = new();

    public int ReadRegister(int? entityId, int index)
        => registersByOwner.TryGetValue(entityId ?? ScenarioOwner, out var registers)
            && registers.TryGetValue(index, out var value)
                ? value
                : 0;

    public void WriteRegister(int? entityId, int index, int? value)
    {
        var owner = entityId ?? ScenarioOwner;
        if (!registersByOwner.TryGetValue(owner, out var registers))
        {
            registers = new Dictionary<int, int>();
            registersByOwner.Add(owner, registers);
        }
        // An unresolvable assignment invalidates the register: reading its stale
        // value would be worse than admitting the branch cannot be decided.
        if (value is { } resolved) registers[index] = resolved;
        else registers.Remove(index);
    }

    public bool TryReadFlag(int index, out bool value) => flags.TryGetValue(index, out value);

    public void WriteFlag(int index, bool value) => flags[index] = value;

    /// <summary>
    /// The status bit-mask an entity carries, written by OP43 (set bits) and
    /// OP44 (clear bits) and read back by the OP42 query. The engine creates an
    /// entity with a cleared mask, so a bit this replay never wrote reads 0.
    /// </summary>
    public int ReadEntityStatus(int entityId) => entityStatus.GetValueOrDefault(entityId);

    public void WriteEntityStatus(int entityId, int mask, bool set)
        => entityStatus[entityId] = set
            ? ReadEntityStatus(entityId) | mask
            : ReadEntityStatus(entityId) & ~mask;
}

/// <summary>
/// Evaluates a decompiled expression (a stack program) when every operand is
/// known. Sub-operation codes match the decompiler's expression table.
/// </summary>
internal static class ScriptExpressionEvaluator
{
    /// <summary>Reads an entity's status mask (OP42).</summary>
    private const int EntityStatusOpcode = 42;

    public static bool TryEvaluate(
        IReadOnlyList<ExprElement>? expression,
        ScriptVariableState variables,
        int? selfEntityId,
        out int result)
    {
        ArgumentNullException.ThrowIfNull(variables);
        result = 0;
        if (expression is null || expression.Count == 0) return false;
        var stack = new Stack<int>();
        foreach (var element in expression)
        {
            switch (element.SubOp)
            {
                case 0x01: // END
                    return stack.TryPop(out result);
                case 0x00: // push <literal>
                    stack.Push(element.Value);
                    break;
                case 0x1e: // flag[n]
                    if (!variables.TryReadFlag(element.Value, out var flag)) return false;
                    stack.Push(flag ? 1 : 0);
                    break;
                case 0x1f: // reg[n]
                    stack.Push(variables.ReadRegister(selfEntityId, element.Value));
                    break;
                case 0x08: // ==0
                    if (!stack.TryPop(out var zeroOperand)) return false;
                    stack.Push(zeroOperand == 0 ? 1 : 0);
                    break;
                case 0x0e: // neg
                    if (!stack.TryPop(out var negated)) return false;
                    stack.Push(-negated);
                    break;
                case 0x1d: // ~
                    if (!stack.TryPop(out var complemented)) return false;
                    stack.Push(~complemented);
                    break;
                case 0x13: // nop
                    break;
                case 0x1c: // redispatch: the nested instruction's result
                    if (element.NestedOpcode != EntityStatusOpcode
                        || element.NestedArguments is not { Count: > 0 } queryArguments)
                    {
                        return false;
                    }
                    stack.Push(variables.ReadEntityStatus(
                        queryArguments[0] == -2 && selfEntityId is { } self
                            ? self
                            : queryArguments[0]));
                    break;
                default:
                    if (!TryApplyBinary(element.SubOp, stack)) return false;
                    break;
            }
        }
        return stack.TryPop(out result);
    }

    private static bool TryApplyBinary(int subOp, Stack<int> stack)
    {
        if (stack.Count < 2) return false;
        var right = stack.Pop();
        var left = stack.Pop();
        switch (subOp)
        {
            case 0x02: stack.Push(left == right ? 1 : 0); return true;
            case 0x03: stack.Push(left != right ? 1 : 0); return true;
            case 0x04: stack.Push(left < right ? 1 : 0); return true;
            case 0x05: stack.Push(left > right ? 1 : 0); return true;
            case 0x06: stack.Push(left <= right ? 1 : 0); return true;
            case 0x07: stack.Push(left >= right ? 1 : 0); return true;
            case 0x09: stack.Push(left != 0 && right != 0 ? 1 : 0); return true;
            case 0x0a: case 0x19: stack.Push(left & right); return true;
            case 0x0b: case 0x1b: stack.Push(left | right); return true;
            case 0x0c: case 0x17: stack.Push(left + right); return true;
            case 0x0d: case 0x18: stack.Push(left - right); return true;
            case 0x0f: case 0x1a: stack.Push(left ^ right); return true;
            case 0x10: case 0x14: stack.Push(left * right); return true;
            case 0x11: case 0x15:
                if (right == 0) return false;
                stack.Push(left / right);
                return true;
            case 0x12: case 0x16:
                if (right == 0) return false;
                stack.Push(left % right);
                return true;
            // Sub-routine results (0x1c), queries (0x21) and randomness (0x22)
            // depend on engine state this replay does not model.
            default: return false;
        }
    }
}
