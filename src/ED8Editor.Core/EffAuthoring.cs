namespace ED8Editor.Core;

/// <summary>
/// The structural edits an effect needs: adding a segment, taking one out,
/// moving one under another parent.
///
/// A segment's place in the tree is not stored on the segment: it is stored in
/// the spawn descriptors of whoever fires it, which name their target by its
/// index in the file. So every edit here is really an edit of those descriptors,
/// and taking a segment out means renumbering the ones that point past it.
/// </summary>
public static class EffAuthoring
{
    /// <summary>Where a spawn descriptor keeps the segment it fires.</summary>
    private const int TargetByteShift = 8;

    /// <summary>
    /// A new effect with a single segment, built from the format itself rather
    /// than copied from anything: the version the PC release reads, no texture
    /// declared yet, and the tail of zero bytes every file of the corpus ends
    /// with.
    /// </summary>
    public static EffFile CreateEffect(string name)
    {
        var effect = new EffFile
        {
            Version = EffGameVersion.Pc,
            EffectName = name,
            // Eight bytes follow the last segment in every file the game ships.
            Trailing = new byte[8],
        };
        effect.Segments.Add(CreateSegment(effect.Version, "root"));
        return effect;
    }

    /// <summary>
    /// A segment written from what the format says, not copied: it is drawn
    /// (bit 0), it faces the camera (bit 4), it lives a second, it keeps its own
    /// size, and it is white. Everything this project has not reversed stays
    /// zero — which is what an unauthored field is.
    ///
    /// The blocks the PC layout always lays out after the spawn list are written
    /// too: without them the file would not read back, since that layout has no
    /// flag word of its own and always expects them.
    /// </summary>
    public static EffSegment CreateSegment(uint version, string name)
    {
        var segment = new EffSegment { Name = name };
        segment.Data02[1] = 0x11;
        // A lifetime, so the segment ends instead of hanging around for ever.
        segment.Data04[4] = 1f;
        // The corners of the unit quad a flat segment draws.
        float[] corners = { -0.5f, 0.5f, 0f, 0f, 0.5f, -0.5f, 0f, 0f };
        corners.CopyTo(segment.Data08, 0);
        segment.Position.Add(Keyframe(0f, 0f, 0f, 0f));
        segment.Rotation.Add(Keyframe(0f, 0f, 0f, 0f));
        segment.Scale.Add(Keyframe(1f, 1f, 1f, 1f));
        segment.ColorMultiply.Add(Keyframe(1f, 1f, 1f, 1f));
        segment.ColorAdd.Add(Keyframe(0f, 0f, 0f, 0f));
        if (version < EffGameVersion.PlayStationCs2) segment.Data05 = new float[3];
        if (version <= EffGameVersion.Pc)
        {
            segment.Data15 = new float[2];
            segment.StructFlags = 3;
            segment.Data16 = new float[16];
            segment.Data17PcRaw = new byte[16];
        }
        return segment;
    }

    private static EffKeyframe Keyframe(float x, float y, float z, float w)
    {
        var keyframe = new EffKeyframe();
        keyframe.Floats[0] = x;
        keyframe.Floats[1] = y;
        keyframe.Floats[2] = z;
        keyframe.Floats[3] = w;
        return keyframe;
    }

    /// <summary>
    /// Adds a segment written from the format, fired by <paramref name="parent"/>.
    /// </summary>
    public static int AddNewSegment(EffFile effect, uint version, int? parent, string name)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.Segments.Count >= 255)
        {
            throw new InvalidOperationException("An effect cannot hold more than 255 segments.");
        }
        effect.Segments.Add(CreateSegment(version, name));
        var index = effect.Segments.Count - 1;
        if (parent is { } owner) Attach(effect, index, owner);
        return index;
    }

    /// <summary>
    /// Adds a copy of <paramref name="source"/> to the file and makes
    /// <paramref name="parent"/> fire it. A copy is used rather than an empty
    /// segment because a segment full of zeroes draws nothing: the new one
    /// starts as something the game already knows how to play.
    /// </summary>
    public static int AddSegment(EffFile effect, int source, int? parent, string name)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (source < 0 || source >= effect.Segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        if (effect.Segments.Count >= 255)
        {
            // A descriptor names its target in one byte.
            throw new InvalidOperationException("An effect cannot hold more than 255 segments.");
        }
        var added = Clone(effect.Segments[source]);
        added.Name = name;
        // A fresh copy fires nothing: its children belong to the segment it was
        // copied from, not to it.
        added.Children.Clear();
        effect.Segments.Add(added);
        var index = effect.Segments.Count - 1;
        if (parent is { } owner) Attach(effect, index, owner);
        return index;
    }

    /// <summary>
    /// Removes a segment, everything it fires, and every descriptor that fired
    /// it. The segments that follow move up, so the descriptors that name them
    /// are renumbered.
    /// </summary>
    public static void RemoveSegment(EffFile effect, int index)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (index < 0 || index >= effect.Segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        foreach (var doomed in Descendants(effect, index).OrderByDescending(value => value))
        {
            RemoveOne(effect, doomed);
        }
    }

    /// <summary>Moves a segment under another parent, or up to a root of its own.</summary>
    public static void Reparent(EffFile effect, int index, int? parent)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (index < 0 || index >= effect.Segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (parent is { } owner)
        {
            if (owner < 0 || owner >= effect.Segments.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(parent));
            }
            // A segment cannot end up firing itself, directly or through its own
            // children: that would be a tree with no root.
            if (owner == index || Descendants(effect, index).Contains(owner))
            {
                throw new InvalidOperationException(
                    "A segment cannot be moved under itself or under one of its own children.");
            }
        }

        EffKeyframe? descriptor = null;
        foreach (var segment in effect.Segments)
        {
            for (var entry = segment.Children.Count - 1; entry >= 0; entry--)
            {
                if (TargetOf(segment.Children[entry]) != index) continue;
                descriptor ??= segment.Children[entry];
                segment.Children.RemoveAt(entry);
            }
        }
        if (parent is not { } newParent) return;
        // The descriptor that fired it keeps its timing; a segment that nobody
        // fired takes the new parent's own way of firing.
        var moved = descriptor ?? BorrowDescriptor(effect, newParent);
        SetTarget(moved, index);
        effect.Segments[newParent].Children.Add(moved);
    }

    /// <summary>The segments no other segment fires: the ones the engine starts.</summary>
    public static IReadOnlyList<int> Roots(EffFile effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var spawned = new HashSet<int>();
        foreach (var segment in effect.Segments)
        {
            foreach (var descriptor in segment.Children)
            {
                spawned.Add(TargetOf(descriptor));
            }
        }
        return Enumerable.Range(0, effect.Segments.Count)
            .Where(index => !spawned.Contains(index))
            .ToArray();
    }

    /// <summary>A segment and everything it fires, however deep.</summary>
    public static IReadOnlyCollection<int> Descendants(EffFile effect, int index)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var found = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(index);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current < 0 || current >= effect.Segments.Count || !found.Add(current)) continue;
            foreach (var descriptor in effect.Segments[current].Children)
            {
                pending.Push(TargetOf(descriptor));
            }
        }
        return found;
    }

    private static void Attach(EffFile effect, int index, int parent)
    {
        var descriptor = BorrowDescriptor(effect, parent);
        SetTarget(descriptor, index);
        effect.Segments[parent].Children.Add(descriptor);
    }

    /// <summary>
    /// A spawn descriptor to start from: the parent's own if it already fires
    /// something, otherwise any of the file's, so the counts, the trigger, the
    /// delay and the interval are the ones the game authored. A file where
    /// nothing spawns anything has none to copy, and gets the plainest
    /// descriptor the format can express: fire once, at once.
    /// </summary>
    private static EffKeyframe BorrowDescriptor(EffFile effect, int parent)
    {
        if (effect.Segments[parent].Children.Count > 0)
        {
            return effect.Segments[parent].Children[0].Clone();
        }
        foreach (var segment in effect.Segments)
        {
            if (segment.Children.Count > 0) return segment.Children[0].Clone();
        }
        var descriptor = new EffKeyframe();
        // floats[0] packs the origin mode, the target, the number of bursts and
        // the particles per burst, one to a byte; floats[1] carries the trigger
        // and floats[8] the delay, and the first integer the re-fire interval.
        descriptor.Floats[0] = BitConverter.UInt32BitsToSingle(
            (1u << 16) | (1u << 24));
        descriptor.Ints[0] = BitConverter.SingleToUInt32Bits(1f / 30f);
        return descriptor;
    }

    private static void RemoveOne(EffFile effect, int index)
    {
        effect.Segments.RemoveAt(index);
        foreach (var segment in effect.Segments)
        {
            for (var entry = segment.Children.Count - 1; entry >= 0; entry--)
            {
                var target = TargetOf(segment.Children[entry]);
                if (target == index)
                {
                    segment.Children.RemoveAt(entry);
                }
                else if (target > index)
                {
                    SetTarget(segment.Children[entry], target - 1);
                }
            }
        }
    }

    public static int TargetOf(EffKeyframe descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return (int)((BitConverter.SingleToUInt32Bits(descriptor.Floats[0]) >> TargetByteShift) & 0xFF);
    }

    public static void SetTarget(EffKeyframe descriptor, int index)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (index is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(index));
        var packed = BitConverter.SingleToUInt32Bits(descriptor.Floats[0]);
        packed = (packed & ~(0xFFu << TargetByteShift)) | ((uint)index << TargetByteShift);
        descriptor.Floats[0] = BitConverter.UInt32BitsToSingle(packed);
    }

    /// <summary>A deep copy of a segment: nothing is shared with the original.</summary>
    public static EffSegment Clone(EffSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var copy = new EffSegment
        {
            Name = segment.Name,
            NameRaw = (byte[])segment.NameRaw.Clone(),
            TextureName = segment.TextureName,
            TextureNameRaw = (byte[])segment.TextureNameRaw.Clone(),
            ModelName = segment.ModelName,
            ModelNameRaw = (byte[])segment.ModelNameRaw.Clone(),
            StructFlags = segment.StructFlags,
            Data03 = (float[]?)segment.Data03?.Clone(),
            Data05 = (float[]?)segment.Data05?.Clone(),
            Data07 = (float[]?)segment.Data07?.Clone(),
            Data15 = (float[]?)segment.Data15?.Clone(),
            Data16 = (float[]?)segment.Data16?.Clone(),
            Data17PcRaw = (byte[])segment.Data17PcRaw.Clone(),
            Data18 = (uint[]?)segment.Data18?.Clone(),
            Data19 = (uint[]?)segment.Data19?.Clone(),
            Data1A = (float[]?)segment.Data1A?.Clone(),
            Data1C = (float[]?)segment.Data1C?.Clone(),
            Data1D = (float[]?)segment.Data1D?.Clone(),
            Data1E = (uint[]?)segment.Data1E?.Clone(),
            Data1F = (uint[]?)segment.Data1F?.Clone(),
            Data20 = (float[]?)segment.Data20?.Clone(),
        };
        segment.Data02.CopyTo(copy.Data02, 0);
        segment.Data04.CopyTo(copy.Data04, 0);
        segment.Data06.CopyTo(copy.Data06, 0);
        segment.Data08.CopyTo(copy.Data08, 0);
        CopyTrack(segment.Position, copy.Position);
        CopyTrack(segment.Rotation, copy.Rotation);
        CopyTrack(segment.Scale, copy.Scale);
        CopyTrack(segment.Rotation2, copy.Rotation2);
        CopyTrack(segment.ColorMultiply, copy.ColorMultiply);
        CopyTrack(segment.ColorAdd, copy.ColorAdd);
        CopyTrack(segment.Data0F, copy.Data0F);
        CopyTrack(segment.Data10, copy.Data10);
        CopyTrack(segment.Data11, copy.Data11);
        CopyTrack(segment.Data12, copy.Data12);
        CopyTrack(segment.Children, copy.Children);
        foreach (var nested in segment.Data13)
        {
            var inner = new List<EffKeyframe>();
            CopyTrack(nested, inner);
            copy.Data13.Add(inner);
        }
        foreach (var record in segment.Data17)
        {
            copy.Data17.Add(new EffRecord72
            {
                Ints0 = (uint[])record.Ints0.Clone(),
                F0 = record.F0,
                Int1 = record.Int1,
                Floats = (float[])record.Floats.Clone(),
                Ints1 = (uint[])record.Ints1.Clone(),
            });
        }
        foreach (var triple in segment.Data1B) copy.Data1B.Add((uint[])triple.Clone());
        return copy;
    }

    private static void CopyTrack(List<EffKeyframe> source, List<EffKeyframe> target)
    {
        foreach (var keyframe in source) target.Add(keyframe.Clone());
    }
}
