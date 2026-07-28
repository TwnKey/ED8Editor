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
    /// <summary>Columns a single region may claim, so a wide graph stays placeable.</summary>
    private const double MaximumRegionWidth = 256d;

    private const int HeaderHeight = 29;
    private const int BlockPadding = 7;

    private static readonly Color EdgeSequential = Color.FromArgb(120, 138, 158);
    private static readonly Color EdgeTrue = Color.FromArgb(96, 190, 120);
    private static readonly Color EdgeFalse = Color.FromArgb(226, 132, 62);
    private static readonly Color EdgeUnconditional = Color.FromArgb(126, 152, 224);
    private static readonly Color EdgeSelected = Color.DeepSkyBlue;
    private static readonly Color NodeNormal = Color.FromArgb(45, 46, 52);
    private static readonly Color NodeSelected = Color.FromArgb(72, 82, 101);
    private static readonly Color NodeDimmed = Color.FromArgb(38, 38, 42);
    private static readonly Color FlashBackground = Color.FromArgb(96, 84, 40);

    private readonly Dictionary<int, FlowNode> nodes = new();
    private readonly Dictionary<int, int> branchChoice = new();
    private readonly HashSet<int> activePath = new();
    private readonly HashSet<int> hiddenInstructions = new();

    private static readonly Font HeaderFont = new("Consolas", 9.5f, FontStyle.Bold);
    private static readonly Font SummaryFont = new("Consolas", 8.5f);
    private static readonly Font AnchorFont = new("Segoe UI", 9f, FontStyle.Bold);

    private readonly System.Windows.Forms.Timer flashClock = new() { Interval = 700 };
    private int? flashedInstruction;
    private Point? blockDragOrigin;
    private int? blockDragInstruction;

    private DecompiledFunction? currentFunction;
    private IReadOnlyList<GraphEdge> edges = Array.Empty<GraphEdge>();
    private readonly Dictionary<GraphEdge, int> edgeLanes = new();
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
        flashClock.Tick += (_, _) =>
        {
            flashClock.Stop();
            flashedInstruction = null;
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) flashClock.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A block was clicked.</summary>
    public event Action<int>? InstructionSelected;

    /// <summary>A block was double-clicked: open its editor.</summary>
    public event Action<int>? InstructionActivated;

    public event Action<int, int>? MoveRequested;

    public event Action<int, int>? JumpEditRequested;

    /// <summary>The active path changed: branch points and the branch taken at each.</summary>
    public event Action<IReadOnlyList<BranchDecision>>? ActivePathChanged;

    public int? SelectedInstruction => selectedInstruction;

    /// <summary>Instructions actually walked to reach the current point.</summary>
    public IReadOnlyCollection<int> ActivePath => activePath;

    /// <summary>
    /// Replaces the scene. Blocks are drawn, not instantiated: a scene of a
    /// thousand instructions is a thousand rectangles to paint instead of three
    /// thousand windows to create, which is what made every edit stall.
    /// </summary>
    public void SetGraph(IReadOnlyList<ScriptFlowBlock> blocks, DecompiledFunction function)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(function);
        currentFunction = function;
        nodes.Clear();
        branchChoice.Clear();
        hiddenInstructions.Clear();

        // Local unconditional jumps get no block: the arrow replaces them.
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == 3 && LocalJump(function, instruction) is not null)
                hiddenInstructions.Add(instruction.Index);
        }

        foreach (var block in blocks)
        {
            if (hiddenInstructions.Contains(block.Instruction)) continue;
            nodes[block.Instruction] = new FlowNode(block.Instruction)
            {
                Header = block.Header,
                Summary = block.Summary,
                HeaderColor = block.HeaderColor,
            };
        }
        foreach (var instruction in function.Instructions.Where(value => value.Opcode == 5))
            nodes[instruction.Index] = new FlowNode(instruction.Index) { Anchor = FlowAnchor.Fork };
        if (function.Instructions.Count > 0 && function.Instructions[0].Opcode != 5)
            nodes[StartKey] = new FlowNode(StartKey) { Anchor = FlowAnchor.Start };

        edges = BuildEdges(function).ToArray();
        selectedEdge = null;
        LayoutGraph(function);
        AssignEdgeLanes();
        RecomputeActivePath();
        Invalidate();
    }

    /// <summary>
    /// Puts the entry of the scene under the eye. A branchy scene spreads far to
    /// the sides and far down; opening it anywhere else means hunting for where
    /// it starts.
    /// </summary>
    public void CenterOnEntry()
    {
        var entry = nodes.TryGetValue(StartKey, out var start)
            ? start
            : nodes.Count == 0 ? null
            : nodes.OrderBy(pair => pair.Value.Top).ThenBy(pair => pair.Key).First().Value;
        if (entry is null)
        {
            AutoScrollPosition = Point.Empty;
            return;
        }
        AutoScrollPosition = new Point(
            Math.Max(0, entry.Left + entry.Width / 2 - ClientSize.Width / 2),
            Math.Max(0, entry.Top - CanvasPadding));
        Invalidate();
    }

    /// <summary>
    /// Follows a block during playback: it scrolls only when the block has left
    /// the view, so a scene that runs inside one screenful does not shake, and
    /// it never flashes — the highlight already says which block is playing.
    /// </summary>
    public void FollowInstruction(int instruction)
    {
        if (!nodes.TryGetValue(instruction, out var node)) return;
        var view = new Rectangle(
            -AutoScrollPosition.X, -AutoScrollPosition.Y, ClientSize.Width, ClientSize.Height);
        var block = new Rectangle(node.Left, node.Top, node.Width, node.Height);
        if (view.Contains(block)) return;
        AutoScrollPosition = new Point(
            Math.Max(0, node.Left - Math.Max(0, (view.Width - node.Width) / 2)),
            Math.Max(0, node.Top - Math.Max(0, (view.Height - node.Height) / 2)));
        Invalidate();
    }

    /// <summary>Brings a block into view and flashes it.</summary>
    public void ScrollInstructionIntoView(int instruction)
    {
        if (!nodes.TryGetValue(instruction, out var node)) return;
        var view = new Rectangle(
            -AutoScrollPosition.X, -AutoScrollPosition.Y, ClientSize.Width, ClientSize.Height);
        var target = new Point(
            Math.Max(0, node.Left - Math.Max(0, (view.Width - node.Width) / 2)),
            Math.Max(0, node.Top - Math.Max(0, (view.Height - node.Height) / 2)));
        AutoScrollPosition = target;
        flashedInstruction = instruction;
        flashClock.Stop();
        flashClock.Start();
        Invalidate();
    }

    public void SelectInstruction(int instruction)
    {
        selectedEdge = null;
        if (selectedInstruction != instruction)
        {
            selectedInstruction = instruction;
            Invalidate();
        }
        Invalidate();
    }

    public void ClearSelection()
    {
        selectedInstruction = null;
        selectedEdge = null;
        Invalidate();
        Invalidate();
    }

    public void BeginInstructionDrag(int instruction)
    {
        SelectInstruction(instruction);
        DoDragDrop(new InstructionDrag(instruction), DragDropEffects.Move);
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
            Invalidate();
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

        Invalidate();
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

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    // ---------------------------------------------------------------- painting
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Drawing happens in view coordinates, not graph ones: GDI+ flattens
        // paths through a 16.16 fixed-point pipeline and throws past ±32768, and
        // a scene of a thousand blocks is far taller than that.
        var view = new Rectangle(
            -AutoScrollPosition.X + eventArgs.ClipRectangle.X,
            -AutoScrollPosition.Y + eventArgs.ClipRectangle.Y,
            eventArgs.ClipRectangle.Width,
            eventArgs.ClipRectangle.Height);
        view.Inflate(Grid * 4, Grid * 4);

        foreach (var edge in edges.Where(value => !IsActive(value))) DrawEdge(graphics, edge, view);
        foreach (var edge in edges.Where(IsActive)) DrawEdge(graphics, edge, view);
        foreach (var node in nodes.Values)
        {
            if (view.IntersectsWith(node.Bounds)) DrawNode(graphics, node);
        }
        if (dropTarget is { } target && nodes.TryGetValue(target.AnchorInstruction, out var anchor))
        {
            var marked = ToView(anchor.Bounds);
            var y = target.Before ? marked.Top - 4 : marked.Bottom + 4;
            using var marker = new Pen(Color.DeepSkyBlue, 4f);
            graphics.DrawLine(marker, marked.Left, y, marked.Right, y);
        }
    }

    /// <summary>Graph coordinates to the coordinates GDI+ is handed.</summary>
    private Rectangle ToView(Rectangle bounds)
        => new(
            bounds.X + AutoScrollPosition.X,
            bounds.Y + AutoScrollPosition.Y,
            bounds.Width,
            bounds.Height);

    /// <summary>
    /// A point of an edge, moved into view coordinates and kept inside a window
    /// GDI+ can handle. Every routed segment is axis aligned, so clamping a point
    /// that lies far outside the viewport never moves the part that is visible.
    /// </summary>
    private Point ToView(Point point)
    {
        const int limit = 20000;
        var x = point.X + AutoScrollPosition.X;
        var y = point.Y + AutoScrollPosition.Y;
        return new Point(Math.Clamp(x, -limit, limit), Math.Clamp(y, -limit, limit));
    }

    private static int MeasureBlockHeight(FlowNode node)
        => Math.Max(
            64,
            HeaderHeight + BlockPadding * 2 + TextRenderer.MeasureText(
                node.Summary,
                SummaryFont,
                new Size(NodeWidth - BlockPadding * 2, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);

    private void DrawNode(Graphics graphics, FlowNode node)
    {
        var bounds = ToView(node.Bounds);
        var dimmed = !IsActive(node.Instruction);
        if (node.IsAnchor)
        {
            var circle = new Rectangle(
                bounds.Left + 1, bounds.Top + 1, bounds.Width - 3, bounds.Height - 3);
            var isStart = node.Anchor == FlowAnchor.Start;
            var fillColor = isStart ? Color.FromArgb(50, 125, 92) : Color.FromArgb(240, 157, 55);
            var borderColor = isStart ? Color.FromArgb(125, 225, 170) : Color.Gold;
            if (dimmed)
            {
                fillColor = Blend(fillColor, NodeDimmed, 0.62f);
                borderColor = Blend(borderColor, NodeDimmed, 0.62f);
            }
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(borderColor, 2f);
            graphics.FillEllipse(fill, circle);
            graphics.DrawEllipse(border, circle);
            if (isStart)
            {
                TextRenderer.DrawText(graphics, "Init", AnchorFont, circle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            return;
        }

        var selected = selectedInstruction == node.Instruction;
        var flashing = flashedInstruction == node.Instruction;
        var background = flashing ? FlashBackground
            : selected ? NodeSelected
            : dimmed ? NodeDimmed
            : NodeNormal;
        using (var fill = new SolidBrush(background))
            graphics.FillRectangle(fill, bounds);
        var headerColor = dimmed ? Blend(node.HeaderColor, NodeDimmed, 0.55f) : node.HeaderColor;
        using (var fill = new SolidBrush(headerColor))
            graphics.FillRectangle(fill, bounds.Left, bounds.Top, bounds.Width, HeaderHeight);
        using (var border = new Pen(
            selected ? EdgeSelected : Color.FromArgb(70, 74, 84), selected ? 2f : 1f))
        {
            graphics.DrawRectangle(border, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
        }
        var textColor = dimmed ? Blend(Color.White, NodeDimmed, 0.55f) : Color.White;
        TextRenderer.DrawText(
            graphics, node.Header, HeaderFont,
            new Rectangle(bounds.Left + BlockPadding, bounds.Top, bounds.Width - BlockPadding * 2, HeaderHeight),
            textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        var summaryColor = dimmed ? Blend(Color.Gainsboro, NodeDimmed, 0.55f) : Color.Gainsboro;
        TextRenderer.DrawText(
            graphics, node.Summary, SummaryFont,
            new Rectangle(
                bounds.Left + BlockPadding,
                bounds.Top + HeaderHeight + 2,
                bounds.Width - BlockPadding * 2,
                bounds.Height - HeaderHeight - BlockPadding),
            summaryColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private void DrawEdge(Graphics graphics, GraphEdge edge, Rectangle view)
    {
        if (!nodes.TryGetValue(edge.From, out var source) || !nodes.TryGetValue(edge.To, out var target)) return;
        if (!view.IntersectsWith(Rectangle.Union(source.Bounds, target.Bounds))) return;
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
        var path = Array.ConvertAll(
            GetEdgePath(source, target, edgeLanes.GetValueOrDefault(edge)), ToView);
        graphics.DrawLines(pen, path);
        DrawArrowHead(graphics, color, path[^2], path[^1], width);
        if (!dimmed && edge.Label.Length > 0) DrawEdgeLabel(graphics, edge, path, color);
    }

    private static Point[] GetEdgePath(FlowNode source, FlowNode target, int lane = 0)
    {
        // Fan the departures and the arrivals across the width of a block so two
        // edges leaving or reaching the same block stay distinguishable.
        var spread = Math.Min(Grid, source.Width / 6);
        var sourceCenter = source.Left + source.Width / 2 + LaneOffset(lane) * spread;
        var targetCenter = target.Left + target.Width / 2 + LaneOffset(lane) * spread;
        if (target.Top >= source.Bottom)
        {
            if (Math.Abs(sourceCenter - targetCenter) <= Grid)
                return new[] { new Point(sourceCenter, source.Bottom), new Point(targetCenter, target.Top) };
            // Each edge of the band runs at its own height.
            var gap = target.Top - source.Bottom;
            var midY = source.Bottom + Math.Max(4, gap / 2 + LaneOffset(lane) * Math.Min(6, gap / 4));
            return new[]
            {
                new Point(sourceCenter, source.Bottom),
                new Point(sourceCenter, midY),
                new Point(targetCenter, midY),
                new Point(targetCenter, target.Top),
            };
        }

        // upward edge (loop): route around the side, one lane per edge
        var side = Math.Max(source.Right, target.Right) + Grid * 2 + lane * Grid;
        return new[]
        {
            new Point(source.Right, source.Top + Math.Min(24, source.Height / 2)),
            new Point(side, source.Top + Math.Min(24, source.Height / 2)),
            new Point(side, target.Top + Math.Min(24, target.Height / 2)),
            new Point(target.Right, target.Top + Math.Min(24, target.Height / 2)),
        };
    }

    /// <summary>Lane 0 stays centred, the next ones alternate around it.</summary>
    private static int LaneOffset(int lane)
        => lane == 0 ? 0 : (lane % 2 == 1 ? 1 : -1) * ((lane + 1) / 2);

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
        if (HitTestNode(eventArgs.Location) is { } node)
        {
            selectedEdge = null;
            SelectInstruction(node.Instruction);
            if (!node.IsAnchor)
            {
                InstructionSelected?.Invoke(node.Instruction);
                // A press on a block may become a drag to reorder it.
                blockDragOrigin = eventArgs.Location;
                blockDragInstruction = node.Instruction;
            }
            return;
        }
        if (HitTestEdge(GraphPoint(eventArgs.Location)) is { } edge)
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
        if (blockDragOrigin is { } dragStart && blockDragInstruction is { } dragged
            && eventArgs.Button == MouseButtons.Left)
        {
            var slack = SystemInformation.DragSize;
            if (Math.Abs(eventArgs.X - dragStart.X) >= Math.Max(2, slack.Width / 2)
                || Math.Abs(eventArgs.Y - dragStart.Y) >= Math.Max(2, slack.Height / 2))
            {
                blockDragOrigin = null;
                blockDragInstruction = null;
                BeginInstructionDrag(dragged);
            }
            return;
        }
        if (panOrigin is not { } origin || eventArgs.Button != MouseButtons.Left) return;
        AutoScrollPosition = new Point(
            Math.Max(0, panScrollOrigin.X - (eventArgs.X - origin.X)),
            Math.Max(0, panScrollOrigin.Y - (eventArgs.Y - origin.Y)));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        blockDragOrigin = null;
        blockDragInstruction = null;
        if (eventArgs.Button != MouseButtons.Left || panOrigin is null) return;
        panOrigin = null;
        Capture = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs eventArgs)
    {
        base.OnMouseDoubleClick(eventArgs);
        if (eventArgs.Button != MouseButtons.Left) return;
        if (HitTestNode(eventArgs.Location) is { IsAnchor: false } node)
        {
            InstructionActivated?.Invoke(node.Instruction);
            return;
        }
        if (HitTestEdge(GraphPoint(eventArgs.Location)) is not { } edge) return;
        if (edge.ArgumentIndex < 0 || edge.Owner < 0) return;
        selectedEdge = edge;
        Invalidate();
        JumpEditRequested?.Invoke(edge.Owner, edge.ArgumentIndex);
    }

    /// <summary>Mouse position in the graph's own coordinates.</summary>
    private Point GraphPoint(Point client)
        => new(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);

    private FlowNode? HitTestNode(Point client)
    {
        var point = GraphPoint(client);
        foreach (var node in nodes.Values)
            if (node.Bounds.Contains(point)) return node;
        return null;
    }

    private GraphEdge? HitTestEdge(Point point)
    {
        foreach (var edge in edges.Reverse())
        {
            if (!nodes.TryGetValue(edge.From, out var source) || !nodes.TryGetValue(edge.To, out var target)) continue;
            var path = GetEdgePath(source, target, edgeLanes.GetValueOrDefault(edge));
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

        foreach (var node in nodes.Values)
        {
            node.Bounds = node.Anchor switch
            {
                FlowAnchor.Start => new Rectangle(Point.Empty, new Size(86, 54)),
                FlowAnchor.Fork => new Rectangle(Point.Empty, new Size(26, 26)),
                _ => new Rectangle(Point.Empty, new Size(NodeWidth, MeasureBlockHeight(node))),
            };
        }

        var successors = BuildSuccessorMap();
        var columns = AssignColumns(successors);
        var rows = AssignRows(successors);

        // Node bounds are the graph's own coordinates and painting applies the
        // scroll offset, so the bounds computed below describe exactly what was
        // placed whatever the view is scrolled to.
        var scroll = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);

        // Columns are placement values, not slots: they leave wide gaps where a
        // region was reserved and not used. Ranking the distinct values packs the
        // graph without changing the left-to-right order of anything.
        var ranks = columns.Values.Distinct().OrderBy(value => value)
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => (double)pair.index);
        foreach (var key in columns.Keys.ToArray())
            columns[key] = ranks[columns[key]];

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
            // A row nothing landed on takes no space: an empty band would read as
            // a gap in the scene.
            if (rowHeights[row] == 0) continue;
            currentY = rowTops[row] + rowHeights[row] + RowGap;
        }

        int RowTop(int key) => rowTops[rows.TryGetValue(key, out var row) ? row : 0];

        // Two blocks stacked in one column with nothing joining them read as a
        // single thread. Move the lower one aside: this is the ambiguity a reader
        // actually hits, and it costs one column only where it occurs.
        var joined = edges
            .Select(edge => (edge.From, edge.To))
            .Concat(edges.Select(edge => (edge.To, edge.From)))
            .ToHashSet();
        foreach (var column in nodes.Keys
                     .GroupBy(key => columns.TryGetValue(key, out var value) ? value : 0d)
                     .ToArray())
        {
            var ordered = column
                .OrderBy(key => rows.TryGetValue(key, out var row) ? row : 0)
                .ThenBy(key => key)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                // Measured where the reader sees it: rows that nothing occupies
                // take no space, so two blocks far apart in row numbers can still
                // end up touching.
                var gap = RowTop(current) - (RowTop(previous) + nodes[previous].Height);
                if (gap > RowGap * 3) continue;
                if (joined.Contains((previous, current))) continue;  // one thread
                columns[current] = columns.Values.Max() + 1d;
            }
        }

        // Nothing in the region placement guarantees that two nodes of the same
        // row land on different columns: when they do, one block hides the other
        // outright. Nudge the later ones aside, keeping their order.
        foreach (var row in nodes.Keys.GroupBy(key => rows.TryGetValue(key, out var r) ? r : 0))
        {
            var previous = double.NegativeInfinity;
            foreach (var key in row
                .OrderBy(value => columns.TryGetValue(value, out var c) ? c : 0d)
                .ThenBy(value => value))
            {
                var column = columns.TryGetValue(key, out var value) ? value : 0d;
                if (column <= previous) column = previous + 1d;
                columns[key] = column;
                previous = column;
            }
        }

        var minColumn = columns.Count == 0 ? 0d : columns.Values.Min();
        var step = NodeWidth + ColumnGap;
        var right = 0;
        var bottom = 0;
        foreach (var pair in nodes)
        {
            var column = columns.TryGetValue(pair.Key, out var value) ? value : 0d;
            var row = rows.TryGetValue(pair.Key, out var r) ? r : 0;
            // Columns are ranks here, so they are bounded by the number of nodes:
            // the placement cannot overflow, and clamping the pixel value would
            // only stack the far-right blocks on top of each other.
            var centerX = (int)Math.Round(
                CanvasPadding + (column - minColumn) * step + NodeWidth / 2.0);
            var location = new Point(centerX - pair.Value.Width / 2, rowTops[row]);
            pair.Value.Bounds = new Rectangle(location, pair.Value.Bounds.Size);
            right = Math.Max(right, location.X + pair.Value.Width);
            bottom = Math.Max(bottom, location.Y + pair.Value.Height);
        }

        // A loop arrow is routed around the right-hand side of the blocks it
        // joins, so the canvas has to reach past the widest block for it.
        if (edges.Any(edge => IsUpward(edge))) right += Grid * 2 + Grid;
        AutoScrollMinSize = new Size(right + CanvasPadding, bottom + CanvasPadding);
        AutoScrollPosition = scroll;
    }

    /// <summary>
    /// Lays out a scene shaped like a real one (a straight run, a branch and a
    /// backward jump) and checks the canvas describes exactly what it holds: no
    /// band of emptiness under the last block, nothing reachable only past the
    /// end of a scrollbar, and the closing RETURN at the bottom of the flow.
    /// </summary>
    internal static void VerifySmoke()
    {
        const int count = 40;
        var instructions = new List<DecompiledInstruction>();
        for (var index = 0; index < count; index++)
        {
            instructions.Add(new DecompiledInstruction(
                index, index * 4, $"OP{20 + index % 5}", 20 + index % 5,
                Array.Empty<InstructionArgument>(), Array.Empty<JumpTarget>()));
        }
        // A branch forward and a jump back, the two shapes that route edges aside.
        instructions[10] = new DecompiledInstruction(
            10, 40, "Jump_if_false", 5,
            Array.Empty<InstructionArgument>(),
            new[] { new JumpTarget(1, 20, 80, 0) });
        instructions[19] = new DecompiledInstruction(
            19, 76, "Jump", 3,
            Array.Empty<InstructionArgument>(),
            new[] { new JumpTarget(0, 5, 20, 0) });
        instructions.Add(new DecompiledInstruction(
            count, count * 4, "Return", 1,
            Array.Empty<InstructionArgument>(), Array.Empty<JumpTarget>()));
        var function = new DecompiledFunction(0, "EV_SMOKE", true, instructions);
        VerifyLayout(function, expectLoopArrow: true, expectClosingReturnDeepest: true);
    }

    /// <summary>
    /// Blocks left stacked in a column with nothing joining them: a reader takes
    /// them for one thread. Counted by the layout verification.
    /// </summary>
    internal static int StackedAmbiguities { get; set; }

    /// <summary>Describes the placed graph, to diagnose an unreadable scene.</summary>
    internal static string DescribeLayout(DecompiledFunction function)
    {
        using var panel = new ScriptFlowPanel { Size = new Size(1200, 800) };
        var blocks = function.Instructions
            .Where(value => value.Opcode != 5)
            .Where(value => value.Opcode != 3 || !value.Jumps.Any(jump =>
                jump.TargetFunctionIndex == function.Index && jump.TargetInstructionIndex >= 0))
            .Select(value => new ScriptFlowBlock(
                value.Index, $"#{value.Index} {value.Name}", "operands", Color.Gray))
            .ToArray();
        panel.SetGraph(blocks, function);
        var report = new System.Text.StringBuilder();
        report.AppendLine(
            $"{function.Name}: {blocks.Length} blocks, canvas {panel.AutoScrollMinSize}");
        foreach (var node in panel.nodes.OrderBy(pair => pair.Value.Top).ThenBy(pair => pair.Value.Left))
        {
            report.AppendLine(
                $"  #{node.Key,4} {(node.Value.IsAnchor ? "anchor" : "block ")} {node.Value.Bounds}");
        }
        return report.ToString();
    }

    /// <summary>
    /// Lays a real (or synthetic) scene out and checks the canvas matches it.
    /// </summary>
    internal static void VerifyLayout(
        DecompiledFunction function,
        bool expectLoopArrow,
        bool expectClosingReturnDeepest = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        using var panel = new ScriptFlowPanel { Size = new Size(900, 600) };
        var blocks = new List<ScriptFlowBlock>();
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == 5) continue;
            if (instruction.Opcode == 3 && instruction.Jumps.Any(value =>
                value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0)) continue;
            blocks.Add(new ScriptFlowBlock(
                instruction.Index, $"#{instruction.Index} {instruction.Name}", "operands", Color.Gray));
        }
        panel.SetGraph(blocks, function);

        var placed = panel.nodes.Values.ToArray();
        if (placed.Length == 0) return;
        var lowest = placed.Max(value => value.Bottom);
        var widest = placed.Max(value => value.Right);
        if (panel.AutoScrollMinSize.Height < lowest || panel.AutoScrollMinSize.Width < widest)
            throw new InvalidOperationException(
                $"{function.Name}: the graph canvas does not reach its own blocks.");
        if (panel.AutoScrollMinSize.Height > lowest + CanvasPadding + RowGap)
        {
            throw new InvalidOperationException(
                $"{function.Name}: the graph canvas keeps"
                + $" {panel.AutoScrollMinSize.Height - lowest} px of empty space under its last block.");
        }
        // The loop arrow is routed past the blocks: the canvas must include it.
        if (expectLoopArrow && panel.AutoScrollMinSize.Width <= widest + Grid)
            throw new InvalidOperationException(
                $"{function.Name}: the graph canvas cuts off the loop arrow drawn on its right.");
        // Only a scene that ends in sequence closes at the bottom: one that ends
        // on a loop exits through a branch taken higher up, and its RETURN
        // legitimately sits above the body it escapes from.
        var last = expectClosingReturnDeepest
            ? function.Instructions.LastOrDefault(value => value.Opcode == 1)
            : null;
        if (last is not null && panel.nodes.TryGetValue(last.Index, out var closing)
            && closing.Top < placed.Where(value => value != closing).Max(value => value.Top))
        {
            throw new InvalidOperationException(
                $"{function.Name}: the closing RETURN is not the deepest block of the flow.");
        }
        // Two blocks may never sit on top of each other: a node the column pass
        // forgot ends up stacked on another, which reads as "the scene has one
        // block" when it has fifteen.
        var boxes = panel.nodes.Values.Where(value => !value.IsAnchor).ToArray();
        for (var first = 0; first < boxes.Length; first++)
        for (var second = first + 1; second < boxes.Length; second++)
        {
            if (!boxes[first].Bounds.IntersectsWith(boxes[second].Bounds)) continue;
            throw new InvalidOperationException(
                $"{function.Name}: two blocks overlap at {boxes[first].Bounds}"
                + $" and {boxes[second].Bounds}.");
        }
        if (placed.Any(value => value.Left < 0 || value.Top < 0))
            throw new InvalidOperationException(
                $"{function.Name}: a block sits outside the canvas, where it cannot be scrolled to.");
        // Two blocks stacked in one column with nothing joining them read as a
        // single thread: that is how two separate branches were confused.
        var connected = panel.edges
            .Select(edge => (edge.From, edge.To))
            .Concat(panel.edges.Select(edge => (edge.To, edge.From)))
            .ToHashSet();
        foreach (var first in panel.nodes)
        foreach (var second in panel.nodes)
        {
            if (first.Key >= second.Key) continue;
            if (first.Value.IsAnchor || second.Value.IsAnchor) continue;
            if (first.Value.Left != second.Value.Left) continue;
            var gap = Math.Abs(second.Value.Top - first.Value.Bottom);
            if (second.Value.Top < first.Value.Bottom) gap = Math.Abs(first.Value.Top - second.Value.Bottom);
            if (gap > RowGap * 2) continue;               // not stacked
            if (connected.Contains((first.Key, second.Key))) continue;
            // Reported rather than fatal: the placement removes most of these,
            // and the ones that survive are a readability defect, not a broken
            // canvas. --verify-graph counts them so the number can only go down.
            StackedAmbiguities++;
        }
        panel.VerifyPainting(function);
    }

    /// <summary>
    /// Paints the scene at several scroll positions. A tall scene reaches
    /// coordinates GDI+ refuses, and only actually painting catches it.
    /// </summary>
    private void VerifyPainting(DecompiledFunction function)
    {
        using var surface = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        using var graphics = Graphics.FromImage(surface);
        var height = AutoScrollMinSize.Height;
        var steps = new[] { 0, height / 3, height / 2, Math.Max(0, height - ClientSize.Height) };
        foreach (var offset in steps.Distinct())
        {
            AutoScrollPosition = new Point(0, offset);
            var clip = new Rectangle(Point.Empty, surface.Size);
            try
            {
                OnPaint(new PaintEventArgs(graphics, clip));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"{function.Name}: painting the scene at scroll {offset} failed"
                    + $" ({exception.GetType().Name}: {exception.Message}).",
                    exception);
            }
        }
        AutoScrollPosition = Point.Empty;
    }

    /// <summary>
    /// Gives each edge of a same gap between rows its own horizontal lane. Two
    /// edges crossing the same band otherwise ran along the very same line, and
    /// the reader could not tell which branch they belonged to.
    /// </summary>
    private void AssignEdgeLanes()
    {
        edgeLanes.Clear();
        foreach (var band in edges
                     .Where(edge => nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To))
                     .GroupBy(edge => (nodes[edge.From].Bottom + nodes[edge.To].Top) / (Grid * 4)))
        {
            var lane = 0;
            foreach (var edge in band
                         .OrderBy(edge => nodes[edge.From].Left)
                         .ThenBy(edge => edge.From)
                         .ThenBy(edge => edge.To))
            {
                edgeLanes[edge] = lane++;
            }
        }
    }

    /// <summary>An edge drawn around the side rather than straight down.</summary>
    private bool IsUpward(GraphEdge edge)
        => nodes.TryGetValue(edge.From, out var source)
            && nodes.TryGetValue(edge.To, out var target)
            && target.Top < source.Bottom;

    /// <summary>Row = longest path from the entry (back edges are ignored).</summary>
    private Dictionary<int, int> AssignRows(Dictionary<int, List<GraphEdge>> successors)
    {
        var rows = new Dictionary<int, int>();
        foreach (var key in nodes.Keys) rows[key] = 0;
        var order = nodes.Keys.OrderBy(value => value == StartKey ? int.MinValue : value).ToArray();
        // Eight passes stopped short on long scenes: rows then under-estimated the
        // depth and the closing RETURN did not end up at the bottom of the graph.
        var passLimit = Math.Min(Math.Max(8, nodes.Count), 256);
        for (var pass = 0; pass < passLimit; pass++)
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
        // A region is laid out once. Without this a backward jump whose branch
        // reaches its own fork again made the placement recurse until the stack
        // gave out, because every call started a fresh walk.
        var placedRegions = new HashSet<(int Start, int Stop)>();
        var leafSides = new Dictionary<int, double>();
        var walkLimit = nodes.Count + 8;
        var entry = nodes.ContainsKey(StartKey) ? StartKey : 0;
        PlaceRegion(entry, int.MinValue, 0d, 0);

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
                    // A fork whose one branch is a single block keeps the other on
                    // the trunk and only puts that block aside: a scene testing 64
                    // flags in a row then reads as one column of tests with its
                    // actions beside it, instead of fanning out at every level.
                    // (Taking twice the widest doubled the width at every nested
                    // fork: 2^64 columns, which overflowed the placement outright.)
                    total = Math.Max(total, Math.Min(RegionWidth(first, second), MaximumRegionWidth));
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
        void PlaceRegion(int start, int stop, double center, int depth)
        {
            if (depth > walkLimit || !placedRegions.Add((start, stop))) return;
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
                    if (first <= 1d && second > 1d)
                    {
                        // The short branch steps aside and the continuation stays
                        // on the trunk. Successive forks alternate the side they
                        // step to: stacking their blocks in one column made two
                        // separate branches read as a single thread.
                        PlaceRegion(outgoing[0].To, join, center + NextLeafSide(node), depth + 1);
                        PlaceRegion(outgoing[1].To, join, center, depth + 1);
                    }
                    else if (second <= 1d && first > 1d)
                    {
                        PlaceRegion(outgoing[0].To, join, center, depth + 1);
                        PlaceRegion(outgoing[1].To, join, center + NextLeafSide(node), depth + 1);
                    }
                    else
                    {
                        // symmetric: one branch left, the other right of the fork
                        var offset = Math.Min(Math.Max(first, second), MaximumRegionWidth) / 2d + 0.5d;
                        PlaceRegion(outgoing[0].To, join, center - offset, depth + 1);
                        PlaceRegion(outgoing[1].To, join, center + offset, depth + 1);
                    }
                    if (join == int.MinValue) break;
                    node = join;              // the join comes back onto the trunk
                    continue;
                }
                node = outgoing.Count == 1 ? outgoing[0].To : int.MinValue;
            }
        }

        // Side a fork's short branch steps to, alternating down a chain of forks.
        double NextLeafSide(int fork)
        {
            if (!leafSides.TryGetValue(fork, out var side))
            {
                side = leafSides.Count % 2 == 0 ? -1d : 1d;
                leafSides[fork] = side;
            }
            return side;
        }

        // Columns a fork claims, mirroring how PlaceRegion lays its branches out.
        static double RegionWidth(double first, double second)
            => first <= 1d || second <= 1d
                ? Math.Max(first, second) + 1d
                : first + second;

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

    private enum FlowAnchor { None, Fork, Start }

    /// <summary>
    /// A drawn node: an instruction block, a branch pivot or the entry marker.
    /// It carries only what painting and hit testing need.
    /// </summary>
    private sealed class FlowNode
    {
        public FlowNode(int instruction) => Instruction = instruction;

        public int Instruction { get; }
        public FlowAnchor Anchor { get; init; } = FlowAnchor.None;
        public string Header { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public Color HeaderColor { get; init; } = Color.FromArgb(70, 78, 92);
        public Rectangle Bounds { get; set; }

        public bool IsAnchor => Anchor != FlowAnchor.None;
        public int Left => Bounds.Left;
        public int Top => Bounds.Top;
        public int Right => Bounds.Right;
        public int Bottom => Bounds.Bottom;
        public int Width => Bounds.Width;
        public int Height => Bounds.Height;
    }
}
