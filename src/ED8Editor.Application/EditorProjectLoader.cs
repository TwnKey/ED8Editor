using ED8Editor.Core;
using ED8Editor.ScriptHeaders;

namespace ED8Editor.Application;

public sealed class EditorProjectLoader
{
    private readonly ScriptBootstrapper scriptBootstrapper;
    private readonly IMapSceneReader mapSceneReader;
    private readonly IAssetPackageResolverFactory? assetResolverFactory;
    private readonly IPackageArchiveReader? packageArchiveReader;
    private readonly IAssetManifestReader? assetManifestReader;
    private readonly IPhyreModelReader? modelReader;
    private readonly IPhyreTextureReader? textureReader;

    public EditorProjectLoader(
        IMapSceneReader mapSceneReader,
        IAssetPackageResolverFactory? assetResolverFactory = null,
        IPackageArchiveReader? packageArchiveReader = null,
        IAssetManifestReader? assetManifestReader = null,
        IPhyreModelReader? modelReader = null,
        IPhyreTextureReader? textureReader = null,
        ScriptBootstrapper? scriptBootstrapper = null)
    {
        this.mapSceneReader = mapSceneReader ?? throw new ArgumentNullException(nameof(mapSceneReader));
        this.assetResolverFactory = assetResolverFactory;
        this.packageArchiveReader = packageArchiveReader;
        this.assetManifestReader = assetManifestReader;
        this.modelReader = modelReader;
        this.textureReader = textureReader;
        this.scriptBootstrapper = scriptBootstrapper ?? new ScriptBootstrapper();
    }

    public EditorSession OpenScript(string scriptPath, string? explicitGameDataPath = null)
    {
        var script = scriptBootstrapper.Open(scriptPath, explicitGameDataPath);
        var map = script.MapOpsPath is null ? null : mapSceneReader.Read(script.MapOpsPath);
        var resolutions = ResolveAssets(script, map);
        var manifests = LoadManifests(resolutions);
        var models = LoadModels(manifests);
        return new EditorSession(script, map, resolutions, manifests, models);
    }

    private IReadOnlyDictionary<string, AssetModelLoad> LoadModels(
        IReadOnlyDictionary<string, AssetManifestLoad> manifests)
    {
        if (packageArchiveReader is null || modelReader is null)
        {
            return new Dictionary<string, AssetModelLoad>(StringComparer.OrdinalIgnoreCase);
        }

        return manifests.Values.ToDictionary(
            manifest => manifest.AssetId,
            LoadModel,
            StringComparer.OrdinalIgnoreCase);
    }

    private AssetModelLoad LoadModel(AssetManifestLoad load)
    {
        var manifest = load.Manifest;
        var primaryAsset = manifest?.PrimaryAsset;
        var resource = primaryAsset?.Resources.FirstOrDefault(value => value.Kind == AssetResourceKind.Model);
        if (load.Status != AssetManifestLoadStatus.Loaded || manifest is null || primaryAsset is null || resource is null)
        {
            return new AssetModelLoad(load.AssetId, AssetModelLoadStatus.Missing, null, "No primary model resource was resolved.");
        }

        try
        {
            var archive = packageArchiveReader!.Read(manifest.SourcePackagePath);
            var model = modelReader!.Read(load.AssetId, archive.ReadEntry(resource.ArchiveEntryName));
            if (textureReader is not null)
            {
                var textures = primaryAsset.Resources
                    .Where(value => value.Kind == AssetResourceKind.Texture)
                    .Select(value => textureReader.Read(
                        Path.GetFileNameWithoutExtension(value.ArchiveEntryName),
                        archive.ReadEntry(value.ArchiveEntryName)))
                    .ToArray();
                model = model with { Textures = textures };
            }
            return new AssetModelLoad(load.AssetId, AssetModelLoadStatus.Loaded, model, null);
        }
        catch (IOException exception)
        {
            return new AssetModelLoad(load.AssetId, AssetModelLoadStatus.Invalid, null, exception.Message);
        }
    }

    private IReadOnlyDictionary<string, AssetManifestLoad> LoadManifests(
        IReadOnlyDictionary<string, AssetResolution> resolutions)
    {
        if (packageArchiveReader is null || assetManifestReader is null)
        {
            return new Dictionary<string, AssetManifestLoad>(StringComparer.OrdinalIgnoreCase);
        }

        return resolutions.Values.ToDictionary(
            resolution => resolution.AssetId,
            LoadManifest,
            StringComparer.OrdinalIgnoreCase);
    }

    private AssetManifestLoad LoadManifest(AssetResolution resolution)
    {
        if (resolution.SelectedPackage is null)
        {
            return new AssetManifestLoad(
                resolution.AssetId,
                AssetManifestLoadStatus.Missing,
                null,
                "No package was resolved for this asset.");
        }

        try
        {
            var archive = packageArchiveReader!.Read(resolution.SelectedPackage.Path);
            var manifest = assetManifestReader!.Read(archive, resolution.AssetId);
            if (manifest.PrimaryAsset is null)
            {
                return new AssetManifestLoad(
                    resolution.AssetId,
                    AssetManifestLoadStatus.Invalid,
                    manifest,
                    $"No manifest symbol matches '{resolution.AssetId}'.");
            }

            return new AssetManifestLoad(
                resolution.AssetId,
                AssetManifestLoadStatus.Loaded,
                manifest,
                null);
        }
        catch (FileNotFoundException exception)
        {
            return new AssetManifestLoad(
                resolution.AssetId,
                AssetManifestLoadStatus.Missing,
                null,
                exception.Message);
        }
        catch (IOException exception)
        {
            return new AssetManifestLoad(
                resolution.AssetId,
                AssetManifestLoadStatus.Invalid,
                null,
                exception.Message);
        }
    }

    private IReadOnlyDictionary<string, AssetResolution> ResolveAssets(
        ScriptOpenResult script,
        MapScene? map)
    {
        if (assetResolverFactory is null || script.GameDataPath is null || map is null)
        {
            return new Dictionary<string, AssetResolution>(StringComparer.OrdinalIgnoreCase);
        }

        var resolver = assetResolverFactory.Create(script.GameDataPath);
        var preference = UsesEnglishAssets(script.Header.SourcePath)
            ? AssetVariantPreference.English
            : AssetVariantPreference.Base;

        return map.Props
            .Select(prop => prop.AssetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                assetId => assetId,
                assetId => resolver.Resolve(assetId, preference),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool UsesEnglishAssets(string scriptPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(scriptPath)!);
        while (directory is not null)
        {
            if (directory.Name.Equals("dat_us", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (directory.Name.Equals("dat", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            directory = directory.Parent;
        }

        return false;
    }
}
