using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.ScriptHeaders;

public sealed class ScriptHeaderReader
{
    public const uint ExpectedMagic = 0xABCDEF00;
    private const int FixedPreambleSize = 0x20;
    private const int MaximumIdentifierLength = 255;

    public ScriptHeader Read(string path)
    {
        EnsureNotNullOrWhiteSpace(path, nameof(path));

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream, Path.GetFullPath(path), ScriptClassifier.FromPath(path));
    }

    public ScriptHeader Read(Stream stream, string sourcePath, ScriptKind kind = ScriptKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureNotNullOrWhiteSpace(sourcePath, nameof(sourcePath));

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The script stream must be readable and seekable.", nameof(stream));
        }

        if (stream.Length < FixedPreambleSize + 1)
        {
            throw new InvalidScriptHeaderException("The file is too short to contain an ED8 script header.");
        }

        Span<byte> preamble = stackalloc byte[FixedPreambleSize];
        stream.Position = 0;
        stream.ReadExactly(preamble);

        // The second uint32 points to the script identifier. It is usually 0x20,
        // but t0600.dat proves it can point past tables stored at the beginning.
        var identifierOffset = BinaryPrimitives.ReadUInt32LittleEndian(preamble[0x04..0x08]);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(preamble[0x1c..0x20]);

        if (magic != ExpectedMagic)
        {
            throw new InvalidScriptHeaderException($"Unexpected script marker 0x{magic:X8}; expected 0x{ExpectedMagic:X8}.");
        }

        if (identifierOffset < FixedPreambleSize || identifierOffset >= stream.Length)
        {
            throw new InvalidScriptHeaderException($"Identifier offset 0x{identifierOffset:X} lies outside the file.");
        }

        var (identifier, identifierEndOffset) = ReadIdentifier(stream, identifierOffset);
        var targetKind = ScriptClassifier.TargetFor(kind);

        return new ScriptHeader(
            sourcePath,
            identifier,
            kind,
            targetKind,
            identifierOffset,
            identifierEndOffset,
            preamble.ToArray());
    }

    private static (string Identifier, uint EndOffset) ReadIdentifier(Stream stream, uint offset)
    {
        var available = checked((int)Math.Min(stream.Length - offset, MaximumIdentifierLength + 1L));
        var bytes = new byte[available];
        stream.Position = offset;
        stream.ReadExactly(bytes);

        var terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator <= 0)
        {
            throw new InvalidScriptHeaderException("The script identifier is empty or not null-terminated.");
        }

        for (var index = 0; index < terminator; index++)
        {
            var value = bytes[index];
            var valid = value is >= (byte)'a' and <= (byte)'z'
                or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'_'
                or (byte)'-';

            if (!valid)
            {
                throw new InvalidScriptHeaderException($"The script identifier contains invalid byte 0x{value:X2}.");
            }
        }

        var identifier = Encoding.ASCII.GetString(bytes, 0, terminator);
        return (identifier, checked(offset + (uint)terminator + 1));
    }

    private static void EnsureNotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }
    }
}
