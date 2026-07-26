namespace ED8Editor.Viewer;

/// <summary>
/// Decodes OP16's authored delay for the editor's 60 Hz preview clock. The low
/// bit behavior comes directly from FUN_0064f420: at frame times below 20 ms,
/// odd values greater than one are stored as (value - 1) / 2.
/// </summary>
internal static class ScriptWaitDuration
{
    public const float PreviewFramesPerSecond = 60f;
    public const float SecondsPerPreviewFrame = 1f / PreviewFramesPerSecond;

    public static int DecodePreviewFrames(int encodedValue)
    {
        var value = unchecked((ushort)encodedValue);
        if ((value & 1) != 0 && value != 1)
            return (value - 1) / 2;
        return value;
    }
}
