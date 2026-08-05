using System.Text;

namespace ED8Editor.Decompiler;

/// <summary>
/// Writes a script condition the way a person reads one.
///
/// The engine stores a condition as a stack program, and showing it as one — the
/// elements strung together in the order they are pushed — produces
/// <c>work[5] push 4 ==</c>, which states everything and explains nothing. The
/// operator sits after its operands, the word "push" is machinery rather than
/// meaning, and a reader has to run the stack in their head to find out that the
/// condition is <c>work[5] = 4</c>.
///
/// So the stack is run here instead. Each element either pushes a value or consumes
/// the ones below it, exactly as <see cref="ScriptExpressionEvaluator"/> executes
/// it — the same sub-operation table, so the text and the evaluation cannot drift —
/// and what comes back out is the expression written in the usual order, with
/// brackets only where precedence needs them.
///
/// An element the table does not cover keeps the decompiler's own label rather than
/// being dropped or guessed at: an unreadable name is a smaller problem than a
/// condition that reads well and says the wrong thing.
/// </summary>
public static class ScriptExpressionText
{
    /// <summary>Binding strength, so brackets appear only where they change meaning.</summary>
    private const int Atom = 100;

    /// <summary>
    /// The condition, written in the usual order. Empty when there is nothing to
    /// write.
    /// </summary>
    public static string Format(IReadOnlyList<ExprElement>? expression)
    {
        if (Build(expression) is not { } built) return string.Empty;
        return built.Text;
    }

    /// <summary>
    /// The condition negated, for the branch taken when it does not hold.
    ///
    /// A comparison is negated by flipping it, which is what a reader would write:
    /// the other side of <c>work[5] = 4</c> is <c>work[5] ≠ 4</c>, not
    /// <c>not (work[5] = 4)</c>. Anything else is wrapped, which is correct if
    /// less pretty.
    /// </summary>
    public static string FormatNegated(IReadOnlyList<ExprElement>? expression)
    {
        if (Build(expression) is not { } built) return string.Empty;
        if (built.Opposite is { } flipped) return flipped;
        return built.Precedence >= Atom ? "not " + built.Text : "not (" + built.Text + ")";
    }

    /// <summary>
    /// What the condition reads as on its own, when it is a bare test.
    ///
    /// A flag pushed and tested for truth is the commonest condition in these
    /// scripts and the least readable as a formula: <c>flag[256]</c> alone says
    /// nothing about what is being asked of it.
    /// </summary>
    private static string Sentence(string text, bool negated)
    {
        var bare = text.StartsWith("flag[", StringComparison.Ordinal)
            && text.EndsWith("]", StringComparison.Ordinal);
        if (bare) return negated ? text + " is clear" : text + " is set";
        return negated ? "not " + text : text;
    }

    /// <summary>The condition as a phrase: what has to hold for the branch to run.</summary>
    public static string Describe(IReadOnlyList<ExprElement>? expression, bool taken)
    {
        if (Build(expression) is not { } built) return string.Empty;
        if (!taken && built.Opposite is { } flipped) return flipped;
        return Sentence(built.Text, !taken);
    }

    private static Written? Build(IReadOnlyList<ExprElement>? expression)
    {
        if (expression is null || expression.Count == 0) return null;
        var stack = new Stack<Written>();
        foreach (var element in expression)
        {
            switch (element.SubOp)
            {
                case 0x01:   // END
                case 0x13:   // nop
                    continue;

                case 0x00:   // a literal
                    stack.Push(new Written(Number(element.Value), Atom));
                    continue;

                case 0x1e:   // a scenario flag
                    stack.Push(new Written($"flag[{element.Value}]", Atom));
                    continue;

                case 0x1f:   // an entity's own register
                    stack.Push(new Written($"reg[{element.Value}]", Atom));
                    continue;

                case 0x1c:   // the result of a nested instruction
                case 0x21:
                case 0x22:
                    stack.Push(new Written(
                        string.IsNullOrEmpty(element.NestedInstruction)
                            ? Fallback(element)
                            : element.NestedInstruction!,
                        Atom));
                    continue;
            }

            if (Unary(element.SubOp) is { } unary)
            {
                if (!stack.TryPop(out var operand)) return Verbatim(expression);
                stack.Push(unary(operand));
                continue;
            }

            if (Binary(element.SubOp) is { } binary)
            {
                if (stack.Count < 2) return Verbatim(expression);
                var right = stack.Pop();
                var left = stack.Pop();
                stack.Push(binary(left, right));
                continue;
            }

            // Nothing here knows this element. Keeping its own label is what lets an
            // expression with one unknown step still read as a formula rather than
            // collapsing to raw stack notation.
            stack.Push(new Written(Fallback(element), Atom));
        }

        if (stack.Count == 0) return Verbatim(expression);
        // Anything left below the top was pushed and never consumed. Saying so is
        // better than showing only the top and implying the rest was not there.
        var top = stack.Pop();
        if (stack.Count == 0) return top;
        var rest = string.Join(", ", stack.Reverse().Select(value => value.Text));
        return new Written($"{top.Text}   [unused: {rest}]", 0);
    }

    /// <summary>
    /// The elements as the decompiler names them, in order. Used when the stack
    /// does not balance, which means this cannot claim to have understood it.
    /// </summary>
    private static Written Verbatim(IReadOnlyList<ExprElement> expression)
    {
        var text = new StringBuilder();
        foreach (var element in expression)
        {
            if (element.SubOp == 0x01) continue;
            if (text.Length != 0) text.Append(' ');
            text.Append(Fallback(element));
        }
        return new Written(text.ToString(), 0);
    }

    private static string Fallback(ExprElement element)
        => !string.IsNullOrEmpty(element.NestedInstruction)
            ? element.NestedInstruction!
            : string.IsNullOrEmpty(element.Label)
                ? $"op{element.SubOp:X2}"
                : element.Label;

    private static Func<Written, Written>? Unary(int subOp) => subOp switch
    {
        // "== 0" is how the engine writes a negation. Negating a comparison is the
        // opposite comparison, which is what a reader would write; negating anything
        // else needs the word.
        0x08 => operand => operand.Opposite is { } flipped
            ? new Written(flipped, operand.Precedence, operand.Text)
            : new Written("not " + Bracket(operand, 12), 12, Bracket(operand, 12)),
        0x0e => operand => new Written("-" + Bracket(operand, 12), 12),
        0x1d => operand => new Written("~" + Bracket(operand, 12), 12),
        _ => null,
    };

    private static Func<Written, Written, Written>? Binary(int subOp)
    {
        // Written the way a reader writes them, not the way C does. A doubled equals
        // sign is a programming language's way of telling assignment from
        // comparison, and there is no assignment here to tell it from; the same goes
        // for spelling "at most" as two characters that have to be read as one.
        var (symbol, precedence, opposite) = subOp switch
        {
            0x02 => ("=", 6, "≠"),
            0x03 => ("≠", 6, "="),
            0x04 => ("<", 7, "≥"),
            0x05 => (">", 7, "≤"),
            0x06 => ("≤", 7, ">"),
            0x07 => ("≥", 7, "<"),
            0x09 => ("and", 2, null),
            0x0a or 0x19 => ("&", 5, null),
            0x0b or 0x1b => ("|", 3, null),
            0x0c or 0x17 => ("+", 9, null),
            0x0d or 0x18 => ("-", 9, null),
            0x0f or 0x1a => ("^", 4, null),
            0x10 or 0x14 => ("*", 10, null),
            0x11 or 0x15 => ("/", 10, null),
            0x12 or 0x16 => ("%", 10, null),
            _ => (null, 0, null),
        };
        if (symbol is null) return null;
        return (left, right) => new Written(
            $"{Bracket(left, precedence)} {symbol} {Bracket(right, precedence + 1)}",
            precedence,
            opposite is null
                ? null
                : $"{Bracket(left, precedence)} {opposite} {Bracket(right, precedence + 1)}");
    }

    private static string Bracket(Written value, int needed)
        => value.Precedence >= needed ? value.Text : "(" + value.Text + ")";

    /// <summary>
    /// A literal, in the base it was most likely written in. A mask reads as one in
    /// hexadecimal and as nothing in decimal.
    /// </summary>
    private static string Number(int value)
        => value is > 255 or < -1 && (value & (value - 1)) == 0
            ? $"0x{value:X}"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="Opposite">
    /// The same expression with its top-level comparison flipped, when it has one.
    /// </param>
    private sealed record Written(string Text, int Precedence, string? Opposite = null);
}
