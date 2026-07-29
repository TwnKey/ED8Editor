using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

// Runs the Phyre writing checks against the clusters a game folder ships. It
// lives outside the solution on purpose: the writer is being built as a piece of
// its own, and nothing in the editor depends on it yet.
//
//   dotnet run --project tools/PhyreAuthoringProbe -- "<game>/data" [pattern] [take]

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "usage: PhyreAuthoringProbe <game data directory> [package pattern] [how many]");
    return 2;
}

var assets = Path.Combine(args[0], "asset", "D3D11");
var pattern = args.Length > 1 ? args[1] : "I_EFTEX*.pkg";
var take = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;
var layout = pattern == "--layout";
var emitSchema = pattern == "--emit-schema";
if (layout || emitSchema) pattern = "I_EFTEX000.pkg";
var packings = pattern == "--packings";
var blocks = pattern == "--blocks";
var geometry = pattern == "--geometry";
if (geometry) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var emitLibrary = pattern == "--emit-library";
var schemaUnion = pattern == "--schema-union" || emitLibrary;
if (schemaUnion) pattern = args.Length > 3 ? args[3] : "*.pkg";
var libraryCheck = pattern == "--library-check";
if (libraryCheck) pattern = args.Length > 3 ? args[3] : "*.pkg";
var objectCoverage = pattern == "--object-coverage";
if (objectCoverage) pattern = args.Length > 3 ? args[3] : "*.pkg";
var headerClass = pattern == "--header-class";
if (headerClass) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var parameters = pattern == "--parameters";
if (parameters) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var headerCheck = pattern == "--header-class-check";
if (headerCheck) pattern = args.Length > 3 ? args[3] : "*.pkg";
var objectExtent = pattern == "--object-extent";
if (objectExtent) pattern = args.Length > 3 ? args[3] : "*.pkg";
var objectWrite = pattern == "--object-write";
if (objectWrite) pattern = args.Length > 3 ? args[3] : "*.pkg";
var wholeCluster = pattern == "--whole-cluster";
if (wholeCluster) pattern = args.Length > 3 ? args[3] : "*.pkg";
var pointerDiff = pattern == "--pointer-diff";
if (pointerDiff) pattern = args.Length > 3 ? args[3] : "C_NPC499.pkg";
var graphCheck = pattern == "--graph-check";
if (graphCheck) pattern = args.Length > 3 ? args[3] : "*.pkg";
var assembleCheck = pattern == "--assemble-check";
if (assembleCheck) pattern = args.Length > 3 ? args[3] : "*.pkg";
var replaceCheck = pattern == "--replace-check";
if (replaceCheck) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var animation = pattern == "--animation";
if (animation) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var clipTargets = pattern == "--clip-targets";
if (clipTargets) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var conformCheck = pattern == "--conform-check";
if (conformCheck) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var rigTransfer = pattern == "--rig-transfer";
if (rigTransfer) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var vertexLayout = pattern == "--vertex-layout";
if (vertexLayout) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var packCheck = pattern == "--pack-check";
if (packCheck) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var compare = pattern == "--compare";
if (compare) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
if (blocks || packings) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var reader = new PkgArchiveReader();
if (pattern == "--compare")
{
    pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
}
if (geometry)
{
    // What a model's GPU payload is made of. Replacing geometry means writing
    // that payload again, so the first thing to know is whether the buffers the
    // objects point at cover it exactly, or whether something else lives there.
    var missing = 0;
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new ED8Editor.Phyre.PhyreD3D11ModelReader().Read(
                Path.GetFileNameWithoutExtension(name), cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            Console.WriteLine($"  {name}: not readable as a model — {exception.Message}");
            continue;
        }

        // Where every buffer sits, from the fields that describe them.
        var clusterData = new ED8Editor.Phyre.PhyreClusterReader().Read(cluster);
        var ranges = PhyreModelGeometry.Ranges(clusterData);
        var unclaimed = PhyreModelGeometry.Unclaimed(clusterData, cut.Payload.Length);
        // Handing the buffers back unchanged has to give the model back.
        var rebuilt = PhyreModelGeometryWriter.Rebuild(cluster);
        var identical = rebuilt.AsSpan().SequenceEqual(cluster);
        if (!identical)
        {
            missing++;
            var at = 0;
            while (at < cluster.Length && cluster[at] == rebuilt[at]) at++;
            var payloadStart = cluster.Length - cut.Payload.Length;
            foreach (var r in ranges.Where(v => v.Kind == "indices").OrderBy(v => v.Offset))
            {
                Console.WriteLine(
                    $"      indices of segment {r.ObjectId}: 0x{r.Offset:X}..0x{r.Offset + r.Size:X}"
                    + $" ({r.Size} bytes)");
            }
            // Which object the differing field belongs to, and where in it.
            var meta = clusterData.Metadata;
            var walk = meta.Header.ObjectDataOffset;
            foreach (var g in meta.InstanceGroups)
            {
                if (at >= walk && at < walk + g.Size)
                {
                    var inside = at - walk;
                    var each = g.Count == 0 ? 0 : g.ObjectsSize / g.Count;
                    Console.WriteLine(
                        $"      inside group {g.Index} ({g.ClassName}), object"
                        + $" {(each == 0 ? 0 : inside / each)}, field +0x{(each == 0 ? inside : inside % each):X}"
                        + $" (objects {g.ObjectsSize}, arrays {g.ArraysSize})");
                    break;
                }
                walk += g.Size;
            }
            Console.WriteLine(
                $"      index region: shipped {BitConverter.ToUInt32(cluster, 72)},"
                + $" written {BitConverter.ToUInt32(rebuilt, 72)};"
                + $" vertex region: shipped {BitConverter.ToUInt32(cluster, 76)},"
                + $" written {BitConverter.ToUInt32(rebuilt, 76)}");
            Console.WriteLine(
                $"      first difference at 0x{at:X}"
                + $" ({(at >= payloadStart ? $"in the payload, +{at - payloadStart}" : "in the cluster")})"
                + $": {cluster[at]:X2} became {rebuilt[at]:X2}");
        }
        Console.WriteLine(
            $"  {name}: {ranges.Count} buffers located, {unclaimed} bytes unclaimed,"
            + $" payload rewritten {(identical ? "identically" : $"DIFFERENTLY ({rebuilt.Length} against {cluster.Length})")}");

        long vertices = 0;
        long indices = 0;
        var primitives = 0;
        foreach (var mesh in model.Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                primitives++;
                indices += primitive.Indices.Data.Length;
                foreach (var buffer in primitive.VertexBuffers) vertices += buffer.Data.Length;
            }
        }
        var covered = vertices + indices;
        var payload = cut.Payload.Length;
        if (covered != payload) missing++;
        Console.WriteLine(
            $"  {name}: payload {payload}, buffers {covered}"
            + $" ({vertices} of vertices, {indices} of indices, {primitives} primitives)"
            + $" -> {(covered == payload ? "covered exactly" : $"{payload - covered} bytes unaccounted")}");
    }
    return missing == 0 ? 0 : 1;
}
if (compare)
{
    // The blocks the game's file holds, next to the ones this writer forms, so
    // the first place the two disagree can be read rather than guessed.
    var one = ReadClusters().First(value => value.Name.EndsWith(".dae.phyre"));
    var cut = PhyreClusterSectionReader.Read(one.Cluster);
    ED8Editor.Phyre.PhyreFixupReader.Blocks.Clear();
    ED8Editor.Phyre.PhyreFixupReader.TraceBlocks = true;
    var decoded = new ED8Editor.Phyre.PhyreFixupReader().Read(one.Cluster, cut.Metadata);
    ED8Editor.Phyre.PhyreFixupReader.TraceBlocks = false;
    // The reader walks three tables in a row — pointer arrays, pointers, then
    // arrays — and each starts its offsets again at zero. The pointer table is
    // the run between the first restart and the second.
    var all = ED8Editor.Phyre.PhyreFixupReader.Blocks;
    var restarts = Enumerable.Range(1, all.Count - 1)
        .Where(index => all[index].Offset <= all[index - 1].Offset)
        .ToArray();
    var start = cut.PointerArrayFixups.Length == 0 ? 0 : restarts[0];
    var stop = cut.PointerArrayFixups.Length == 0 ? restarts[0] : restarts[1];
    var shipped = all.Skip(start).Take(stop - start).ToArray();

    PhyreFixupWriter.BeginTrace();
    PhyreFixupWriter.WritePointers(decoded.Pointers, cut.Metadata.InstanceGroups);
    var written = PhyreFixupWriter.LastBlocks.ToArray();
    PhyreFixupWriter.EndTrace();

    Console.WriteLine($"{one.Name}: {shipped.Length} blocks shipped, {written.Length} written");
    // The fixups of the block the bytes disagree in, in the order this writer
    // would put them.
    var wanted = args.Length > 4 ? uint.Parse(args[4], System.Globalization.NumberStyles.HexNumber) : 0x80000034u;
    foreach (var fixup in decoded.Pointers
                 .Where(value => value.SourceOffsetOrMember == wanted)
                 .OrderBy(value => value, ED8Editor.Phyre.Authoring.PhyreFixupOrderView.Instance)
                 .Take(6))
    {
        Console.WriteLine(
            $"    list {fixup.SourceListIndex} object {fixup.SourceObjectId}"
            + $" -> object {fixup.DestinationObjectId} list {fixup.DestinationListIndex}"
            + $" +{fixup.DestinationOffset} array {fixup.ArrayIndex}"
            + $" user {fixup.UserFixupId?.ToString() ?? "-"}");
    }
    for (var index = 0; index < Math.Max(shipped.Length, written.Length); index++)
    {
        var left = index < shipped.Length ? shipped[index] : default;
        var right = index < written.Length ? written[index] : default;
        var same = index < shipped.Length && index < written.Length
            && left.Offset == right.Offset && left.Packing == right.Packing
            && left.Mask == right.Mask && left.Source == right.Source && left.Count == right.Count;
        if (same) continue;
        Console.WriteLine(
            $"  #{index}: shipped 0x{left.Offset:X} pack {left.Packing} mask 0x{left.Mask:X}"
            + $" source 0x{left.Source:X} x{left.Count}"
            + $" | written 0x{right.Offset:X} pack {right.Packing} mask 0x{right.Mask:X}"
            + $" source 0x{right.Source:X} x{right.Count}");
        if (index > 4) break;
    }
    return 0;
}
if (blocks)
{
    var target = args.Length > 4 ? long.Parse(args[4]) : 0x3B6;
    var one = ReadClusters().First(value => value.Name.EndsWith(".dae.phyre"));
    var cut = PhyreClusterSectionReader.Read(one.Cluster);
    ED8Editor.Phyre.PhyreFixupReader.Blocks.Clear();
    ED8Editor.Phyre.PhyreFixupReader.TraceBlocks = true;
    new ED8Editor.Phyre.PhyreFixupReader().Read(one.Cluster, cut.Metadata);
    ED8Editor.Phyre.PhyreFixupReader.TraceBlocks = false;
    // Offsets are counted from the start of each fixup section.
    foreach (var block in ED8Editor.Phyre.PhyreFixupReader.Blocks
                 .Where(value => value.Offset > target - 40 && value.Offset < target + 24))
    {
        Console.WriteLine(
            $"  block at 0x{block.Offset:X}: packing {block.Packing},"
            + $" mask 0x{block.Mask:X}, source 0x{block.Source:X}, {block.Count} fixups");
    }
    return 0;
}
if (!Directory.Exists(assets))
{
    Console.Error.WriteLine($"{assets} is not there.");
    return 2;
}


if (layout)
{
    // Prints how one namespace lays its names out, to learn the order they go in.
    var first = ReadClusters().First();
    var sections = PhyreClusterSectionReader.Read(first.Cluster);
    var span = sections.PackedNamespace.Span;
    var typeCount = BitConverter.ToUInt32(span[8..]);
    var classCount = BitConverter.ToUInt32(span[12..]);
    var memberCount = BitConverter.ToUInt32(span[16..]);
    var tableSize = BitConverter.ToUInt32(span[20..]);
    var typeOffsets = 32;
    var classOffsets = typeOffsets + (int)typeCount * 4;
    var memberOffsets = classOffsets + (int)classCount * 36;
    var table = memberOffsets + (int)memberCount * 24;
    Console.WriteLine(
        $"{first.Name}: {typeCount} types, {classCount} classes, {memberCount} members,"
        + $" string table {tableSize} bytes at 0x{table:X} of {span.Length}");
    for (var index = 0; index < typeCount; index++)
    {
        Console.WriteLine($"  type {index}: name at 0x{BitConverter.ToUInt32(span[(typeOffsets + index * 4)..]):X}");
    }
    for (var index = 0; index < Math.Min(classCount, 4); index++)
    {
        var descriptor = classOffsets + index * 36;
        Console.WriteLine($"  class {index}: name at 0x{BitConverter.ToUInt32(span[(descriptor + 8)..]):X}");
    }
    for (var index = 0; index < Math.Min(memberCount, 4); index++)
    {
        Console.WriteLine($"  member {index}: name at 0x{BitConverter.ToUInt32(span[(memberOffsets + index * 24)..]):X}");
    }
    var text = System.Text.Encoding.ASCII.GetString(
        span.Slice(table, Math.Min((int)tableSize, span.Length - table)));
    Console.WriteLine("  table: " + string.Join(" | ", text.Split((char)0).Take(24)));
    return 0;
}
if (pattern == "--manifest")
{
    pattern = args.Length > 3 ? args[3] : "I_EFTEX000.pkg";
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(2))
    {
        var package = new PkgArchive(reader.Read(path));
        foreach (var entry in package.Entries.Where(value => value.EndsWith(".xml")))
        {
            Console.WriteLine($"{Path.GetFileName(path)}/{entry}:");
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(package.Read(entry)));
        }
    }
    return 0;
}
if (pattern == "--synthetic")
{
    // Sizes and formats no shipped texture uses: the corpus proves the writer
    // agrees with Falcom on what Falcom wrote, this proves it still holds where
    // Falcom wrote nothing.
    var failures = 0;
    foreach (var (width, height, format) in new[]
    {
        (1, 1, "ARGB8"), (3, 5, "ARGB8"), (64, 16, "RGBA8"), (100, 60, "ARGB8"),
        (256, 256, "DXT5"), (40, 24, "DXT1"), (2048, 8, "ARGB8"),
    })
    {
        var mips = 1;
        for (var size = Math.Max(width, height); size > 1; size /= 2) mips++;
        var bytes = 0;
        var (levelWidth, levelHeight) = (width, height);
        for (var level = 0; level < mips; level++)
        {
            bytes += format switch
            {
                "DXT1" => Math.Max(1, (levelWidth + 3) / 4) * Math.Max(1, (levelHeight + 3) / 4) * 8,
                "DXT5" => Math.Max(1, (levelWidth + 3) / 4) * Math.Max(1, (levelHeight + 3) / 4) * 16,
                _ => levelWidth * levelHeight * 4,
            };
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }
        var pixels = new byte[bytes];
        for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index % 253);

        var cluster = PhyreTextureClusterWriter.Write(
            PhyreTextureClusterWriter.AssetPathFor("i_eftex900"), width, height, format, mips, pixels);
        var read = new ED8Editor.Phyre.PhyreD3D11TextureReader().Read("check", cluster);
        var ok = read.Width == width && read.Height == height && read.Format == format
            && read.MipCount == mips && read.Data.AsSpan().SequenceEqual(pixels);
        Console.WriteLine(
            $"  {width}x{height} {format}, {mips} mips, {cluster.Length} bytes:"
            + (ok ? " reads back as written" : $" READ BACK AS {read.Width}x{read.Height}"
                + $" {read.Format}, {read.MipCount} mips, {read.Data.Length} of {pixels.Length} bytes"));
        if (!ok) failures++;
    }
    Console.WriteLine(failures == 0
        ? "PASS every synthetic texture reads back as written"
        : $"FAIL {failures} synthetic textures");
    return failures == 0 ? 0 : 1;
}
if (packings)
{
    // Which packings the game's own writer uses, and how much they save. This is
    // the measurement that has to come before writing the tighter ones.
    var histogram = new SortedDictionary<int, int>();
    var groupsWithOne = 0;
    var groupsWithMany = 0;
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            if (group.Count > 1) groupsWithMany++; else groupsWithOne++;
        }
        // Walking every block means decoding the stream, which the reader does.
        new ED8Editor.Phyre.PhyreFixupReader().Read(cluster, cut.Metadata);
    }
    Console.WriteLine(
        $"groups: {groupsWithOne} with one object, {groupsWithMany} with several");
    var census = ED8Editor.Phyre.PhyreFixupReader.PackingCensus
        .Select((value, index) => (Packing: index, value.Blocks, value.Fixups))
        .Where(value => value.Blocks > 0)
        .OrderByDescending(value => value.Fixups);
    foreach (var entry in census)
    {
        var label = entry.Packing switch
        {
            0 => "all objects",
            1 => "grouped targets",
            2 => "inclusive list",
            3 => "exclusive list",
            4 => "bitmask",
            5 => "raw",
            6 => "strided",
            _ => "?",
        };
        Console.WriteLine(
            $"  packing {entry.Packing} ({label}): {entry.Blocks} blocks,"
            + $" {entry.Fixups} fixups");
    }
    foreach (var entry in histogram)
    {
        var label = entry.Key switch
        {
            0 => "all objects",
            1 => "grouped targets",
            2 => "inclusive list",
            3 => "exclusive list",
            4 => "bitmask",
            5 => "raw",
            6 => "strided",
            _ => "?",
        };
        Console.WriteLine($"  first block packed as {entry.Key} ({label}): {entry.Value} tables");
    }
    return 0;
}
if (pattern == "--objects")
{
    pattern = "I_EFTEX000.pkg";
    var one = ReadClusters().First();
    var cut = PhyreClusterSectionReader.Read(one.Cluster);
    Console.WriteLine($"{one.Name}: header {Convert.ToHexString(cut.Header.Span)}");
    Console.WriteLine($"  instance headers {Convert.ToHexString(cut.InstanceHeaders.Span)}");
    Console.WriteLine($"  objects {Convert.ToHexString(cut.ObjectData.Span)}");
    Console.WriteLine($"  user data {Convert.ToHexString(cut.UserFixupData.Span)}");
    Console.WriteLine($"  user descriptors {Convert.ToHexString(cut.UserFixupDescriptors.Span)}");
    return 0;
}
if (pattern == "--fixups")
{
    pattern = "I_EFTEX000.pkg";
    var one = ReadClusters().First();
    var cut = PhyreClusterSectionReader.Read(one.Cluster);
    Console.WriteLine($"{one.Name}");
    foreach (var (sectionName, bytes) in cut.InOrder)
    {
        if (!sectionName.Contains("fixup")) continue;
        Console.WriteLine($"  {sectionName}: {Convert.ToHexString(bytes.Span)}");
    }
    var fixups = new ED8Editor.Phyre.PhyreFixupReader().Read(one.Cluster, cut.Metadata);
    foreach (var pointer in fixups.Pointers)
    {
        Console.WriteLine(
            $"  pointer: list {pointer.SourceListIndex} object {pointer.SourceObjectId}"
            + $" member/offset 0x{pointer.SourceOffsetOrMember:X} -> list {pointer.DestinationListIndex}"
            + $" object {pointer.DestinationObjectId} +{pointer.DestinationOffset}"
            + $" arrayIndex {pointer.ArrayIndex} user {pointer.UserFixupId?.ToString() ?? "-"}");
    }
    foreach (var array in fixups.Arrays)
    {
        Console.WriteLine(
            $"  array: list {array.SourceListIndex} object {array.SourceObjectId}"
            + $" member/offset 0x{array.SourceOffsetOrMember:X} count {array.Count} offset {array.Offset}");
    }
    return 0;
}
if (packCheck)
{
    // The packer held to the game's own bytes.
    //
    // A mesh the game ships is read back into the neutral form an importer would
    // hand over, packed again, and each stream compared with the one it came
    // from. Position and the rest are copied straight through, so they say
    // whether the layout is right; the bitangent is not handed over at all but
    // rebuilt from the normal and the tangent, so it says whether that
    // reconstruction is the one the game used.
    var streams = 0L;
    var same = 0L;
    var bySemantic = new SortedDictionary<string, (long Same, long All)>(StringComparer.Ordinal);
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new PhyreD3D11ModelReader().Read(
                Path.GetFileNameWithoutExtension(name), cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            continue;
        }

        foreach (var mesh in model.Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var found = new Dictionary<string, ED8Editor.Core.CpuVertexBuffer>(StringComparer.Ordinal);
                var count = 0;
                foreach (var buffer in primitive.VertexBuffers)
                {
                    foreach (var attribute in buffer.Attributes)
                    {
                        found[attribute.Semantic.ToString()
                            + (attribute.SemanticIndex == 0 ? "" : attribute.SemanticIndex.ToString())] = buffer;
                        count = Math.Max(count, buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride);
                    }
                }
                if (!found.ContainsKey("Position") || !found.ContainsKey("Normal")
                    || !found.ContainsKey("Tangent") || !found.ContainsKey("TextureCoordinate"))
                {
                    continue;
                }

                Vector3 V3(string key, int at)
                {
                    var span = found[key].Data.AsSpan(at * 12);
                    return new Vector3(
                        BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..]),
                        BitConverter.ToSingle(span[8..]));
                }

                var vertices = new List<PhyreVertexSource>(count);
                for (var at = 0; at < count; at++)
                {
                    var uvSpan = found["TextureCoordinate"].Data.AsSpan(at * 8);
                    var joints = new int[4];
                    var weights = new float[4];
                    if (found.TryGetValue("JointIndices", out var ji))
                    {
                        for (var slot = 0; slot < 4; slot++) joints[slot] = ji.Data[at * 4 + slot];
                    }
                    if (found.TryGetValue("JointWeights", out var jw))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            weights[slot] = BitConverter.ToSingle(jw.Data.AsSpan(at * 16 + slot * 4));
                        }
                    }
                    // The tangent's fourth component is the handedness the file
                    // keeps in its bitangent; recover it from the two.
                    var normal = V3("Normal", at);
                    var tangent = V3("Tangent", at);
                    var w = 1f;
                    if (found.ContainsKey("Bitangent"))
                    {
                        var stored = V3("Bitangent", at);
                        w = Vector3.Dot(Vector3.Cross(normal, tangent), stored) < 0 ? -1f : 1f;
                    }
                    var sets = new List<PhyreTexCoordSet>();
                    for (var set = 0; set < 4; set++)
                    {
                        var suffix = set == 0 ? "" : set.ToString();
                        if (!found.ContainsKey("TextureCoordinate" + suffix)) break;
                        var span = found["TextureCoordinate" + suffix].Data.AsSpan(at * 8);
                        sets.Add(new PhyreTexCoordSet(
                            new Vector2(BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..])),
                            found.ContainsKey("Tangent" + suffix) ? V3("Tangent" + suffix, at) : default,
                            found.ContainsKey("Bitangent" + suffix) ? V3("Bitangent" + suffix, at) : default));
                    }
                    vertices.Add(new PhyreVertexSource(
                        V3("Position", at), normal, sets, joints, weights));
                }

                // The joints themselves are not what is being tested, but the
                // source refuses a vertex that follows one it has not been
                // given — so stand up as many as the mesh names.
                var jointCount = vertices.SelectMany(v => v.Joints).DefaultIfEmpty(0).Max() + 1;
                var skeleton = Enumerable.Range(0, jointCount)
                    .Select(index => new PhyreJointSource(
                        $"j{index}", -1, Matrix4x4.Identity, Matrix4x4.Identity))
                    .ToArray();
                var source = new PhyreModelSource(
                    "check",
                    new[] { new PhyreMeshSource("m", vertices, new[] { 0, 1, 2 }) },
                    skeleton);
                PhyrePackedGeometry packed;
                try
                {
                    packed = PhyreModelGeometryPacker.Pack(source)[0];
                }
                catch (InvalidOperationException exception)
                {
                    Console.WriteLine($"    refused: {exception.Message}");
                    continue;
                }

                foreach (var stream in packed.Streams)
                {
                    var key = stream.Semantic
                        + (stream.SemanticIndex == 0 ? "" : stream.SemanticIndex.ToString());
                    if (!found.TryGetValue(key, out var original)) continue;
                    streams++;
                    var current = bySemantic.GetValueOrDefault(key);
                    var equal = original.Data.AsSpan(0, Math.Min(original.Data.Length, stream.Data.Length))
                        .SequenceEqual(stream.Data.AsSpan(0, Math.Min(original.Data.Length, stream.Data.Length)));
                    if (equal) same++;
                    bySemantic[key] = (current.Same + (equal ? 1 : 0), current.All + 1);
                }
            }
        }
    }

    Console.WriteLine($"{same} of {streams} streams packed back to the bytes they came from");
    foreach (var (key, value) in bySemantic)
    {
        Console.WriteLine($"  {value.Same,6} of {value.All,6} for {key}");
    }
    return same == streams ? 0 : 1;
}
if (replaceCheck)
{
    // A model given its own mesh back has to come out as the file it was.
    //
    // This is the join between the packer and the payload writer: the mesh is
    // read out of the cluster into the neutral form an importer fills in, then
    // written straight back. Anything the join gets wrong — the order the
    // streams go in, which buffer belongs to which mesh — shows up as bytes that
    // do not match, and nothing else in the chain can hide it.
    var models = 0;
    var identical = 0;
    var examples = new List<string>();
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new PhyreD3D11ModelReader().Read(
                Path.GetFileNameWithoutExtension(name), cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            continue;
        }

        var meshes = new List<PhyreMeshSource>();
        var joints = 0;
        var usable = true;
        foreach (var mesh in model.Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var found = new Dictionary<string, ED8Editor.Core.CpuVertexBuffer>(StringComparer.Ordinal);
                var count = 0;
                foreach (var buffer in primitive.VertexBuffers)
                {
                    foreach (var attribute in buffer.Attributes)
                    {
                        found[attribute.Semantic.ToString()
                            + (attribute.SemanticIndex == 0 ? "" : attribute.SemanticIndex.ToString())] = buffer;
                        count = Math.Max(count, buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride);
                    }
                }
                if (!found.ContainsKey("Position") || !found.ContainsKey("Normal")
                    || !found.ContainsKey("Tangent") || !found.ContainsKey("TextureCoordinate")
                    || !found.ContainsKey("Bitangent"))
                {
                    usable = false;
                    break;
                }

                Vector3 V3(string key, int at)
                {
                    var span = found[key].Data.AsSpan(at * 12);
                    return new Vector3(
                        BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..]),
                        BitConverter.ToSingle(span[8..]));
                }

                var vertices = new List<PhyreVertexSource>(count);
                for (var at = 0; at < count; at++)
                {
                    var vertexJoints = new int[4];
                    var vertexWeights = new float[4];
                    if (found.TryGetValue("JointIndices", out var ji))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            vertexJoints[slot] = ji.Data[at * 4 + slot];
                            joints = Math.Max(joints, vertexJoints[slot] + 1);
                        }
                    }
                    if (found.TryGetValue("JointWeights", out var jw))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            vertexWeights[slot] = BitConverter.ToSingle(jw.Data.AsSpan(at * 16 + slot * 4));
                        }
                    }
                    var sets = new List<PhyreTexCoordSet>();
                    for (var set = 0; set < 4; set++)
                    {
                        var suffix = set == 0 ? "" : set.ToString();
                        if (!found.ContainsKey("TextureCoordinate" + suffix)) break;
                        var span = found["TextureCoordinate" + suffix].Data.AsSpan(at * 8);
                        sets.Add(new PhyreTexCoordSet(
                            new Vector2(BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..])),
                            found.ContainsKey("Tangent" + suffix) ? V3("Tangent" + suffix, at) : default,
                            found.ContainsKey("Bitangent" + suffix) ? V3("Bitangent" + suffix, at) : default));
                    }
                    vertices.Add(new PhyreVertexSource(
                        V3("Position", at), V3("Normal", at), sets, vertexJoints, vertexWeights));
                }

                // The indices as the file states them, not re-derived.
                // The width follows the vertex count, as the packer's does.
                var stride = vertices.Count < 0x10000 ? 2 : 4;
                var indices = new int[primitive.Indices.Data.Length / stride];
                for (var at = 0; at < indices.Length; at++)
                {
                    indices[at] = stride == 2
                        ? BitConverter.ToUInt16(primitive.Indices.Data.AsSpan(at * 2))
                        : (int)BitConverter.ToUInt32(primitive.Indices.Data.AsSpan(at * 4));
                }
                meshes.Add(new PhyreMeshSource("m", vertices, indices));
            }
            if (!usable) break;
        }
        if (!usable || meshes.Count == 0) continue;

        models++;
        var skeleton = Enumerable.Range(0, Math.Max(joints, 1))
            .Select(index => new PhyreJointSource(
                $"j{index}", -1, Matrix4x4.Identity, Matrix4x4.Identity))
            .ToArray();
        var source = new PhyreModelSource("check", meshes, skeleton);
        try
        {
            var written = PhyreModelReplacement.Replace(cluster, source);
            if (written.AsSpan().SequenceEqual(cluster)) { identical++; continue; }
            var at = 0;
            while (at < cluster.Length && at < written.Length && cluster[at] == written[at]) at++;
            if (examples.Count < 8)
            {
                examples.Add($"{name}: differs at {at} of {cluster.Length} (wrote {written.Length})");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            if (examples.Count < 8) examples.Add($"{name}: {exception.Message}");
        }
    }

    Console.WriteLine($"{identical} of {models} models given their own mesh back unchanged");
    foreach (var line in examples) Console.WriteLine($"  {line}");
    return identical == models ? 0 : 1;
}
if (rigTransfer)
{
    // Moving a model onto the game's skeleton, put to two questions.
    //
    // A rig is stood up from a character's own skeleton, renamed out of all
    // recognition and stretched — a taller character — and a mesh is hung on it,
    // one vertex per bone, each fully weighted to its own. Then:
    //
    //  1. does the game's skeleton land on this character's proportions, rather
    //     than on its own?
    //  2. does each vertex's weight come out on the game joint its rig bone was
    //     mapped to?
    //
    // The stretch matters: a rig of the same size would pass even if the fitting
    // did nothing at all.
    const float stretch = 1.7f;
    var models = 0;
    var worstPlace = 0f;
    var misrouted = 0;
    var summary = "";
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new PhyreD3D11ModelReader().Read("m", cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            continue;
        }
        var game = model.Skeleton;
        if (game is null || game.Joints.Count == 0) continue;

        var rig = new List<PhyreJointSource>();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < game.Joints.Count; index++)
        {
            var joint = game.Joints[index];
            var local = joint.DefaultLocalTransform;
            local.M41 *= stretch;
            local.M42 *= stretch;
            local.M43 *= stretch;
            var mine = $"foreign_bone_{index}";
            rig.Add(new PhyreJointSource(mine, joint.ParentIndex, local, Matrix4x4.Identity));
            if (joint.Name.Length != 0) mapping[mine] = joint.Name;
        }

        // One vertex per rig bone, wholly its own.
        var vertices = new List<PhyreVertexSource>();
        for (var index = 0; index < rig.Count; index++)
        {
            vertices.Add(new PhyreVertexSource(
                Vector3.Zero, Vector3.UnitY,
                new[] { new PhyreTexCoordSet(Vector2.Zero, Vector3.UnitX, Vector3.UnitZ) },
                new[] { index, 0, 0, 0 }, new[] { 1f, 0f, 0f, 0f }));
        }
        var source = new PhyreModelSource(
            "t", new[] { new PhyreMeshSource("m", vertices, new[] { 0, 1, 2 }) }, rig);

        var (moved, card) = PhyreRigTransfer.Apply(source, game, mapping);
        models++;

        // Where the fitted joints actually sit, against where the stretched rig
        // put them.
        var fitted = new Matrix4x4[moved.Joints.Count];
        for (var index = 0; index < moved.Joints.Count; index++)
        {
            var parent = moved.Joints[index].ParentIndex;
            fitted[index] = parent < 0 || parent >= index
                ? moved.Joints[index].LocalTransform
                : Matrix4x4.Multiply(fitted[parent], moved.Joints[index].LocalTransform);
        }
        var expected = new Matrix4x4[game.Joints.Count];
        for (var index = 0; index < game.Joints.Count; index++)
        {
            var parent = game.Joints[index].ParentIndex;
            var local = game.Joints[index].DefaultLocalTransform;
            local.M41 *= stretch; local.M42 *= stretch; local.M43 *= stretch;
            expected[index] = parent < 0 || parent >= index
                ? local
                : Matrix4x4.Multiply(expected[parent], local);
        }
        for (var index = 0; index < game.Joints.Count; index++)
        {
            if (game.Joints[index].Name.Length == 0) continue;
            var a = new Vector3(fitted[index].M41, fitted[index].M42, fitted[index].M43);
            var b = new Vector3(expected[index].M41, expected[index].M42, expected[index].M43);
            worstPlace = Math.Max(worstPlace, (a - b).Length());
        }

        var landed = moved.Meshes[0].Vertices;
        for (var index = 0; index < landed.Count && index < game.Joints.Count; index++)
        {
            if (game.Joints[index].Name.Length == 0) continue;
            if (landed[index].Joints[0] != index || Math.Abs(landed[index].Weights[0] - 1f) > 1e-5f)
            {
                misrouted++;
            }
        }
        if (models == 1)
        {
            summary = $"  {name}: {card.Placed.Count} joints placed from the rig,"
                + $" {card.Derived.Count} derived, {card.Merged.Count} fed by several bones,"
                + $" at most {card.DroppedWeight:0.###} of a vertex's weight dropped";
        }
    }

    Console.WriteLine(summary);
    Console.WriteLine(
        $"{models} models moved onto the game's skeleton;"
        + $" joints land at most {worstPlace:0.0000000} from where the rig puts them,"
        + $" {misrouted} weights on the wrong joint");
    return worstPlace < 1e-3f && misrouted == 0 ? 0 : 1;
}
if (conformCheck)
{
    // Conforming has one job it must never fail at: re-express a rig in the
    // game's frame without moving the skin.
    //
    // A rig is stood up from a character's own skeleton and then deliberately
    // knocked out of alignment — every bone's rest orientation turned by a
    // different amount, so no two frames agree with the game's. Conforming has
    // to put the frames back and correct the bind matrices, and the skin at rest
    // has to land exactly where it started. Testing with an already-aligned rig
    // would prove nothing, since doing nothing would pass.
    var models = 0;
    var worst = 0f;
    var worstOn = "";
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new PhyreD3D11ModelReader().Read("m", cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            continue;
        }
        if (model.Skeleton is null || model.Skeleton.Joints.Count == 0) continue;

        var game = model.Skeleton;
        var rig = new List<PhyreJointSource>();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < game.Joints.Count; index++)
        {
            var joint = game.Joints[index];
            // Knock this bone out of the game's frame, each by a different turn.
            var turn = Matrix4x4.CreateFromYawPitchRoll(
                0.3f + index * 0.11f, 0.2f + index * 0.07f, 0.5f - index * 0.05f);
            var local = Matrix4x4.Multiply(turn, joint.DefaultLocalTransform);
            var bind = index < game.InverseBindMatrices.Count
                ? game.InverseBindMatrices[index]
                : Matrix4x4.Identity;
            var mine = $"rig_{index}";
            rig.Add(new PhyreJointSource(mine, joint.ParentIndex, local, bind));
            if (joint.Name.Length != 0) mapping[mine] = joint.Name;
        }

        var conformed = PhyreSkeletonConform.Conform(rig, game, mapping);
        var error = PhyreSkeletonConform.RestPoseError(rig, conformed);
        models++;
        if (error > worst) { worst = error; worstOn = name; }
        if (models <= 3)
        {
            Console.WriteLine(
                $"  {name}: {rig.Count} bones, {conformed.Unmapped.Count} unmapped,"
                + $" {conformed.Missing.Count} the game has and the rig has not,"
                + $" rest pose moves by {error:0.0000000}");
        }
    }

    Console.WriteLine(
        $"{models} rigs conformed out of the game's frame and back;"
        + $" the skin moves at most {worst:0.0000000}"
        + (worstOn.Length == 0 ? "" : $" (on {worstOn})"));
    return worst < 1e-3f ? 0 : 1;
}
if (clipTargets)
{
    // Do a clip's channels name the bones of the model's skeleton?
    //
    // Everything about reusing the game's animations on an imported rig rests on
    // this: if a channel names its target, then a skeleton carrying those names
    // is driven by every clip the game has, and not one byte of animation ever
    // has to be written. This reads a character's skeleton and one of its clips
    // and puts the two name sets side by side.
    var skeleton = new List<string>();
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            var model = new PhyreD3D11ModelReader().Read("m", cluster);
            if (model.Skeleton is null) continue;
            skeleton.AddRange(model.Skeleton.Joints.Select(joint => joint.Name));
            Console.WriteLine($"{name}: {skeleton.Count} joints");
            Console.WriteLine("  first ten: " + string.Join(", ", skeleton.Take(10)));
            break;
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
        }
    }
    if (skeleton.Count == 0) { Console.WriteLine("no skeleton found"); return 1; }

    var known = new HashSet<string>(skeleton, StringComparer.Ordinal);
    var clips = args.Length > 4 ? args[4] : "C_PLY000_DF1.pkg";
    foreach (var path in Directory.EnumerateFiles(assets, clips).Order().Take(1))
    {
        var package = new PkgArchive(reader.Read(path));
        var read = 0;
        foreach (var entry in package.Entries)
        {
            if (!entry.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
            CpuAnimationClip clip;
            try
            {
                clip = new PhyreAnimationReader().Read(entry, package.Read(entry));
            }
            catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
                or InvalidDataException or ArgumentException)
            {
                Console.WriteLine($"  {entry}: not readable — {exception.Message}");
                continue;
            }
            var targets = clip.Channels.Select(channel => channel.TargetName).Distinct().ToArray();
            var matched = targets.Count(known.Contains);
            Console.WriteLine(
                $"  {entry}: {clip.Channels.Count} channels over {targets.Length} targets,"
                + $" {matched} of them name a joint of the skeleton"
                + $" ({clip.Duration:0.###}s)");
            foreach (var stray in targets.Where(value => !known.Contains(value)).Take(5))
            {
                Console.WriteLine($"      not a joint: {stray}");
            }
            if (++read >= 4) break;
        }
    }
    return 0;
}
if (animation)
{
    // Where the animation of a character actually lives.
    //
    // PAnimationClip holds its channels and its name; PAnimationChannelTimes
    // holds the time keys. So the keys are in a cluster — the question is which
    // one: the character's own file, or the clip assets its symbols point at.
    // Counting the animation groups per cluster answers it.
    foreach (var (name, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var rows = new List<string>();
        var keys = 0L;
        foreach (var group in data.Metadata.InstanceGroups)
        {
            var className = group.ClassName ?? "";
            var prefix = args.Length > 4 ? args[4] : "PAnimation";
            if (!className.StartsWith(prefix, StringComparison.Ordinal)) continue;
            rows.Add($"{className} x{group.Count}");
            if (className == "PAnimationChannelTimes")
            {
                // m_keyCount at 0, one per object.
                var stored = data.GetGroupObjectsData(group.Index).Span;
                var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
                for (uint id = 0; id < group.Count && size >= 4; id++)
                {
                    keys += BitConverter.ToUInt32(stored[(int)(id * size)..]);
                }
            }
        }
        if (rows.Count == 0) continue;
        Console.WriteLine($"{name}: {string.Join(", ", rows)}"
            + (keys == 0 ? "" : $" — {keys} time keys in all"));
    }
    return 0;
}
if (vertexLayout)
{
    // Which vertex layouts the game's own characters use.
    //
    // Writing a mesh means choosing a stride and a set of streams, and that
    // choice should be one the engine already ships rather than one invented
    // here: the shaders a model binds expect the semantics they were compiled
    // against. This counts the layouts in use, so the packer can target a real
    // one.
    var layouts = new SortedDictionary<string, long>(StringComparer.Ordinal);
    var models = 0;
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try
        {
            model = new PhyreD3D11ModelReader().Read(
                Path.GetFileNameWithoutExtension(name), cluster);
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException)
        {
            continue;
        }
        models++;
        foreach (var mesh in model.Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                foreach (var buffer in primitive.VertexBuffers)
                {
                    var text = $"stride {buffer.Stride,3}: " + string.Join(", ",
                        buffer.Attributes.OrderBy(value => value.Offset).Select(value =>
                            $"{value.Semantic}{(value.SemanticIndex == 0 ? "" : value.SemanticIndex.ToString())}"
                            + $"@{value.Offset} {value.SourceFormat}"));
                    layouts[text] = layouts.GetValueOrDefault(text) + 1;
                }
            }
        }
    }

    Console.WriteLine($"{models} models read, {layouts.Count} distinct vertex layouts");
    foreach (var (text, count) in layouts.OrderByDescending(entry => entry.Value).Take(12))
    {
        Console.WriteLine($"  {count,6} x {text}");
    }
    return 0;
}
if (assembleCheck)
{
    // The assembler put to its proper test.
    //
    // It orders the classes it lists alphabetically, while a shipped file has
    // its own order — so demanding the game's bytes back would fail for a reason
    // that says nothing. What has to hold instead is that the cluster it builds
    // carries the same thing: read the assembled bytes back with our own
    // readers and require the same groups, the same objects and the same
    // fixups.
    var clusters = 0;
    var good = 0;
    var examples = new List<string>();
    foreach (var (name, cluster) in ReadClusters())
    {
        clusters++;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        var groups = new List<PhyreGroupContents>();
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            var className = group.ClassName ?? "";
            var objects = new List<PhyreObjectContents>();
            var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            var stored = data.GetGroupObjectsData(group.Index).Span;
            for (uint id = 0; id < group.Count; id++)
            {
                objects.Add(PhyreObjectWriter.ReadObject(
                    stored.Slice((int)(id * size), size), className, classes));
            }
            groups.Add(new PhyreGroupContents(
                className,
                objects,
                group.ArraysSize == 0
                    ? ReadOnlyMemory<byte>.Empty
                    : data.GetArrayData(group.Index, 0, group.ArraysSize)));
        }

        var contents = new PhyreClusterContents(
            cut.Metadata.Types,
            groups,
            fixups,
            fixups.UserFixups,
            cut.HeaderClasses,
            cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..]);

        byte[] built;
        try
        {
            built = PhyreClusterAssembler.Assemble(contents);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            if (examples.Count < 8) examples.Add($"{name}: will not assemble — {exception.Message}");
            continue;
        }

        // Read what was built, and hold it to what it was made from.
        string? wrong = null;
        try
        {
            var again = PhyreClusterSectionReader.Read(built);
            var againData = new PhyreClusterReader().Read(built);
            var againFixups = new PhyreFixupReader().Read(built, again.Metadata);
            if (again.Metadata.InstanceGroups.Count != groups.Count)
            {
                wrong = $"{again.Metadata.InstanceGroups.Count} groups against {groups.Count}";
            }
            for (var index = 0; wrong is null && index < groups.Count; index++)
            {
                var group = again.Metadata.InstanceGroups[index];
                if (group.ClassName != groups[index].ClassName)
                {
                    wrong = $"group {index} came back as {group.ClassName}";
                    break;
                }
                if (group.Count != groups[index].Objects.Count)
                {
                    wrong = $"group {index} came back with {group.Count} objects";
                    break;
                }
                var before = data.GetGroupObjectsData(index).Span;
                var after = againData.GetGroupObjectsData(index).Span;
                if (!before.SequenceEqual(after)) wrong = $"group {index} objects differ";
            }
            if (wrong is null && againFixups.Pointers.Count != fixups.Pointers.Count)
            {
                wrong = $"{againFixups.Pointers.Count} pointers against {fixups.Pointers.Count}";
            }
            if (wrong is null && againFixups.Arrays.Count != fixups.Arrays.Count)
            {
                wrong = $"{againFixups.Arrays.Count} arrays against {fixups.Arrays.Count}";
            }
            if (wrong is null && againFixups.PointerArrays.Count != fixups.PointerArrays.Count)
            {
                wrong = $"{againFixups.PointerArrays.Count} pointer arrays"
                    + $" against {fixups.PointerArrays.Count}";
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException or NotSupportedException or ED8Editor.Phyre.InvalidPhyreException)
        {
            wrong = $"will not read back — {exception.Message}";
        }

        if (wrong is null) { good++; continue; }
        if (examples.Count < 8) examples.Add($"{name}: {wrong}");
    }

    Console.WriteLine(
        $"{good} of {clusters} clusters assembled from a description and read back whole");
    foreach (var line in examples) Console.WriteLine($"  {line}");
    return good == clusters ? 0 : 1;
}
if (graphCheck)
{
    // Whether the schema tells which members point at something.
    //
    // Producing a cluster means producing its pointer fixups, and that is only
    // derivable if the answer to "does this field point somewhere" is a property
    // of the member rather than of the file. The claim under test: a member that
    // ever carries a pointer fixup is, in every object, either zero or the
    // source of one — never a non-zero value with no fixup.
    //
    // If it holds, writing the graph is: walk the objects, and for every such
    // member that is set, emit a fixup. If it fails, something else decides.
    var pointing = new SortedSet<string>(StringComparer.Ordinal);
    var checkedFields = 0L;
    var setWithFixup = 0L;
    var setWithout = 0L;
    var offenders = new SortedDictionary<string, long>(StringComparer.Ordinal);
    foreach (var (_, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        var groups = data.Metadata.InstanceGroups;

        // Which (group, object, member offset) triples a fixup starts from.
        var sources = new HashSet<(int Group, uint Object, uint Where)>();
        foreach (var fixup in data.Fixups.Pointers)
        {
            sources.Add((fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember));
        }

        foreach (var group in groups)
        {
            if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            var members = PhyreObjectWriter.Chain(descriptor, classes).ToList();
            var stored = data.GetGroupObjectsData(group.Index).Span;
            var size = (int)(group.ObjectsSize / group.Count);
            if (size == 0) continue;

            foreach (var member in members)
            {
                // A member the file ever points from, named by either the member
                // id or its raw offset — the encoding uses the high bit to say
                // which.
                // The high bit says the source is a raw offset; without it the
                // source is the member's id — its index in the packed member
                // table (PhyreNamespacePacked.cpp:1404), which is the running
                // count this reader already assigns.
                var byId = (uint)member.Index;
                var carries = false;
                for (uint id = 0; id < group.Count && !carries; id++)
                {
                    carries = sources.Contains((group.Index, id, byId))
                        || sources.Contains((group.Index, id, 0x80000000u | member.ValueOffset));
                }
                if (!carries) continue;
                pointing.Add($"{descriptor.Name}.{member.Name}");

                for (uint id = 0; id < group.Count; id++)
                {
                    if (member.ValueOffset + 4 > size) continue;
                    var value = BitConverter.ToUInt32(
                        stored[(int)(id * size + member.ValueOffset)..]);
                    checkedFields++;
                    if (value == 0) continue;
                    if (sources.Contains((group.Index, id, byId))
                        || sources.Contains((group.Index, id, 0x80000000u | member.ValueOffset)))
                    {
                        setWithFixup++;
                    }
                    else
                    {
                        setWithout++;
                        var key = $"{descriptor.Name}.{member.Name}";
                        offenders[key] = offenders.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }
    }

    foreach (var (_, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        Console.WriteLine("  what the file states as a fixup source:");
        foreach (var fixup in data.Fixups.Pointers.Take(8))
        {
            Console.WriteLine(
                $"    list {fixup.SourceListIndex} object {fixup.SourceObjectId}"
                + $" source 0x{fixup.SourceOffsetOrMember:X}");
        }
        foreach (var group in data.Metadata.InstanceGroups.Take(6))
        {
            if (group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            var members = PhyreObjectWriter.Chain(descriptor, classes).ToList();
            Console.WriteLine(
                $"    group {group.Index} {descriptor.Name}: members "
                + string.Join(", ", members.Take(6).Select(m => $"{m.Name}#{m.Index}@{m.ValueOffset}")));
        }
        break;
    }

    Console.WriteLine(
        $"{pointing.Count} members ever point somewhere; of their {checkedFields} fields,"
        + $" {setWithFixup} are set and have a fixup, {setWithout} are set without one");
    foreach (var (key, count) in offenders.OrderByDescending(entry => entry.Value).Take(15))
    {
        Console.WriteLine($"  {count,8} set without a fixup in {key}");
    }
    return setWithout == 0 ? 0 : 1;
}
if (pointerDiff)
{
    // Where a re-encoded pointer table first parts company with the shipped one,
    // and which block that byte belongs to.
    //
    // The block shapes already agree — same packing, same mask, same source,
    // same count — so whatever differs is inside a block: either the order the
    // fixups are written in, or how one of their numbers is encoded. Printing
    // the block's bytes both ways, with the fixups it holds, says which.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var decoded = new PhyreFixupReader().Read(cluster, cut.Metadata);
        PhyreFixupWriter.BeginTrace();
        var written = PhyreFixupWriter.WritePointers(decoded.Pointers, cut.Metadata.InstanceGroups);
        var traced = PhyreFixupWriter.LastBlocks?.ToList() ?? new();
        PhyreFixupWriter.EndTrace();

        var shipped = cut.PointerFixups.Span;
        var at = 0;
        while (at < shipped.Length && at < written.Length && shipped[at] == written[at]) at++;
        if (at >= shipped.Length && shipped.Length == written.Length)
        {
            Console.WriteLine($"{name}: pointer table identical ({shipped.Length} bytes)");
            continue;
        }

        var index = 0;
        for (var walk = 0; walk < traced.Count; walk++)
        {
            if (traced[walk].Offset <= at) index = walk; else break;
        }
        var start = (int)traced[index].Offset;
        var stop = index + 1 < traced.Count ? (int)traced[index + 1].Offset : written.Length;
        Console.WriteLine(
            $"{name}: {shipped.Length} bytes shipped, {written.Length} written,"
            + $" first difference at {at}");
        Console.WriteLine(
            $"  block {index} covers {start}..{stop}: packing {traced[index].Packing},"
            + $" mask 0x{traced[index].Mask:X}, source 0x{traced[index].Source:X},"
            + $" {traced[index].Count} fixups");
        Console.WriteLine($"  shipped {Convert.ToHexString(shipped[start..Math.Min(stop, shipped.Length)])}");
        Console.WriteLine($"  written {Convert.ToHexString(written.AsSpan(start, stop - start))}");

        // The reader hands the fixups back in the order the file states them,
        // and the blocks are consecutive runs of that list — so skipping the
        // fixups of the earlier blocks lands exactly on this one's. That is the
        // shipped order, without reading a single byte by hand.
        var skip = traced.Take(index).Sum(value => value.Count);
        var shippedOrder = decoded.Pointers.Skip(skip).Take(traced[index].Count).ToList();
        var ourOrder = shippedOrder.OrderBy(value => value.SourceObjectId).ToList();
        Console.WriteLine("    shipped order | our order");
        for (var line = 0; line < shippedOrder.Count; line++)
        {
            var a = shippedOrder[line];
            var b = ourOrder[line];
            var same = ReferenceEquals(a, b) ? " " : "*";
            Console.WriteLine(
                $"    {same} obj {a.SourceObjectId,3} -> list {a.DestinationListIndex,2}"
                + $" object {a.DestinationObjectId,3} +{a.DestinationOffset,3}"
                + $" arr {a.ArrayIndex,3}   |   obj {b.SourceObjectId,3}"
                + $" -> list {b.DestinationListIndex,2} object {b.DestinationObjectId,3}"
                + $" +{b.DestinationOffset,3} arr {b.ArrayIndex,3}");
        }
        break;
    }
    return 0;
}
if (wholeCluster)
{
    // Every piece that has a structured form, put together at once: the schema
    // from the library, the objects from the object writer, the fixup tables
    // re-encoded, the sections composed. Carried over: array data, the header
    // class section, the instance list headers, the GPU payload.
    //
    // A cluster that comes back byte for byte says the whole chain holds
    // together — not each link on its own bench.
    var clusters = 0;
    var identical = 0;
    var models = 0;
    var modelsIdentical = 0;
    var examples = new List<string>();
    var bySection = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var (name, cluster) in ReadClusters())
    {
        var isModel = name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase);
        clusters++;
        if (isModel) models++;
        byte[] written;
        try
        {
            written = PhyreClusterWriter.RebuildFromContents(cluster);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException or ArgumentException)
        {
            if (examples.Count < 10) examples.Add($"{name}: {exception.Message}");
            continue;
        }
        if (written.AsSpan().SequenceEqual(cluster))
        {
            identical++;
            if (isModel) modelsIdentical++;
            continue;
        }
        var at = 0;
        while (at < cluster.Length && at < written.Length && cluster[at] == written[at]) at++;

        // Which section the first difference falls in — so what is left is
        // named rather than counted.
        var cut = PhyreClusterSectionReader.Read(cluster);
        var where = "past the end";
        var cursor = 0;
        foreach (var (section, bytes) in cut.InOrder)
        {
            if (at < cursor + bytes.Length) { where = section; break; }
            cursor += bytes.Length;
        }
        bySection[where] = bySection.GetValueOrDefault(where) + 1;
        if (examples.Count < 10)
        {
            examples.Add(
                $"{name}: differs at {at} of {cluster.Length} in the {where}"
                + $" (wrote {written.Length})");
        }
    }

    Console.WriteLine(
        $"{identical} of {clusters} clusters rebuilt byte for byte from their contents,"
        + $" including {modelsIdentical} of {models} models");
    foreach (var (section, count) in bySection.OrderByDescending(entry => entry.Value))
    {
        Console.WriteLine($"  {count,6} first differ in the {section}");
    }
    foreach (var line in examples) Console.WriteLine($"  {line}");
    return identical == clusters ? 0 : 1;
}
if (objectWrite)
{
    // Every object the game ships, read into what it holds and written again.
    //
    // The reading keeps only what the members name, plus the payload a header
    // class carries past its declared size. So an object that comes back
    // identical proves there was nothing else in it — the writer is complete for
    // that class, and the two rules it rests on hold.
    var objects = 0L;
    var differing = 0L;
    var clusters = 0;
    var byClass = new SortedDictionary<string, long>(StringComparer.Ordinal);
    foreach (var (name, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        clusters++;
        foreach (var group in data.Metadata.InstanceGroups)
        {
            if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            var stored = data.GetGroupObjectsData(group.Index).Span;
            var objectSize = (int)(group.ObjectsSize / group.Count);
            if (objectSize == 0) continue;
            for (uint id = 0; id < group.Count; id++)
            {
                var original = stored.Slice((int)(id * objectSize), objectSize);
                var contents = PhyreObjectWriter.ReadObject(original, descriptor.Name, classes);
                var written = PhyreObjectWriter.WriteObject(contents, classes, objectSize);
                objects++;
                if (written.AsSpan().SequenceEqual(original)) continue;
                differing++;
                byClass[descriptor.Name] = byClass.GetValueOrDefault(descriptor.Name) + 1;
                if (differing <= 5)
                {
                    var at = 0;
                    while (at < objectSize && written[at] == original[at]) at++;
                    Console.WriteLine(
                        $"  {name} group {group.Index} ({descriptor.Name}) object {id}:"
                        + $" differs at {at} of {objectSize}"
                        + $" ({original[at]:X2} became {written[at]:X2})");
                }
            }
        }
    }

    Console.WriteLine(
        $"{clusters} clusters, {objects} objects written from what they hold,"
        + $" {differing} that do not come back identical");
    foreach (var (className, count) in byClass.OrderByDescending(entry => entry.Value).Take(15))
    {
        Console.WriteLine($"  {count,10} in {className}");
    }
    return differing == 0 ? 0 : 1;
}
if (objectExtent)
{
    // How big an object really is, against how big its class says it is.
    //
    // This matters because the coverage measurement read each object as its
    // class size — which is what PhyreClusterData.GetObject hands back. Wherever
    // an instance list sizes its objects larger than that, bytes were never
    // looked at, and the claim "what the members do not cover is zero" does not
    // reach them. This counts exactly those bytes and names the classes.
    var groups = 0L;
    var stretched = 0L;
    var declaredBytes = 0L;
    var storedBytes = 0L;
    var byClass = new SortedDictionary<string, (long Groups, long Extra)>(StringComparer.Ordinal);
    foreach (var (_, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        foreach (var group in data.Metadata.InstanceGroups)
        {
            if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            groups++;
            var declared = (long)descriptor.Size * group.Count;
            declaredBytes += declared;
            storedBytes += group.ObjectsSize;
            if (group.ObjectsSize == declared) continue;
            stretched++;
            var current = byClass.GetValueOrDefault(descriptor.Name);
            byClass[descriptor.Name] = (current.Groups + 1, current.Extra + group.ObjectsSize - declared);
        }
    }

    Console.WriteLine(
        $"{groups} instance groups, {storedBytes} bytes of objects stored,"
        + $" {declaredBytes} accounted for by class sizes,"
        + $" {storedBytes - declaredBytes} never examined, in {stretched} groups");
    foreach (var (className, value) in byClass.OrderByDescending(entry => entry.Value.Extra))
    {
        Console.WriteLine($"  {value.Extra,12} extra bytes over {value.Groups,7} groups of {className}");
    }
    return 0;
}
if (headerCheck)
{
    // Three claims about the header class section, put to the corpus.
    //
    //  1. There is one child count per group whose class is flagged header, in
    //     group order — so the section can be indexed without reading it.
    //  2. A parameter buffer declares as many children as it declares shader
    //     parameters (the count at m_tweakableShaderParameterDefinitions).
    //  3. The children tile the object exactly, from the end of the declared
    //     class to the end of the object as the instance list sizes it.
    //
    // Together they say the section is the layout of a shader's parameters, and
    // that it is derivable rather than opaque.
    var clusters = 0;
    var instances = 0L;
    var badOrder = 0;
    var badCount = 0;
    var badTile = 0;
    var examples = new List<string>();
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        if (cut.HeaderClasses.Length == 0) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        var section = cut.HeaderClasses.Span;
        var instanceCount = (int)data.Metadata.Header.HeaderClassInstanceCount;

        var headerGroups = data.Metadata.InstanceGroups
            .Select(group => (Group: group,
                Descriptor: classes.FirstOrDefault(value => value.Name == group.ClassName)))
            .Where(pair => pair.Descriptor is not null && (pair.Descriptor.Flags & 4) != 0)
            .ToList();
        clusters++;
        if (headerGroups.Count != instanceCount)
        {
            badOrder++;
            if (examples.Count < 10)
            {
                examples.Add($"{name}: {headerGroups.Count} flagged groups, {instanceCount} declared");
            }
            continue;
        }

        var childIndex = 0;
        for (var index = 0; index < instanceCount; index++)
        {
            var (group, descriptor) = headerGroups[index];
            var declared = BitConverter.ToInt32(section[(index * 4)..]);
            instances++;

            var objects = data.GetGroupObjectsData(group.Index).Span;
            var objectSize = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);

            if (group.ClassName == "PParameterBuffer" && objectSize >= 16)
            {
                var parameterCount = BitConverter.ToInt32(objects[8..]);
                if (parameterCount != declared)
                {
                    badCount++;
                    if (examples.Count < 10)
                    {
                        examples.Add(
                            $"{name} group {group.Index}: {declared} children,"
                            + $" {parameterCount} parameters");
                    }
                }
            }

            // Do the children cover the object past its declared class?
            var reached = (int)descriptor!.Size;
            var top = reached;
            for (var child = 0; child < declared; child++, childIndex++)
            {
                var at = instanceCount * 4 + childIndex * 16;
                if (at + 16 > section.Length) break;
                var offset = (int)BitConverter.ToUInt32(section[(at + 4)..]);
                var count = (int)BitConverter.ToUInt32(section[(at + 12)..]);
                if (offset < reached) reached = offset;
                if (offset > top) top = offset + (count == 0 ? 0 : 4 * count);
            }
            if (declared != 0 && top != objectSize)
            {
                badTile++;
                if (examples.Count < 10)
                {
                    examples.Add(
                        $"{name} group {group.Index} ({group.ClassName}): children reach"
                        + $" {top}, object is {objectSize}");
                }
            }
        }
    }

    Console.WriteLine(
        $"{clusters} clusters with a header class section, {instances} header instances;"
        + $" {badOrder} whose flagged groups do not match the declared count,"
        + $" {badCount} parameter buffers whose children do not match their parameters,"
        + $" {badTile} whose children do not reach the end of the object");
    foreach (var line in examples) Console.WriteLine($"  {line}");
    return badOrder + badCount == 0 ? 0 : 1;
}
if (parameters)
{
    // A parameter buffer, laid next to the shader parameters it declares.
    //
    // The header class section says where things sit inside the buffer; the
    // buffer's own m_tweakableShaderParameterDefinitions says what they are.
    // If the second predicts the first, then binding a compiled shader is a
    // matter of reading its parameter definitions — which is the whole of what
    // phase C rests on.
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        if (cut.HeaderClasses.Length == 0) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        var groups = data.Metadata.InstanceGroups;
        var types = data.Metadata.Types;

        string TypeName(uint id)
        {
            if (id < types.Count) return types[(int)id];
            var index = (int)id - types.Count - 1;
            return index >= 0 && index < classes.Count ? classes[index].Name : $"<id {id}>";
        }

        var target = groups.FirstOrDefault(group => group.ClassName == "PParameterBuffer");
        if (target is null) continue;
        var buffer = data.GetObject(target.Index, 0).Span;
        Console.WriteLine(
            $"{name}: group {target.Index}, {target.Count} objects,"
            + $" objects {target.ObjectsSize}, arrays {target.ArraysSize}");
        Console.WriteLine(
            $"  buffer object is {buffer.Length} bytes; m_effectVariant @4,"
            + $" m_tweakableShaderParameterDefinitions @8 says count"
            + $" {BitConverter.ToUInt32(buffer[8..])}");

        foreach (var fixup in data.Fixups.Arrays.Where(value => value.SourceListIndex == target.Index))
        {
            Console.WriteLine(
                $"  array fixup: object {fixup.SourceObjectId}, source"
                + $" {fixup.SourceOffsetOrMember}, {fixup.Count} elements at {fixup.Offset}");
        }

        var definitions = data.Fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == target.Index && value.SourceObjectId == 0);
        if (definitions is not null)
        {
            var bytes = data.GetArrayData(target.Index, definitions.Offset, definitions.Count * 16).Span;
            for (var index = 0; index < definitions.Count; index++)
            {
                var at = index * 16;
                Console.WriteLine(
                    $"    definition {index}: elements {BitConverter.ToUInt16(bytes[at..])},"
                    + $" parameterType {bytes[at + 2]}, dataType {bytes[at + 3]},"
                    + $" bufferLoc {BitConverter.ToUInt32(bytes[(at + 8)..])},"
                    + $" constantBufferLocation {BitConverter.ToUInt32(bytes[(at + 12)..])}");
            }
        }

        var section = cut.HeaderClasses.Span;
        var instanceCount = (int)data.Metadata.Header.HeaderClassInstanceCount;
        var first = BitConverter.ToInt32(section);
        Console.WriteLine($"  first header class instance declares {first} children:");
        for (var child = 0; child < first; child++)
        {
            var at = instanceCount * 4 + child * 16;
            Console.WriteLine(
                $"    child {child}: type {TypeName(BitConverter.ToUInt32(section[at..]))}"
                + $" @{BitConverter.ToUInt32(section[(at + 4)..])}"
                + $" flags 0x{BitConverter.ToUInt32(section[(at + 8)..]):X}"
                + $" count {BitConverter.ToUInt32(section[(at + 12)..])}");
        }
        break;
    }
    return 0;
}
if (headerClass)
{
    // The last structurally unexplained part of a cluster, read against the
    // schema that should produce it.
    //
    // The engine (PhyreClusterReaderBinary.cpp, loadAndFixHeaderClasses) walks
    // the instance lists and, for each whose class carries
    // PE_CLASS_DESCRIPTOR_HEADER (1 << 2), takes one child count from the first
    // run of the section and that many 16-byte records from the second. A record
    // is { type id, offset in the parent, flags, how many } and describes objects
    // laid inside the header object itself.
    //
    // Printing them next to the members of the class is what says which member
    // produces which record, and in what order — the rule an author has to
    // apply in reverse.
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        var section = cut.HeaderClasses.Span;
        if (section.Length == 0) continue;
        var classes = cut.Metadata.Classes.ToList();
        var types = cut.Metadata.Types;
        var groups = cut.Metadata.InstanceGroups;

        string TypeName(uint id)
        {
            if (id < types.Count) return types[(int)id];
            var index = (int)id - types.Count - 1;
            return index >= 0 && index < classes.Count ? classes[index].Name : $"<id {id}>";
        }

        var headerGroups = groups
            .Select(group => (Group: group,
                Descriptor: classes.FirstOrDefault(value => value.Name == group.ClassName)))
            .Where(pair => pair.Descriptor is not null && (pair.Descriptor.Flags & 4) != 0)
            .ToList();

        var instanceCount = (int)cut.Metadata.Header.HeaderClassInstanceCount;
        var childCount = (int)cut.Metadata.Header.HeaderClassChildCount;
        Console.WriteLine(
            $"{name}: {instanceCount} header class instances, {childCount} children;"
            + $" {headerGroups.Count} groups whose class is flagged header,"
            + $" {headerGroups.Sum(pair => (long)pair.Group.Count)} objects in them");

        var counts = new int[instanceCount];
        for (var index = 0; index < instanceCount; index++)
        {
            counts[index] = BitConverter.ToInt32(section[(index * 4)..]);
        }
        var childBase = instanceCount * 4;
        var childIndex = 0;
        for (var index = 0; index < instanceCount && index < headerGroups.Count; index++)
        {
            var (group, descriptor) = headerGroups[index];
            Console.WriteLine(
                $"  instance {index}: {counts[index]} children"
                + $" — group {group.Index} {group.ClassName}, {group.Count} objects,"
                + $" class size {descriptor!.Size}, flags 0x{descriptor.Flags:X}");
            foreach (var member in descriptor.Members)
            {
                Console.WriteLine(
                    $"      member {member.Name} : {TypeName(member.TypeId)} @{member.ValueOffset}"
                    + $" size {member.Size} flags 0x{member.Flags:X} fixed {member.FixedArraySize}");
            }
            for (var child = 0; child < counts[index] && childIndex < childCount; child++, childIndex++)
            {
                var at = childBase + childIndex * 16;
                Console.WriteLine(
                    $"      child {child}: type {TypeName(BitConverter.ToUInt32(section[at..]))}"
                    + $" @{BitConverter.ToUInt32(section[(at + 4)..])}"
                    + $" flags 0x{BitConverter.ToUInt32(section[(at + 8)..]):X}"
                    + $" count {BitConverter.ToUInt32(section[(at + 12)..])}");
            }
        }
        break;
    }
    return 0;
}
if (objectCoverage)
{
    // What an object is made of, beyond the members its class declares.
    //
    // A class only declares part of itself: PTexture2D is 112 bytes and declares
    // none, because its fields come from the classes it derives from — and even
    // walking that chain leaves bytes over, the ones the runtime fills once the
    // asset is loaded (resource pointers and the like).
    //
    // Writing an object from nothing therefore hangs on one question: are those
    // leftover bytes zero in the file? If they are, authoring an object is
    // "zero-fill, then write the members", and nothing has to be understood
    // about them. This counts them.
    var objects = 0L;
    var bytes = 0L;
    var declared = 0L;
    var leftoverNonZero = 0L;
    var offenders = new SortedDictionary<string, long>(StringComparer.Ordinal);
    foreach (var (_, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        for (var groupIndex = 0; groupIndex < data.Metadata.InstanceGroups.Count; groupIndex++)
        {
            var group = data.Metadata.InstanceGroups[groupIndex];
            if (group.Count == 0) continue;
            var className = group.ClassName;
            var descriptor = className is null
                ? null
                : classes.FirstOrDefault(value => value.Name == className);
            if (descriptor is null || className is null) continue;

            // Members come from the whole inheritance chain, not just the class.
            var covered = new List<(uint From, uint To)>();
            for (var walk = descriptor; walk is not null;)
            {
                foreach (var member in walk.Members)
                {
                    // A fixed array counts once per element: a PMatrix4 declares
                    // one member of four bytes sixteen times over, not four
                    // bytes of matrix and sixty of mystery.
                    var span = member.Size * Math.Max(member.FixedArraySize, 1);
                    covered.Add((member.ValueOffset, member.ValueOffset + span));
                }
                walk = walk.SuperClassId == 0 || walk.SuperClassId - 1 >= classes.Count
                    ? null
                    : classes[(int)walk.SuperClassId - 1];
            }

            for (uint id = 0; id < group.Count; id++)
            {
                var span = data.GetObject(groupIndex, id).Span;
                var mask = new bool[span.Length];
                foreach (var (from, to) in covered)
                {
                    for (var at = (int)from; at < (int)to && at < mask.Length; at++) mask[at] = true;
                }
                objects++;
                bytes += span.Length;
                var offending = 0;
                for (var at = 0; at < span.Length; at++)
                {
                    if (mask[at]) { declared++; continue; }
                    if (span[at] != 0) offending++;
                }
                if (offending == 0) continue;
                leftoverNonZero += offending;
                offenders[className] = offenders.GetValueOrDefault(className) + offending;
            }
        }
    }

    Console.WriteLine(
        $"{objects} objects, {bytes} bytes, {declared} covered by declared members,"
        + $" {bytes - declared} left over, of which {leftoverNonZero} are not zero");
    foreach (var (className, count) in offenders.OrderByDescending(value => value.Value).Take(25))
    {
        Console.WriteLine($"  {count,10} non-zero leftover bytes in {className}");
    }
    return 0;
}
if (libraryCheck)
{
    // The library, put to the only test that matters: for every cluster the game
    // ships, take the type and class names it lists — nothing else from the file
    // — rebuild the class table from the library alone, and require the packed
    // namespace this produces to be the one the cluster carries, byte for byte.
    //
    // Passing means the library holds a complete description of every class in
    // the game, and that names resolve back to ids the way the engine reads
    // them. Failing names the class that is wrong.
    var checkedCount = 0;
    var failures = new List<string>();
    var unknown = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        var classNames = cut.Metadata.Classes.Select(value => value.Name).ToArray();
        var missing = classNames.Where(value => !PhyreSchemaLibrary.Knows(value)).ToArray();
        if (missing.Length != 0)
        {
            foreach (var value in missing) unknown.Add(value);
            failures.Add($"{name}: not described — {string.Join(", ", missing)}");
            continue;
        }

        var descriptors = PhyreSchemaLibrary.Descriptors(cut.Metadata.Types, classNames);
        var written = PhyreNamespaceWriter.Write(
            cut.Metadata.Types,
            descriptors,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace));
        checkedCount++;
        if (written.AsSpan().SequenceEqual(cut.PackedNamespace.Span)) continue;
        var at = 0;
        var shipped = cut.PackedNamespace.Span;
        while (at < written.Length && at < shipped.Length && written[at] == shipped[at]) at++;
        failures.Add(
            $"{name}: namespace differs at {at} of {shipped.Length}"
            + $" (wrote {written.Length})");
    }

    Console.WriteLine(
        $"{checkedCount} namespaces rebuilt from the library alone,"
        + $" {failures.Count} that do not match the game");
    foreach (var line in failures.Take(20)) Console.WriteLine($"  {line}");
    if (unknown.Count != 0)
    {
        Console.WriteLine($"  classes the library does not describe: {string.Join(", ", unknown)}");
    }
    return failures.Count == 0 ? 0 : 1;
}
if (schemaUnion)
{
    // A class definition is a fact about the engine, not about a file: a class
    // has the same size, and the same members at the same offsets, in every
    // cluster that carries it. But a cluster's namespace lists only the classes
    // it happens to use, so writing a model from nothing needs the union of all
    // those listings — keyed by name, because the ids inside a cluster are
    // indices into that cluster's own tables and mean nothing outside it.
    //
    // Ids are resolved the way the engine resolves them
    // (PhyreNamespacePacked.cpp, PPackedIDMapping::getType and
    // PNamespaceMapping::getType): a member's type id below the type count names
    // a type, at or above it names class[id - typeCount - 1]; a superclass id is
    // one-based over the classes alone, zero meaning none.
    //
    // The run answers one question: are those definitions consistent across the
    // corpus? A conflict would mean a class is not a fact but a per-file layout,
    // and the whole approach would have to change.
    var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
    var origin = new Dictionary<string, string>(StringComparer.Ordinal);
    var memberCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    var rows = new Dictionary<string, (PhyreClassDescriptor Descriptor, string Super, string[] Types)>(
        StringComparer.Ordinal);
    var primitives = new HashSet<string>(StringComparer.Ordinal);
    var conflicts = new List<string>();
    var clusters = 0;
    foreach (var (name, cluster) in ReadClusters())
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        clusters++;
        var types = cut.Metadata.Types;
        var classes = cut.Metadata.Classes;
        foreach (var type in types) primitives.Add(type);

        string TypeName(uint id)
        {
            if (id < types.Count) return types[(int)id];
            var index = (int)id - types.Count - 1;
            return index >= 0 && index < classes.Count ? classes[index].Name : $"<id {id}>";
        }

        foreach (var descriptor in classes)
        {
            var super = descriptor.SuperClassId == 0
                ? "-"
                : descriptor.SuperClassId - 1 < classes.Count
                    ? classes[(int)descriptor.SuperClassId - 1].Name
                    : $"<class {descriptor.SuperClassId}>";
            var text = string.Join("|", new[]
            {
                $"{super}:{descriptor.Size}:{descriptor.Alignment}",
                $"{descriptor.OffsetFromParent}:{descriptor.OffsetToBase}",
                $"{descriptor.OffsetToBaseInAllocatedBlock}:{descriptor.Flags:X}",
                $"{descriptor.DefaultBufferOffset}",
            }.Concat(descriptor.Members.Select(member =>
                $"{member.Name}={TypeName(member.TypeId)}@{member.ValueOffset}"
                + $":{member.Size}:{member.Flags:X}:{member.FixedArraySize}")));

            seen[descriptor.Name] = seen.GetValueOrDefault(descriptor.Name) + 1;
            if (definitions.TryAdd(descriptor.Name, text))
            {
                origin[descriptor.Name] = name;
                memberCounts[descriptor.Name] = descriptor.Members.Count;
                rows[descriptor.Name] = (
                    descriptor,
                    super,
                    descriptor.Members.Select(member => TypeName(member.TypeId)).ToArray());
            }
            else if (definitions[descriptor.Name] != text)
            {
                conflicts.Add($"{descriptor.Name}: {origin[descriptor.Name]} vs {name}");
            }
        }
    }

    if (emitLibrary)
    {
        // The same table, as C# source. Names, not ids: an id only means
        // something inside the cluster it came from, so a library that outlives
        // one file has to speak names and resolve them when a namespace is
        // written.
        Console.WriteLine($"// Generated from {clusters} clusters by --emit-library.");
        Console.WriteLine("private static readonly string[] PrimitiveTypes =");
        Console.WriteLine("{");
        foreach (var type in primitives.Order()) Console.WriteLine($"    \"{type}\",");
        Console.WriteLine("};");
        Console.WriteLine();
        Console.WriteLine("private static readonly ClassRow[] Rows =");
        Console.WriteLine("{");
        foreach (var key in rows.Keys.Order())
        {
            var (descriptor, super, memberTypes) = rows[key];
            var head =
                $"    new(\"{descriptor.Name}\", \"{super}\", {descriptor.Size}, {descriptor.Alignment},"
                + $" {descriptor.OffsetFromParent}, {descriptor.OffsetToBase},"
                + $" {descriptor.OffsetToBaseInAllocatedBlock}, 0x{descriptor.Flags:X},"
                + $" {descriptor.DefaultBufferOffset},";
            if (descriptor.Members.Count == 0)
            {
                Console.WriteLine($"{head} NoMembers),");
                continue;
            }
            Console.WriteLine($"{head} new MemberRow[]");
            Console.WriteLine("    {");
            for (var index = 0; index < descriptor.Members.Count; index++)
            {
                var member = descriptor.Members[index];
                Console.WriteLine(
                    $"        new(\"{member.Name}\", \"{memberTypes[index]}\", {member.ValueOffset},"
                    + $" {member.Size}, 0x{member.Flags:X}, {member.FixedArraySize}),");
            }
            Console.WriteLine("    }),");
        }
        Console.WriteLine("};");
        return conflicts.Count == 0 ? 0 : 1;
    }

    Console.WriteLine(
        $"{clusters} clusters read, {definitions.Count} distinct classes,"
        + $" {memberCounts.Values.Sum()} members,"
        + $" {conflicts.Select(value => value.Split(':')[0]).Distinct().Count()} classes"
        + " whose definition is not the same everywhere");
    foreach (var line in conflicts.Distinct().Take(20)) Console.WriteLine($"  conflict {line}");
    foreach (var key in definitions.Keys.Order())
    {
        Console.WriteLine($"  {seen[key],6} x {key} ({memberCounts[key]} members)");
    }
    return conflicts.Count == 0 ? 0 : 1;
}
if (emitSchema)
{
    // Prints the schema of a shipped cluster as C# source. The values are facts
    // about the format — sizes, offsets, flags of each class and member — and
    // turning them into code is what lets a cluster be written without opening
    // one of the game's files. What is printed is then checked back against the
    // game: the namespace it produces has to be the shipped one, byte for byte.
    var source = ReadClusters().First();
    var schema = PhyreClusterSectionReader.Read(source.Cluster);
    var carried = PhyreNamespaceWriter.ReadUnmodelledHeader(schema.PackedNamespace);
    Console.WriteLine("// Generated from " + source.Name);
    Console.WriteLine($"private static readonly string[] TypeNames = {{ {string.Join(", ", schema.Metadata.Types.Select(value => $"\"{value}\""))} }};");
    Console.WriteLine($"private static readonly PhyreNamespaceWriter.UnmodelledHeader Carried = new(0x{carried.First:X}, 0x{carried.Second:X}, 0x{carried.Third:X}, 0x{carried.Fourth:X});");
    Console.WriteLine("private static readonly ClassRow[] ClassRows =");
    Console.WriteLine("{");
    foreach (var descriptor in schema.Metadata.Classes)
    {
        Console.WriteLine(
            $"    new(\"{descriptor.Name}\", {descriptor.SuperClassId}, {descriptor.Size}, {descriptor.Alignment},"
            + $" {descriptor.OffsetFromParent}, {descriptor.OffsetToBase}, {descriptor.OffsetToBaseInAllocatedBlock},"
            + $" 0x{descriptor.Flags:X}, {descriptor.DefaultBufferOffset}, new MemberRow[]");
        Console.WriteLine("    {");
        foreach (var member in descriptor.Members)
        {
            Console.WriteLine(
                $"        new(\"{member.Name}\", {member.TypeId}, {member.ValueOffset}, {member.Size},"
                + $" 0x{member.Flags:X}, {member.FixedArraySize}),");
        }
        Console.WriteLine("    }),");
    }
    Console.WriteLine("};");
    return 0;
}
var report = PhyreAuthoringCheck.Run(ReadClusters());
Console.WriteLine(report);
foreach (var failure in report.Failures.Take(20)) Console.Error.WriteLine($"  {failure}");
return report.Passed ? 0 : 1;

IEnumerable<(string Name, byte[] Cluster)> ReadClusters()
{
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        PkgArchive? package = null;
        try
        {
            package = new PkgArchive(reader.Read(path));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"  {Path.GetFileName(path)}: {exception.Message}");
        }
        if (package is null) continue;
        foreach (var entry in package.Entries)
        {
            if (!entry.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase)) continue;
            // Shaders are clusters too, but they are the biggest thing in a
            // package and nothing is authored into them yet.
            if (entry.StartsWith("ed8.fx", StringComparison.OrdinalIgnoreCase)) continue;
            byte[]? bytes = null;
            try
            {
                bytes = package.Read(entry);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"  {entry}: {exception.Message}");
            }
            if (bytes is not null) yield return ($"{Path.GetFileName(path)}/{entry}", bytes);
        }
    }
}

/// <summary>The entries of one package, kept simple for the probe.</summary>
internal sealed class PkgArchive
{
    private readonly ED8Editor.Core.IPackageArchive archive;

    public PkgArchive(ED8Editor.Core.IPackageArchive archive) => this.archive = archive;

    public IEnumerable<string> Entries => archive.Entries.Select(entry => entry.Name);

    public byte[] Read(string name) => archive.ReadEntry(name);
}
