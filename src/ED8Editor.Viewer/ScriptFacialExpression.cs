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

    public ScriptFacialPose Evaluate()
    {
        var primary = ReadFirstFrame(PrimaryEyes, 0);
        var secondary = SecondaryEyes.Equals("#b", StringComparison.Ordinal)
            ? primary
            : ReadFirstFrame(SecondaryEyes, primary);
        return new ScriptFacialPose(
            primary,
            secondary,
            ReadFirstFrame(Mouth, 0),
            ReadFirstFrame(Complexion, 0));
    }

    private static int ReadFirstFrame(string pattern, int fallback)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            if (value == '#')
            {
                index++;
                while (index < pattern.Length && !char.IsLower(pattern[index])) index++;
                continue;
            }
            if (value is >= '0' and <= '9') return value - '0';
            if (value is >= 'A' and <= 'J') return value - 'A' + 10;
        }
        return fallback;
    }
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
