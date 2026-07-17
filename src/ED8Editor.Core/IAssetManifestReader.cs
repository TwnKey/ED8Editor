namespace ED8Editor.Core;

public interface IAssetManifestReader
{
    AssetManifest Read(IPackageArchive archive, string expectedAssetId);
}
