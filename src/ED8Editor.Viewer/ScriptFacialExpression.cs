using System.Text;

namespace ED8Editor.Viewer;

internal sealed record ScriptFacialExpression(
    string PrimaryEyes,
    string Mouth,
    string SecondaryEyes,
    string Complexion,
    int StartFrame)
{
    public static ScriptFacialExpression Neutral { get; } =
        new("0", "0", "#b", "0", 0);

    /// <summary>
    /// Resolves the four facial channels <paramref name="seconds"/> after the
    /// command that set them. Every channel is an independent pattern: eye and
    /// mouth sequences advance on their own, so blinking keeps running while a
    /// mouth pattern loops through a line of dialogue.
    /// </summary>
    public ScriptFacialPose Evaluate(float seconds = 0f)
    {
        var primary = FrameAt(PrimaryEyes, seconds, EyesSeed, 0);
        // "#b" mirrors the eye channel onto its symmetric counterpart, so both
        // eye materials blink together instead of drifting apart.
        var secondary = SecondaryEyes.Equals("#b", StringComparison.Ordinal)
            ? primary
            : FrameAt(SecondaryEyes, seconds, EyesSeed, primary);
        return new ScriptFacialPose(
            primary,
            secondary,
            FrameAt(Mouth, seconds, MouthSeed, 0),
            FrameAt(Complexion, seconds, ComplexionSeed, 0));
    }

    private const int EyesSeed = 1;
    private const int MouthSeed = 2;
    private const int ComplexionSeed = 3;

    private static int FrameAt(string pattern, float seconds, int seed, int fallback)
        => ScriptFacialPattern.Parse(pattern, HashCode.Combine(pattern, seed))
            .FrameAt(seconds, fallback);
}

internal readonly record struct ScriptFacialPose(
    int PrimaryEyes,
    int SecondaryEyes,
    int Mouth,
    int Complexion);

internal static class ScriptFacialCommandParser
{
    public static ScriptFacialExpression ApplyComposite(
        ScriptFacialExpression current,
        string command,
        Func<string, string> expand,
        int startFrame)
    {
        var primary = current.PrimaryEyes;
        var mouth = current.Mouth;
        var secondary = current.SecondaryEyes;
        var complexion = current.Complexion;
        for (var index = 0; index < command.Length;)
        {
            if (command[index++] != '#' || index >= command.Length) continue;
            var channel = command[index++];
            if (channel is not ('E' or 'M' or 'e' or 'H')) continue;
            string pattern;
            if (index < command.Length && command[index] == '[')
            {
                var depth = 1;
                var start = ++index;
                while (index < command.Length && depth > 0)
                {
                    if (command[index] == '[') depth++;
                    else if (command[index] == ']') depth--;
                    index++;
                }
                if (depth != 0) break;
                pattern = command[start..(index - 1)];
            }
            else
            {
                if (index >= command.Length) break;
                pattern = command[index++].ToString();
            }
            pattern = expand(pattern);
            switch (channel)
            {
                case 'E': primary = pattern; break;
                case 'M': mouth = pattern; break;
                case 'e': secondary = pattern; break;
                case 'H': complexion = pattern; break;
            }
        }
        return new ScriptFacialExpression(
            primary, mouth, secondary, complexion, startFrame);
    }
}
