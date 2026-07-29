using ED8Editor.Packages;
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
