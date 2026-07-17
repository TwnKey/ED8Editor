namespace ED8Editor.Core;

public interface IPackageArchive
{
    string SourcePath { get; }

    uint Magic { get; }

    IReadOnlyList<PackageEntry> Entries { get; }

    byte[] ReadEntry(PackageEntry entry);

    byte[] ReadEntry(string name);
}
