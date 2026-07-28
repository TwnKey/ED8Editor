using System.Numerics;

namespace ED8Editor.Core;

/// <summary>
/// One node of a playing effect: a segment, spawned by its parent at a given
/// time, with the placement and colour its tracks give it at the instant asked
/// for.
/// </summary>
/// <param name="SegmentIndex">Which segment of the file this node plays.</param>
/// <param name="Depth">How far down the spawn chain the node sits.</param>
/// <param name="LocalTime">Seconds since this node was spawned.</param>
/// <param name="Position">Where the node sits, in the effect's own space.</param>
/// <param name="Rotation">The node's orientation, without its scale.</param>
/// <param name="Scale">The node's size, its parent's scale already folded in.</param>
/// <param name="ColorMultiply">The colour the node multiplies its texture by.</param>
/// <param name="ColorAdd">The colour the node adds on top.</param>
/// <param name="Drawn">
/// False for a container segment, which only places its children. The engine
/// registers it on a render pass of its own and never draws its quad.
/// </param>
/// <param name="Billboard">True when the segment's quad turns to face the camera.</param>
public sealed record EffNode(
    int SegmentIndex,
    int Depth,
    float LocalTime,
    Vector3 Position,
    Matrix4x4 Rotation,
    Vector3 Scale,
    Vector4 ColorMultiply,
    Vector4 ColorAdd,
    bool Drawn,
    bool Billboard);

/// <summary>
/// How far a preview is allowed to unroll an effect. An emitter can be told to
/// fire for ever, so the preview stops somewhere and says where it stopped
/// rather than pretending it drew everything.
/// </summary>
/// <param name="Bursts">Bursts kept from an endless emitter.</param>
/// <param name="ParticlesPerBurst">Particles kept from one burst.</param>
/// <param name="Depth">How deep the spawn chain is followed.</param>
/// <param name="Nodes">The most nodes a single evaluation may produce.</param>
public sealed record EffPreviewLimits(
    int Bursts = 30,
    int ParticlesPerBurst = 16,
    int Depth = 6,
    int Nodes = 4096);

/// <summary>What one evaluation produced, and what it had to leave out.</summary>
public sealed record EffFrame(IReadOnlyList<EffNode> Nodes, bool Truncated);

/// <summary>
/// Plays an effect: walks the segments an effect spawns, evaluates each one's
/// tracks at its own local time and places it against its parent. The rules come
/// from the format itself — a segment's flag words say what it inherits from its
/// parent, how long it lives and how it is thrown, and its spawn descriptors say
/// which segments it fires, how many and how often.
/// </summary>
public static class EffSimulation
{
    /// <summary>Track defaults: an unauthored position is the parent's, an unauthored scale is 1.</summary>
    private static readonly float[] Origin = { 0f, 0f, 0f, 0f };
    private static readonly float[] Unit = { 1f, 1f, 1f, 1f };
    private static readonly float[] White = { 1f, 1f, 1f, 1f };

    private const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>A segment whose flag word marks it as a container draws nothing.</summary>
    private const uint ContainerBit = 0x1;

    /// <summary>
    /// The segment's quad turns to face the camera instead of keeping the
    /// orientation its tracks give it.
    /// </summary>
    private const uint BillboardBit = 0x10;

    /// <summary>
    /// The node's own second rotation turns its quad as well as its trajectory.
    /// </summary>
    private const uint OrientAlongMotionBit = 0x0008_0000;

    /// <summary>The node takes its parent's live placement.</summary>
    private const uint InheritLivePositionBits = 0x3000;

    /// <summary>The node keeps the placement its parent had when it was spawned.</summary>
    private const uint InheritSpawnPositionBit = 0x8000;

    private const uint InheritLiveScaleBit = 0x4000;
    private const uint InheritLiveRotationBit = 0x2000;
    private const uint InheritSpawnScaleBit = 0x2_0000;
    private const uint InheritSpawnRotationBit = 0x1_0000;

    /// <summary>
    /// The effect's nodes at <paramref name="time"/> seconds after it started.
    /// Segments with no spawn descriptor pointing at them are the roots the
    /// engine starts on its own.
    /// </summary>
    public static EffFrame Evaluate(EffFile effect, float time, EffPreviewLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var bounds = limits ?? new EffPreviewLimits();
        var nodes = new List<EffNode>();
        var spawned = new HashSet<int>();
        foreach (var segment in effect.Segments)
        {
            foreach (var descriptor in ReadSpawns(segment, effect.Segments.Count))
            {
                spawned.Add(descriptor.SegmentIndex);
            }
        }

        var truncated = false;
        for (var index = 0; index < effect.Segments.Count; index++)
        {
            if (spawned.Contains(index)) continue;
            truncated |= !Play(
                effect,
                index,
                depth: 0,
                spawnTime: 0f,
                time,
                EffParentFrame.Root,
                EffTrackEvaluator.InstanceSeed(index, 0, 0, 0),
                bounds,
                nodes);
        }
        return new EffFrame(nodes, truncated);
    }

    /// <summary>
    /// How long the effect runs, when it runs out at all: the deepest chain of
    /// spawn delays plus the life of the segment that ends last. An effect whose
    /// segments live for ever, or one with an emitter that never stops firing,
    /// has no end and returns null — a preview of it looks the same whenever it
    /// is watched, once it has built up.
    /// </summary>
    public static float? FiniteDuration(EffFile effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var spawned = new HashSet<int>();
        foreach (var segment in effect.Segments)
        {
            foreach (var descriptor in ReadSpawns(segment, effect.Segments.Count))
            {
                spawned.Add(descriptor.SegmentIndex);
            }
        }
        var duration = 0f;
        for (var index = 0; index < effect.Segments.Count; index++)
        {
            if (spawned.Contains(index)) continue;
            if (MeasureBranch(effect, index, 0f, 0, new HashSet<int>()) is not { } branch) return null;
            duration = Math.Max(duration, branch);
        }
        return duration;
    }

    private static float? MeasureBranch(
        EffFile effect,
        int segmentIndex,
        float spawnTime,
        int depth,
        ISet<int> visiting)
    {
        if (depth > 32 || !visiting.Add(segmentIndex)) return null;
        try
        {
            var segment = effect.Segments[segmentIndex];
            var lifetime = segment.Data04[4];
            // A segment with no lifetime is only bounded by the effect being
            // stopped, which the file itself does not say.
            var duration = lifetime > 0f ? spawnTime + lifetime : (float?)null;
            var children = ReadSpawns(segment, effect.Segments.Count).ToArray();
            if (children.Length == 0 && duration is null) return null;
            var longest = duration ?? 0f;
            foreach (var descriptor in children)
            {
                if (descriptor.Bursts == EffSpawn.Endless) return null;
                if (descriptor.SegmentIndex == segmentIndex) continue;
                var first = descriptor.Trigger != 0 && lifetime > 0f ? lifetime : descriptor.Delay;
                var last = spawnTime + first
                    + Math.Max(0, descriptor.Bursts - 1) * descriptor.Interval;
                if (MeasureBranch(effect, descriptor.SegmentIndex, last, depth + 1, visiting)
                    is not { } branch)
                {
                    return null;
                }
                longest = Math.Max(longest, branch);
            }
            return duration is null && children.Length == 0 ? null : longest;
        }
        finally
        {
            visiting.Remove(segmentIndex);
        }
    }

    /// <summary>
    /// Places one node and everything it spawns. Returns false when a limit cut
    /// the walk short.
    /// </summary>
    private static bool Play(
        EffFile effect,
        int segmentIndex,
        int depth,
        float spawnTime,
        float time,
        EffParentFrame parent,
        uint seed,
        EffPreviewLimits limits,
        List<EffNode> nodes)
    {
        if (depth > limits.Depth) return false;
        if (nodes.Count >= limits.Nodes) return false;
        var segment = effect.Segments[segmentIndex];
        var localTime = time - spawnTime;
        if (localTime < 0f) return true;

        // A segment with a lifetime is gone once it has run out; one without
        // lives as long as the effect does.
        var lifetime = segment.Data04[4];
        if (lifetime > 0f && localTime > lifetime) return true;

        var placement = Place(segment, localTime, parent, seed);
        var complete = true;
        nodes.Add(new EffNode(
            segmentIndex,
            depth,
            localTime,
            placement.Position,
            placement.Rotation,
            placement.Scale,
            placement.ColorMultiply,
            placement.ColorAdd,
            (segment.Data02[0] & ContainerBit) == 0,
            (segment.Data02[1] & BillboardBit) != 0));

        foreach (var descriptor in ReadSpawns(segment, effect.Segments.Count))
        {
            if (descriptor.SegmentIndex == segmentIndex) continue;
            // A descriptor that does not fire on time waits for its parent to
            // die, which for a preview is the end of the parent's life.
            var firstSpawn = descriptor.Trigger != 0 && lifetime > 0f
                ? lifetime
                : descriptor.Delay;
            var bursts = descriptor.Bursts == EffSpawn.Endless
                ? limits.Bursts
                : Math.Min(descriptor.Bursts, limits.Bursts);
            if (descriptor.Bursts == EffSpawn.Endless || descriptor.Bursts > limits.Bursts)
            {
                complete = false;
            }
            var particles = Math.Min(descriptor.ParticlesPerBurst, limits.ParticlesPerBurst);
            if (descriptor.ParticlesPerBurst > limits.ParticlesPerBurst) complete = false;

            for (var burst = 0; burst < bursts; burst++)
            {
                var childSpawn = spawnTime + firstSpawn + burst * descriptor.Interval;
                if (childSpawn > time) break;
                // The parent's placement is captured at the moment it fires, for
                // children that keep it rather than follow their parent.
                var frozen = Place(segment, childSpawn - spawnTime, parent, seed);
                var frame = new EffParentFrame(
                    placement.Position, placement.Rotation, placement.Scale,
                    frozen.Position, frozen.Rotation, frozen.Scale);
                for (var particle = 0; particle < particles; particle++)
                {
                    complete &= Play(
                        effect,
                        descriptor.SegmentIndex,
                        depth + 1,
                        childSpawn,
                        time,
                        frame,
                        EffTrackEvaluator.InstanceSeed(
                            descriptor.SegmentIndex, (uint)burst, (uint)particle, seed),
                        limits,
                        nodes);
                }
            }
        }
        return complete;
    }

    /// <summary>
    /// A segment's placement at a given moment of its own life: its tracks read
    /// against whatever it inherits from its parent.
    /// </summary>
    private static EffPlacement Place(
        EffSegment segment,
        float localTime,
        EffParentFrame parent,
        uint seed)
    {
        var flags = segment.Data02[2];
        var basisPosition = (flags & InheritLivePositionBits) != 0
            ? parent.LivePosition
            : (flags & InheritSpawnPositionBit) != 0
                ? parent.SpawnPosition
                : Vector3.Zero;
        var basisRotation = (flags & InheritSpawnRotationBit) != 0
            ? parent.SpawnRotation
            : (flags & InheritLiveRotationBit) != 0
                ? parent.LiveRotation
                : Matrix4x4.Identity;
        var basisScale = (flags & (InheritLiveScaleBit | InheritLiveRotationBit)) != 0
            ? parent.LiveScale
            : (flags & InheritSpawnScaleBit) != 0
                ? parent.SpawnScale
                : Vector3.One;

        var offset = Evaluate(segment.Position, localTime, Origin, seed ^ 0x09);
        var spin = Evaluate(segment.Rotation, localTime, Origin, seed ^ 0x0a);
        var size = Evaluate(segment.Scale, localTime, Unit, seed ^ 0x0b);
        var trajectory = Evaluate(segment.Rotation2, localTime, Origin, seed ^ 0x0c);
        var multiply = Evaluate(segment.ColorMultiply, localTime, White, seed ^ 0x0d);
        var add = Evaluate(segment.ColorAdd, localTime, Origin, seed ^ 0x0e);

        // Thrown segments follow y = v0·t - ½g·t², with the launch speed rolled
        // once per instance between its two bounds.
        var gravity = segment.Data04[10];
        var thrown = 0f;
        if (gravity != 0f)
        {
            var low = segment.Data04[8];
            var high = segment.Data04[9];
            var launch = low + (high - low) * Roll(seed);
            thrown = launch * localTime - 0.5f * gravity * localTime * localTime;
        }

        // The second rotation track turns the frame the node's own offset is
        // measured in, which is how an emitter scatters its particles.
        var trajectoryRotation = Euler(trajectory);
        var frame = trajectoryRotation * basisRotation;
        var local = new Vector3(
            offset[0], offset[1] + thrown + segment.Data06[8], offset[2]) * basisScale;
        var position = basisPosition + Vector3.Transform(local, frame);

        // The base orientation the segment was authored with lies the quad down
        // or stands it up; the rotation track turns it from there.
        var rotation = Euler(segment.Data06[5], segment.Data06[6], segment.Data06[7])
            * Euler(spin)
            * ((segment.Data02[2] & OrientAlongMotionBit) != 0 ? frame : basisRotation);
        return new EffPlacement(
            position,
            rotation,
            new Vector3(size[0], size[1], size[2]) * basisScale,
            new Vector4(multiply[0], multiply[1], multiply[2], multiply[3]),
            new Vector4(add[0], add[1], add[2], add[3]));
    }

    private static float[] Evaluate(
        List<EffKeyframe> track, float time, float[] fallback, uint seed)
        => EffTrackEvaluator.Evaluate(track, time, fallback, seed);

    /// <summary>The launch speed a thrown segment rolled when it was spawned.</summary>
    private static float Roll(uint seed)
    {
        var hash = seed ^ 0x9E37_79B9u;
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7FEB_352Du);
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x846C_A68Bu);
        hash ^= hash >> 16;
        return (hash >> 8) / 16_777_216f;
    }

    private static Matrix4x4 Euler(float[] degrees) => Euler(degrees[0], degrees[1], degrees[2]);

    /// <summary>
    /// The engine turns a point about X, then Y, then Z — the order its own
    /// Euler routine applies, not the yaw-pitch-roll order the framework helper
    /// uses.
    /// </summary>
    private static Matrix4x4 Euler(float x, float y, float z)
        => Matrix4x4.CreateRotationX(x * DegreesToRadians)
            * Matrix4x4.CreateRotationY(y * DegreesToRadians)
            * Matrix4x4.CreateRotationZ(z * DegreesToRadians);

    /// <summary>
    /// The segments a segment fires. A descriptor packs its target and its
    /// counts into the bytes of its first float, its trigger into the next one,
    /// its delay into the keyframe's time slot and its re-fire interval into the
    /// first integer.
    /// </summary>
    public static IEnumerable<EffSpawn> ReadSpawns(EffSegment segment, int segmentCount)
    {
        ArgumentNullException.ThrowIfNull(segment);
        foreach (var record in segment.Children)
        {
            var packed = BitConverter.SingleToUInt32Bits(record.Floats[0]);
            var target = (int)((packed >> 8) & 0xFF);
            if (target >= segmentCount) continue;
            var interval = BitConverter.UInt32BitsToSingle(record.Ints[0]);
            yield return new EffSpawn(
                target,
                (int)((packed >> 16) & 0xFF),
                (int)((packed >> 24) & 0xFF),
                (byte)(BitConverter.SingleToUInt32Bits(record.Floats[1]) & 0xFF),
                record.Floats[8],
                // A descriptor with no interval fires once a frame.
                interval > 0f ? interval : 1f / 30f);
        }
    }

    private readonly record struct EffPlacement(
        Vector3 Position,
        Matrix4x4 Rotation,
        Vector3 Scale,
        Vector4 ColorMultiply,
        Vector4 ColorAdd);

    private readonly record struct EffParentFrame(
        Vector3 LivePosition,
        Matrix4x4 LiveRotation,
        Vector3 LiveScale,
        Vector3 SpawnPosition,
        Matrix4x4 SpawnRotation,
        Vector3 SpawnScale)
    {
        public static EffParentFrame Root => new(
            Vector3.Zero, Matrix4x4.Identity, Vector3.One,
            Vector3.Zero, Matrix4x4.Identity, Vector3.One);
    }
}

/// <summary>One entry of a segment's spawn list.</summary>
/// <param name="SegmentIndex">The segment fired.</param>
/// <param name="Bursts">How many times it fires, or <see cref="Endless"/>.</param>
/// <param name="ParticlesPerBurst">How many copies each firing makes.</param>
/// <param name="Trigger">0 fires on time, anything else on the parent's end.</param>
/// <param name="Delay">Seconds before the first firing.</param>
/// <param name="Interval">Seconds between firings.</param>
public sealed record EffSpawn(
    int SegmentIndex,
    int Bursts,
    int ParticlesPerBurst,
    byte Trigger,
    float Delay,
    float Interval)
{
    /// <summary>The count an emitter that never stops carries.</summary>
    public const int Endless = 0xFF;
}
