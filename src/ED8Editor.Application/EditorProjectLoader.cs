using ED8Editor.Core;
using ED8Editor.Phyre;
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

    public AssetModelLoad LoadAsset(
        string assetId,
        string gameDataPath,
        AssetVariantPreference preference = AssetVariantPreference.English)
    {
        if (assetResolverFactory is null || packageArchiveReader is null || assetManifestReader is null || modelReader is null)
        {
            throw new InvalidOperationException("The project loader has no complete asset-loading pipeline.");
        }
        var resolution = assetResolverFactory.Create(gameDataPath).Resolve(assetId, preference);
        return LoadModel(LoadManifest(resolution));
    }

    public Task<IReadOnlyDictionary<string, CpuModel>> LoadEffectMetadataAsync(
        EditorSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.Run(() => LoadEffectMetadata(session, cancellationToken), cancellationToken);
    }

    private IReadOnlyDictionary<string, CpuModel> LoadEffectMetadata(
        EditorSession session,
        CancellationToken cancellationToken)
    {
        var updated = new Dictionary<string, CpuModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var load in session.AssetModels.Values.Where(value => value.Model is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.AssetManifests.TryGetValue(load.AssetId, out var manifest)
                || manifest.Manifest is null || packageArchiveReader is null) continue;
            var archive = packageArchiveReader.Read(manifest.Manifest.SourcePackagePath);
            var model = BindEffectMetadata(load.Model!, archive);
            if (model.Materials.Any(value => value.EffectSwitches is not null)) updated[load.AssetId] = model;
        }
        return updated;
    }

    private CpuModel BindEffectMetadata(CpuModel model, IPackageArchive archive)
    {
        var reader = new PhyreEffectRenderPassReader();
        var passResolver = new PhyreMaterialRenderPassResolver();
        var assetResolver = new PhyreArchiveAssetResolver();
        var effects = new Dictionary<string, PhyreEffectMetadata>(StringComparer.OrdinalIgnoreCase);
        var materials = model.Materials.Select(material =>
        {
            if (material.EffectAssetName is null) return material;
            var entry = assetResolver.Resolve(archive.Entries, material.EffectAssetName);
            if (entry is null) return material;
            if (!effects.TryGetValue(entry.Name, out var effect))
            {
                effect = reader.ReadMetadata(archive.ReadEntry(entry));
                effects.Add(entry.Name, effect);
            }
            return passResolver.Resolve(material, effect);
        }).ToArray();
        return model with { Materials = materials };
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
                var textureIndices = textures
                    .Select((value, index) => (value.Name, Index: index))
                    .ToDictionary(value => value.Name, value => value.Index, StringComparer.OrdinalIgnoreCase);
                var materials = model.Materials.Select(material => BindMaterialTextures(material, textureIndices)).ToArray();
                model = model with { Materials = materials, Textures = textures };
            }
            return new AssetModelLoad(load.AssetId, AssetModelLoadStatus.Loaded, model, null);
        }
        catch (IOException exception)
        {
            return new AssetModelLoad(load.AssetId, AssetModelLoadStatus.Invalid, null, exception.Message);
        }
    }

    private static CpuMaterial BindMaterialTextures(
        CpuMaterial material,
        IReadOnlyDictionary<string, int> textureIndices)
    {
        var bindings = material.SourceTextureReferences
            .Where(value => textureIndices.ContainsKey(value.Value))
            .ToDictionary(value => value.Key, value => textureIndices[value.Value], StringComparer.Ordinal);
        var diffuse = bindings.FirstOrDefault(value =>
            value.Key.Equals("DiffuseMapSampler", StringComparison.OrdinalIgnoreCase)
            || value.Key.Equals("DiffuseSampler", StringComparison.OrdinalIgnoreCase));
        if (diffuse.Key is null)
        {
            diffuse = bindings.FirstOrDefault(value =>
                value.Key.Contains("diffuse", StringComparison.OrdinalIgnoreCase)
                && !value.Key.Contains("spec", StringComparison.OrdinalIgnoreCase));
        }

        return material with
        {
            BaseColorTextureIndex = diffuse.Key is null ? null : diffuse.Value,
            TextureBindings = bindings,
        };
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
