using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Packages;

/// <summary>
/// Writes a .pkg archive, compressing entries with NISLZSS like the game's own.
/// Use <see cref="WriteRaw"/> when entries must keep their original encoding.
/// </summary>
public sealed class PkgArchiveWriter
{
    private const int HeaderSize = 8;
    private const int EntrySize = 80;
    private const int NameSize = 64;
    private const uint FlagNisLzss = 1;
    public const uint DefaultMagic = 0x59679451;

    /// <summary>
    /// Writes a package where every entry is NISLZSS-compressed from its
    /// decompressed form. The game reads flag 1 and decompresses.
    /// </summary>
    public byte[] Write(uint magic, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        var compressed = entries
            .Select(e => (e.Name, Uncompressed: e.Data, Stored: NisLzss.Compress(e.Data), Flag: FlagNisLzss))
            .ToArray();
        return Build(magic, compressed);
    }

    /// <summary>
    /// Writes a package where each entry names its own stored bytes and
    /// compression flag — for repacking: unchanged entries are passed through
    /// as-is, modified entries are NISLZSS-compressed.
    /// </summary>
    public byte[] WriteRaw(
        uint magic,
        IReadOnlyList<(string Name, byte[] Uncompressed, byte[] Stored, uint Flag)> entries)
        => Build(magic, entries);

    private static byte[] Build(
        uint magic,
        IReadOnlyList<(string Name, byte[] Uncompressed, byte[] Stored, uint Flag)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var offsets = new int[entries.Count];
        var offset = checked(HeaderSize + entries.Count * EntrySize);
        for (var index = 0; index < entries.Count; index++)
        {
            var (name, _, stored, _) = entries[index];
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A package entry needs a name.", nameof(entries));
            if (Encoding.ASCII.GetByteCount(name) > NameSize - 1)
                throw new ArgumentException($"Package entry name '{name}' does not fit in {NameSize - 1} bytes.", nameof(entries));
            offsets[index] = offset;
            offset = checked(offset + stored.Length);
        }

        var package = new byte[offset];
        BinaryPrimitives.WriteUInt32LittleEndian(package, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(4), (uint)entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var (name, uncompressed, stored, flag) = entries[index];
            var record = HeaderSize + index * EntrySize;
            Encoding.ASCII.GetBytes(name, package.AsSpan(record, NameSize - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(record + NameSize), (uint)uncompressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(record + NameSize + 4), (uint)stored.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(record + NameSize + 8), (uint)offsets[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(record + NameSize + 12), flag);
            stored.CopyTo(package.AsSpan(offsets[index]));
        }
        return package;
    }

    public void Write(string path, uint magic, IReadOnlyList<(string Name, byte[] Data)> entries)
        => File.WriteAllBytes(path, Write(magic, entries));

    public void Write(string path, uint magic,
        IReadOnlyList<(string Name, byte[] Uncompressed, byte[] Stored, uint Flag)> entries)
        => File.WriteAllBytes(path, Build(magic, entries));

    /// <summary>Convenience overload that calls <see cref="WriteRaw"/>.</summary>
    public void WriteRaw(string path, uint magic,
        IReadOnlyList<(string Name, byte[] Uncompressed, byte[] Stored, uint Flag)> entries)
        => Write(path, magic, entries);
}
