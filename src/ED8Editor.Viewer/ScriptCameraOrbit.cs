using System.Numerics;

namespace ED8Editor.Viewer;

/// <summary>
/// The engine's scenario camera is an orbit camera: a look-at centre, a distance
/// and two angles. Reversed from the OP45 selector-4 handler, which converts the
/// authored degrees to radians and builds the eye as
/// <c>eye = centre + distance · (Rz(0)·Ry(yaw)·Rx(-pitch))·(0,0,1)</c>, that is
/// <c>eye = centre + distance · (sin yaw·cos pitch, sin pitch, cos yaw·cos pitch)</c>.
///
/// So the authored angles describe where the EYE sits relative to what it looks
/// at — a positive pitch lifts the camera above the centre, and yaw 0 places it
/// on +Z. The editor camera stores the opposite vector (the view direction), so
/// every conversion between the two conventions goes through this type.
/// </summary>
internal static class ScriptCameraOrbit
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float RadiansToDegrees = 180f / MathF.PI;

    /// <summary>Unit vector from the look-at centre towards the eye.</summary>
    public static Vector3 EyeOffsetDirection(float pitchDegrees, float yawDegrees)
    {
        var pitch = pitchDegrees * DegreesToRadians;
        var yaw = yawDegrees * DegreesToRadians;
        var cosPitch = MathF.Cos(pitch);
        return new Vector3(
            MathF.Sin(yaw) * cosPitch,
            MathF.Sin(pitch),
            MathF.Cos(yaw) * cosPitch);
    }

    /// <summary>Unit vector the camera looks along, i.e. eye towards centre.</summary>
    public static Vector3 ViewDirection(float pitchDegrees, float yawDegrees)
        => -EyeOffsetDirection(pitchDegrees, yawDegrees);

    /// <summary>
    /// Authored angles that reproduce a view direction. Yaw is returned in
    /// [0, 360) because that is how the scenario scripts store it.
    /// </summary>
    public static (float PitchDegrees, float YawDegrees) FromViewDirection(Vector3 viewDirection)
    {
        if (viewDirection.LengthSquared() <= 1e-12f) return (0f, 0f);
        var offset = -Vector3.Normalize(viewDirection);
        var pitch = MathF.Asin(Math.Clamp(offset.Y, -1f, 1f)) * RadiansToDegrees;
        var yaw = MathF.Atan2(offset.X, offset.Z) * RadiansToDegrees;
        if (yaw < 0f) yaw += 360f;
        return (pitch, yaw);
    }

    /// <summary>
    /// Checks the authored-angle convention against the values the engine
    /// computes: yaw 0 puts the eye on +Z, yaw 90 on +X, and a positive pitch
    /// lifts it above what it looks at.
    /// </summary>
    internal static void VerifySmoke()
    {
        Expect(EyeOffsetDirection(0f, 0f), new Vector3(0f, 0f, 1f), "yaw 0 must place the eye on +Z");
        Expect(EyeOffsetDirection(0f, 90f), new Vector3(1f, 0f, 0f), "yaw 90 must place the eye on +X");
        Expect(EyeOffsetDirection(90f, 0f), new Vector3(0f, 1f, 0f), "pitch 90 must place the eye overhead");
        Expect(
            ViewDirection(0f, 0f),
            new Vector3(0f, 0f, -1f),
            "the view direction must oppose the eye offset");
        // t1000 EV_C05E14S02: centre (0.25, 5.5, -16.5), pitch 3, yaw 215, distance 16.
        var eye = new Vector3(0.25f, 5.5f, -16.5f) + 16f * EyeOffsetDirection(3f, 215f);
        Expect(
            eye,
            new Vector3(-8.9146f, 6.3374f, -29.5885f),
            "the authored shot does not resolve to its eye");
        foreach (var pitch in new[] { -60f, -12f, 0f, 3f, 45f })
        foreach (var yaw in new[] { 0f, 42.39f, 180f, 215f, 295.5f })
        {
            var round = FromViewDirection(ViewDirection(pitch, yaw));
            if (MathF.Abs(round.PitchDegrees - pitch) > 0.01f
                || MathF.Abs(round.YawDegrees - yaw) > 0.01f)
            {
                throw new InvalidOperationException(
                    $"Camera angles ({pitch}, {yaw}) did not survive a round trip"
                    + $" (got ({round.PitchDegrees}, {round.YawDegrees})).");
            }
        }
        if (MathF.Abs(NormalizeTowards(350f, 10f) - 370f) > 0.001f
            || MathF.Abs(NormalizeTowards(10f, 350f) + 10f) > 0.001f
            || MathF.Abs(NormalizeTowards(10f, 100f) - 100f) > 0.001f)
        {
            throw new InvalidOperationException(
                "The shortest-arc normalisation does not keep the move under half a turn.");
        }

        static void Expect(Vector3 value, Vector3 expected, string message)
        {
            if (Vector3.Distance(value, expected) > 0.001f)
                throw new InvalidOperationException($"{message} (got {value}, expected {expected}).");
        }
    }

    /// <summary>
    /// The engine's shortest-arc normalisation: it rewrites the destination
    /// angle so the interpolation never travels more than half a turn. It runs
    /// once, when the command starts, and only when its flag byte is set —
    /// clearing the flag is how a script asks for the long way round.
    /// </summary>
    public static float NormalizeTowards(float currentDegrees, float targetDegrees)
    {
        var delta = targetDegrees - currentDegrees;
        if (delta > 180f && currentDegrees < targetDegrees) return targetDegrees - 360f;
        if (delta < -180f && targetDegrees < currentDegrees) return targetDegrees + 360f;
        return targetDegrees;
    }
}
