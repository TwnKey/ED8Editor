using System.Drawing.Drawing2D;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Flow canvas for a script function.
///
/// Readability rules:
///  - unconditional jumps (op3) have no block: the arrow stands for them (edges are
///    contracted through them);
///  - a branch point (op5) is a small pivot node and its two branches are laid out
///    symmetrically on either side of it, forming a Y;
///  - arrows are coloured by nature (conditional true/false vs unconditional);
///  - an "active path" is maintained: blocks and arrows that are not walked to reach
///    the current block are dimmed, yet remain readable for analysis.
///
/// Clicking any thread of a branch selects that branch: the active path is rerouted so
/// that it goes through the clicked edge, and downstream branches update accordingly.
/// </summary>
internal sealed class ScriptFlowPanel : Panel
{
    private const int Grid = 16;
    private const int NodeWidth = 340;
    private const int ColumnGap = 72;
    private const int RowGap = 14;
    private const int CanvasPadding = 24;
    private const int StartKey = -1;

    private static readonly Color EdgeSequential = Color.FromArgb(120, 138, 158);
    private static readonly Color EdgeTrue = Color.FromArgb(96, 190, 120);
    private static readonly Color EdgeFalse = Color.FromArgb(226, 132, 62);
    private static readonly Color EdgeUnconditional = Color.FromArgb(126, 152, 224);
    private static readonly Color EdgeSelected = Color.DeepSkyBlue;
    private static readonly Color NodeNormal = Color.FromArgb(45, 46, 52);
    private static readonly Color NodeSelected = Color.FromArgb(72, 82, 101);
    private static readonly Color NodeDimmed = Color.FromArgb(38, 38, 42);

    private readonly Dictionary<int, Control> nodes = new();
    private readonly Dictionary<Control, Color> originalForeground = new();
    private readonly Dictionary<int, int> branchChoice = new();
    private readonly HashSet<int> activePath = new();
    private readonly HashSet<int> hiddenInstructions = new();

    private DecompiledFunction? currentFunction;
    private IReadOnlyList<GraphEdge> edges = Array.Empty<GraphEdge>();
    private int? selectedInstruction;
    private GraphEdge? selectedEdge;
    private DropTarget? dropTarget;
    private Point? panOrigin;
    private Point panScrollOrigin;

    public ScriptFlowPanel()
    {
        AutoScroll = true;
        DoubleBuffered = true;
        AllowDrop = true;
        TabStop = true;
        DragOver += HandleDragOver;
        DragLeave += (_, _) => ClearDropTarget();
        DragDrop += HandleDragDrop;
    }

    public event Action<int, int>? MoveRequested;

    public event Action<int, int>? JumpEditRequested;

    /// <summary>The active path changed: branch points and the branch taken at each.</summary>
    public event Action<IReadOnlyList<BranchDecision>>? ActivePathChanged;

    public int? SelectedInstruction => selectedInstruction;

    /// <summary>Instructions actually walked to reach the current point.</summary>
    public IReadOnlyCollection<int> ActivePath => activePath;

    public void SetGraph(IReadOnlyDictionary<int, Control> instructionNodes, DecompiledFunction function)
    {
        currentFunction = function;
        nodes.Clear();
        originalForeground.Clear();
        branchChoice.Clear();
        hiddenInstructions.Clear();

        // Local unconditional jumps get no block: the arrow replaces them.
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == 3 && LocalJump(function, instruction) is not null)
                hiddenInstructions.Add(instruction.Index);
        }

        foreach (var pair in instructionNodes)
        {
            if (hiddenInstructions.Contains(pair.Key)) continue;
            nodes[pair.Key] = pair.Value;
        }
        foreach (var instruction in function.Instructions.Where(value => value.Opcode == 5))
        {
            var anchor = new FlowAnchorNode(isStart: false);
            nodes[instruction.Index] = anchor;
            Controls.Add(anchor);
        }
        if (function.Instructions.Count > 0 && function.Instructions[0].Opcode != 5)
        {
            var start = new FlowAnchorNode(isStart: true);
            nodes[StartKey] = start;
            Controls.Add(start);
        }

        edges = BuildEdges(function).ToArray();
        selectedEdge = null;
        LayoutGraph(function);
        RecomputeActivePath();
        Invalidate();
    }

    public void SelectInstruction(int instruction)
    {
        selectedEdge = null;
        if (selectedInstruction != instruction)
        {
            selectedInstruction = instruction;
            RefreshNodeVisuals();
        }
        Invalidate();
    }

    public void ClearSelection()
    {
        selectedInstruction = null;
        selectedEdge = null;
        RefreshNodeVisuals();
        Invalidate();
    }

    public void BeginInstructionDrag(Control source, int instruction)
    {
        SelectInstruction(instruction);
        source.DoDragDrop(new InstructionDrag(instruction), DragDropEffects.Move);
    }

    // ---------------------------------------------------------------- active path
    /// <summary>Pins the branch taken at a branch point, then recomputes the path.</summary>
    public void ChooseBranch(int forkInstruction, int successor)
    {
        branchChoice[forkInstruction] = successor;
        RecomputeActivePath();
        Invalidate();
    }

    /// <summary>Flips a branch point of the active path onto its other branch.</summary>
    public void ToggleBranch(int forkInstruction)
    {
        var successors = BuildSuccessorMap();
        if (!successors.TryGetValue(forkInstruction, out var outgoing) || outgoing.Count < 2) return;
        var current = branchChoice.TryGetValue(forkInstruction, out var value) ? value : outgoing[0].To;
        var other = outgoing.FirstOrDefault(edge => edge.To != current);
        if (other is null) return;
        ChooseBranch(forkInstruction, other.To);
    }

    public void ResetBranchChoices()
    {
        branchChoice.Clear();
        RecomputeActivePath();
        Invalidate();
    }

    /// <summary>
    /// Reroutes the active path so that it goes through the given edge. Any thread of a
    /// branch can therefore be clicked, not only the one leaving the branch point.
    /// </summary>
    private void RouteThrough(GraphEdge edge)
    {
        var successors = BuildSuccessorMap();
        var route = new List<(int Fork, int Successor)>();
        var entry = nodes.ContainsKey(StartKey) ? StartKey : 0;
        if (TryFindRoute(entry, edge.From, successors, new HashSet<int>(), route))
        {
            foreach (var step in route) branchChoice[step.Fork] = step.Successor;
        }
        if (successors.TryGetValue(edge.From, out var outgoing) && outgoing.Count > 1)
            branchChoice[edge.From] = edge.To;
        RecomputeActivePath();
        Invalidate();
    }

    private static bool TryFindRoute(
        int from,
        int target,
        Dictionary<int, List<GraphEdge>> successors,
        HashSet<int> visited,
        List<(int Fork, int Successor)> route)
    {
        if (from == target) return true;
        if (!visited.Add(from)) return false;
        if (!successors.TryGetValue(from, out var outgoing)) return false;
        foreach (var edge in outgoing)
        {
            var mark = route.Count;
            if (outgoing.Count > 1) route.Add((from, edge.To));
            if (TryFindRoute(edge.To, target, successors, visited, route)) return true;
            route.RemoveRange(mark, route.Count - mark);
        }
        return false;
    }

    private void RecomputeActivePath()
    {
        activePath.Clear();
        var decisions = new List<BranchDecision>();
        if (currentFunction is null)
        {
            RefreshNodeVisuals();
            return;
        }

        var successors = BuildSuccessorMap();
        var node = nodes.ContainsKey(StartKey) ? StartKey : 0;
        var guard = 0;
        var limit = nodes.Count + 8;
        while (node != int.MinValue && guard++ < limit)
        {
            if (!activePath.Add(node) && node != StartKey) break;   // loop: stop here
            if (!successors.TryGetValue(node, out var outgoing) || outgoing.Count == 0) break;
            if (outgoing.Count == 1)
            {
                node = outgoing[0].To;
                continue;
            }

            var chosen = branchChoice.TryGetValue(node, out var wanted)
                && outgoing.Any(value => value.To == wanted)
                ? outgoing.First(value => value.To == wanted)
                : outgoing[0];
            decisions.Add(new BranchDecision(node, chosen.To, chosen.Kind == EdgeKind.ConditionalTrue, chosen.Label));
            node = chosen.To;
        }

        RefreshNodeVisuals();
        ActivePathChanged?.Invoke(decisions);
    }

    private Dictionary<int, List<GraphEdge>> BuildSuccessorMap()
    {
        var map = new Dictionary<int, List<GraphEdge>>();
        foreach (var edge in edges)
        {
            if (!map.TryGetValue(edge.From, out var list)) map.Add(edge.From, list = new List<GraphEdge>());
            list.Add(edge);
        }
        // TRUE (immediate continuation) first, for a stable default choice
        foreach (var list in map.Values) list.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));
        return map;

        static int Rank(EdgeKind kind) => kind switch
        {
            EdgeKind.ConditionalTrue => 0,
            EdgeKind.Sequential => 1,
            EdgeKind.Unconditional => 2,
            _ => 3,
        };
    }

    private bool IsActive(int node) => activePath.Count == 0 || activePath.Contains(node);

    private bool IsActive(GraphEdge edge) =>
        activePath.Count == 0 || (activePath.Contains(edge.From) && activePath.Contains(edge.To));

    private void RefreshNodeVisuals()
    {
        foreach (var pair in nodes)
        {
            var dimmed = !IsActive(pair.Key);
            var selected = selectedInstruction == pair.Key;
            if (pair.Value is FlowAnchorNode anchor)
            {
                anchor.Dimmed = dimmed;
                anchor.Invalidate();
                continue;
            }
            pair.Value.Padding = new Padding(2);
            pair.Value.BackColor = selected ? NodeSelected : dimmed ? NodeDimmed : NodeNormal;
            ApplyForegroundDim(pair.Value, dimmed);
        }
    }

    /// <summary>Dims a block's text without making it unreadable (analysis stays possible).</summary>
    private void ApplyForegroundDim(Control root, bool dimmed)
    {
        foreach (Control child in root.Controls)
        {
            if (!originalForeground.TryGetValue(child, out var original))
                originalForeground[child] = original = child.ForeColor;
            child.ForeColor = dimmed ? Blend(original, NodeDimmed, 0.55f) : original;
            if (child.Controls.Count > 0) ApplyForegroundDim(child, dimmed);
        }
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    // ---------------------------------------------------------------- painting
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var edge in edges.Where(value => !IsActive(value))) DrawEdge(eventArgs.Graphics, edge);
        foreach (var edge in edges.Where(IsActive)) DrawEdge(eventArgs.Graphics, edge);
        if (dropTarget is { } target && nodes.TryGetValue(target.AnchorInstruction, out var anchor))
        {
            var y = target.Before ? anchor.Top - 4 : anchor.Bottom + 4;
            using var marker = new Pen(Color.DeepSkyBlue, 4f);
            eventArgs.Graphics.DrawLine(marker, anchor.Left, y, anchor.Right, y);
        }
    }

    private void DrawEdge(Graphics graphics, GraphEdge edge)
    {
        if (!nodes.TryGetValue(edge.From, out var source) || !nodes.TryGetValue(edge.To, out var target)) return;
        var isSelected = edge == selectedEdge;
        var dimmed = !IsActive(edge);
        var baseColor = edge.Kind switch
        {
            EdgeKind.ConditionalTrue => EdgeTrue,
            EdgeKind.ConditionalFalse => EdgeFalse,
            EdgeKind.Unconditional => EdgeUnconditional,
            _ => EdgeSequential,
        };
        var color = isSelected ? EdgeSelected : dimmed ? Blend(baseColor, BackColor, 0.68f) : baseColor;
        var width = isSelected ? 5f : edge.Kind == EdgeKind.Sequential ? 2.6f : 3.6f;
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (edge.Kind == EdgeKind.Unconditional) pen.DashStyle = DashStyle.Dash;
        var path = GetEdgePath(source, target);
        graphics.DrawLines(pen, path);
        DrawArrowHead(graphics, color, path[^2], path[^1], width);
        if (!dimmed && edge.Label.Length > 0) DrawEdgeLabel(graphics, edge, path, color);
    }

    private static Point[] GetEdgePath(Control source, Control target)
    {
        var sourceCenter = source.Left + source.Width / 2;
        var targetCenter = target.Left + target.Width / 2;
        if (target.Top >= source.Bottom)
        {
            if (Math.Abs(sourceCenter - targetCenter) <= Grid)
                return new[] { new Point(sourceCenter, source.Bottom), new Point(targetCenter, target.Top) };
            var midY = (source.Bottom + target.Top) / 2;
            return new[]
            {
                new Point(sourceCenter, source.Bottom),
                new Point(sourceCenter, midY),
                new Point(targetCenter, midY),
                new Point(targetCenter, target.Top),
            };
        }

        // upward edge (loop): route around the side
        var side = Math.Max(source.Right, target.Right) + Grid * 2;
        return new[]
        {
            new Point(source.Right, source.Top + Math.Min(24, source.Height / 2)),
            new Point(side, source.Top + Math.Min(24, source.Height / 2)),
            new Point(side, target.Top + Math.Min(24, target.Height / 2)),
            new Point(target.Right, target.Top + Math.Min(24, target.Height / 2)),
        };
    }

    private static void DrawEdgeLabel(Graphics graphics, GraphEdge edge, IReadOnlyList<Point> path, Color color)
    {
        var text = edge.Label;
        if (text.Length > 42) text = text[..39] + "...";
        var bounds = GetEdgeLabelBounds(text, path);
        using var background = new SolidBrush(Color.FromArgb(225, 30, 30, 34));
        graphics.FillRectangle(background, bounds);
        TextRenderer.DrawText(graphics, text, SystemFonts.MessageBoxFont, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static Rectangle GetEdgeLabelBounds(string text, IReadOnlyList<Point> path)
    {
        var anchor = path.Count > 2 ? path[1]
            : new Point((path[0].X + path[^1].X) / 2, (path[0].Y + path[^1].Y) / 2);
        var size = TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont);
        return new Rectangle(anchor.X + 5, anchor.Y - size.Height / 2, size.Width + 6, size.Height);
    }

    private static void DrawArrowHead(Graphics graphics, Color color, Point from, Point to, float width)
    {
        var direction = new PointF(to.X - from.X, to.Y - from.Y);
        var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length < 0.001f) return;
        direction = new PointF(direction.X / length, direction.Y / length);
        var perpendicular = new PointF(-direction.Y, direction.X);
        var head = Math.Max(9f, width * 3f);
        var half = Math.Max(4.5f, width * 1.5f);
        var basePoint = new PointF(to.X - direction.X * head, to.Y - direction.Y * head);
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, new[]
        {
            new PointF(to.X, to.Y),
            new PointF(basePoint.X + perpendicular.X * half, basePoint.Y + perpendicular.Y * half),
            new PointF(basePoint.X - perpendicular.X * half, basePoint.Y - perpendicular.Y * half),
        });
    }

    // ---------------------------------------------------------------- mouse
    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || GetChildAtPoint(eventArgs.Location) is not null) return;
        Focus();
        if (HitTestEdge(eventArgs.Location) is { } edge)
        {
            selectedEdge = edge;
            RouteThrough(edge);   // any thread of a branch selects that branch
            return;
        }
        selectedEdge = null;
        panOrigin = eventArgs.Location;
        panScrollOrigin = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
        Capture = true;
        Cursor = Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (panOrigin is not { } origin || eventArgs.Button != MouseButtons.Left) return;
        AutoScrollPosition = new Point(
            Math.Max(0, panScrollOrigin.X - (eventArgs.X - origin.X)),
            Math.Max(0, panScrollOrigin.Y - (eventArgs.Y - origin.Y)));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || panOrigin is null) return;
        panOrigin = null;
        Capture = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs eventArgs)
    {
        base.OnMouseDoubleClick(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || HitTestEdge(eventArgs.Location) is not { } edge) return;
        if (edge.ArgumentIndex < 0 || edge.Owner < 0) return;
        selectedEdge = edge;
        Invalidate();
        JumpEditRequested?.Invoke(edge.Owner, edge.ArgumentIndex);
    }

    private GraphEdge? HitTestEdge(Point point)
    {
        foreach (var edge in edges.Reverse())
        {
            if (!nodes.TryGetValue(edge.From, out var source) || !nodes.TryGetValue(edge.To, out var target)) continue;
            var path = GetEdgePath(source, target);
            if (edge.Label.Length > 0)
            {
                var label = edge.Label.Length > 42 ? edge.Label[..39] + "..." : edge.Label;
                if (GetEdgeLabelBounds(label, path).Contains(point)) return edge;
            }
            for (var index = 1; index < path.Length; index++)
                if (DistanceToSegment(point, path[index - 1], path[index]) <= 8f) return edge;
        }
        return null;
    }

    private static float DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (dx == 0 && dy == 0) return VectorDistance(point.X - start.X, point.Y - start.Y);
        var amount = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy)
            / (float)(dx * dx + dy * dy), 0f, 1f);
        return VectorDistance(point.X - (start.X + amount * dx), point.Y - (start.Y + amount * dy));
    }

    private static float VectorDistance(float x, float y) => MathF.Sqrt(x * x + y * y);

    // ---------------------------------------------------------------- graph
    private static JumpTarget? LocalJump(DecompiledFunction function, DecompiledInstruction instruction) =>
        instruction.Jumps.FirstOrDefault(value =>
            value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0);

    /// <summary>Follows hidden unconditional jumps until an actually drawn node.</summary>
    private int Resolve(DecompiledFunction function, int index)
    {
        var guard = 0;
        while (index >= 0 && index < function.Instructions.Count
            && hiddenInstructions.Contains(index) && guard++ < 64)
        {
            var jump = LocalJump(function, function.Instructions[index]);
            if (jump is null) return index;
            index = jump.TargetInstructionIndex;
        }
        return index;
    }

    private IEnumerable<GraphEdge> BuildEdges(DecompiledFunction function)
    {
        if (function.Instructions.Count == 0) yield break;
        if (nodes.ContainsKey(StartKey))
        {
            var first = Resolve(function, 0);
            if (nodes.ContainsKey(first))
                yield return new GraphEdge(StartKey, first, EdgeKind.Sequential, -1, -1, -1, string.Empty);
        }

        for (var index = 0; index < function.Instructions.Count; index++)
        {
            if (hiddenInstructions.Contains(index)) continue;   // stands for the arrow
            var instruction = function.Instructions[index];
            var conditional = instruction.Opcode == 5;
            var condition = conditional ? FormatCondition(instruction) : string.Empty;

            foreach (var jump in instruction.Jumps.Where(value =>
                value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0))
            {
                var target = Resolve(function, jump.TargetInstructionIndex);
                if (!nodes.ContainsKey(target)) continue;
                yield return new GraphEdge(index, target,
                    conditional ? EdgeKind.ConditionalFalse : EdgeKind.Unconditional,
                    index, jump.ArgumentIndex,
                    conditional ? index : -1,
                    conditional ? $"FALSE · {condition}" : string.Empty);
            }

            // natural continuation; OP1 = RETURN, OP3 = unconditional jump (hidden)
            if (instruction.Opcode is 1 or 3) continue;
            var next = Resolve(function, index + 1);
            if (next >= function.Instructions.Count || !nodes.ContainsKey(next)) continue;
            var argument = conditional ? instruction.Jumps.FirstOrDefault()?.ArgumentIndex ?? -1 : -1;
            yield return new GraphEdge(index, next,
                conditional ? EdgeKind.ConditionalTrue : EdgeKind.Sequential,
                conditional ? index : -1, argument,
                conditional ? index : -1,
                conditional ? $"TRUE · {condition}" : string.Empty);
        }
    }

    private static string FormatCondition(DecompiledInstruction instruction)
    {
        var expression = instruction.Arguments.FirstOrDefault(value => value.Kind == "expr")?.Expression;
        if (expression is null || expression.Count == 0) return "condition";
        var text = string.Join(" ", expression.Where(element => element.SubOp != 0x01)
            .Select(element => !string.IsNullOrEmpty(element.NestedInstruction)
                ? "call " + element.NestedInstruction
                : element.Label));
        return text.Length == 0 ? "condition" : text;
    }

    // ---------------------------------------------------------------- layout
    private void LayoutGraph(DecompiledFunction function)
    {
        if (nodes.Count == 0)
        {
            AutoScrollMinSize = Size.Empty;
            return;
        }

        foreach (var pair in nodes)
        {
            if (pair.Value is FlowAnchorNode) continue;
            pair.Value.AutoSize = false;
            var preferred = pair.Value.GetPreferredSize(new Size(NodeWidth, 0));
            pair.Value.Size = new Size(NodeWidth, Math.Max(44, preferred.Height));
        }

        var successors = BuildSuccessorMap();
        var columns = AssignColumns(successors);
        var rows = AssignRows(successors);

        var rowCount = rows.Count == 0 ? 1 : rows.Values.Max() + 1;
        var rowHeights = new int[rowCount];
        foreach (var pair in nodes)
        {
            var row = rows.TryGetValue(pair.Key, out var value) ? value : 0;
            rowHeights[row] = Math.Max(rowHeights[row], pair.Value.Height);
        }
        var rowTops = new int[rowCount];
        var currentY = CanvasPadding;
        for (var row = 0; row < rowCount; row++)
        {
            rowTops[row] = Snap(currentY);
            currentY = rowTops[row] + rowHeights[row] + RowGap;
        }

        var minColumn = columns.Count == 0 ? 0d : columns.Values.Min();
        var step = NodeWidth + ColumnGap;
        foreach (var pair in nodes)
        {
            var column = columns.TryGetValue(pair.Key, out var value) ? value : 0d;
            var row = rows.TryGetValue(pair.Key, out var r) ? r : 0;
            var centerX = (int)Math.Round(CanvasPadding + (column - minColumn) * step + NodeWidth / 2.0);
            pair.Value.Location = new Point(centerX - pair.Value.Width / 2, rowTops[row]);
        }

        // scroll bounds follow the real extent of the blocks: no empty area below
        var right = nodes.Values.Max(value => value.Right);
        var bottom = nodes.Values.Max(value => value.Bottom);
        AutoScrollMinSize = new Size(right + CanvasPadding, bottom + CanvasPadding);
    }

    /// <summary>Row = longest path from the entry (back edges are ignored).</summary>
    private Dictionary<int, int> AssignRows(Dictionary<int, List<GraphEdge>> successors)
    {
        var rows = new Dictionary<int, int>();
        foreach (var key in nodes.Keys) rows[key] = 0;
        var order = nodes.Keys.OrderBy(value => value == StartKey ? int.MinValue : value).ToArray();
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;
            foreach (var node in order)
            {
                if (!successors.TryGetValue(node, out var outgoing)) continue;
                foreach (var edge in outgoing)
                {
                    if (!rows.ContainsKey(edge.To) || IsBackEdge(node, edge.To)) continue;
                    if (rows[edge.To] >= rows[node] + 1) continue;
                    rows[edge.To] = rows[node] + 1;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return rows;

        static bool IsBackEdge(int from, int to) => to <= from && from != StartKey;
    }

    /// <summary>
    /// Columns: a branch point sits centred above its two branches, which are placed
    /// symmetrically left and right of it. Hence the Y shape.
    /// </summary>
    private Dictionary<int, double> AssignColumns(Dictionary<int, List<GraphEdge>> successors)
    {
        var columns = new Dictionary<int, double>();
        var widthCache = new Dictionary<(int Start, int Stop), double>();
        var walkLimit = nodes.Count + 8;
        var entry = nodes.ContainsKey(StartKey) ? StartKey : 0;
        PlaceRegion(entry, int.MinValue, 0d);

        // unreached nodes (dead code): stacked to the right
        var free = columns.Count == 0 ? 0d : columns.Values.Max() + 1d;
        foreach (var key in nodes.Keys.Where(value => !columns.ContainsKey(value)).OrderBy(value => value))
            columns[key] = free++;
        return columns;

        // width of a region, in columns
        double Width(int start, int stop)
        {
            if (widthCache.TryGetValue((start, stop), out var cached)) return cached;
            widthCache[(start, stop)] = 1d;   // recursion guard
            var total = 1d;
            var node = start;
            var seen = new HashSet<int>();
            var guard = 0;
            while (node != stop && node != int.MinValue && nodes.ContainsKey(node)
                && seen.Add(node) && guard++ < walkLimit)
            {
                var outgoing = Outgoing(node);
                if (outgoing.Count >= 2)
                {
                    var join = FindJoin(node, outgoing);
                    var first = Width(outgoing[0].To, join);
                    var second = Width(outgoing[1].To, join);
                    // both branches straddle the fork, so the region spans twice the widest
                    total = Math.Max(total, Math.Max(first, second) * 2d);
                    if (join == int.MinValue) break;
                    node = join;
                    continue;
                }
                node = outgoing.Count == 1 ? outgoing[0].To : int.MinValue;
            }
            widthCache[(start, stop)] = total;
            return total;
        }

        // places a region centred on the given column
        void PlaceRegion(int start, int stop, double center)
        {
            var node = start;
            var seen = new HashSet<int>();
            var guard = 0;
            while (node != stop && node != int.MinValue && nodes.ContainsKey(node)
                && seen.Add(node) && guard++ < walkLimit)
            {
                if (!columns.ContainsKey(node)) columns[node] = center;
                var outgoing = Outgoing(node);
                if (outgoing.Count >= 2)
                {
                    var join = FindJoin(node, outgoing);
                    var first = Width(outgoing[0].To, join);
                    var second = Width(outgoing[1].To, join);
                    // symmetric: one branch to the left, the other to the right of the fork
                    var offset = Math.Max(first, second) / 2d + 0.5d;
                    PlaceRegion(outgoing[0].To, join, center - offset);
                    PlaceRegion(outgoing[1].To, join, center + offset);
                    if (join == int.MinValue) break;
                    node = join;              // the join comes back onto the trunk
                    continue;
                }
                node = outgoing.Count == 1 ? outgoing[0].To : int.MinValue;
            }
        }

        List<GraphEdge> Outgoing(int node) =>
            successors.TryGetValue(node, out var list) ? list : new List<GraphEdge>();

        // where both branches meet again (int.MinValue when they never do)
        int FindJoin(int fork, List<GraphEdge> outgoing)
        {
            var first = Reachable(outgoing[0].To, fork);
            var second = Reachable(outgoing[1].To, fork);
            first.IntersectWith(second);
            return first.Count == 0 ? int.MinValue : first.Min();
        }

        HashSet<int> Reachable(int start, int fork)
        {
            var result = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            var guard = 0;
            var limit = walkLimit * 4;
            while (stack.Count > 0 && guard++ < limit)
            {
                var node = stack.Pop();
                if (node == int.MinValue || node == fork || !nodes.ContainsKey(node) || !result.Add(node)) continue;
                foreach (var edge in Outgoing(node))
                    if (edge.To > fork) stack.Push(edge.To);   // forward only
            }
            return result;
        }
    }

    private static int Snap(int value) => (int)Math.Ceiling(value / (double)Grid) * Grid;

    // ---------------------------------------------------------------- drag and drop
    private void HandleDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetData(typeof(InstructionDrag)) is not InstructionDrag) return;
        eventArgs.Effect = DragDropEffects.Move;
        var point = PointToClient(new Point(eventArgs.X, eventArgs.Y));
        var nearest = nodes.Where(pair => pair.Key >= 0)
            .OrderBy(pair => DistanceSquared(pair.Value.Bounds, point)).FirstOrDefault();
        if (nearest.Value is null) return;
        var next = new DropTarget(nearest.Key, point.Y < nearest.Value.Top + nearest.Value.Height / 2);
        if (dropTarget == next) return;
        dropTarget = next;
        Invalidate();
    }

    private void HandleDragDrop(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetData(typeof(InstructionDrag)) is not InstructionDrag drag || dropTarget is not { } target)
            return;
        var insertionSlot = target.Before ? target.AnchorInstruction : target.AnchorInstruction + 1;
        var destination = drag.Instruction < insertionSlot ? insertionSlot - 1 : insertionSlot;
        ClearDropTarget();
        if (destination != drag.Instruction) MoveRequested?.Invoke(drag.Instruction, destination);
    }

    private void ClearDropTarget()
    {
        if (dropTarget is null) return;
        dropTarget = null;
        Invalidate();
    }

    private static long DistanceSquared(Rectangle bounds, Point point)
    {
        var x = point.X < bounds.Left ? bounds.Left - point.X : point.X > bounds.Right ? point.X - bounds.Right : 0;
        var y = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y > bounds.Bottom ? point.Y - bounds.Bottom : 0;
        return (long)x * x + (long)y * y;
    }

    // ---------------------------------------------------------------- types
    private enum EdgeKind { Sequential, ConditionalTrue, ConditionalFalse, Unconditional }

    /// <summary>Graph edge. <c>Fork</c> is the branch point it leaves (-1 otherwise).</summary>
    private sealed record GraphEdge(
        int From, int To, EdgeKind Kind, int Owner, int ArgumentIndex, int Fork, string Label);

    /// <summary>Branch taken at a branch point of the active path.</summary>
    internal sealed record BranchDecision(int ForkInstruction, int TakenSuccessor, bool TakenTrue, string Label);

    private sealed record InstructionDrag(int Instruction);
    private sealed record DropTarget(int AnchorInstruction, bool Before);

    private sealed class FlowAnchorNode : Control
    {
        private readonly bool isStart;

        public FlowAnchorNode(bool isStart)
        {
            this.isStart = isStart;
            Size = isStart ? new Size(86, 54) : new Size(26, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public bool Dimmed { get; set; }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            var fillColor = isStart ? Color.FromArgb(50, 125, 92) : Color.FromArgb(240, 157, 55);
            var borderColor = isStart ? Color.FromArgb(125, 225, 170) : Color.Gold;
            if (Dimmed)
            {
                fillColor = Blend(fillColor, Color.FromArgb(38, 38, 42), 0.62f);
                borderColor = Blend(borderColor, Color.FromArgb(38, 38, 42), 0.62f);
            }
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(borderColor, 2f);
            eventArgs.Graphics.FillEllipse(fill, bounds);
            eventArgs.Graphics.DrawEllipse(border, bounds);
            if (isStart)
                TextRenderer.DrawText(eventArgs.Graphics, "Init", new Font("Segoe UI", 9f, FontStyle.Bold), bounds,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
