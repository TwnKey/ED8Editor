using System.Text;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>
/// Voice lines declared by <c>t_voice.tbl</c>. A record of the "voice" category
/// is the identifier the dialogue header carries, followed by the name of the
/// audio file that plays it — so a line's id can be shown as a file name instead
/// of a number.
/// </summary>
internal sealed class ScriptVoiceTable
{
    private readonly Dictionary<int, string> filesById = new();

    private ScriptVoiceTable()
    {
    }

    public static ScriptVoiceTable Empty { get; } = new();

    public int Count => filesById.Count;

    public static ScriptVoiceTable Load(string? gameDataPath, string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) return Empty;
        var preferEnglish = !string.IsNullOrWhiteSpace(scriptPath)
            && Path.GetFullPath(scriptPath)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("dat_us", StringComparison.OrdinalIgnoreCase));
        var path = new[] { preferEnglish ? "dat_us" : "dat", preferEnglish ? "dat" : "dat_us" }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, "t_voice.tbl"))
            .FirstOrDefault(File.Exists);
        if (path is null) return Empty;
        var table = new ScriptVoiceTable();
        try
        {
            foreach (var entry in Cs1TableDocument.Read(path).Entries)
            {
                if (!entry.Category.Equals("voice", StringComparison.Ordinal)) continue;
                if (entry.Data.Length < 3) continue;
                var id = entry.Data[0] | (entry.Data[1] << 8);
                var end = Array.IndexOf(entry.Data, (byte)0, 2);
                if (end < 3) continue;
                table.filesById.TryAdd(id, Encoding.ASCII.GetString(entry.Data, 2, end - 2));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or IndexOutOfRangeException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read '{path}': {exception.Message}");
            return Empty;
        }
        return table;
    }

    public string? FindFile(int voiceId)
        => filesById.TryGetValue(voiceId, out var file) ? file : null;

    /// <summary>Locates the audio file of a line inside the installation.</summary>
    public string? FindAudioPath(string gameDataPath, int voiceId)
    {
        var file = FindFile(voiceId);
        if (file is null || string.IsNullOrWhiteSpace(gameDataPath)) return null;
        var voiceRoot = Path.Combine(gameDataPath, "voice");
        if (!Directory.Exists(voiceRoot)) return null;
        // The installation keeps the clips in data/voice/wav (and wav_jp for the
        // Japanese track); look there before walking the whole folder.
        foreach (var folder in new[] { "wav", "wav_jp", string.Empty })
        foreach (var extension in new[] { ".wav", ".ogg", ".at9", ".snd" })
        {
            var direct = Path.Combine(voiceRoot, folder, file + extension);
            if (File.Exists(direct)) return direct;
        }
        return Directory
            .EnumerateFiles(voiceRoot, file + ".*", SearchOption.AllDirectories)
            .FirstOrDefault();
    }
}
