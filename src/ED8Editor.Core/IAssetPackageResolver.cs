namespace ED8Editor.Core;

public interface IAssetPackageResolver
{
    AssetResolution Resolve(string assetId, AssetVariantPreference preference);
}
