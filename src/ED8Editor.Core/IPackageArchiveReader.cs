namespace ED8Editor.Core;

public interface IPackageArchiveReader
{
    IPackageArchive Read(string path);
}
