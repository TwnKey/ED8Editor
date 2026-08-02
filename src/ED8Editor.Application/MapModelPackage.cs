using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>
/// Writes the package loaded by a newly authored map. The package is
/// self-contained: ED8Editor authors its manifest, model, material effect and
/// D3D11 shader bytecode. No shipped package or model is used as a template.
/// </summary>
/// <summary>Which parameter block a replaced prop is written with.</summary>
public enum PropMaterial
{
    /// <summary>The prop's own, copied whole.</summary>
    Whole,

    /// <summary>
    /// A new default material authored against the selected effect's exact ABI.
    /// </summary>
    Minimal,

    /// <summary>None, which states a zero-byte constant buffer.</summary>
    None,
}

public static class MapModelPackage
{
    private const string NeutralTextureName = "ed8editor_neutral.dds.phyre";
/// <summary>
    /// The asset id the material asks for. It is a logical path, not the package
    /// entry name: the entry is "ed8editor_neutral.dds.phyre" while the id keeps the
    /// "map/images/" prefix every shipped model that renders uses.
    ///
    /// The prefix was removed once, on the grounds that the CS1AssetProcessor cluster
    /// names its texture bare — but that cluster has never rendered anything but a
    /// black surface, so it was the wrong thing to copy. O_T10LIG04, which does
    /// render, names "map/images/5t10ob00.dds".
    /// </summary>
    private const string NeutralTextureAsset = "ed8editor_neutral.dds";

    /// <param name="shippedShaderPackage">
    /// A package to take a compiled shader from, instead of the one ED8Editor
    /// compiles itself. This is a diagnostic, not the intended way to make a map: it
    /// answers whether an authored model that will not draw is failing because of its
    /// shader or because of the model. The model is authored from nothing either way —
    /// what changes is only which compiled program the material names.
    ///
    /// The name matters and is kept exactly. The game's shaders are called
    /// <c>ed8.fx#&lt;HASH&gt;.phyre</c>, and the hash is the shader's identity: it is
    /// how a material asks for one, so a copy under any other name is a shader nothing
    /// will ever ask for.
    /// </param>
    public static string WriteMinimal(
        ModProject project,
        string mapName,
        PhyreModelSource model,
        Action<string>? say = null,
        string? shippedShaderPackage = null,
        bool withCollision = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(model);

        var lower = mapName.ToLowerInvariant();
        // An imported file keeps its source name (for example r0510.glb).
        // Inside a p_collada cluster, however, exported asset IDs belong to the
        // cluster's logical DAE path.  A z9100 cluster exporting r0510 objects
        // has no visual scene for the asset M_Z9100 to instantiate.
        var authoredModel = model with { AssetName = lower };
        var packed = PhyreModelGeometryPacker.Pack(authoredModel);
        // Collision is the render mesh itself, which for a map is far heavier than
        // what the game ships: r0510 carries three simplified shapes totalling 75 KB
        // where ours is one shape of 1.8 MB. Being able to leave it out separates
        // "the map does not draw" from "the map's collision is rejected", which is
        // the order these have to be answered in.
        var collision = withCollision ? Collision(authoredModel) : null;
        say?.Invoke(collision is null
            ? "collision: none"
            : $"collision: {collision.Vertices.Count} vertices,"
                + $" {collision.Indices.Count / 3} triangles");

        var shaders = shippedShaderPackage is null
            ? new (string Name, byte[] Data)[]
                { (PhyreMinimalEffectWriter.FileName, PhyreMinimalEffectWriter.Write()) }
            : ShippedShaders(shippedShaderPackage);
        // The block the shader wants a material to fill, read from the package the
        // shader itself came from. A material that declares no parameter issues no
        // draw, so without this the model is written correctly and never appears.
        PhyreMaterialTable? parameters = null;
        if (shippedShaderPackage is not null)
        {
            parameters = ShaderParameters(shippedShaderPackage);
            say?.Invoke($"material: {parameters.DefinitionCount} parameters,"
                + $" {parameters.ParameterBufferSize}-byte buffer,"
                + $" {parameters.SamplerStates.Count} sampler(s)");
        }

        // The shader the parameter block belongs to, not whichever happens to be
        // first: a package can hold three, and a block paired with the wrong one
        // states a constant buffer size the shader does not have.
        var shaderAsset = parameters?.ShaderAsset
            ?? (shippedShaderPackage is null
                ? PhyreMinimalEffectWriter.AssetId
                : "shaders/" + Path.GetFileNameWithoutExtension(shaders[0].Name));

        var material = new PhyreShaderBinding(shaderAsset, parameters);
        // The same schema profile as a prop, when there is no collision to write.
        // A prop written this way loads; a map written the "native" way does not, and
        // the profile is one of the few things that has always differed between the
        // two paths. It could not be tried before: the profile's class table has no
        // physics classes, so a map carrying collision could not use it.
        var modelCluster = PhyreClusterAssembler.Assemble(
            PhyreModelClusterWriter.Contents(
                authoredModel, material, packed, collision,
                // Always the profile that loads. It used to fall back to the
                // "native" layout as soon as a map carried collision, because that
                // profile's class table has no physics classes — the table now
                // borrows them, so collision no longer changes the layout of
                // everything else.
                schemaProfile: PhyreSchemaProfile.FalcomAssetProcessor));
        say?.Invoke($"model: {modelCluster.Length} bytes, authored from the imported geometry");
        foreach (var (name, data) in shaders)
        {
            say?.Invoke($"shader: {name} — {data.Length} bytes, "
                + (shippedShaderPackage is null
                    ? "authored and compiled by ED8Editor"
                    : "taken compiled from " + Path.GetFileName(shippedShaderPackage)));
        }
        say?.Invoke($"the material asks for '{shaderAsset}'");

        // Only the shader the material asks for. A shipped map's package holds five,
        // but they belong to its thirteen materials and to a second asset — its
        // minimap, M_R0510_MI, which we do not write. Carrying them all made our
        // manifest declare a minimap shader with no minimap to use it, a dangling
        // declaration the shipped file never has.
        if (shippedShaderPackage is not null)
        {
            var asked = Path.GetFileName(shaderAsset) + ".phyre";
            var only = shaders
                .Where(value => value.Name.Equals(asked, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (only.Length != 0)
            {
                foreach (var (name, _) in shaders.Except(only))
                {
                    say?.Invoke($"  laisse de cote : {name}");
                }
                shaders = only;
            }
        }

        // The textures the material names, from the package the material came from.
        // A map took its material from a shipped package and inherited that
        // material's texture imports — "map/images/8r05gr00.dds" and its like — while
        // shipping none of them and declaring none in the manifest. The prop path
        // carries them; this one never did, so every authored map has been asking the
        // loader for a texture that is nowhere in the file.
        var mapTextures = Array.Empty<(string Name, byte[] Data)>();
        if (shippedShaderPackage is not null && parameters is not null)
        {
            var wanted = parameters.Imports
                .Where(import => import.Member != "m_effectVariant")
                .Select(import => Path.GetFileName(import.Asset))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var source = new PkgArchiveReader().Read(shippedShaderPackage);
            mapTextures = source.Entries
                .Where(entry =>
                    entry.Name.EndsWith(".dds.phyre", StringComparison.OrdinalIgnoreCase)
                    && wanted.Contains(Path.GetFileNameWithoutExtension(entry.Name)))
                .Select(entry => (entry.Name, Data: source.ReadEntry(entry)))
                .ToArray();
            foreach (var (name, data) in mapTextures)
            {
                say?.Invoke($"texture: {name} — {data.Length} bytes, from "
                    + Path.GetFileName(shippedShaderPackage));
            }
            var missing = wanted
                .Where(file => mapTextures.All(texture =>
                    !Path.GetFileNameWithoutExtension(texture.Name)
                        .Equals(file, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (missing.Length != 0)
            {
                throw new InvalidDataException(
                    $"The material names {string.Join(", ", missing)}, which"
                    + $" '{Path.GetFileName(shippedShaderPackage)}' does not carry."
                    + " A map that ships neither the texture nor its declaration asks"
                    + " the loader for something the file does not contain.");
            }
        }

        var symbol = MapAuthoring.ModelAsset(mapName);
        var manifest = Manifest(
            symbol, lower,
            shaders.Select(value => value.Name).ToArray(),
            mapTextures.Select(value => value.Name).ToArray());
        var entries = new[]
        {
            ("asset_D3D11.xml", new System.Text.UTF8Encoding(false).GetBytes(manifest)),
            (lower + ".dae.phyre", modelCluster),
        }.Concat(mapTextures).Concat(shaders).ToArray();

        var destination = Path.Combine(
            project.GameDirectory, "data", "asset", "D3D11", symbol + ".pkg");
        project.CaptureOriginal(destination);
        // Use the same magic as the shipped package, not a hardcoded default
        var magic = shippedShaderPackage is not null
            ? new PkgArchiveReader().Read(shippedShaderPackage).Magic
            : PkgArchiveWriter.DefaultMagic;
        new PkgArchiveWriter().Write(destination, magic, entries);
        project.TrackSave(destination);
        say?.Invoke($"package: {entries.Length} entries — manifest, model and"
            + $" {shaders.Length} shader(s)");
        return destination;
    }

    /// <summary>
    /// The compiled shaders of a shipped package, kept under the names they have.
    /// </summary>
    /// <summary>
    /// Replaces a shipped prop's package with an authored model, keeping the prop's
    /// own shader and parameter block.
    ///
    /// This exists to tell two failures apart. A map that draws nothing could be a
    /// model this tool writes wrongly, or a map that is linked wrongly — and nothing
    /// distinguishes them while both are new. A prop is neither: the map around it
    /// already draws, the prop already draws, and its asset id, its place in the
    /// scene, its shader and its material all stay exactly as they were. The only
    /// thing that changes is the geometry, so what appears — or does not — is about
    /// the geometry alone.
    /// </summary>
    /// <param name="withMaterial">
    /// Whether to carry the prop's parameter block. Setting it aside is a bisection,
    /// not a mode: a map written with an empty buffer drew nothing but LOADED, while
    /// every prop written with a copied block has crashed. Turning the one difference
    /// off says which of the two is at fault.
    /// </param>
    public static string WriteProp(
        ModProject project,
        string assetId,
        PhyreModelSource model,
        Action<string>? say = null,
        string? shaderPackage = null,
        PropMaterial material = PropMaterial.Minimal)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(model);

        var source = Path.Combine(
            project.GameDirectory, "data", "asset", "D3D11", assetId + ".pkg");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"There is no prop package called '{assetId}'.", source);
        }

        // Its own shader and its own parameter block: the point is to change one
        // thing, and the shader is not the thing being tested here.
        var original = project.OriginalCopyPath(
            project.RelativePathOf(source) ?? string.Empty) ?? source;
        var shaderSource = shaderPackage ?? original;
        var shaders = ShippedShaders(shaderSource);
        var parameters = shaderPackage != null
            ? ShaderParameters(shaderSource)
            : ShaderParameters(original);
        RequireShippedShader(parameters, shaders, shaderSource);
        // Its textures too. The parameter block names them, so a package without them
        // is a material pointing at something the file does not contain.  When an
        // explicit shader/material donor is used, its parameter block and its
        // textures are one indivisible dependency graph; taking the block from that
        // donor but retaining the target prop's textures creates dangling imports.
        var donor = new PkgArchiveReader().Read(original);
        var materialDonor = shaderPackage is null
            ? donor
            : new PkgArchiveReader().Read(shaderSource);
        // Only when something references them. A minimal material names no texture, so
        // shipping them anyway asks the loader to read data nothing asked for — and the
        // debugger caught the overrun in exactly that step, a memcpy running past the
        // end of a freshly allocated texture.
        var requiredWholeTextureFiles = parameters.Imports
            .Where(import => import.Member != "m_effectVariant")
            .Select(import => Path.GetFileName(import.Asset))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = material switch
        {
            PropMaterial.Whole => materialDonor.Entries
                .Where(entry =>
                    entry.Name.EndsWith(".dds.phyre", StringComparison.OrdinalIgnoreCase)
                    && requiredWholeTextureFiles.Contains(
                        Path.GetFileNameWithoutExtension(entry.Name)))
                .Select(entry => (entry.Name, Data: materialDonor.ReadEntry(entry)))
                .ToArray(),
            PropMaterial.Minimal => new[]
            {
                (
                    Name: NeutralTextureName,
                    Data: PhyreTextureClusterWriter.Write(
                        NeutralTextureAsset,
                        1,
                        1,
                        "ARGB8",
                        1,
                        new byte[] { 255, 255, 255, 255 }))
            },
            _ => Array.Empty<(string Name, byte[] Data)>(),
        };
        var packagedTextureAssets = material switch
        {
            PropMaterial.Whole => ResolvePackagedTextureAssets(parameters, textures),
            PropMaterial.Minimal => new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [NeutralTextureName] = NeutralTextureAsset,
            },
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        say?.Invoke($"keeping {assetId}'s own shader ({shaders.Length}) and material"
            + $" ({parameters.DefinitionCount} parameters, {parameters.SamplerStates.Count} samplers)");

        var folder = assetId.StartsWith("O_", StringComparison.OrdinalIgnoreCase)
            ? assetId[2..].ToLowerInvariant()
            : assetId.ToLowerInvariant();
        // Named after where the manifest puts it: a prop lives under map/objects.
        var authored = model with { AssetName = folder, AssetFolder = $"map/objects/{folder}" };
        var packed = PhyreModelGeometryPacker.Pack(authored);
        // Keep the exact switch values and sampler topology of the material the
        // source mesh actually binds. Effect metadata alone declares its ABI but
        // does not provide the material/context switches that select a render pass.
        // Only its texture capture is rebound to our authored neutral texture.
        var compatibleDefault = material == PropMaterial.Minimal
            ? PhyreMaterialTableReader.WithTextureAsset(
                parameters,
                NeutralTextureAsset)
            : null;
        var binding = material switch
        {
            PropMaterial.Whole => new PhyreShaderBinding(parameters.ShaderAsset, parameters),
            PropMaterial.Minimal => new PhyreShaderBinding(
                parameters.ShaderAsset, compatibleDefault),
            _ => new PhyreShaderBinding(parameters.ShaderAsset),
        };
        say?.Invoke(material == PropMaterial.Minimal
            ? $"material: source-bound neutral default"
                + $" ({compatibleDefault!.DefinitionCount} parameters,"
                + $" {compatibleDefault.ParameterBufferSize} bytes,"
                + $" {compatibleDefault.SamplerStates.Count} sampler states)"
            : $"material: {material}");
        say?.Invoke($"the material asks for '{parameters.ShaderAsset}'");
        var cluster = PhyreClusterAssembler.Assemble(
            PhyreModelClusterWriter.Contents(
                authored,
                binding,
                packed,
                // Keep the complete namespace emitted by Falcom's AssetProcessor.
                // CS1 reports the non-instantiated PIndexDataBlock interface as
                // unknown, then deliberately skips it while mapping the concrete
                // PIndexDataBlockD3D11 instances. Removing that namespace entry
                // changes the ordered authoring ABI before the runtime converter
                // has a chance to perform that mapping.
                schemaProfile: PhyreSchemaProfile.FalcomAssetProcessor));
        say?.Invoke($"model: {cluster.Length} bytes, authored from the imported geometry");

        var text = new System.Text.StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<fassets>\r\n");
        text.Append($"  <asset symbol=\"{assetId}\">\r\n");
        text.Append($"    <cluster path=\"data/D3D11/map/objects/{folder}/{folder}.dae.phyre\""
            + " type=\"p_collada\" />\r\n");
        foreach (var texture in textures)
        {
            text.Append($"    <cluster path=\"data/D3D11/"
                + $"{packagedTextureAssets[texture.Name]}.phyre\""
                + " type=\"p_texture\" />\r\n");
        }
        foreach (var shader in shaders)
        {
            text.Append($"    <cluster path=\"data/D3D11/shaders/{shader.Name}\""
                + " type=\"binary\" />\r\n");
        }
        text.Append("  </asset>\r\n</fassets>\r\n");

        var entries = new[]
        {
            ("asset_D3D11.xml", new System.Text.UTF8Encoding(false).GetBytes(text.ToString())),
            (folder + ".dae.phyre", cluster),
        }.Concat(textures).Concat(shaders).ToArray();

        project.CaptureOriginal(source);
        // Read the magic from the actual source file (just restored by the
        // user), not from a potentially stale project backup.
        var magic = new PkgArchiveReader().Read(source).Magic;
        new PkgArchiveWriter().Write(source, magic, entries);
        project.TrackSave(source);
        say?.Invoke($"package: {entries.Length} entries, written over {assetId}");
        return source;
    }

    /// <summary>
    /// Resolves every texture entry against the asset id already carried by the
    /// material which references it.
    ///
    /// A copied texture cluster carries that same id internally. Publishing it under
    /// a newly invented folder would therefore disagree with both the model import
    /// and the texture itself. Preserve the donor's actual asset namespace and fail
    /// if the package cannot satisfy it.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ResolvePackagedTextureAssets(
        PhyreMaterialTable material,
        IReadOnlyList<(string Name, byte[] Data)> textures)
    {
        var entriesByAssetFile = textures.ToDictionary(
            texture => Path.GetFileNameWithoutExtension(texture.Name),
            texture => texture.Name,
            StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in material.Imports.Where(
                     import => import.Member != "m_effectVariant"))
        {
            var assetFile = Path.GetFileName(import.Asset);
            if (!entriesByAssetFile.TryGetValue(assetFile, out var entryName))
            {
                throw new InvalidDataException(
                    $"Material texture import '{import.Asset}' has no matching"
                    + " texture cluster in the authored prop package.");
            }
            if (result.TryGetValue(entryName, out var existing)
                && !existing.Equals(import.Asset, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Texture entry '{entryName}' is referenced through two distinct"
                    + $" asset ids: '{existing}' and '{import.Asset}'.");
            }
            result[entryName] = import.Asset;
        }
        return result;
    }

    /// <summary>
    /// The shader's parameter block, taken from the model in the same package. The
    /// block belongs to the shader — it is byte-identical wherever that shader is
    /// used — so reading it beside the shader keeps the two from disagreeing.
    /// </summary>
    private static PhyreMaterialTable ShaderParameters(string packagePath)
    {
        var package = new PkgArchiveReader().Read(packagePath);
        var model = package.Entries.FirstOrDefault(entry =>
            entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"'{Path.GetFileName(packagePath)}' holds no model, so it cannot say what"
                + " its shader expects a material to supply.");
        return PhyreMaterialTableReader.Read(package.ReadEntry(model));
    }

    /// <summary>
    /// Refuses to write a package whose material names a shader the package does not
    /// carry.
    ///
    /// A shipped prop may get away with it: O_T10LIG03's material asks for
    /// ed8.fx#D506953A…, which its own package does not hold — the file is resolved
    /// from another package that happens to be loaded in the same map. Sweeping the
    /// game's 4341 packages finds that shader in nine of them, none of which is
    /// O_T10LIG03. Its own package ships ed8.fx#9B711195… instead.
    ///
    /// Copying such a material into an authored package inherits the dependency
    /// without the neighbour that satisfied it, and the result is a material bound to
    /// nothing: the model draws with no shader and no texture, which is the black
    /// surface this has been chasing. Saying so here costs a sentence; finding it in
    /// the game costs a launch.
    /// </summary>
    private static void RequireShippedShader(
        PhyreMaterialTable parameters,
        (string Name, byte[] Data)[] shaders,
        string shaderSource)
    {
        // The material names an asset ("shaders/ed8.fx#HASH"); a package names an
        // entry ("ed8.fx#HASH.phyre"). Compare on the part they share.
        var wanted = Path.GetFileName(parameters.ShaderAsset);
        foreach (var (name, _) in shaders)
        {
            if (name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException(
            $"The material asks for '{parameters.ShaderAsset}', which"
            + $" '{Path.GetFileName(shaderSource)}' does not carry."
            + $" It ships {string.Join(", ", shaders.Select(value => value.Name))}."
            + " Pick a donor whose own package holds the shader its material binds,"
            + " or the written package will name a shader nothing resolves.");
    }

    private static (string Name, byte[] Data)[] ShippedShaders(string packagePath)
    {
        var package = new PkgArchiveReader().Read(packagePath);
        var shaders = package.Entries
            .Where(entry => entry.Name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)
                && entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (entry.Name, Data: package.ReadEntry(entry)))
            .ToArray();
        if (shaders.Length == 0)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(packagePath)}' carries no compiled shader."
                + " A package's shaders are the entries named ed8.fx#<HASH>.phyre.");
        }
        return shaders;
    }

    private static ReadOnlyMemory<byte> EffectFor(
        string shaderAsset,
        IReadOnlyList<(string Name, byte[] Data)> shaders)
    {
        var entryName = Path.GetFileName(shaderAsset) + ".phyre";
        var matches = shaders
            .Where(value => value.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? matches[0].Data
            : throw new InvalidDataException(
                $"Material asks for '{shaderAsset}', but the package contains"
                + $" {matches.Length} matching compiled effects.");
    }

    private static string Manifest(
        string symbol, string lower, IReadOnlyList<string> shaders,
        IReadOnlyList<string>? textures = null)
    {
        static string Quote(string value) => "\"" + value + "\"";
        var text = new System.Text.StringBuilder();
        void Line(string value) => text.Append(value).Append("\r\n");
        Line("<?xml version=" + Quote("1.0") + " encoding=" + Quote("utf-8") + "?>");
        Line("<fassets>");
        Line("  <asset symbol=" + Quote(symbol) + ">");
        Line("    <cluster path="
            + Quote($"data/D3D11/map/{lower}/{lower}.dae.phyre")
            + " type=" + Quote("p_collada") + " />");
        // A texture is declared under map/images, which is the path its asset id
        // states: a shipped map names "map/images/8r05gr00.dds" and declares
        // "data/D3D11/map/images/8r05gr00.dds.phyre". The two have to agree.
        foreach (var texture in textures ?? Array.Empty<string>())
        {
            Line("    <cluster path=" + Quote($"data/D3D11/map/images/{texture}")
                + " type=" + Quote("p_texture") + " />");
        }
        foreach (var shader in shaders)
        {
            Line("    <cluster path=" + Quote($"data/D3D11/shaders/{shader}")
                + " type=" + Quote("binary") + " />");
        }
        Line("  </asset>");
        Line("</fassets>");
        return text.ToString();
    }

    private static PhyrePhysicsSource Collision(PhyreModelSource model)
    {
        var vertices = model.Meshes
            .SelectMany(mesh => mesh.Vertices.Select(vertex => vertex.Position))
            .ToArray();
        var indices = new List<int>();
        var vertexBase = 0;
        foreach (var mesh in model.Meshes)
        {
            foreach (var index in mesh.Indices) indices.Add(vertexBase + index);
            vertexBase += mesh.Vertices.Count;
        }
        return new PhyrePhysicsSource(vertices, indices);
    }
}
