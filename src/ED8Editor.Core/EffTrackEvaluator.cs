namespace ED8Editor.Core;

/// <summary>
/// Evaluates a segment's keyframe track the way the engine does. A keyframe
/// holds a four-component value (floats 0..3), a second bound a random keyframe
/// rolls against (floats 4..7), its time (float 8) and a mode word (the low half
/// of the first integer):
/// <list type="bullet">
/// <item>bit 0 — additive: the value adds to the previous keyframe's result.</item>
/// <item>bit 1 — uniform: the first component is broadcast to x, y and z.</item>
/// <item>bit 2 — random: the value is rolled between the two bounds.</item>
/// <item>bit 4 — the keyframe a loop returns to.</item>
/// <item>bit 5 — the keyframe that closes the loop and jumps back.</item>
/// </list>
/// Between keyframes the engine interpolates linearly, and it holds the first
/// and last values outside the track's range.
/// </summary>
public static class EffTrackEvaluator
{
    private const int Components = 4;

    /// <summary>
    /// The track's value at time <paramref name="time"/>, in seconds from the
    /// start of the segment. <paramref name="fallback"/> is the value an empty
    /// track keeps, and the base the first additive keyframe adds to.
    /// <paramref name="seed"/> makes a random keyframe roll the same way for a
    /// given instance on every frame, so a particle does not flicker.
    /// </summary>
    public static float[] Evaluate(
        IReadOnlyList<EffKeyframe> track,
        float time,
        float[] fallback,
        uint seed)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(fallback);
        if (track.Count == 0) return (float[])fallback.Clone();

        // Every keyframe's value is chained from the start of the track: an
        // additive keyframe means nothing without the one before it.
        var targets = new float[track.Count][];
        var previous = fallback;
        for (var index = 0; index < track.Count; index++)
        {
            targets[index] = EvaluateKeyframe(track[index], previous, seed, index);
            previous = targets[index];
        }

        var local = time;
        var loopEnd = IndexOfFlag(track, 0x20);
        var loopStart = IndexOfFlag(track, 0x10);
        if (loopEnd >= 0 && loopStart >= 0 && loopStart <= loopEnd)
        {
            var period = track[loopEnd].Time - track[loopStart].Time;
            if (period > 0.0001f && time > track[loopEnd].Time)
            {
                var passes = (int)MathF.Floor((time - track[loopEnd].Time) / period) + 1;
                local = track[loopStart].Time + (time - track[loopEnd].Time) - (passes - 1) * period;
                // The loop region is re-chained once per pass: additive keyframes
                // keep accumulating across iterations, absolute ones settle at
                // once — which is why the loop stops as soon as nothing changes.
                for (var pass = 0; pass < Math.Min(passes, 1000); pass++)
                {
                    var chained = targets[loopEnd];
                    var changed = false;
                    for (var index = loopStart; index <= loopEnd; index++)
                    {
                        var value = EvaluateKeyframe(track[index], chained, seed, index);
                        if (!Same(value, targets[index])) changed = true;
                        targets[index] = value;
                        chained = value;
                    }
                    if (!changed) break;
                }
            }
        }

        if (local <= track[0].Time) return targets[0];
        var last = track.Count - 1;
        if (local >= track[last].Time) return targets[last];
        for (var index = 0; index < last; index++)
        {
            var from = track[index].Time;
            var to = track[index + 1].Time;
            if (local < from || local > to) continue;
            if (to - from <= 0.0001f) return targets[index + 1];
            var ratio = (local - from) / (to - from);
            var result = new float[Components];
            for (var component = 0; component < Components; component++)
            {
                result[component] = targets[index][component]
                    + (targets[index + 1][component] - targets[index][component]) * ratio;
            }
            return result;
        }
        return targets[last];
    }

    /// <summary>
    /// One keyframe's value, given the value the previous one settled on.
    /// </summary>
    public static float[] EvaluateKeyframe(
        EffKeyframe keyframe,
        float[] previous,
        uint seed,
        int index)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        ArgumentNullException.ThrowIfNull(previous);
        var flags = keyframe.Flags;
        var value = new float[Components];
        if ((flags & 2) != 0)
        {
            // Uniform: one roll, one component, broadcast to x, y and z.
            var uniform = Component(keyframe, flags, seed, index, 0);
            value[0] = uniform;
            value[1] = uniform;
            value[2] = uniform;
            value[3] = 0f;
        }
        else
        {
            for (var component = 0; component < Components; component++)
            {
                value[component] = Component(keyframe, flags, seed, index, component);
            }
        }
        if ((flags & 1) != 0)
        {
            for (var component = 0; component < Components; component++)
            {
                value[component] += previous[component];
            }
        }
        return value;
    }

    private static float Component(EffKeyframe keyframe, ushort flags, uint seed, int index, int component)
    {
        if ((flags & 4) == 0) return keyframe.Floats[component];
        var roll = Roll(seed, index, component);
        return keyframe.Floats[component]
            + (keyframe.Floats[component + 4] - keyframe.Floats[component]) * roll;
    }

    /// <summary>
    /// A roll in [0, 1) hashed from the instance's seed, the keyframe and the
    /// component, so a given particle rolls once per keyframe and keeps that
    /// value from frame to frame.
    /// </summary>
    private static float Roll(uint seed, int index, int component)
    {
        var hash = seed ^ 0x9E37_79B9u
            ^ unchecked((uint)index * 0x85EB_CA6Bu)
            ^ unchecked((uint)component * 0xC2B2_AE35u);
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7FEB_352Du);
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x846C_A68Bu);
        hash ^= hash >> 16;
        return (hash >> 8) / 16_777_216f;
    }

    /// <summary>
    /// The seed of one spawned instance of a segment. A root-level spawn's first
    /// instance uses burst, particle and parent all at zero.
    /// </summary>
    public static uint InstanceSeed(int segmentIndex, uint burst, uint particle, uint parentSeed)
        => unchecked((uint)segmentIndex * 0x9E37_79B9u
            + burst * 0x85EB_CA6Bu
            + particle * 0xC2B2_AE35u
            + ((parentSeed << 9) | (parentSeed >> 23)));

    private static int IndexOfFlag(IReadOnlyList<EffKeyframe> track, ushort flag)
    {
        for (var index = 0; index < track.Count; index++)
        {
            if ((track[index].Flags & flag) != 0) return index;
        }
        return -1;
    }

    private static bool Same(float[] left, float[] right)
    {
        for (var index = 0; index < Components; index++)
        {
            if (left[index] != right[index]) return false;
        }
        return true;
    }
}
