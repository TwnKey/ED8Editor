using System.Runtime.InteropServices;
using System.Text;

namespace ED8Editor.Viewer;

/// <summary>
/// One spoken line of a dialogue operand: the voice clip it plays (the id
/// t_voice.tbl resolves to an audio file), its text, and the bytes that
/// separated it from the previous line, kept so the operand round-trips.
/// </summary>
internal sealed record ScriptDialogLine(int? VoiceId, string Text, byte[] Separator);

/// <summary>
/// Converts a dialogue operand between its stored bytes and an editable text.
///
/// A dialogue is a byte stream mixing readable text with control bytes: it opens
/// with a 0x11 header carrying the line's message id, 0x01 breaks the line, 0x02
/// closes a page, 0x00 terminates. Anything that is not readable text is shown as
/// a <c>{XX}</c> hex escape, so the exact bytes always survive a round trip even
/// where the meaning of a control code is unknown — the writer only has to leave
/// the escapes alone and edit the words between them.
/// </summary>
internal static class ScriptDialogText
{
    private const byte LineBreak = 0x01;
    private const byte Header = 0x11;
    private const int HeaderPayload = 4;

    public static Encoding ResolveEncoding(string? scriptPath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Localised scripts live under dat_us and are UTF-8; the Japanese
        // originals under dat are cp932.
        var isEnglish = !string.IsNullOrWhiteSpace(scriptPath)
            && Path.GetFullPath(scriptPath)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("dat_us", StringComparison.OrdinalIgnoreCase));
        return isEnglish
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string Decode(ReadOnlySpan<byte> raw, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var text = new StringBuilder();
        var run = new List<byte>();
        for (var index = 0; index < raw.Length; index++)
        {
            var value = raw[index];
            if (value >= 0x20 && value != 0x7f)
            {
                run.Add(value);
                continue;
            }
            FlushRun(run, text, encoding);
            if (value == LineBreak)
            {
                text.Append('\n');
                continue;
            }
            Escape(text, value);
            // The four bytes after the header carry the line's message id. They
            // are often printable by accident, so escaping them keeps an edit of
            // the words from silently rewriting the id.
            if (value != Header) continue;
            for (var payload = 0; payload < HeaderPayload && index + 1 < raw.Length; payload++)
                Escape(text, raw[++index]);
        }
        FlushRun(run, text, encoding);
        return text.ToString();
    }

    private static void Escape(StringBuilder text, byte value)
        => text.Append('{').Append(value.ToString("X2")).Append('}');

    public static byte[] Encode(string text, Encoding encoding)
    {
        var bytes = EncodeBody(text, encoding);
        // The stream is length-delimited by its terminator: losing it while
        // editing would make the next operand unreadable.
        return bytes.Length != 0 && bytes[^1] == 0x00
            ? bytes
            : bytes.Append((byte)0x00).ToArray();
    }

    private static byte[] EncodeBody(string text, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        var bytes = new List<byte>(text.Length);
        var run = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\r') continue;
            if (value == '\n')
            {
                FlushText(run, bytes, encoding);
                bytes.Add(LineBreak);
                continue;
            }
            if (value == '{' && index + 3 < text.Length && text[index + 3] == '}'
                && byte.TryParse(
                    text.AsSpan(index + 1, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var escaped))
            {
                FlushText(run, bytes, encoding);
                bytes.Add(escaped);
                index += 3;
                continue;
            }
            run.Append(value);
        }
        FlushText(run, bytes, encoding);
        return bytes.ToArray();
    }

    /// <summary>
    /// Splits the operand into the lines it holds. One dialogue operand can carry
    /// several spoken lines, each opened by a 0x11 header naming its voice clip
    /// in t_voice.tbl and closed by a page break, so each line gets its own voice
    /// and its own text instead of being edited as one blob.
    /// </summary>
    public static IReadOnlyList<ScriptDialogLine> Split(ReadOnlySpan<byte> raw, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var lines = new List<ScriptDialogLine>();
        var body = new List<byte>();
        int? voice = null;
        var pending = new List<byte>();          // bytes between two lines
        var separator = new List<byte>();
        for (var index = 0; index < raw.Length; index++)
        {
            var value = raw[index];
            if (value == Header && index + HeaderPayload < raw.Length)
            {
                if (voice is not null || body.Count != 0)
                {
                    lines.Add(new ScriptDialogLine(
                        voice, Decode(CollectionsMarshal.AsSpan(body), encoding), separator.ToArray()));
                    body.Clear();
                    separator.Clear();
                }
                separator.AddRange(pending);
                pending.Clear();
                voice = raw[index + 1] | (raw[index + 2] << 8)
                    | (raw[index + 3] << 16) | (raw[index + 4] << 24);
                index += HeaderPayload;
                continue;
            }
            // A terminator or a page break closes the line; what follows it belongs
            // to the next one.
            if (value is 0x00 or 0x03 && body.Count != 0)
            {
                pending.Add(value);
                continue;
            }
            if (pending.Count != 0)
            {
                body.AddRange(pending);
                pending.Clear();
            }
            body.Add(value);
        }
        if (voice is not null || body.Count != 0)
        {
            lines.Add(new ScriptDialogLine(
                voice, Decode(CollectionsMarshal.AsSpan(body), encoding), separator.ToArray()));
        }
        return lines.Count == 0
            ? new[] { new ScriptDialogLine(null, Decode(raw, encoding), Array.Empty<byte>()) }
            : lines;
    }

    /// <summary>Rebuilds the operand from its lines.</summary>
    public static byte[] Join(IReadOnlyList<ScriptDialogLine> lines, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(encoding);
        var bytes = new List<byte>();
        foreach (var line in lines)
        {
            bytes.AddRange(line.Separator);
            if (line.VoiceId is { } voice)
            {
                bytes.Add(Header);
                bytes.Add((byte)voice);
                bytes.Add((byte)(voice >> 8));
                bytes.Add((byte)(voice >> 16));
                bytes.Add((byte)(voice >> 24));
            }
            bytes.AddRange(EncodeBody(line.Text, encoding));
        }
        if (bytes.Count == 0 || bytes[^1] != 0x00) bytes.Add(0x00);
        return bytes.ToArray();
    }

    /// <summary>One-line preview for the instruction block.</summary>
    public static string Summarize(ReadOnlySpan<byte> raw, Encoding encoding, int maximumLength = 60)
    {
        var text = Decode(raw, encoding)
            .Replace("\n", " ⏎ ", StringComparison.Ordinal);
        var readable = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            // Hide the escapes from the preview: only the words matter there.
            if (text[index] == '{' && index + 3 < text.Length && text[index + 3] == '}')
            {
                index += 3;
                continue;
            }
            readable.Append(text[index]);
        }
        var preview = readable.ToString().Trim();
        return preview.Length <= maximumLength ? preview : preview[..maximumLength] + "…";
    }

    /// <summary>
    /// Round-trips an authored line (t1000 EV_C05E14S02) and a Japanese one:
    /// editing a dialogue must never disturb the bytes around the words.
    /// </summary>
    internal static void VerifySmoke()
    {
        var english = Convert.FromHexString(
            "11F3F100004C6F6F6B73206C696B6520746865207261696E206D75737427"
            + "766501" + "73746F70706564207768696C6520492077617320696E736964652E0200");
        var utf8 = new UTF8Encoding(false, true);
        var decoded = Decode(english, utf8);
        if (!decoded.Contains("Looks like the rain must've", StringComparison.Ordinal)
            || !decoded.Contains('\n'))
        {
            throw new InvalidOperationException(
                $"An authored dialogue did not decode to its text: '{decoded}'.");
        }
        if (!decoded.StartsWith("{11}{F3}{F1}{00}{00}", StringComparison.Ordinal))
            throw new InvalidOperationException($"The message id was not kept apart: '{decoded}'.");
        if (!Encode(decoded, utf8).SequenceEqual(english))
            throw new InvalidOperationException("An unedited dialogue did not round-trip byte for byte.");
        var edited = Encode(
            decoded.Replace("rain", "snow", StringComparison.Ordinal), utf8);
        if (edited.Length != english.Length
            || !Decode(edited, utf8).Contains("snow", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Editing a dialogue did not keep its structure.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var japanese = Encoding.GetEncoding(
            932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var line = new List<byte> { 0x11, 0xF3, 0xF1, 0x00, 0x00 };
        line.AddRange(japanese.GetBytes("こんにちは"));
        line.AddRange(new byte[] { 0x02, 0x00 });
        var source = line.ToArray();
        var japaneseText = Decode(source, japanese);
        if (!japaneseText.Contains("こんにちは", StringComparison.Ordinal))
            throw new InvalidOperationException("A cp932 dialogue lost its text.");
        if (!Encode(japaneseText, japanese).SequenceEqual(source))
            throw new InvalidOperationException("A cp932 dialogue did not round-trip byte for byte.");

        // Two spoken lines in one operand: each keeps its own voice and text.
        var pair = Convert.FromHexString(
            "11FFF100002348233054592D596F75277265204D697374793F0203"
            + "1100F2000023453223234D3046726F6D204162656E642054696D653F2102 00".Replace(" ", string.Empty));
        var lines = Split(pair, utf8);
        if (lines.Count != 2)
            throw new InvalidOperationException($"A two-line dialogue split into {lines.Count} line(s).");
        if (lines[0].VoiceId != 0xF1FF || lines[1].VoiceId != 0xF200)
            throw new InvalidOperationException(
                $"The voice ids were misread ({lines[0].VoiceId}, {lines[1].VoiceId}).");
        if (!lines[0].Text.Contains("You're Misty?", StringComparison.Ordinal)
            || !lines[1].Text.Contains("From Abend Time?!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A split line lost its text.");
        }
        if (!Join(lines, utf8).SequenceEqual(pair))
            throw new InvalidOperationException("Splitting then joining a dialogue changed its bytes.");
    }

    private static void FlushRun(List<byte> run, StringBuilder text, Encoding encoding)
    {
        if (run.Count == 0) return;
        var bytes = run.ToArray();
        run.Clear();
        try
        {
            text.Append(encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            // Binary payload of a control code (a message id, a parameter): keep
            // it byte for byte instead of guessing a character for it.
            foreach (var value in bytes) text.Append('{').Append(value.ToString("X2")).Append('}');
        }
    }

    private static void FlushText(StringBuilder run, List<byte> bytes, Encoding encoding)
    {
        if (run.Length == 0) return;
        bytes.AddRange(encoding.GetBytes(run.ToString()));
        run.Clear();
    }
}
