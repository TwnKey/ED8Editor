using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Packages;

/// <summary>
/// Writes a .pkg archive: the magic, one 80-byte record per entry (a 64-byte
/// name, the uncompressed size, the stored size, the offset and the compression
/// flag) and then the entry data, laid end to end after the table.
///
/// Entries are stored uncompressed. The loader reads that as well as it reads
/// the game's own NISLZSS entries — the flag says which — and it keeps a
/// compressor out of the trust chain of every mod this editor ships.
/// </summary>
public sealed class PkgArchiveWriter
{
    private const int HeaderSize = 8;
    private const int EntrySize = 80;
    private const int NameSize = 64;

    /// <summary>The magic the game's own effect-texture packages carry.</summary>
    public const uint DefaultMagic = 0x59679451;

    public byte[] Write(uint magic, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var offsets = new int[entries.Count];
        var offset = checked(HeaderSize + entries.Count * EntrySize);
        for (var index = 0; index < entries.Count; index++)
        {
            var (name, data) = entries[index];
            ArgumentNullException.ThrowIfNull(data);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A package entry needs a name.", nameof(entries));
            }
            // The name field keeps room for its terminator.
            if (Encoding.ASCII.GetByteCount(name) > NameSize - 1)
            {
                throw new ArgumentException(
                    $"Package entry name '{name}' does not fit in {NameSize - 1} bytes.",
                    nameof(entries));
            }
            offsets[index] = offset;
            offset = checked(offset + data.Length);
        }

        var package = new byte[offset];
        BinaryPrimitives.WriteUInt32LittleEndian(package, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(4), (uint)entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var (name, data) = entries[index];
            var record = HeaderSize + index * EntrySize;
            Encoding.ASCII.GetBytes(name, package.AsSpan(record, NameSize - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(
                package.AsSpan(record + NameSize), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                package.AsSpan(record + NameSize + 4), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                package.AsSpan(record + NameSize + 8), (uint)offsets[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(
                package.AsSpan(record + NameSize + 12), 0);
            data.CopyTo(package.AsSpan(offsets[index]));
        }
        return package;
    }

    public void Write(string path, uint magic, IReadOnlyList<(string Name, byte[] Data)> entries)
        => File.WriteAllBytes(path, Write(magic, entries));
}
