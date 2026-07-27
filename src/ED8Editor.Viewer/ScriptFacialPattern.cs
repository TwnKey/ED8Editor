namespace ED8Editor.Viewer;

/// <summary>
/// One displayed facial frame and how long it stays on screen.
/// </summary>
internal readonly record struct ScriptFacialPatternStep(int Frame, float Seconds);

/// <summary>
/// A parsed facial pattern: the ordered frames of one channel plus whether the
/// engine restarts the sequence at its end.
///
/// Syntax (shared with the later Cold Steel entries, and the same strings the
/// CS1 <c>face.dat</c> macros expand to):
/// <list type="bullet">
/// <item>a digit is a frame index, an uppercase letter A..J is frame 10..19;</item>
/// <item><c>+</c> after a frame is the half step between it and the next frame —
/// CS1 selects whole face textures, so the preview keeps the lower frame;</item>
/// <item><c>#</c> + optional value + a lowercase letter is a tag:
/// <c>r</c> holds the current frame for a randomised idle delay,
/// <c>s</c> sets the playback speed in percent, <c>x</c> closes a looping
/// pattern, <c>b</c> mirrors the channel onto its symmetric counterpart;</item>
/// <item>brackets only group a pattern (an unexpanded <c>face.dat</c> macro
/// name is left in place by the expander) and carry no frame of their own.</item>
/// </list>
/// </summary>
internal sealed record ScriptFacialPattern(
    IReadOnlyList<ScriptFacialPatternStep> Steps,
    bool Loops)
{
    /// <summary>
    /// A pattern step lasts a tenth of a second at nominal speed. The scripts
    /// only express the idle delay in seconds (the <c>#r</c> tag), so this is
    /// the one preview-timing constant that is not read from game data.
    /// </summary>
    public const float StepSeconds = 0.1f;

    /// <summary>Idle delay of the <c>#r</c> tag: four seconds plus a random share.</summary>
    public const float RandomHoldSeconds = 4f;

    public const float RandomHoldRangeSeconds = 2f;

    public static ScriptFacialPattern Empty { get; } =
        new(Array.Empty<ScriptFacialPatternStep>(), false);

    public float Duration => Steps.Sum(value => value.Seconds);

    public int FrameAt(float seconds, int fallback)
    {
        if (Steps.Count == 0) return fallback;
        var duration = Duration;
        var time = Loops && duration > 0f
            ? seconds - MathF.Floor(seconds / duration) * duration
            : seconds;
        if (time < 0f) time = 0f;
        foreach (var step in Steps)
        {
            if (time < step.Seconds) return step.Frame;
            time -= step.Seconds;
        }
        return Steps[^1].Frame;
    }

    /// <param name="randomSeed">
    /// Selects the idle delay of every <c>#r</c> tag. The engine draws it at
    /// random; the preview derives it from the channel so a frame redraw never
    /// changes the pose that is already on screen.
    /// </param>
    public static ScriptFacialPattern Parse(string pattern, int randomSeed)
    {
        if (string.IsNullOrEmpty(pattern)) return Empty;
        var steps = new List<ScriptFacialPatternStep>();
        var speed = 1f;
        var loops = false;
        var randomIndex = 0;
        for (var index = 0; index < pattern.Length;)
        {
            var value = pattern[index];
            if (value == '#')
            {
                index++;
                var start = index;
                while (index < pattern.Length && !char.IsLower(pattern[index])) index++;
                if (index >= pattern.Length) break;
                var argument = pattern[start..index];
                switch (pattern[index++])
                {
                    case 's':
                        if (int.TryParse(argument, out var percent) && percent > 0)
                            speed = percent / 100f;
                        break;
                    case 'r':
                        if (steps.Count != 0)
                        {
                            steps.Add(new ScriptFacialPatternStep(
                                steps[^1].Frame, RandomHold(randomSeed, randomIndex++)));
                        }
                        break;
                    case 'x':
                        loops = true;
                        break;
                }
                continue;
            }
            index++;
            if (value is '[' or ']') continue;
            var frame = value switch
            {
                >= '0' and <= '9' => value - '0',
                >= 'A' and <= 'J' => value - 'A' + 10,
                _ => -1,
            };
            if (frame < 0) continue;
            // "0+" is the half step towards the next frame. Whole textures are
            // the only thing CS1 can display, so it stays on the lower frame.
            if (index < pattern.Length && pattern[index] == '+') index++;
            steps.Add(new ScriptFacialPatternStep(frame, StepSeconds / speed));
        }
        return steps.Count == 0 ? Empty : new ScriptFacialPattern(steps, loops);
    }

    /// <summary>
    /// Checks the pattern grammar against the two authored <c>face.dat</c>
    /// macros every character uses: the automatic blink and the talking mouth.
    /// </summary>
    internal static void VerifySmoke()
    {
        // "0" + FC_autoE0: open, open, idle delay, blink, back to the start.
        var blink = Parse("00#r1#0x", 1);
        if (blink.Steps.Count != 4 || !blink.Loops)
            throw new InvalidOperationException(
                $"The automatic blink pattern parsed as {blink.Steps.Count} steps"
                + $" (loops={blink.Loops}) instead of 4 looping steps.");
        if (blink.Steps[2].Seconds < RandomHoldSeconds
            || blink.Steps[2].Seconds > RandomHoldSeconds + RandomHoldRangeSeconds)
        {
            throw new InvalidOperationException(
                "The blink idle delay left its authored four-second range.");
        }
        if (blink.FrameAt(0f, -1) != 0 || blink.FrameAt(blink.Duration - 0.05f, -1) != 1)
            throw new InvalidOperationException(
                "The automatic blink does not close the eye on its last step.");
        if (blink.FrameAt(blink.Duration + 0.05f, -1) != 0)
            throw new InvalidOperationException(
                "The automatic blink does not restart at its first frame.");
        // FC_autoM0 alternates two mouth shapes; A..J address frames 10..19.
        var mouth = Parse("13A3A#0x", 2);
        if (mouth.Steps.Select(value => value.Frame).ToArray() is not [1, 3, 10, 3, 10])
            throw new InvalidOperationException(
                "The talking mouth pattern did not decode its uppercase frames.");
        // "#70s" halves nothing but stretches every following step by 100/70.
        var slowed = Parse("0#70s0", 3);
        if (slowed.Steps.Count != 2
            || Math.Abs(slowed.Steps[1].Seconds - StepSeconds / 0.7f) > 0.0001f)
        {
            throw new InvalidOperationException(
                "The speed tag did not scale the duration of the following steps.");
        }
    }

    private static float RandomHold(int seed, int occurrence)
    {
        var hash = (uint)HashCode.Combine(seed, occurrence);
        return RandomHoldSeconds + RandomHoldRangeSeconds * (hash % 1000u) / 1000f;
    }
}
