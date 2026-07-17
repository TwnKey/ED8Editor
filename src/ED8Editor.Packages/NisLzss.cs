using System.Buffers.Binary;

namespace ED8Editor.Packages;

internal static class NisLzss
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
}
