using System.Drawing.Drawing2D;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Constrained visual-programming canvas for a script function. Positions are derived from
/// the real control-flow graph and snapped to a fixed grid; drag/drop requests a native
/// instruction reorder instead of storing presentation-only coordinates.
/// </summary>
internal sealed class ScriptFlowPanel : Panel
{
    private const int Grid = 16;
    private const int NodeWidth = 340;
    private const int ColumnGap = 96;
    private const int RowGap = 12;
    private const int CanvasPadding = 24;

    private readonly Dictionary<int, Control> nodes = new();
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

    public int? SelectedInstruction => selectedInstruction;

    public void SetGraph(IReadOnlyDictionary<int, Control> instructionNodes, DecompiledFunction function)
    {
        nodes.Clear();
        foreach (var pair in instructionNodes) nodes.Add(pair.Key, pair.Value);
        foreach (var instruction in function.Instructions.Where(value => value.Opcode == 5))
        {
            var anchor = new FlowAnchorNode(instruction.Index == 0);
            nodes.Add(instruction.Index, anchor);
            Controls.Add(anchor);
        }
        if (function.Instructions.Count > 0 && function.Instructions[0].Opcode != 5)
        {
            var start = new FlowAnchorNode(isStart: true);
            nodes.Add(-1, start);
            Controls.Add(start);
        }
        edges = BuildEdges(function).ToArray();
        selectedEdge = null;
        LayoutGraph(function);
        Invalidate();
    }

    public void SelectInstruction(int instruction)
    {
        selectedEdge = null;
        if (selectedInstruction == instruction) return;
        if (selectedInstruction is { } previous && nodes.TryGetValue(previous, out var previousNode))
            SetNodeSelected(previousNode, false);
        selectedInstruction = instruction;
        if (nodes.TryGetValue(instruction, out var node)) SetNodeSelected(node, true);
        Invalidate();
    }

    public void ClearSelection()
    {
        if (selectedInstruction is { } previous && nodes.TryGetValue(previous, out var previousNode))
            SetNodeSelected(previousNode, false);
        selectedInstruction = null;
        selectedEdge = null;
        Invalidate();
    }

    public void BeginInstructionDrag(Control source, int instruction)
    {
        SelectInstruction(instruction);
        source.DoDragDrop(new InstructionDrag(instruction), DragDropEffects.Move);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var edge in edges) DrawEdge(eventArgs.Graphics, edge);
        if (dropTarget is { } target && nodes.TryGetValue(target.AnchorInstruction, out var anchor))
        {
            var y = target.Before ? anchor.Top - 4 : anchor.Bottom + 4;
            using var marker = new Pen(Color.DeepSkyBlue, 4f);
            eventArgs.Graphics.DrawLine(marker, anchor.Left, y, anchor.Right, y);
        }
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || GetChildAtPoint(eventArgs.Location) is not null) return;
        Focus();
        if (HitTestJumpEdge(eventArgs.Location) is { } edge)
        {
            selectedEdge = edge;
            Invalidate();
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
        if (eventArgs.Button != MouseButtons.Left || HitTestJumpEdge(eventArgs.Location) is not { } edge) return;
        selectedEdge = edge;
        Invalidate();
        JumpEditRequested?.Invoke(edge.Source, edge.ArgumentIndex);
    }

    private void LayoutGraph(DecompiledFunction function)
    {
        if (function.Instructions.Count == 0)
        {
            AutoScrollMinSize = Size.Empty;
            return;
        }

        var depths = CalculateDepths(function, edges);
        var lanes = AssignLanes(function, edges, depths);
        var rowHeights = new int[depths.Max() + 1];
        foreach (var instruction in function.Instructions)
        {
            var node = nodes[instruction.Index];
            if (node is not FlowAnchorNode)
            {
                node.AutoSize = false;
                var preferred = node.GetPreferredSize(new Size(NodeWidth, 0));
                node.Size = new Size(NodeWidth, Math.Max(44, preferred.Height));
            }
            rowHeights[depths[instruction.Index]] = Math.Max(
                rowHeights[depths[instruction.Index]], node.Height);
        }

        var rowTops = new int[rowHeights.Length];
        var currentY = nodes.TryGetValue(-1, out var standaloneStart)
            ? CanvasPadding + standaloneStart.Height + RowGap
            : CanvasPadding;
        for (var row = 0; row < rowHeights.Length; row++)
        {
            rowTops[row] = Snap(currentY);
            currentY = rowTops[row] + rowHeights[row] + RowGap;
        }

        var maximumLane = 0;
        foreach (var instruction in function.Instructions)
        {
            var lane = lanes[instruction.Index];
            maximumLane = Math.Max(maximumLane, lane);
            var node = nodes[instruction.Index];
            var columnLeft = Snap(CanvasPadding + lane * (NodeWidth + ColumnGap));
            node.Location = new Point(
                node is FlowAnchorNode ? columnLeft + (NodeWidth - node.Width) / 2 : columnLeft,
                rowTops[depths[instruction.Index]]);
        }
        if (nodes.TryGetValue(-1, out var startNode))
            startNode.Location = new Point(CanvasPadding + (NodeWidth - startNode.Width) / 2, CanvasPadding);
        AutoScrollMinSize = new Size(
            CanvasPadding * 2 + (maximumLane + 1) * NodeWidth + maximumLane * ColumnGap,
            currentY + CanvasPadding);
    }

    private static IReadOnlyList<int> CalculateDepths(DecompiledFunction function, IReadOnlyList<GraphEdge> graphEdges)
    {
        var depths = Enumerable.Repeat(-1, function.Instructions.Count).ToArray();
        depths[0] = 0;
        for (var index = 0; index < depths.Length; index++)
        {
            if (depths[index] < 0) depths[index] = index == 0 ? 0 : depths[index - 1] + 1;
            foreach (var edge in graphEdges.Where(value => value.Source == index && value.Target > index))
                depths[edge.Target] = Math.Max(depths[edge.Target], depths[index] + 1);
        }
        return depths;
    }

    private static IReadOnlyList<int> AssignLanes(
        DecompiledFunction function,
        IReadOnlyList<GraphEdge> graphEdges,
        IReadOnlyList<int> depths)
    {
        // A forward conditional edge delimits the fall-through branch between its source
        // and destination. Nested branch regions naturally occupy additional columns and
        // return to their parent column at the exact join instruction.
        var branchRegions = graphEdges
            .GroupBy(edge => edge.Source)
            .Where(group => group.Any(edge => edge.Kind == EdgeKind.Fallthrough))
            .SelectMany(group => group
                .Where(edge => edge.Kind == EdgeKind.Jump && edge.Target > edge.Source + 1)
                .Select(edge => new BranchRegion(edge.Source, edge.Target)))
            .ToArray();
        var lanes = new int[function.Instructions.Count];
        var occupied = new Dictionary<int, HashSet<int>>();
        for (var index = 0; index < lanes.Length; index++)
        {
            var lane = branchRegions.Count(region => region.Source < index && index < region.Join);
            while (occupied.TryGetValue(lane, out var occupiedRows) && occupiedRows.Contains(depths[index])) lane++;
            lanes[index] = lane;
            if (!occupied.TryGetValue(lane, out var rows)) occupied.Add(lane, rows = new HashSet<int>());
            rows.Add(depths[index]);
        }
        return lanes;
    }

    private static IEnumerable<GraphEdge> BuildEdges(DecompiledFunction function)
    {
        if (function.Instructions.Count > 0 && function.Instructions[0].Opcode != 5)
            yield return new GraphEdge(-1, 0, EdgeKind.Fallthrough, -1, -1, string.Empty);
        for (var index = 0; index < function.Instructions.Count; index++)
        {
            var instruction = function.Instructions[index];
            var condition = instruction.Opcode == 5 ? FormatCondition(instruction) : string.Empty;
            var localTargets = instruction.Jumps
                .Where(value => value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0)
                .Select(value => value.TargetInstructionIndex)
                .Distinct()
                .ToArray();
            foreach (var target in localTargets)
            {
                var jump = instruction.Jumps.First(value => value.TargetInstructionIndex == target
                    && value.TargetFunctionIndex == function.Index);
                yield return new GraphEdge(
                    index, target, EdgeKind.Jump, jump.ArgumentIndex, instruction.Opcode,
                    instruction.Opcode == 5 ? $"FALSE · {condition}" : string.Empty);
            }

            // OP1 is RETURN and OP3 is the verified unconditional JMP in the instruction registry.
            if (index + 1 < function.Instructions.Count && instruction.Opcode is not (1 or 3))
            {
                var conditional = instruction.Opcode == 5;
                var argumentIndex = conditional
                    ? instruction.Jumps.FirstOrDefault()?.ArgumentIndex ?? -1
                    : -1;
                yield return new GraphEdge(index, index + 1,
                    EdgeKind.Fallthrough,
                    argumentIndex, instruction.Opcode, conditional ? $"TRUE · {condition}" : string.Empty);
            }
        }
    }

    private void DrawEdge(Graphics graphics, GraphEdge edge)
    {
        if (!nodes.TryGetValue(edge.Source, out var source) || !nodes.TryGetValue(edge.Target, out var target)) return;
        var editable = edge.ArgumentIndex >= 0;
        var isSelected = editable && edge == selectedEdge;
        var color = isSelected ? Color.DeepSkyBlue
            : editable ? Color.FromArgb(240, 157, 55) : Color.FromArgb(115, 135, 155);
        using var pen = new Pen(color, isSelected ? 4f : editable ? 2.4f : 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var path = GetEdgePath(source, target);
        var end = path[^1];
        graphics.DrawLines(pen, path);
        DrawArrowHead(graphics, color, path[^2], end);
        if (editable) DrawEdgeLabel(graphics, edge, path, color);
    }

    private static Point[] GetEdgePath(Control source, Control target)
    {
        if (source.Left == target.Left && target.Top > source.Top)
            return new[]
            {
                new Point(source.Left + source.Width / 2, source.Bottom),
                new Point(target.Left + target.Width / 2, target.Top),
            };
        var start = new Point(source.Right, source.Top + Math.Min(24, source.Height / 2));
        var end = new Point(target.Left, target.Top + Math.Min(24, target.Height / 2));
        var bendX = Snap((start.X + end.X) / 2);
        if (target.Top < source.Top && target.Left <= source.Left)
            bendX = Math.Max(source.Right, target.Right) + Grid * 2;
        return new[] { start, new Point(bendX, start.Y), new Point(bendX, end.Y), end };
    }

    private static void DrawEdgeLabel(Graphics graphics, GraphEdge edge, IReadOnlyList<Point> path, Color color)
    {
        var text = edge.Label;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 42) text = text[..39] + "...";
        var bounds = GetEdgeLabelBounds(text, path);
        using var background = new SolidBrush(Color.FromArgb(225, 30, 30, 34));
        graphics.FillRectangle(background, bounds);
        TextRenderer.DrawText(graphics, text, SystemFonts.MessageBoxFont, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private GraphEdge? HitTestJumpEdge(Point point)
    {
        foreach (var edge in edges.Where(value => value.ArgumentIndex >= 0).Reverse())
        {
            if (!nodes.TryGetValue(edge.Source, out var source) || !nodes.TryGetValue(edge.Target, out var target))
                continue;
            var path = GetEdgePath(source, target);
            if (edge.Label.Length > 0)
            {
                var label = edge.Label.Length > 42 ? edge.Label[..39] + "..." : edge.Label;
                if (GetEdgeLabelBounds(label, path).Contains(point)) return edge;
            }
            for (var index = 1; index < path.Length; index++)
                if (DistanceToSegment(point, path[index - 1], path[index]) <= 7f) return edge;
        }
        return null;
    }

    private static Rectangle GetEdgeLabelBounds(string text, IReadOnlyList<Point> path)
    {
        var anchor = path.Count > 2 ? path[1]
            : new Point((path[0].X + path[^1].X) / 2, (path[0].Y + path[^1].Y) / 2);
        var size = TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont);
        return new Rectangle(anchor.X + 5, anchor.Y - size.Height / 2, size.Width + 6, size.Height);
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

    private void HandleDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetData(typeof(InstructionDrag)) is not InstructionDrag) return;
        eventArgs.Effect = DragDropEffects.Move;
        var point = PointToClient(new Point(eventArgs.X, eventArgs.Y));
        var nearest = nodes.OrderBy(pair => DistanceSquared(pair.Value.Bounds, point)).FirstOrDefault();
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

    private static int Snap(int value) => (int)Math.Ceiling(value / (double)Grid) * Grid;

    private static void DrawArrowHead(Graphics graphics, Color color, Point from, Point to)
    {
        var direction = new PointF(to.X - from.X, to.Y - from.Y);
        var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length < 0.001f) return;
        direction = new PointF(direction.X / length, direction.Y / length);
        var perpendicular = new PointF(-direction.Y, direction.X);
        var basePoint = new PointF(to.X - direction.X * 8f, to.Y - direction.Y * 8f);
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, new[]
        {
            new PointF(to.X, to.Y),
            new PointF(basePoint.X + perpendicular.X * 4f, basePoint.Y + perpendicular.Y * 4f),
            new PointF(basePoint.X - perpendicular.X * 4f, basePoint.Y - perpendicular.Y * 4f),
        });
    }

    private static void SetNodeSelected(Control node, bool selected)
    {
        node.Padding = new Padding(2);
        node.BackColor = selected ? Color.FromArgb(72, 82, 101) : Color.FromArgb(45, 46, 52);
    }

    private enum EdgeKind { Fallthrough, Jump }
    private sealed record GraphEdge(
        int Source, int Target, EdgeKind Kind, int ArgumentIndex, int Opcode, string Label);
    private sealed record BranchRegion(int Source, int Join);
    private sealed record InstructionDrag(int Instruction);
    private sealed record DropTarget(int AnchorInstruction, bool Before);

    private sealed class FlowAnchorNode : Control
    {
        private readonly bool isStart;

        public FlowAnchorNode(bool isStart)
        {
            this.isStart = isStart;
            Size = isStart ? new Size(86, 54) : new Size(18, 18);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            using var fill = new SolidBrush(isStart ? Color.FromArgb(50, 125, 92) : Color.FromArgb(240, 157, 55));
            using var border = new Pen(isStart ? Color.FromArgb(125, 225, 170) : Color.Gold, 2f);
            eventArgs.Graphics.FillEllipse(fill, bounds);
            eventArgs.Graphics.DrawEllipse(border, bounds);
            if (isStart)
                TextRenderer.DrawText(eventArgs.Graphics, "Init", new Font("Segoe UI", 9f, FontStyle.Bold), bounds,
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
