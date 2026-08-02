using System.Xml.Linq;
using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>What a map's model package was made from, and what came out.</summary>
public sealed record MapPackageResult(
    string Path,
    string Symbol,
    int Entries,
    bool GeometryReplaced);

/// <summary>
/// Makes the model package a new map loads.
///
/// A map named <c>z9001</c> loads a package called <c>M_Z9001</c>, and inside it
/// the game expects a manifest that agrees with itself: a symbol, a
/// <c>p_collada</c> cluster for the model, and a <c>p_texture</c> cluster per
/// texture, each named by a path under <c>data/D3D11/map/&lt;map&gt;/</c>. Getting
/// any of those three names out of step is enough for the map to load nothing.
///
/// The package starts from one the game ships, for the same reason the
/// <c>.ops</c> does: its materials already bind shaders the engine compiled, its
/// textures exist, and — for a map — its physics objects are the collision. A
/// package built from nothing would have none of that. What is replaced is the
/// geometry, and only the geometry.
///
/// That replacement is the piece measured hardest: handing a model its own mesh
/// back reproduces the file byte for byte, on 11 of 11 readable map models and
/// 70 of 70 characters. Collision survives because it is never decoded.
/// </summary>
public sealed class MapPackageAuthoring
{
    private const string ManifestEntry = "asset_D3D11.xml";

    private readonly ModProject project;
    private readonly IPackageArchiveReader reader;

    public MapPackageAuthoring(ModProject project, IPackageArchiveReader reader)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Writes <c>M_&lt;NAME&gt;.pkg</c> from the package of <paramref name="baseMapName"/>.
    /// When <paramref name="model"/> is given, the model cluster carries that mesh
    /// instead of the one it came with.
    /// </summary>
    public MapPackageResult CreatePackage(
        string mapName,
        string baseMapName,
        PhyreModelSource? model = null)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            throw new ArgumentException("A map needs a name.", nameof(mapName));
        }

        var assets = Path.Combine(project.GameDirectory, "data", "asset", "D3D11");
        var source = Path.Combine(assets, MapAuthoring.ModelAsset(baseMapName) + ".pkg");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"No package for map '{baseMapName}'.", source);
        }

        var lower = mapName.ToLowerInvariant();
        var baseLower = baseMapName.ToLowerInvariant();
        var symbol = MapAuthoring.ModelAsset(mapName);
        var archive = reader.Read(source);

        var replaced = false;
        var entries = new List<(string Name, byte[] Data)>();
        foreach (var entry in archive.Entries)
        {
            var bytes = archive.ReadEntry(entry);
            if (entry.Name.Equals(ManifestEntry, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add((entry.Name, Manifest(bytes, symbol, baseLower, lower)));
                continue;
            }

            // The model cluster is the one entry named after the map, so it is
            // the one that has to be renamed with it.
            var name = entry.Name.Equals(baseLower + ".dae.phyre", StringComparison.OrdinalIgnoreCase)
                ? lower + ".dae.phyre"
                : entry.Name;
            if (model is not null && name.Equals(lower + ".dae.phyre", StringComparison.OrdinalIgnoreCase))
            {
                bytes = PhyreModelReplacement.Replace(bytes, model);
                replaced = true;
            }
            entries.Add((name, bytes));
        }

        if (model is not null && !replaced)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(source)}' holds no cluster named '{baseLower}.dae.phyre',"
                + " so there was nothing to put the mesh into.");
        }

        var destination = Path.Combine(assets, symbol + ".pkg");
        if (model is not null)
        {
            var problems = Problems(baseMapName, model);
            if (problems.Count != 0)
            {
                throw new InvalidOperationException(
                    "This mesh cannot go into that map: " + string.Join("; ", problems));
            }
        }
        project.CaptureOriginal(destination);
        // A package that replaces one keeps that package's build stamp; a genuinely
        // new one has none to keep and takes the default.
        var magic = File.Exists(destination)
            ? new PkgArchiveReader().Read(destination).Magic
            : PkgArchiveWriter.DefaultMagic;
        new PkgArchiveWriter().Write(destination, magic, entries);
        project.TrackSave(destination);
        return new MapPackageResult(destination, symbol, entries.Count, replaced);
    }

    /// <summary>
    /// What would stop <paramref name="model"/> from going into the map package
    /// of <paramref name="baseMapName"/>, asked before anything is written.
    ///
    /// The constraint is real and worth stating plainly: geometry is replaced
    /// buffer by buffer, so a mesh has to bring as many groups as the host model
    /// holds. A host buffer with nothing to put in it keeps what it had, which
    /// would leave pieces of the original map standing in the new one — so a
    /// mismatch is refused rather than half-applied.
    /// </summary>
    public IReadOnlyList<string> Problems(string baseMapName, PhyreModelSource model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var assets = Path.Combine(project.GameDirectory, "data", "asset", "D3D11");
        var source = Path.Combine(assets, MapAuthoring.ModelAsset(baseMapName) + ".pkg");
        if (!File.Exists(source))
        {
            return new[] { $"No package for map '{baseMapName}'." };
        }

        var archive = reader.Read(source);
        var cluster = archive.Entries
            .FirstOrDefault(entry => entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase));
        if (cluster is null)
        {
            return new[] { $"'{Path.GetFileName(source)}' holds no model cluster." };
        }

        var data = new ED8Editor.Phyre.PhyreClusterReader().Read(archive.ReadEntry(cluster));
        return PhyreModelReplacement.Problems(data, model);
    }

    /// <summary>
    /// The manifest again, agreeing with the new name. The symbol names the
    /// package and every cluster path names the map's own folder, so both move
    /// together; the texture file names are the artist's and stay as they are.
    /// </summary>
    private static byte[] Manifest(byte[] original, string symbol, string baseLower, string lower)
    {
        var document = XDocument.Parse(
            System.Text.Encoding.UTF8.GetString(original), LoadOptions.PreserveWhitespace);
        foreach (var asset in document.Descendants("asset"))
        {
            asset.SetAttributeValue("symbol", symbol);
        }
        foreach (var cluster in document.Descendants("cluster"))
        {
            var path = cluster.Attribute("path")?.Value;
            if (path is null) continue;
            cluster.SetAttributeValue(
                "path",
                path.Replace("/map/" + baseLower + "/", "/map/" + lower + "/",
                        StringComparison.OrdinalIgnoreCase)
                    .Replace("/" + baseLower + ".dae.phyre", "/" + lower + ".dae.phyre",
                        StringComparison.OrdinalIgnoreCase));
        }

        using var output = new MemoryStream();
        using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(document.Declaration is null
                ? document.ToString(SaveOptions.DisableFormatting)
                : document.Declaration + Environment.NewLine
                    + document.ToString(SaveOptions.DisableFormatting));
        }
        return output.ToArray();
    }
}
