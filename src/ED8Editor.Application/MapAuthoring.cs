using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Models;
using ED8Editor.Ops;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>What making or changing a map touched.</summary>
public sealed record MapAuthoringResult(
    string MapName,
    IReadOnlyList<string> Files);

/// <summary>
/// Makes and changes maps as one thing, when the game keeps them as three.
///
/// A map called <c>a0000</c> is an <c>.ops</c> in <c>data/ops</c>, a scene script
/// in <c>data/scripts/scena/dat</c> (and its English twin in <c>dat_us</c>), and
/// a model package <c>M_A0000</c> in <c>data/asset/D3D11</c>. Nothing in the game
/// ties them together beyond the name, so an author who wants one map has to
/// remember three files, three folders and two languages. That is what this
/// hides.
///
/// Every file goes through the mod project — captured before, tracked after — so
/// a new map is as removable as an edited one. A file that did not exist has no
/// original, which <see cref="ModProject"/> records rather than trips over.
/// </summary>
public sealed class MapAuthoring
{
    private readonly ModProject project;

    public MapAuthoring(ModProject project)
        => this.project = project ?? throw new ArgumentNullException(nameof(project));

    /// <summary>Every map the game holds, by name.</summary>
    public IReadOnlyList<string> Maps()
    {
        var directory = Path.Combine(project.GameDirectory, "data", "ops");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*.ops")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Where a map's three files live, whether or not they exist yet.</summary>
    public MapPaths PathsFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A map needs a name.", nameof(name));
        }
        var data = Path.Combine(project.GameDirectory, "data");
        return new MapPaths(
            Path.Combine(data, "ops", name + ".ops"),
            Path.Combine(data, "scripts", "scena", "dat", name + ".dat"),
            Path.Combine(data, "scripts", "scena", "dat_us", name + ".dat"),
            Path.Combine(data, "asset", "D3D11", ModelAsset(name) + ".pkg"));
    }

    /// <summary>The model package name a map of this name loads.</summary>
    public static string ModelAsset(string mapName) => "M_" + mapName.ToUpperInvariant();

    /// <summary>
    /// Makes a new map by starting from one the game ships.
    ///
    /// The base map is not copied for want of understanding it — an
    /// <c>.ops</c> is XML whose every field the editor already reads and writes.
    /// It is copied because a map needs a camera, a fog range, a light rig and an
    /// entry box before it will load at all, and starting from values the game
    /// itself uses beats asking an author to invent them. All of it stays
    /// editable afterwards, through the same reader and writer as any other map.
    /// </summary>
    public MapAuthoringResult CreateMap(string name, string baseMapName, string? modelAsset = null)
    {
        var paths = PathsFor(name);
        var basePaths = PathsFor(baseMapName);
        if (File.Exists(paths.Ops))
        {
            throw new InvalidOperationException($"A map called '{name}' already exists.");
        }
        if (!File.Exists(basePaths.Ops))
        {
            throw new FileNotFoundException($"No map called '{baseMapName}' to start from.", basePaths.Ops);
        }

        var written = new List<string>();
        var scene = new OpsReader().Read(basePaths.Ops);
        var edited = WithModel(scene, modelAsset ?? ModelAsset(name));
        var bytes = new OpsWriter().Serialize(scene, edited);
        written.Add(Write(paths.Ops, bytes));

        // The scene script is what the game runs when the map loads; without one
        // the map exists and does nothing. Both language folders are filled:
        // leaving one out gives a map that works in one language only, which is
        // the kind of failure nobody thinks to test.
        foreach (var (from, to) in new[]
                 {
                     (basePaths.Scena, paths.Scena),
                     (basePaths.ScenaEnglish, paths.ScenaEnglish),
                 })
        {
            if (!File.Exists(from)) continue;
            written.Add(Write(to, File.ReadAllBytes(from)));
        }

        return new MapAuthoringResult(name, written);
    }

    /// <summary>
    /// Makes a map from nothing: no map is copied, and every setting is stated.
    ///
    /// Starting from a shipped map was easy to write and hard to answer for — an
    /// author cannot tell what "a0000" implies, and it carries a camera, a fog
    /// range and a lighting rig nobody chose. So the settings are given here, the
    /// <c>.ops</c> is written from them, and the scene script is generated: 69
    /// bytes holding an Init and a Reinit that return, which is exactly what the
    /// game's own smallest map has.
    /// </summary>
    public MapAuthoringResult CreateEmptyMap(
        string name,
        MapSettings? settings = null,
        string? modelAsset = null,
        PhyreModelSource? model = null)
    {
        var paths = PathsFor(name);
        if (File.Exists(paths.Ops))
        {
            throw new InvalidOperationException($"A map called '{name}' already exists.");
        }

        // Where the player arrives is taken from the model when there is one: at
        // the origin it would be outside anything that does not happen to be
        // centred there, which reads as an empty map.
        var chosen = settings ?? MapSettings.Default;
        if (model is not null && model.Meshes.Count != 0)
        {
            var lowest = new Vector3(float.MaxValue);
            var highest = new Vector3(float.MinValue);
            foreach (var mesh in model.Meshes)
            foreach (var vertex in mesh.Vertices)
            {
                lowest = Vector3.Min(lowest, vertex.Position);
                highest = Vector3.Max(highest, vertex.Position);
            }
            if (lowest.X <= highest.X) chosen = chosen.ArrivingIn(lowest, highest);
        }

        var written = new List<string>
        {
            Write(paths.Ops, MinimalOpsWriter.Write(
                name, modelAsset ?? ModelAsset(name), chosen)),
        };
        var script = MinimalScenaWriter.Write(name);
        foreach (var path in new[] { paths.Scena, paths.ScenaEnglish })
        {
            written.Add(Write(path, script));
        }
        return new MapAuthoringResult(name, written);
    }

    /// <summary>
    /// Makes a whole map from an imported model: the settings, the scene script,
    /// and the model package, all four files, all tracked.
    ///
    /// This is the one call the game's shape argues against — it keeps a map as
    /// three files in three folders in two languages, and an author who wants one
    /// map should not have to know that.
    ///
    /// It refuses rather than half-succeeds. Geometry goes into the host package
    /// buffer by buffer, so a mesh that brings fewer groups than the host holds
    /// would leave pieces of the original map standing in the new one; the counts
    /// are checked first and named if they disagree.
    /// </summary>
    public MapAuthoringResult CreateMapFromModel(
        string name,
        string baseMapName,
        ImportedModelScene scene,
        IPackageArchiveReader packageReader,
        out ImportedModelConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(packageReader);

        conversion = ImportedModelAdapter.Convert(scene);
        var packages = new MapPackageAuthoring(project, packageReader);
        var problems = packages.Problems(baseMapName, conversion.Model);
        if (problems.Count != 0)
        {
            throw new InvalidOperationException(
                $"'{scene.Name}' cannot become map '{name}': " + string.Join("; ", problems));
        }

        var made = CreateMap(name, baseMapName);
        var package = packages.CreatePackage(name, baseMapName, conversion.Model);
        return new MapAuthoringResult(name, made.Files.Append(package.Path).ToArray());
    }

    /// <summary>
    /// Points an existing map at another model.
    ///
    /// This already worked at the format level and was simply not reachable: a
    /// map's model is the <c>asset</c> of the <c>AssetObject</c> the map names
    /// <c>map</c>, the reader carries it, and the writer writes it back.
    /// </summary>
    public MapAuthoringResult SetMapModel(string name, string modelAsset)
    {
        if (string.IsNullOrWhiteSpace(modelAsset))
        {
            throw new ArgumentException("A model asset is required.", nameof(modelAsset));
        }
        var paths = PathsFor(name);
        if (!File.Exists(paths.Ops))
        {
            throw new FileNotFoundException($"No map called '{name}'.", paths.Ops);
        }

        var scene = new OpsReader().Read(paths.Ops);
        var edited = WithModel(scene, modelAsset);
        var bytes = new OpsWriter().Serialize(scene, edited);
        return new MapAuthoringResult(name, new[] { Write(paths.Ops, bytes) });
    }

    /// <summary>Which asset a map's own model object points at, if it has one.</summary>
    public string? ModelOf(string name)
    {
        var paths = PathsFor(name);
        if (!File.Exists(paths.Ops)) return null;
        return MapObject(new OpsReader().Read(paths.Ops))?.AssetId;
    }

    /// <summary>
    /// The prop that is the map itself rather than something standing on it. The
    /// game names it <c>map</c>; failing that, the first one is taken, since a
    /// map's own model is stated before anything placed on it.
    /// </summary>
    private static MapProp? MapObject(MapScene scene)
        => scene.Props.FirstOrDefault(prop =>
               prop.Name.Equals("map", StringComparison.OrdinalIgnoreCase))
           ?? scene.Props.FirstOrDefault();

    private static MapScene WithModel(MapScene scene, string modelAsset)
    {
        var target = MapObject(scene)
            ?? throw new InvalidDataException(
                "This map states no model object, so there is nothing to point at a model.");
        var props = scene.Props
            .Select(prop => prop.SourceIndex == target.SourceIndex
                ? prop with { AssetId = modelAsset }
                : prop)
            .ToArray();
        return scene with { Props = props };
    }

    private string Write(string path, byte[] bytes)
    {
        // Only a file the game shipped has an original worth keeping. Capturing
        // one this project wrote a moment ago would enshrine our own output as
        // the thing to restore to, which is worse than having no backup at all.
        var relative = Path.GetRelativePath(project.GameDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!project.Files.Any(file =>
                file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
        {
            project.CaptureOriginal(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        project.TrackSave(path);
        return path;
    }
}

/// <summary>The files one map is made of.</summary>
public sealed record MapPaths(
    string Ops,
    string Scena,
    string ScenaEnglish,
    string ModelPackage);
