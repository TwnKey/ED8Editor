using ED8Editor.Application;
using ED8Editor.Core;
using ED8Editor.Models;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

// Imports a 3D model as a new map, through a mod project so that everything it
// touches is tracked and can be put back.
//
//   MapImport <game root> <project.json> import <name> <model file>
//   MapImport <game root> <project.json> replace-model <name> <model file>
//   MapImport <game root> <project.json> place <map> <display name> [kind]
//   MapImport <game root> <project.json> status
//   MapImport <game root> <project.json> revert
//   MapImport <game root> <project.json> restore-file <game-relative path>
//
// <game root> is the folder that CONTAINS data — the same one a mod project takes
// — not the data folder itself.
//
// Every game file goes through the project: one the game shipped is backed up
// before being touched, one this tool created is remembered as added. Reverting
// restores the first and deletes the second, so a test leaves no trace.

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "usage: MapImport <game root, the folder containing data> <project.json>"
        + " import|status|revert ...");
    return 2;
}

var game = args[0];
var projectPath = args[1];
var command = args[2].ToLowerInvariant();

var project = File.Exists(projectPath)
    ? ModProject.Open(projectPath)
    : ModProject.Create(projectPath, game, "map import");

var replaceModel = command == "replace-model";
switch (command)
{
    case "status":
        Console.WriteLine($"project : {project.ProjectPath}");
        Console.WriteLine($"game    : {project.GameDirectory}");
        Console.WriteLine($"tracked : {project.Files.Count} files");
        foreach (var file in project.Files.OrderBy(value => value.RelativePath))
        {
            Console.WriteLine($"  {file.RelativePath}"
                + (file.HasOriginal ? "  (replaced, original kept)" : "  (added by the mod)"));
        }
        return 0;

    case "revert":
        Console.WriteLine($"put back {project.RestoreOriginals()} files;"
            + " the game folder is as it was.");
        return 0;

    case "restore-file":
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "usage: MapImport <game> <project.json> restore-file <game-relative path>");
            return 2;
        }
        var restored = project.RestoreOriginals(new[] { args[3] });
        Console.WriteLine(restored == 1
            ? $"restored {args[3]}"
            : $"'{args[3]}' is not a tracked file with an original to restore");
        return restored == 1 ? 0 : 1;

    case "place":
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "usage: MapImport <game> <project.json> place <map> <display name> [kind]");
            return 2;
        }
        var kind = args.Length > 5 ? short.Parse(args[5]) : (short)6;
        foreach (var path in new PlaceTableAuthoring(project).Upsert(args[3], args[4], kind))
        {
            Console.WriteLine("updated " + Path.GetRelativePath(game, path));
        }
        return 0;

    case "import":
    case "replace-model":
    case "replace-prop":
    case "repack":
    case "graft-prop":
        break;

    default:
        Console.Error.WriteLine($"unknown command '{command}'");
        return 2;
}

if (args.Length < 5 && command != "repack")
{
    Console.Error.WriteLine(
        "usage: MapImport <game> <project.json> import <name> <model>");
    return 2;
}

var name = args[3];

// Rewrites a shipped package with our own writer, keeping every entry exactly as it
// was. Nothing authored takes part: same names, same bytes, only the container is
// ours. It tests the one assumption every attempt so far has rested on and none has
// checked — that the game reads an uncompressed entry as readily as its own
// compressed ones.
//
//   MapImport <game> <project> repack O_T10LIG03
if (command == "repack")
{
    var target = Path.Combine(game, "data", "asset", "D3D11", name + ".pkg");
    // Read directly from the game file — do not go through the project's
    // backup mechanism which may return a previously-repacked copy.
    var read = new PkgArchiveReader().Read(target);
    // Keep original stored bytes and compression flags — do NOT decompress
    // and recompress. The game's own XML manifest may reference the original
    // encoding and any change breaks it.
    var rawEntries = new List<(string, byte[], byte[], uint)>();
    {
        using var fs = File.OpenRead(target);
        foreach (var entry in read.Entries)
        {
            var stored = new byte[entry.StoredSize];
            fs.Position = entry.Offset;
            fs.ReadExactly(stored);
            rawEntries.Add((entry.Name,
                new byte[entry.UncompressedSize],
                stored,
                (uint)entry.CompressionType));
        }
    } // close the read stream before writing
    Console.WriteLine($"repacking {name}: {rawEntries.Count} entries, magic=0x{read.Magic:X8}");
    foreach (var (entryName, _, stored, compFlag) in rawEntries)
    {
        Console.WriteLine($"  {entryName} — {stored.Length} bytes (flag={compFlag})");
    }
    project.CaptureOriginal(target);
    new PkgArchiveWriter().WriteRaw(target, read.Magic, rawEntries);
    project.TrackSave(target);
    Console.WriteLine();
    Console.WriteLine("written. If this crashes, the fault is the container, not the model.");
    return 0;
}

var modelPath = args[4];

// Puts a mesh into a shipped prop's OWN cluster, keeping every other byte of it.
// The container is now proven and the shipped cluster is proven; this changes the
// one thing between them and nothing else, which is what separates "we cannot write
// a cluster" from "we cannot write geometry".
//
//   MapImport <game> <project> graft-prop O_T10CHR01 <model> [--scale 0.01]
if (command == "graft-prop")
{
    var target = Path.Combine(game, "data", "asset", "D3D11", name + ".pkg");
    var pristinePath = project.OriginalCopyPath(
        project.RelativePathOf(target) ?? string.Empty) ?? target;
    var package = new PkgArchiveReader().Read(pristinePath);
    var clusterEntry = package.Entries.First(entry =>
        entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase));

    var graftFlag = Array.IndexOf(args, "--scale");
    var graftScale = graftFlag >= 0 && graftFlag + 1 < args.Length
        ? float.Parse(args[graftFlag + 1], System.Globalization.CultureInfo.InvariantCulture)
        : 1f;
    var graftModel = ImportedModelAdapter.Convert(
        new AssimpModelImporter().Import(modelPath)).Model;
    if (graftScale != 1f) graftModel = Scaled(graftModel, graftScale);
    Console.WriteLine($"grafting into {clusterEntry.Name}:"
        + $" {graftModel.Meshes.Count} meshes,"
        + $" {graftModel.Meshes.Sum(m => m.Vertices.Count)} vertices");

    var grafted = PhyreModelReplacement.Replace(package.ReadEntry(clusterEntry), graftModel);

    // --shader-from <package>: bind the map shader and its texture instead of the
    // ones this prop ships.
    //
    // O_T10CHR01 is a character. Its shader expects the skinning attributes a
    // character carries, and an unskinned cube handed to it is not a model that
    // draws badly — it is a crash. The graft that rendered used the map shader from
    // the lamppost family, and this is what made it render.
    //
    // The swap is a byte substitution inside the cluster, which works because the
    // two names are the same length: an ed8.fx id is always a 32-digit hash, and the
    // texture names differ only in their middle. Nothing moves, so no offset in the
    // file has to be recomputed.
    var shaderFromFlag = Array.IndexOf(args, "--shader-from");
    var swapped = new Dictionary<string, (byte[] Data, string From)>(StringComparer.Ordinal);
    if (shaderFromFlag >= 0 && shaderFromFlag + 1 < args.Length)
    {
        var donorPath = Path.Combine(
            game, "data", "asset", "D3D11", args[shaderFromFlag + 1] + ".pkg");
        var donorPackage = new PkgArchiveReader().Read(donorPath);
        void Swap(string suffix, string wantedName)
        {
            var mine = package.Entries.FirstOrDefault(entry =>
                entry.Name.Contains(suffix, StringComparison.OrdinalIgnoreCase));
            var theirs = donorPackage.Entries.FirstOrDefault(entry =>
                entry.Name.Equals(wantedName, StringComparison.OrdinalIgnoreCase));
            if (mine is null || theirs is null || mine.Name == theirs.Name) return;
            // The asset id the cluster names is the entry name without ".phyre".
            var before = mine.Name[..^".phyre".Length];
            var after = theirs.Name[..^".phyre".Length];
            if (before.Length != after.Length)
            {
                throw new InvalidDataException(
                    $"'{before}' and '{after}' are not the same length, so one cannot"
                    + " be written over the other without moving everything after it.");
            }
            var from = System.Text.Encoding.ASCII.GetBytes(before);
            var to = System.Text.Encoding.ASCII.GetBytes(after);
            var hits = 0;
            for (var at = 0; at + from.Length <= grafted.Length; at++)
            {
                if (!grafted.AsSpan(at, from.Length).SequenceEqual(from)) continue;
                to.CopyTo(grafted, at);
                hits++;
            }
            Console.WriteLine($"  {before} -> {after} ({hits} occurrence(s))");
            swapped[mine.Name] = (donorPackage.ReadEntry(theirs), theirs.Name);
        }
        Console.WriteLine($"binding from {Path.GetFileName(donorPath)}:");
        Swap(".fx#", donorPackage.Entries.First(entry =>
            entry.Name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)).Name);
        Swap(".dds.phyre", donorPackage.Entries.First(entry =>
            entry.Name.EndsWith(".dds.phyre", StringComparison.OrdinalIgnoreCase)).Name);
    }

    // Keep raw stored bytes for unchanged entries, compress only the grafted one
    var graftEntries = new List<(string, byte[], byte[], uint)>();
    {
        using var fs = File.OpenRead(pristinePath);
        foreach (var entry in package.Entries)
        {
            if (swapped.TryGetValue(entry.Name, out var replacement))
            {
                graftEntries.Add((replacement.From,
                    replacement.Data, replacement.Data, 0u));
                continue;
            }
            if (entry.Name == clusterEntry.Name)
            {
                // Uncompressed, flag 0. Every other entry passes through byte for
                // byte, so this one entry is the whole delta from a file the game
                // loads — and it must not also carry a compressor of our own, which
                // has never been read back by the engine. The loader takes flag 0.
                graftEntries.Add((entry.Name, grafted, grafted, 0u));
            }
            else
            {
                var stored = new byte[entry.StoredSize];
                fs.Position = entry.Offset;
                fs.ReadExactly(stored);
                graftEntries.Add((entry.Name,
                    new byte[entry.UncompressedSize],
                    stored,
                    (uint)entry.CompressionType));
            }
        }
    }
    project.CaptureOriginal(target);
    new PkgArchiveWriter().WriteRaw(target, package.Magic, graftEntries);
    project.TrackSave(target);
    Console.WriteLine($"written: {grafted.Length} bytes of cluster, everything else untouched.");
    return 0;
}

// Replaces a shipped prop's geometry, keeping everything else it had. The cheapest
// way to ask "does an authored model draw at all", because the scene around it
// already does.
//
//   MapImport <game> <project> replace-prop O_T10LIG03 <model> [--scale 0.01]
if (command == "replace-prop")
{
    var scaleFlag = Array.IndexOf(args, "--scale");
    var scale = scaleFlag >= 0 && scaleFlag + 1 < args.Length
        ? float.Parse(args[scaleFlag + 1], System.Globalization.CultureInfo.InvariantCulture)
        : 1f;
    Console.WriteLine($"reading {Path.GetFileName(modelPath)}...");
    var propScene = new AssimpModelImporter().Import(modelPath);
    var propModel = ImportedModelAdapter.Convert(propScene).Model;
    if (scale != 1f) propModel = Scaled(propModel, scale);
    var lowest = new System.Numerics.Vector3(float.MaxValue);
    var highest = new System.Numerics.Vector3(float.MinValue);
    foreach (var mesh in propModel.Meshes)
    foreach (var vertex in mesh.Vertices)
    {
        lowest = System.Numerics.Vector3.Min(lowest, vertex.Position);
        highest = System.Numerics.Vector3.Max(highest, vertex.Position);
    }
    Console.WriteLine($"  {propModel.Meshes.Count} meshes,"
        + $" {propModel.Meshes.Sum(m => m.Vertices.Count)} vertices,"
        + $" measuring {highest.X - lowest.X:0.##} x {highest.Y - lowest.Y:0.##}"
        + $" x {highest.Z - lowest.Z:0.##} metres");
    var propPath = MapModelPackage.WriteProp(
        project, name, propModel, Console.WriteLine,
        shaderPackage: args.Contains("--shader") 
            ? args[Array.IndexOf(args, "--shader") + 1] 
            : null,
        material: args.Contains("--no-material") ? PropMaterial.None
            : args.Contains("--whole-material") ? PropMaterial.Whole
            : PropMaterial.Minimal);
    Console.WriteLine();
    Console.WriteLine($"'{name}' replaced and tracked:");
    Console.WriteLine("  " + Path.GetRelativePath(game, propPath));
    return 0;
}

static PhyreModelSource Scaled(PhyreModelSource model, float scale) => model with
{
    Meshes = model.Meshes
        .Select(mesh => mesh with
        {
            Vertices = mesh.Vertices
                .Select(vertex => vertex with { Position = vertex.Position * scale })
                .ToArray(),
        })
        .ToArray(),
};

var maps = new MapAuthoring(project);
if (maps.Maps().Contains(name, StringComparer.OrdinalIgnoreCase))
{
    if (!replaceModel)
    {
        Console.Error.WriteLine(
            $"a map called '{name}' already exists; pick another name, or use replace-model.");
        return 1;
    }
}
else if (replaceModel)
{
    Console.Error.WriteLine($"there is no map called '{name}' whose model can be replaced.");
    return 1;
}

Console.WriteLine($"reading {Path.GetFileName(modelPath)}...");
var scene = new AssimpModelImporter().Import(modelPath);
Console.WriteLine($"  {scene.Meshes.Count} meshes, {scene.Meshes.Sum(m => m.Vertices.Count)} vertices,"
    + $" {scene.Meshes.Sum(m => m.Indices.Length) / 3} triangles,"
    + $" up {scene.CoordinateSystem.UpAxis}, unit {scene.CoordinateSystem.UnitScaleMeters}");

var converted = ImportedModelAdapter.Convert(scene);
foreach (var note in converted.Notes) Console.WriteLine("  " + note);
if (converted.FlippedTriangles != 0)
{
    Console.WriteLine($"  {converted.FlippedTriangles} triangles turned to agree with their normals");
}

var problems = converted.Model.Problems();
if (problems.Count != 0)
{
    Console.Error.WriteLine("this model cannot be written: " + string.Join("; ", problems.Take(5)));
    return 1;
}

// The package is self-contained: no base map and no host package participate.
// The one exception is deliberate and asked for: --shader-from <package> binds a
// compiled shader the game ships instead of the one ED8Editor compiles, which is how
// a map that will not draw is told apart from a model that will not draw.
var shaderFrom = Array.IndexOf(args, "--shader-from") is var flag && flag >= 0 && flag + 1 < args.Length
    ? args[flag + 1]
    : null;
var packagePath = MapModelPackage.WriteMinimal(
    project, name, converted.Model, Console.WriteLine, shaderFrom);

if (replaceModel)
{
    Console.WriteLine();
    Console.WriteLine($"model package for '{name}' replaced and tracked:");
    Console.WriteLine("  " + Path.GetRelativePath(game, packagePath));
    return 0;
}

var made = maps.CreateEmptyMap(name, MapSettings.Default, model: converted.Model);

Console.WriteLine();
Console.WriteLine($"map '{name}' created. {made.Files.Count + 1} files, all tracked:");
foreach (var file in made.Files.Append(packagePath))
{
    Console.WriteLine("  " + Path.GetRelativePath(game, file));
}
Console.WriteLine();
Console.WriteLine($"to undo everything:  MapImport <game> {Path.GetFileName(projectPath)} revert");
return 0;
