namespace ED8Editor.Decompiler;

/// <summary>Where a value comes from: a number, or one of the script's variables.</summary>
public enum ScriptOperandKind
{
    Number,
    Flag,
    Register,
    Work,
    /// <summary>The result of an instruction run inside the condition.</summary>
    Instruction,
}

/// <summary>
/// A condition as something that can be edited: a tree, not a stack program.
///
/// The engine stores conditions in reverse Polish, which is why the old builder
/// showed two columns whose meaning depended on each other — the left one holding
/// a variable on one row and an operator on the next, with a "value" column beside
/// it that meant nothing in the second case. Nothing can be built safely on top of
/// that shape.
///
/// This is the same condition as an expression: operands that know what they are,
/// and operators that know what they join. An editor over this cannot put an
/// operator where a variable goes, or ask for a value that the chosen test does
/// not take, because the shape does not allow it.
///
/// Reading and writing are the same table the evaluator and the text writer use, so
/// the three cannot drift apart.
/// </summary>
public abstract record ScriptExpressionNode
{
    /// <summary>A number, a flag, a register, a work variable, an instruction's result.</summary>
    public sealed record Operand(
        ScriptOperandKind Kind,
        int Value,
        string? Label = null,
        int NestedOpcode = -1,
        IReadOnlyList<int>? NestedArguments = null) : ScriptExpressionNode;

    /// <summary>A test or an arithmetic step joining two operands.</summary>
    public sealed record Binary(int SubOp, ScriptExpressionNode Left, ScriptExpressionNode Right)
        : ScriptExpressionNode;

    /// <summary>Negation, complement, sign.</summary>
    public sealed record Unary(int SubOp, ScriptExpressionNode Inner) : ScriptExpressionNode;
}

/// <summary>Reads a stack program into a tree and writes it back out.</summary>
public static class ScriptExpressionTree
{
    /// <summary>Sub-operations that take two operands, as the evaluator applies them.</summary>
    public static readonly IReadOnlyList<int> BinaryOps = new[]
    {
        0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x09,
        0x0a, 0x0b, 0x0c, 0x0d, 0x0f, 0x10, 0x11, 0x12,
    };

    /// <summary>
    /// Stands for "no operator": the operand is the condition on its own. Not a
    /// sub-operation the engine has, which is why it is out of their range.
    /// </summary>
    public const int NoOperator = -1;

    /// <summary>Sub-operations that take one.</summary>
    public static readonly IReadOnlyList<int> UnaryOps = new[] { 0x08, 0x0e, 0x1d };

    /// <summary>
    /// The tests a condition can make, named as a person would name them.
    ///
    /// A closed list, which is the point: an editor offering these cannot produce a
    /// comparison the engine does not have, and cannot ask for a value where the
    /// chosen test takes none — "is set" and "is clear" stand alone, the rest take a
    /// right-hand side.
    /// </summary>
    public static IReadOnlyList<(int SubOp, string Label, bool TakesValue)> Tests { get; } = new[]
    {
        // No operator at all: the value is the condition, which is how the scripts
        // test a flag. Its opposite is the engine's "== 0".
        (NoOperator, "is set", false),
        (0x08, "is clear", false),
        (0x02, "is equal to", true),
        (0x03, "is not equal to", true),
        (0x04, "is less than", true),
        (0x05, "is greater than", true),
        (0x06, "is at most", true),
        (0x07, "is at least", true),
        (0x0a, "has all bits of", true),
    };

    /// <summary>The steps that combine two values before a test looks at them.</summary>
    public static IReadOnlyList<(int SubOp, string Label)> Arithmetic { get; } = new[]
    {
        (0x0c, "plus"),
        (0x0d, "minus"),
        (0x10, "times"),
        (0x11, "divided by"),
        (0x12, "remainder of"),
        (0x0a, "bits in common with"),
        (0x0b, "bits together with"),
        (0x0f, "bits differing from"),
    };

    /// <summary>How two conditions are joined.</summary>
    public static IReadOnlyList<(int SubOp, string Label)> Joins { get; } = new[]
    {
        (0x09, "and"),
        (0x0b, "or"),
    };

    /// <summary>
    /// The condition as a tree, or null when the program does not resolve to one.
    ///
    /// Null is a real answer: a program this cannot read is one an editor must not
    /// pretend to be able to rewrite, and saying so is what keeps a half-understood
    /// condition from being written back wrong.
    /// </summary>
    public static ScriptExpressionNode? Parse(IReadOnlyList<ExprElement>? expression)
    {
        if (expression is null || expression.Count == 0) return null;
        var stack = new Stack<ScriptExpressionNode>();
        foreach (var element in expression)
        {
            switch (element.SubOp)
            {
                case 0x01:
                case 0x13:
                    continue;
                case 0x00:
                    stack.Push(new ScriptExpressionNode.Operand(
                        ScriptOperandKind.Number, element.Value));
                    continue;
                case 0x1e:
                    stack.Push(new ScriptExpressionNode.Operand(
                        ScriptOperandKind.Flag, element.Value));
                    continue;
                case 0x1f:
                    stack.Push(new ScriptExpressionNode.Operand(
                        ScriptOperandKind.Register, element.Value));
                    continue;
                case 0x20:
                    stack.Push(new ScriptExpressionNode.Operand(
                        ScriptOperandKind.Work, element.Value));
                    continue;
            }

            if (UnaryOps.Contains(element.SubOp))
            {
                if (!stack.TryPop(out var operand)) return null;
                stack.Push(new ScriptExpressionNode.Unary(element.SubOp, operand));
                continue;
            }
            if (BinaryOps.Contains(element.SubOp))
            {
                if (stack.Count < 2) return null;
                var right = stack.Pop();
                var left = stack.Pop();
                stack.Push(new ScriptExpressionNode.Binary(element.SubOp, left, right));
                continue;
            }

            // Anything else stands for a value this cannot compute but can carry:
            // an instruction run inside the condition, a query, a random draw. It is
            // kept whole so that editing the test around it does not lose it.
            stack.Push(new ScriptExpressionNode.Operand(
                ScriptOperandKind.Instruction,
                element.Value,
                element.NestedInstruction ?? element.Label,
                element.NestedOpcode,
                element.NestedArguments));
        }
        return stack.Count == 1 ? stack.Pop() : null;
    }

    /// <summary>
    /// The tree written back as the stack program the engine reads, ending with the
    /// END element it expects.
    /// </summary>
    public static IReadOnlyList<ExprElement> Flatten(ScriptExpressionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var elements = new List<ExprElement>();
        Write(node, elements);
        elements.Add(new ExprElement(0x01, "end", "end", 0, null));
        return elements;
    }

    private static void Write(ScriptExpressionNode node, List<ExprElement> elements)
    {
        switch (node)
        {
            case ScriptExpressionNode.Operand operand:
                elements.Add(Element(operand));
                return;
            case ScriptExpressionNode.Unary unary:
                Write(unary.Inner, elements);
                elements.Add(new ExprElement(unary.SubOp, "op", string.Empty, 0, null));
                return;
            case ScriptExpressionNode.Binary binary:
                Write(binary.Left, elements);
                Write(binary.Right, elements);
                elements.Add(new ExprElement(binary.SubOp, "op", string.Empty, 0, null));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown node.");
        }
    }

    private static ExprElement Element(ScriptExpressionNode.Operand operand) => operand.Kind switch
    {
        ScriptOperandKind.Number => new ExprElement(
            0x00, "push", $"push {operand.Value}", operand.Value, null),
        ScriptOperandKind.Flag => new ExprElement(
            0x1e, "flag", $"flag[{operand.Value}]", operand.Value, null),
        ScriptOperandKind.Register => new ExprElement(
            0x1f, "reg", $"reg[{operand.Value}]", operand.Value, null),
        ScriptOperandKind.Work => new ExprElement(
            0x20, "work", $"work[{operand.Value}]", operand.Value, null),
        // Written back exactly as it was read: this does not know how to build one,
        // only how not to lose one.
        _ => new ExprElement(
            0x1c,
            "call",
            operand.Label ?? string.Empty,
            operand.Value,
            operand.Label,
            operand.NestedOpcode,
            operand.NestedArguments),
    };
}
