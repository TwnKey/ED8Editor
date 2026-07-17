namespace ED8Editor.Core;

public interface IAssetPackageResolverFactory
{
    IAssetPackageResolver Create(string gameDataPath);
}
