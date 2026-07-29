using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Ops;
using ED8Editor.Packages;
using ED8Editor.Phyre;

var renderDiagnostics = args.Length > 0 && args[0] == "--render-diagnostics";
var firstArgument = renderDiagnostics ? 1 : 0;
if (args.Length - firstArgument is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ED8Editor.Cli <script.dat> [game-data-directory]");
    Console.Error.WriteLine("       ED8Editor.Cli --render-diagnostics <script.dat> [game-data-directory]");
    return 2;
}

try
{
    var loader = new EditorProjectLoader(
            new OpsReader(),
            new GameAssetResolverFactory(),
            new PkgArchiveReader(),
            new AssetManifestReader(),
            new PhyreD3D11ModelReader(),
            new PhyreD3D11TextureReader());
    var session = loader.OpenScript(
        args[firstArgument],
        args.Length > firstArgument + 1 ? args[firstArgument + 1] : null);
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

    if (session.Map is { } map)
    {
        Console.WriteLine(
            $"Indicators : {map.Volumes.Count} volumes, {map.Points.Count} points, "
            + $"{map.Sounds.Count} sounds, {map.Lights.Count} lights");
        foreach (var volume in map.Volumes
                     .OrderByDescending(value => value.Transform.Scale.LengthSquared())
                     .Take(5))
        {
            Console.WriteLine(
                $"  VOLUME [{volume.SourceIndex}] {volume.Kind} "
                + $"scale=({volume.Transform.Scale.X:G6}, {volume.Transform.Scale.Y:G6}, {volume.Transform.Scale.Z:G6}) "
                + $"position=({volume.Transform.Position.X:G6}, {volume.Transform.Position.Y:G6}, {volume.Transform.Position.Z:G6})");
        }
        foreach (var sound in map.Sounds.OrderByDescending(value => value.Range).Take(3))
        {
            Console.WriteLine(
                $"  SOUND [{sound.SourceIndex}] kind={sound.Kind} group={sound.GroupId} range={sound.Range:G6} "
                + $"scale=({sound.SourceScale.X:G6}, {sound.SourceScale.Y:G6}, {sound.SourceScale.Z:G6}) "
                + $"name={sound.SoundName}");
        }
        foreach (var point in map.Points.Where(value =>
                     value.SourceAttributes.GetValueOrDefault("type") is "5" or "7"))
        {
            Console.WriteLine(
                $"  LOOKPOINT [{point.SourceIndex}] {point.Name} "
                + string.Join(" ", point.SourceAttributes.Select(value =>
                    $"{value.Key}={value.Value}")));
        }
        foreach (var light in map.Lights.OrderByDescending(value => value.OuterRange).Take(3))
        {
            Console.WriteLine($"  LIGHT [{light.SourceIndex}] range={light.OuterRange:G6}");
        }
    }

    foreach (var model in session.AssetModels.Values.Where(value => value.Status == ED8Editor.Core.AssetModelLoadStatus.Invalid))
    {
        Console.WriteLine($"  INVALID {model.AssetId}: {model.Error}");
    }

    if (renderDiagnostics)
    {
        var effectModels = await loader.LoadEffectMetadataAsync(session);
        PrintRenderDiagnostics(session, effectModels);
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
    var materials = values.Sum(value => value.Model?.Materials.Count ?? 0);
    var references = values.Sum(value => value.Model?.Materials.Sum(material => material.SourceTextureReferences.Count) ?? 0);
    var bindings = values.Sum(value => value.Model?.Materials.Sum(material => material.TextureBindings.Count) ?? 0);
    var textures = values.Sum(value => value.Model?.Textures.Count ?? 0);
    return $"{loaded} loaded, {missing} missing, {invalid} invalid, {meshes} meshes, {primitives} primitives, {materials} materials, {textures} textures, {bindings}/{references} bindings";
}

static void PrintRenderDiagnostics(
    ED8Editor.Core.EditorSession session,
    IReadOnlyDictionary<string, ED8Editor.Core.CpuModel> effectModels)
{
    foreach (var load in session.AssetModels.Values
                 .Where(value => value.Model is not null)
                 .OrderBy(value => value.AssetId, StringComparer.OrdinalIgnoreCase))
    {
        var model = effectModels.GetValueOrDefault(load.AssetId) ?? load.Model!;
        var mirroredMeshes = model.Meshes.Count(value => value.LocalTransform.GetDeterminant() < 0f);
        var collisionMeshes = model.Meshes.Count(value => value.Purpose == ED8Editor.Core.CpuMeshPurpose.Collision);
        var renderedPrimitives = model.Meshes
            .Where(value => value.Purpose == ED8Editor.Core.CpuMeshPurpose.Render)
            .SelectMany(value => value.Primitives)
            .ToArray();
        var texturedWithoutUv = renderedPrimitives.Count(primitive =>
            (uint)primitive.MaterialIndex < model.Materials.Count
            && model.Materials[primitive.MaterialIndex].BaseColorTextureIndex is not null
            && !primitive.VertexBuffers.SelectMany(value => value.Attributes)
                .Any(value => value.Semantic == ED8Editor.Core.VertexSemantic.TextureCoordinate));
        Console.WriteLine(
            $"MODEL {model.AssetId}: {model.Meshes.Count} meshes, {collisionMeshes} collision, {mirroredMeshes} mirrored, "
            + $"{model.Materials.Count} materials, {model.Textures.Count} textures, "
            + $"{texturedWithoutUv}/{renderedPrimitives.Length} textured primitives without UV");
        if (model.Meshes.Count <= 10)
        {
            foreach (var texture in model.Textures)
            {
                Console.WriteLine(
                    $"  TEXTURE {texture.Name}: {texture.Width}x{texture.Height}, {texture.MipCount} mips, "
                    + $"format={texture.Format}, bytes={texture.Data.Length}");
            }
            foreach (var mesh in model.Meshes.Where(value => value.Purpose == ED8Editor.Core.CpuMeshPurpose.Render))
            {
                for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
                {
                    var primitive = mesh.Primitives[primitiveIndex];
                    var attributes = string.Join(", ", primitive.VertexBuffers
                        .SelectMany(value => value.Attributes)
                        .Select(value => $"{value.Semantic}{value.SemanticIndex}:{value.SourceFormat}"));
                    Console.WriteLine(
                        $"  PRIMITIVE {mesh.Name}[{primitiveIndex}] material={primitive.MaterialIndex}: {attributes}");
                    foreach (var buffer in primitive.VertexBuffers)
                    {
                        var color = buffer.Attributes.FirstOrDefault(value =>
                            value.Semantic == ED8Editor.Core.VertexSemantic.Color
                            && value.SourceFormat == "Float32x4");
                        if (color is not null) Console.WriteLine($"    {DescribeFloat4Range(buffer, color)}");
                    }
                }
            }
        }
        foreach (var mesh in model.Meshes.Where(value =>
                     ED8Editor.Core.SceneEnvironmentVariantSelector.GetAuthoredVariant(value.Name) is not null))
        {
            Console.WriteLine(
                $"  VARIANT {ED8Editor.Core.SceneEnvironmentVariantSelector.GetAuthoredVariant(mesh.Name)}: {mesh.Name}");
        }
        for (var index = 0; index < model.Materials.Count; index++)
        {
            var material = model.Materials[index];
            var state = material.RenderPassState;
            var rasterizer = state?.RasterizerState;
            Console.WriteLine(
                $"  MAT {index}: texture={material.BaseColorTextureIndex?.ToString() ?? "none"} "
                + $"bindings={material.TextureBindings.Count}/{material.SourceTextureReferences.Count} "
                + $"effect={Path.GetFileName(material.EffectAssetName) ?? "none"} "
                + $"pass={material.ResolvedRenderPassName ?? material.RenderPassType ?? "unresolved"} "
                + $"phase={material.RenderPhase} blend={state?.BlendEnabled.ToString() ?? "unknown"} "
                + $"cull={rasterizer?.CullMode.ToString() ?? "unknown"} "
                + $"frontCCW={rasterizer?.FrontCounterClockwise.ToString() ?? "unknown"}");
            if (model.Meshes.Count <= 10)
            {
                Console.WriteLine(
                    $"    base=({material.BaseColor.X:G6},{material.BaseColor.Y:G6},{material.BaseColor.Z:G6},{material.BaseColor.W:G6}) "
                    + $"textures=[{string.Join(", ", material.SourceTextureReferences.Select(value => $"{value.Key}={value.Value}"))}] "
                    + $"switches=[{string.Join(", ", material.EffectSwitches?.Keys ?? Array.Empty<string>())}]");
            }
        }
    }
}

static string DescribeFloat4Range(
    ED8Editor.Core.CpuVertexBuffer buffer,
    ED8Editor.Core.CpuVertexAttribute attribute)
{
    var minimum = new System.Numerics.Vector4(float.PositiveInfinity);
    var maximum = new System.Numerics.Vector4(float.NegativeInfinity);
    for (var index = 0; index < buffer.VertexCount; index++)
    {
        var offset = checked(index * buffer.Stride + attribute.Offset);
        var value = new System.Numerics.Vector4(
            BitConverter.ToSingle(buffer.Data, offset),
            BitConverter.ToSingle(buffer.Data, offset + 4),
            BitConverter.ToSingle(buffer.Data, offset + 8),
            BitConverter.ToSingle(buffer.Data, offset + 12));
        minimum = System.Numerics.Vector4.Min(minimum, value);
        maximum = System.Numerics.Vector4.Max(maximum, value);
    }
    return $"COLOR min=({minimum.X:G5},{minimum.Y:G5},{minimum.Z:G5},{minimum.W:G5}) "
        + $"max=({maximum.X:G5},{maximum.Y:G5},{maximum.Z:G5},{maximum.W:G5})";
}
