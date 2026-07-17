using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Ops;
using ED8Editor.Packages;
using ED8Editor.Phyre;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ED8Editor.Cli <script.dat> [game-data-directory]");
    return 2;
}

try
{
    var session = new EditorProjectLoader(
            new OpsReader(),
            new GameAssetResolverFactory(),
            new PkgArchiveReader(),
            new AssetManifestReader(),
            new PhyreD3D11ModelReader(),
            new PhyreD3D11TextureReader())
        .OpenScript(args[0], args.Length == 2 ? args[1] : null);
    var result = session.Script;

    Console.WriteLine($"Script     : {result.Header.SourcePath}");
    Console.WriteLine($"Identifier : {result.Header.Identifier}");
    Console.WriteLine($"Kind       : {result.Header.Kind}");
    Console.WriteLine($"Target     : {result.Header.TargetKind}");
    Console.WriteLine($"Game data  : {result.GameDataPath ?? "not found"}");
    Console.WriteLine($"Map OPS    : {result.MapOpsPath ?? "not applicable or not found"}");
    Console.WriteLine($"Map props  : {session.Map?.Props.Count.ToString() ?? "not loaded"}");
    Console.WriteLine($"Assets     : {FormatAssetSummary(session.AssetResolutions.Values)}");
    Console.WriteLine($"PKG entries: {CountPackageEntries(session.AssetResolutions.Values)}");
    Console.WriteLine($"Manifests  : {FormatManifestSummary(session.AssetManifests.Values)}");
    Console.WriteLine($"Models     : {FormatModelSummary(session.AssetModels.Values)}");

    foreach (var prop in session.Map?.Props.Take(5) ?? Enumerable.Empty<ED8Editor.Core.MapProp>())
    {
        Console.WriteLine(
            $"  [{prop.SourceIndex}] {prop.AssetId} / {prop.Name} "
            + $"@ ({prop.Transform.Position.X:G6}, {prop.Transform.Position.Y:G6}, {prop.Transform.Position.Z:G6})");
    }

    foreach (var model in session.AssetModels.Values.Where(value => value.Status == ED8Editor.Core.AssetModelLoadStatus.Invalid))
    {
        Console.WriteLine($"  INVALID {model.AssetId}: {model.Error}");
    }

    return 0;
}

catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Cannot open script: {exception.Message}");
    return 1;
}

static string FormatAssetSummary(IEnumerable<ED8Editor.Core.AssetResolution> resolutions)
{
    var values = resolutions.ToArray();
    var resolved = values.Count(value => value.Status == ED8Editor.Core.AssetResolutionStatus.Resolved);
    var missing = values.Count(value => value.Status == ED8Editor.Core.AssetResolutionStatus.Missing);
    var ambiguous = values.Count(value => value.Status == ED8Editor.Core.AssetResolutionStatus.Ambiguous);
    return $"{resolved} resolved, {missing} missing, {ambiguous} ambiguous";
}

static int CountPackageEntries(IEnumerable<ED8Editor.Core.AssetResolution> resolutions)
{
    var reader = new PkgArchiveReader();
    return resolutions
        .Where(resolution => resolution.SelectedPackage is not null)
        .Sum(resolution => reader.Read(resolution.SelectedPackage!.Path).Entries.Count);
}

static string FormatManifestSummary(IEnumerable<ED8Editor.Core.AssetManifestLoad> manifests)
{
    var values = manifests.ToArray();
    var loaded = values.Count(value => value.Status == ED8Editor.Core.AssetManifestLoadStatus.Loaded);
    var missing = values.Count(value => value.Status == ED8Editor.Core.AssetManifestLoadStatus.Missing);
    var invalid = values.Count(value => value.Status == ED8Editor.Core.AssetManifestLoadStatus.Invalid);
    var models = values.Sum(value => value.Manifest?.PrimaryAsset?.Resources.Count(
        resource => resource.Kind == ED8Editor.Core.AssetResourceKind.Model) ?? 0);
    return $"{loaded} loaded, {missing} missing, {invalid} invalid, {models} primary models";
}

static string FormatModelSummary(IEnumerable<ED8Editor.Core.AssetModelLoad> models)
{
    var values = models.ToArray();
    var loaded = values.Count(value => value.Status == ED8Editor.Core.AssetModelLoadStatus.Loaded);
    var missing = values.Count(value => value.Status == ED8Editor.Core.AssetModelLoadStatus.Missing);
    var invalid = values.Count(value => value.Status == ED8Editor.Core.AssetModelLoadStatus.Invalid);
    var meshes = values.Sum(value => value.Model?.Meshes.Count ?? 0);
    var primitives = values.Sum(value => value.Model?.Meshes.Sum(mesh => mesh.Primitives.Count) ?? 0);
    var textures = values.Sum(value => value.Model?.Textures.Count ?? 0);
    return $"{loaded} loaded, {missing} missing, {invalid} invalid, {meshes} meshes, {primitives} primitives, {textures} textures";
}
