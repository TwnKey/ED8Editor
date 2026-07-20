using ED8Editor.Core;

namespace ED8Editor.Application;

public sealed class PhyreArchiveAssetResolver
{
    public PackageEntry? Resolve(IReadOnlyList<PackageEntry> entries, string assetReference)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(assetReference))
            throw new ArgumentException("The Phyre asset reference cannot be empty.", nameof(assetReference));

        var archiveName = Path.GetFileName(assetReference);
        return entries.FirstOrDefault(entry =>
            Matches(entry.Name, assetReference)
            || Matches(entry.Name, archiveName));
    }

    private static bool Matches(string entryName, string referenceName)
        => entryName.Equals(referenceName, StringComparison.OrdinalIgnoreCase)
            || entryName.Equals(referenceName + ".phyre", StringComparison.OrdinalIgnoreCase);
}
