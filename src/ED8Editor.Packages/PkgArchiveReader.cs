using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Packages;

public sealed class PkgArchiveReader : IPackageArchiveReader
{
    private const int HeaderSize = 8;
    private const int EntrySize = 80;
    private const int NameSize = 64;
    private const uint MaximumEntryCount = 100_000;

    public IPackageArchive Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (stream.Length < HeaderSize)
        {
            throw new InvalidPackageException($"Package '{fullPath}' is shorter than its header.");
        }

        var magic = reader.ReadUInt32();
        var entryCount = reader.ReadUInt32();
        if (entryCount > MaximumEntryCount)
        {
            throw new InvalidPackageException(
                $"Package '{fullPath}' declares unreasonable entry count {entryCount}.");
        }

        var tableEnd = HeaderSize + checked((long)entryCount * EntrySize);
        if (tableEnd > stream.Length)
        {
            throw new InvalidPackageException($"Package '{fullPath}' entry table lies outside the file.");
        }

        var entries = new PackageEntry[entryCount];
        var nameBuffer = new byte[NameSize];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Length; index++)
        {
            stream.ReadExactly(nameBuffer);
            var name = ReadName(nameBuffer, fullPath, index);
            var uncompressedSize = reader.ReadUInt32();
            var storedSize = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var rawCompressionType = reader.ReadUInt32();

            if (!names.Add(name))
            {
                throw new InvalidPackageException(
                    $"Package '{fullPath}' contains duplicate entry name '{name}'.");
            }

            if ((ulong)offset + storedSize > (ulong)stream.Length)
            {
                throw new InvalidPackageException(
                    $"Package '{fullPath}' entry '{name}' data lies outside the file.");
            }

            if (offset < tableEnd)
            {
                throw new InvalidPackageException(
                    $"Package '{fullPath}' entry '{name}' overlaps the entry table.");
            }

            if (rawCompressionType > (uint)PackageCompressionType.PlatformSpecific)
            {
                throw new InvalidPackageException(
                    $"Package '{fullPath}' entry '{name}' has unknown compression type {rawCompressionType}.");
            }

            entries[index] = new PackageEntry(
                index,
                name,
                uncompressedSize,
                storedSize,
                offset,
                (PackageCompressionType)rawCompressionType);
        }

        return new PkgArchive(fullPath, magic, entries);
    }

    private static string ReadName(byte[] buffer, string packagePath, int index)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        var length = terminator < 0 ? buffer.Length : terminator;
        if (length == 0)
        {
            throw new InvalidPackageException(
                $"Package '{packagePath}' entry {index} has an empty name.");
        }

        for (var position = 0; position < length; position++)
        {
            if (buffer[position] is < 0x20 or > 0x7e)
            {
                throw new InvalidPackageException(
                    $"Package '{packagePath}' entry {index} has a non-ASCII name.");
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }
}
