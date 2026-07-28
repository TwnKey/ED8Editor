namespace ED8Editor.Viewer;

/// <summary>
/// Decodes the delay a script authors on OP16 for the editor's 60 Hz preview
/// clock.
///
/// The value is in MILLISECONDS, which the corpus settles twice over. A scene
/// that starts a camera move and then waits for it pairs the two: a 3000 ms move
/// is followed by a delay of 3000, a 2000 ms move by 2000, a 4000 ms move by
/// 4000 — never by the ~180 a 60 Hz frame count would need. And of the 68904
/// delays the scenario scripts author, 66542 are multiples of 100 (500, 1000,
/// 300, 50, 1500…), which is how a person writes milliseconds and not how anyone
/// writes frames. Reading them as frames made every wait 16.7 times too long,
/// which turned a twenty-second scene into a five-minute one. Both measurements
/// are reproducible with --calibrate-delay-camera.
///
/// The low-bit rule comes from the engine itself (FUN_0064f420): at frame times
/// below 20 ms, odd values greater than one are stored as (value - 1) / 2. It
/// applies to 106 of those 68904 delays.
/// </summary>
internal static class ScriptWaitDuration
{
    public const float PreviewFramesPerSecond = 60f;
    public const float SecondsPerPreviewFrame = 1f / PreviewFramesPerSecond;

    private const float MillisecondsPerSecond = 1000f;

    /// <summary>The wait the script authored, in milliseconds.</summary>
    public static int DecodeMilliseconds(int encodedValue)
    {
        var value = unchecked((ushort)encodedValue);
        if ((value & 1) != 0 && value != 1)
            return (value - 1) / 2;
        return value;
    }

    /// <summary>The same wait, counted on the preview's own 60 Hz clock.</summary>
    public static int DecodePreviewFrames(int encodedValue)
        => (int)MathF.Round(
            DecodeMilliseconds(encodedValue) * PreviewFramesPerSecond / MillisecondsPerSecond);

    /// <summary>Pins the unit: a delay is milliseconds, on a 60 Hz preview.</summary>
    public static void VerifySmoke()
    {
        Check(1000, 60, "a one-second wait");
        Check(500, 30, "half a second");
        Check(50, 3, "the shortest common wait");
        Check(0, 0, "no wait at all");
        // The engine's low-bit rule, on the milliseconds it decodes.
        Check(501, 15, "an odd value the engine halves");
        Check(1, 0, "the one value the rule spares");

        static void Check(int encoded, int expected, string what)
        {
            var actual = DecodePreviewFrames(encoded);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{what}: delay {encoded} should be {expected} preview frames, not {actual}.");
            }
        }
    }
}
