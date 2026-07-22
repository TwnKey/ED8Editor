using System.Globalization;
using System.Numerics;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

public sealed record ScriptCameraSnapshot(
    Vector3 Position,
    Vector3 Target,
    Vector3 Forward,
    float Distance,
    float YawDegrees,
    float PitchDegrees,
    float VerticalFieldOfViewDegrees);

public sealed class ScriptEditorSemanticContext
{
    public ScriptEditorSemanticContext(Func<ScriptCameraSnapshot> getCameraSnapshot)
        => GetCameraSnapshot = getCameraSnapshot ?? throw new ArgumentNullException(nameof(getCameraSnapshot));

    public Func<ScriptCameraSnapshot> GetCameraSnapshot { get; }
}

internal static class ScriptSemanticValueConverter
{
    public static bool IsCamera(IReadOnlyList<InstructionArgument> arguments, string component)
    {
        if (arguments.Count == 0) return false;
        var first = arguments[0];
        return (first.Sem == "camera" && first.SemArg == component)
            || (first.Sem == $"camera:{component}" && string.IsNullOrEmpty(first.SemArg));
    }

    public static bool IsColor(IReadOnlyList<InstructionArgument> arguments)
        => arguments.Count is 3 or 4
            && arguments[0].Sem == "color"
            && arguments.All(value => value.Kind == "scalar" && value.Type is "f32" or "u8");

    public static bool TryWriteCamera(
        IReadOnlyList<InstructionArgument> arguments,
        ScriptCameraSnapshot snapshot,
        out string component,
        out string[] values)
    {
        if (IsCamera(arguments, "pos") || IsCamera(arguments, "position"))
        {
            component = "position";
            return TryWriteVector3(arguments, snapshot.Position, out values);
        }
        if (IsCamera(arguments, "target"))
        {
            component = "target";
            return TryWriteVector3(arguments, snapshot.Target, out values);
        }
        if (IsCamera(arguments, "forward"))
        {
            component = "forward direction";
            return TryWriteVector3(arguments, snapshot.Forward, out values);
        }
        if (IsCamera(arguments, "distance"))
        {
            component = "distance";
            return TryWriteFloat(arguments, snapshot.Distance, out values);
        }
        if (IsCamera(arguments, "fov") || IsCamera(arguments, "fov-degrees"))
        {
            component = "vertical FOV";
            return TryWriteFloat(arguments, snapshot.VerticalFieldOfViewDegrees, out values);
        }
        if (IsCamera(arguments, "yaw-degrees"))
        {
            component = "yaw";
            return TryWriteFloat(arguments, snapshot.YawDegrees, out values);
        }
        if (IsCamera(arguments, "pitch-degrees"))
        {
            component = "pitch";
            return TryWriteFloat(arguments, snapshot.PitchDegrees, out values);
        }

        component = string.Empty;
        values = Array.Empty<string>();
        return false;
    }

    private static bool TryWriteVector3(
        IReadOnlyList<InstructionArgument> arguments,
        Vector3 value,
        out string[] values)
    {
        if (arguments.Count != 3 || arguments.Any(argument => argument.Kind != "scalar" || argument.Type != "f32"))
        {
            values = Array.Empty<string>();
            return false;
        }
        values = new[] { value.X, value.Y, value.Z }
            .Select(FormatFloat).ToArray();
        return true;
    }

    private static bool TryWriteFloat(
        IReadOnlyList<InstructionArgument> arguments,
        float value,
        out string[] values)
    {
        if (arguments.Count != 1 || arguments[0].Kind != "scalar" || arguments[0].Type != "f32")
        {
            values = Array.Empty<string>();
            return false;
        }
        values = new[] { FormatFloat(value) };
        return true;
    }

    private static string FormatFloat(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    public static Color ReadColor(IReadOnlyList<InstructionArgument> arguments)
    {
        if (!IsColor(arguments)) throw new ArgumentException("The semantic color must contain three or four f32/u8 operands.");
        byte Convert(InstructionArgument value) => value.Type == "u8"
            ? checked((byte)value.IntValue)
            : checked((byte)Math.Clamp((int)MathF.Round((float)value.FloatValue * 255f), 0, 255));
        return Color.FromArgb(arguments.Count == 4 ? Convert(arguments[3]) : byte.MaxValue,
            Convert(arguments[0]), Convert(arguments[1]), Convert(arguments[2]));
    }

    public static string[] WriteColor(Color color, IReadOnlyList<InstructionArgument> arguments)
    {
        if (!IsColor(arguments)) throw new ArgumentException("The semantic color must contain three or four f32/u8 operands.");
        // ColorDialog edits RGB only. Keep an authored alpha operand unchanged.
        var channels = new[] { color.R, color.G, color.B, ReadColor(arguments).A };
        return arguments.Select((argument, index) => argument.Type == "u8"
            ? channels[index].ToString(CultureInfo.InvariantCulture)
            : (channels[index] / 255f).ToString("R", CultureInfo.InvariantCulture)).ToArray();
    }
}
