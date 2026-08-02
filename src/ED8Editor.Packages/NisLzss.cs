using System.Buffers.Binary;

namespace ED8Editor.Packages;

public static class NisLzss
{
    private const int HeaderSize = 12;

    public static byte[] Decompress(
        ReadOnlySpan<byte> source,
        uint expectedUncompressedSize,
        string entryName)
    {
        if (source.Length < HeaderSize)
        {
            throw Error(entryName, "compressed stream is shorter than its 12-byte header");
        }

        var declaredUncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        var declaredStoredSize = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]);
        var escapeValue = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);

        if (declaredUncompressedSize != expectedUncompressedSize)
        {
            throw Error(
                entryName,
                $"declares {declaredUncompressedSize} output bytes, table declares {expectedUncompressedSize}");
        }

        if (declaredStoredSize != source.Length)
        {
            throw Error(entryName, $"declares {declaredStoredSize} stored bytes, received {source.Length}");
        }

        if (escapeValue > byte.MaxValue)
        {
            throw Error(entryName, $"has invalid escape value 0x{escapeValue:X8}");
        }

        if (expectedUncompressedSize > int.MaxValue)
        {
            throw Error(entryName, "is too large to extract in memory");
        }

        var destination = new byte[expectedUncompressedSize];
        var sourcePosition = HeaderSize;
        var destinationPosition = 0;
        var escape = (byte)escapeValue;

        while (sourcePosition < source.Length)
        {
            var value = source[sourcePosition++];
            if (value != escape)
            {
                WriteLiteral(destination, ref destinationPosition, value, entryName);
                continue;
            }

            if (sourcePosition >= source.Length)
            {
                throw Error(entryName, "ends in the middle of an escape token");
            }

            var encodedOffset = source[sourcePosition++];
            if (encodedOffset == escape)
            {
                WriteLiteral(destination, ref destinationPosition, escape, entryName);
                continue;
            }

            if (sourcePosition >= source.Length)
            {
                throw Error(entryName, "ends before a back-reference length");
            }

            var length = source[sourcePosition++];
            var offset = encodedOffset > escape ? encodedOffset - 1 : encodedOffset;
            if (offset == 0 || length == 0)
            {
                throw Error(entryName, "contains a zero-length or zero-offset back-reference");
            }

            if (offset > destinationPosition)
            {
                throw Error(entryName, "contains a back-reference before the start of its output");
            }

            if (length > offset)
            {
                throw Error(entryName, "contains an overlapping back-reference unsupported by the game format");
            }

            if (destinationPosition + length > destination.Length)
            {
                throw Error(entryName, "decompresses past its declared output size");
            }

            var copyStart = destinationPosition - offset;
            destination.AsSpan(copyStart, length).CopyTo(destination.AsSpan(destinationPosition, length));
            destinationPosition += length;
        }

        if (destinationPosition != destination.Length)
        {
            throw Error(
                entryName,
                $"produced {destinationPosition} bytes, expected {destination.Length}");
        }

        return destination;
    }

    private static void WriteLiteral(
        byte[] destination,
        ref int destinationPosition,
        byte value,
        string entryName)
    {
        if (destinationPosition >= destination.Length)
        {
            throw Error(entryName, "decompresses past its declared output size");
        }

        destination[destinationPosition++] = value;
    }

    private static InvalidPackageException Error(string entryName, string message) =>
        new($"PKG entry '{entryName}' {message}.");

    /// <summary>
    /// Compresses data with NISLZSS, returning it with the 12-byte header.
    /// Minimum match length is 4 bytes. Uses least-frequent-byte as escape key.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        var escape = LeastFrequentByte(source);
        var output = new MemoryStream();
        output.Write(BitConverter.GetBytes((uint)source.Length));
        output.Write(new byte[4]); // placeholder
        output.Write(BitConverter.GetBytes((uint)escape));

        var pos = 0;
        while (pos < source.Length)
        {
            var (bestOff, bestLen) = LongestMatch(source, pos);

            if (bestLen >= 4 && bestOff > 0)
            {
                var offsetByte = bestOff >= escape ? bestOff + 1 : bestOff;
                output.WriteByte(escape);
                output.WriteByte((byte)offsetByte);
                output.WriteByte((byte)bestLen);
                pos += bestLen;
            }
            else
            {
                var b = source[pos++];
                output.WriteByte(b);
                if (b == escape) output.WriteByte(escape);
            }
        }

        var result = output.ToArray();
        BitConverter.GetBytes((uint)result.Length).CopyTo(result, 4);
        return result;
    }

    private static (int offset, int length) LongestMatch(ReadOnlySpan<byte> src, int pos)
    {
        var bestOff = 0;
        var bestLen = 0;
        var maxOff = Math.Min(pos, 254);
        var minPos = Math.Max(0, pos - maxOff);
        var remaining = src.Length - pos;

        for (var cur = pos - 1; cur >= minPos; cur--)
        {
            if (src[cur] != src[pos]) continue;
            var len = 1;
            while (cur + len < pos && pos + len < src.Length
                && src[cur + len] == src[pos + len])
                len++;
            if (len > bestLen) { bestLen = len; bestOff = pos - cur; }
        }
        return (bestOff, Math.Min(bestLen, remaining));
    }

    private static byte LeastFrequentByte(ReadOnlySpan<byte> data)
    {
        var counts = new uint[256];
        foreach (var b in data) counts[b]++;
        byte best = 0xFC;
        var lowest = uint.MaxValue;
        for (var i = 0; i < 256; i++)
            if (counts[i] < lowest) { lowest = counts[i]; best = (byte)i; }
        return best;
    }
}
