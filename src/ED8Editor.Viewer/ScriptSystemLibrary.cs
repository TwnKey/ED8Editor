using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Loads the locale-matched scenario system script used by CALL variant 0x0A.
/// </summary>
internal sealed class ScriptSystemLibrary
{
    public ScriptSystemLibrary(
        string? scenarioPath,
        string? instructionDefinitionsPath)
    {
        if (string.IsNullOrWhiteSpace(scenarioPath)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(scenarioPath));
        if (directory is null) return;
        var path = Path.Combine(directory, "system.dat");
        if (!File.Exists(path)) return;
        try
        {
            Script = ScriptDecompiler.Decompile(path, instructionDefinitionsPath);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not decompile system script '{path}': {exception}");
        }
    }

    public DecompiledScript? Script { get; }
}
