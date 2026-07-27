using System.Text;

namespace ED8Editor.Viewer;

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
        // The stream is length-delimited by its terminator: losing it while
        // editing would make the next operand unreadable.
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
