using ED8Editor.Core;

namespace ED8Editor.Packages;

internal sealed class PkgArchive : IPackageArchive
{
    private readonly IReadOnlyDictionary<string, PackageEntry> entriesByName;

    public PkgArchive(string sourcePath, uint magic, IReadOnlyList<PackageEntry> entries)
    {
        SourcePath = sourcePath;
        Magic = magic;
        Entries = entries;
        entriesByName = entries.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public string SourcePath { get; }

    public uint Magic { get; }

    public IReadOnlyList<PackageEntry> Entries { get; }

    public byte[] ReadEntry(string name)
    {
        if (!entriesByName.TryGetValue(name, out var entry))
        {
            throw new FileNotFoundException(
                $"Entry '{name}' was not found in package '{SourcePath}'.",
                name);
        }

        return ReadEntry(entry);
    }

    public byte[] ReadEntry(PackageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Index < 0
            || entry.Index >= Entries.Count
            || !Equals(Entries[entry.Index], entry))
        {
            throw new ArgumentException("The entry does not belong to this package.", nameof(entry));
        }

        if (entry.StoredSize > int.MaxValue)
        {
            throw new InvalidPackageException(
                $"PKG entry '{entry.Name}' is too large to extract in memory.");
        }

        var storedData = new byte[entry.StoredSize];
        using (var stream = File.Open(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(storedData);
        }

        return entry.CompressionType switch
        {
            PackageCompressionType.None => ReadUncompressed(entry, storedData),
            PackageCompressionType.NisLzss => NisLzss.Decompress(
                storedData,
                entry.UncompressedSize,
                entry.Name),
            PackageCompressionType.PlatformSpecific => throw new NotSupportedException(
                $"PKG compression type 2 is not supported for entry '{entry.Name}'."),
            _ => throw new InvalidPackageException(
                $"PKG entry '{entry.Name}' has unknown compression type {entry.CompressionType}."),
        };
    }

    private static byte[] ReadUncompressed(PackageEntry entry, byte[] storedData)
    {
        if (entry.StoredSize != entry.UncompressedSize)
        {
            throw new InvalidPackageException(
                $"Uncompressed PKG entry '{entry.Name}' has mismatched sizes.");
        }

        return storedData;
    }
}
