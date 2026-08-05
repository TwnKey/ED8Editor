using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Builds a condition out of choices, not out of stack steps.
///
/// The old builder laid the engine's stack out in two columns, so one column held a
/// variable on one row and an operator on the next, with a "value" beside it that
/// meant nothing in the second case. Here every field's meaning is fixed by where it
/// is: an operand says what kind of thing it is, a test says whether it takes a
/// right-hand side at all — and the field is simply absent when it does not — and
/// several tests are joined by <c>and</c> / <c>or</c> on their own rows.
///
/// Nothing is typed but numbers, in fields that say which number they are: the index
/// of a flag is labelled as an index, not as a value.
///
/// A condition this cannot represent is not loaded at all rather than shown
/// truncated, and the raw token grid stays available beside this for those. Showing
/// the first two operands of a condition as though they were the whole of it is how
/// an edit silently drops the rest.
/// </summary>
internal sealed class ExpressionBuilderForm : Form
{
    private readonly FlowLayoutPanel rows = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8),
    };

    private readonly Label reading = new()
    {
        Dock = DockStyle.Bottom,
        Height = 40,
        Font = new Font("Consolas", 10f),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.Gainsboro,
    };

    private readonly Label problem = new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        ForeColor = Color.Goldenrod,
        AutoEllipsis = true,
    };

    private readonly List<TestRow> tests = new();

    /// <summary>The condition as the engine stores it, once accepted.</summary>
    public IReadOnlyList<ExprElement>? Result { get; private set; }

    public ExpressionBuilderForm(IReadOnlyList<ExprElement>? existing)
    {
        Text = "Condition";
        Width = 1040;
        Height = 440;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 34);

        var add = new Button { Text = "Add a test", AutoSize = true };
        var ok = new Button { Text = "Use this condition", AutoSize = true };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        add.Click += (_, _) => AddRow();
        ok.Click += (_, _) => Accept();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { add, ok, cancel });

        Controls.Add(rows);
        Controls.Add(tools);
        Controls.Add(reading);
        Controls.Add(problem);
        AcceptButton = ok;
        CancelButton = cancel;

        if (!Fill(existing))
        {
            problem.Text = existing is { Count: > 0 }
                ? "This condition is not a shape this builder can show, so it has not been"
                    + " loaded. Use the token grid for it, or build a new one here."
                : string.Empty;
            AddRow();
        }
        ShowReading();
    }

    private void AddRow()
    {
        var row = new TestRow(tests.Count != 0, ShowReading, Remove);
        tests.Add(row);
        rows.Controls.Add(row.Panel);
        ShowReading();
    }

    /// <summary>Takes a test out. The first one is the condition and stays.</summary>
    private void Remove(TestRow row)
    {
        var at = tests.IndexOf(row);
        if (at <= 0) return;
        rows.Controls.Remove(row.Panel);
        tests.RemoveAt(at);
        row.Panel.Dispose();
        ShowReading();
    }

    /// <summary>
    /// The whole condition: the tests joined left to right, which is the order the
    /// engine's stack produces and therefore the order it reads back in.
    /// </summary>
    private ScriptExpressionNode? Build()
    {
        if (tests.Count == 0) return null;
        var node = tests[0].Build();
        if (node is null) return null;
        for (var at = 1; at < tests.Count; at++)
        {
            var next = tests[at].Build();
            if (next is null) return null;
            node = new ScriptExpressionNode.Binary(tests[at].JoinSubOp, node, next);
        }
        return node;
    }

    /// <summary>
    /// Puts an existing condition into the rows, or reports that it does not fit.
    ///
    /// The joins are unwound from the left, mirroring how they were built; each side
    /// then has to be a test this form can show. One that is not stops the whole
    /// load: a partly-loaded condition written back is a condition changed in ways
    /// nobody asked for.
    /// </summary>
    private bool Fill(IReadOnlyList<ExprElement>? existing)
    {
        if (ScriptExpressionTree.Parse(existing) is not { } root) return false;

        var parts = new List<(int Join, ScriptExpressionNode Node)>();
        var node = root;
        while (node is ScriptExpressionNode.Binary joined
            && ScriptExpressionTree.Joins.Any(value => value.SubOp == joined.SubOp)
            // "or" is the same sub-operation as a bitwise or, so it only counts as a
            // join when its right-hand side is itself a test. Otherwise it is
            // arithmetic, and belongs inside an operand.
            && IsTest(joined.Right))
        {
            parts.Insert(0, (joined.SubOp, joined.Right));
            node = joined.Left;
        }
        parts.Insert(0, (ScriptExpressionTree.NoOperator, node));

        var built = new List<TestRow>();
        foreach (var part in parts)
        {
            var row = new TestRow(built.Count != 0, ShowReading, Remove);
            if (!row.Fill(part.Node))
            {
                foreach (var made in built) made.Panel.Dispose();
                row.Panel.Dispose();
                return false;
            }
            if (built.Count != 0) row.SetJoin(part.Join);
            built.Add(row);
        }

        foreach (var row in built)
        {
            tests.Add(row);
            rows.Controls.Add(row.Panel);
        }
        return true;
    }

    /// <summary>Whether a node reads as one of the tests a row can show.</summary>
    private static bool IsTest(ScriptExpressionNode node) => node switch
    {
        ScriptExpressionNode.Binary binary =>
            ScriptExpressionTree.Tests.Any(value => value.SubOp == binary.SubOp),
        ScriptExpressionNode.Unary unary => unary.SubOp == 0x08,
        _ => true,
    };

    /// <summary>Shows the condition as it will read on the canvas, while it is built.</summary>
    private void ShowReading()
    {
        reading.Text = Build() is { } node
            ? "  " + ScriptExpressionText.Format(ScriptExpressionTree.Flatten(node))
            : "  (incomplete)";
    }

    private void Accept()
    {
        if (Build() is not { } node)
        {
            problem.Text = "Every row needs a complete test before this can be used.";
            return;
        }
        Result = ScriptExpressionTree.Flatten(node);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// One value: a number or a variable, optionally combined with a second one.
    ///
    /// The arithmetic belongs to the operand rather than to a row of its own, because
    /// that is what it is — <c>work[5] + 2 &gt; 10</c> tests one value that happens
    /// to be a sum, not two things joined.
    /// </summary>
    private sealed class OperandEditor
    {
        private static readonly string[] KindLabels = { "number", "flag", "register", "work" };

        private readonly ComboBox kind = Choice(110);
        private readonly NumericUpDown index = Number();
        private readonly ComboBox arithmetic = Choice(160);
        private readonly ComboBox otherKind = Choice(110);
        private readonly NumericUpDown otherIndex = Number();

        /// <summary>
        /// An operand this cannot build but must not lose: the result of an
        /// instruction run inside the condition, a query, a random draw. Loaded, kept
        /// and written back exactly as it came.
        /// </summary>
        private ScriptExpressionNode? preserved;

        public OperandEditor(Action changed)
        {
            kind.Items.AddRange(KindLabels.Cast<object>().ToArray());
            kind.SelectedIndex = 0;
            arithmetic.Items.Add("(on its own)");
            foreach (var one in ScriptExpressionTree.Arithmetic) arithmetic.Items.Add(one.Label);
            arithmetic.SelectedIndex = 0;
            otherKind.Items.AddRange(KindLabels.Cast<object>().ToArray());
            otherKind.SelectedIndex = 0;

            foreach (var control in Controls)
            {
                if (control is ComboBox box) box.SelectedIndexChanged += (_, _) => changed();
                else control.TextChanged += (_, _) => changed();
            }
            arithmetic.SelectedIndexChanged += (_, _) => ShowSecond();
            ShowSecond();
        }

        public Control[] Controls => new Control[]
            { kind, index, arithmetic, otherKind, otherIndex };

        public void SetVisible(bool visible)
        {
            foreach (var control in Controls) control.Visible = visible;
            if (visible) ShowSecond();
        }

        private void ShowSecond()
        {
            var combined = arithmetic.SelectedIndex > 0;
            otherKind.Visible = combined;
            otherIndex.Visible = combined;
        }

        public ScriptExpressionNode? Build()
        {
            var first = preserved ?? Operand(kind, index);
            if (arithmetic.SelectedIndex <= 0) return first;
            var step = ScriptExpressionTree.Arithmetic[arithmetic.SelectedIndex - 1];
            return new ScriptExpressionNode.Binary(
                step.SubOp, first, Operand(otherKind, otherIndex));
        }

        public bool Fill(ScriptExpressionNode node)
        {
            if (node is ScriptExpressionNode.Binary binary)
            {
                var step = ScriptExpressionTree.Arithmetic
                    .Select((value, at) => (value.SubOp, at))
                    .FirstOrDefault(value => value.SubOp == binary.SubOp, (SubOp: -1, at: -1));
                if (step.at < 0) return false;
                if (!Single(binary.Left)) return false;
                arithmetic.SelectedIndex = step.at + 1;
                return Second(binary.Right);
            }
            arithmetic.SelectedIndex = 0;
            return Single(node);
        }

        private bool Single(ScriptExpressionNode node)
        {
            if (node is not ScriptExpressionNode.Operand operand) return false;
            if (operand.Kind == ScriptOperandKind.Instruction)
            {
                // Shown by name, not offered for editing: this does not know how to
                // build one. It travels through untouched.
                preserved = operand;
                kind.Items.Add(operand.Label ?? "instruction result");
                kind.SelectedIndex = kind.Items.Count - 1;
                kind.Enabled = false;
                index.Visible = false;
                return true;
            }
            kind.SelectedIndex = (int)operand.Kind;
            index.Value = operand.Value;
            return true;
        }

        private bool Second(ScriptExpressionNode node)
        {
            if (node is not ScriptExpressionNode.Operand operand
                || operand.Kind == ScriptOperandKind.Instruction)
            {
                return false;
            }
            otherKind.SelectedIndex = (int)operand.Kind;
            otherIndex.Value = operand.Value;
            return true;
        }

        private static ScriptExpressionNode.Operand Operand(ComboBox which, NumericUpDown value)
            => new((ScriptOperandKind)Math.Clamp(which.SelectedIndex, 0, 3), (int)value.Value);

        private static ComboBox Choice(int width) => new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = width,
        };

        private static NumericUpDown Number() => new()
        {
            Width = 88,
            Minimum = int.MinValue,
            Maximum = int.MaxValue,
        };
    }

    /// <summary>One test, and how it joins the one before it.</summary>
    private sealed class TestRow
    {
        private readonly ComboBox join = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70,
        };

        private readonly ComboBox test = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
        };

        private readonly OperandEditor left;
        private readonly OperandEditor right;
        private readonly Button remove = new() { Text = "×", Width = 28, AutoSize = false };

        public TestRow(bool joined, Action changed, Action<TestRow> removed)
        {
            left = new OperandEditor(changed);
            right = new OperandEditor(changed);
            foreach (var one in ScriptExpressionTree.Joins) join.Items.Add(one.Label);
            join.SelectedIndex = 0;
            join.Visible = joined;
            remove.Visible = joined;
            foreach (var one in ScriptExpressionTree.Tests) test.Items.Add(one.Label);
            test.SelectedIndex = 0;

            Panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Panel.Controls.Add(join);
            foreach (var control in left.Controls) Panel.Controls.Add(control);
            Panel.Controls.Add(test);
            foreach (var control in right.Controls) Panel.Controls.Add(control);
            Panel.Controls.Add(remove);

            test.SelectedIndexChanged += (_, _) =>
            {
                ShowRight();
                changed();
            };
            join.SelectedIndexChanged += (_, _) => changed();
            remove.Click += (_, _) => removed(this);
            ShowRight();
        }

        public FlowLayoutPanel Panel { get; }

        public int JoinSubOp => ScriptExpressionTree.Joins[Math.Max(0, join.SelectedIndex)].SubOp;

        public void SetJoin(int subOp)
        {
            var at = ScriptExpressionTree.Joins
                .Select((value, index) => (value.SubOp, index))
                .FirstOrDefault(value => value.SubOp == subOp, (SubOp: -1, index: 0));
            join.SelectedIndex = at.index;
        }

        /// <summary>A test that takes no value has no field for one.</summary>
        private void ShowRight() => right.SetVisible(Chosen().TakesValue);

        private (int SubOp, string Label, bool TakesValue) Chosen()
            => ScriptExpressionTree.Tests[Math.Clamp(
                test.SelectedIndex, 0, ScriptExpressionTree.Tests.Count - 1)];

        public ScriptExpressionNode? Build()
        {
            if (left.Build() is not { } first) return null;
            var chosen = Chosen();
            if (!chosen.TakesValue)
            {
                return chosen.SubOp == ScriptExpressionTree.NoOperator
                    ? first
                    : new ScriptExpressionNode.Unary(chosen.SubOp, first);
            }
            return right.Build() is { } second
                ? new ScriptExpressionNode.Binary(chosen.SubOp, first, second)
                : null;
        }

        public bool Fill(ScriptExpressionNode node)
        {
            switch (node)
            {
                case ScriptExpressionNode.Unary unary when unary.SubOp == 0x08:
                    Select(0x08);
                    return left.Fill(unary.Inner);

                case ScriptExpressionNode.Binary binary
                    when ScriptExpressionTree.Tests.Any(value => value.SubOp == binary.SubOp):
                    Select(binary.SubOp);
                    return left.Fill(binary.Left) && right.Fill(binary.Right);

                default:
                    // No operator: the value is the condition, which is how a flag is
                    // tested. The operand itself may still be an arithmetic step.
                    Select(ScriptExpressionTree.NoOperator);
                    return left.Fill(node);
            }
        }

        private void Select(int subOp)
        {
            var at = ScriptExpressionTree.Tests
                .Select((value, index) => (value.SubOp, index))
                .FirstOrDefault(value => value.SubOp == subOp, (SubOp: -1, index: 0));
            test.SelectedIndex = at.index;
            ShowRight();
        }
    }
}
