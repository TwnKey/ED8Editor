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

if (args.Length > 2 && args[1] == "--list-package")
{
    var package = new PkgArchiveReader().Read(args[2]);
    foreach (var entry in package.Entries)
    {
        Console.WriteLine($"{entry.Name}\t{entry.UncompressedSize}");
    }
    return 0;
}

static byte[] ReadClusterOrPackage(string path)
{
    if (!path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        return File.ReadAllBytes(path);
    var package = new PkgArchiveReader().Read(path);
    var entry = package.Entries.FirstOrDefault(value =>
        value.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"'{path}' has no model cluster.");
    return package.ReadEntry(entry);
}

if (args.Length > 4 && args[1] == "--pack-folder")
{
    var folder = args[2];
    var output = args[3];
    var symbol = args[4];
    var folderEntries = Directory.EnumerateFiles(folder)
        .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
        .Select(path =>
        {
            var data = File.ReadAllBytes(path);
            if (Path.GetFileName(path).Equals(
                    "asset_D3D11.xml", StringComparison.OrdinalIgnoreCase))
            {
                var xml = System.Text.Encoding.UTF8.GetString(data);
                var start = xml.IndexOf("symbol=\"", StringComparison.Ordinal);
                if (start < 0)
                    throw new InvalidDataException("The asset manifest has no symbol.");
                start += "symbol=\"".Length;
                var end = xml.IndexOf('"', start);
                if (end < 0)
                    throw new InvalidDataException("The asset manifest symbol is unterminated.");
                xml = xml[..start] + symbol + xml[end..];
                data = new System.Text.UTF8Encoding(false).GetBytes(xml);
            }
            return (Name: Path.GetFileName(path), Data: data);
        })
        .ToArray();
    new PkgArchiveWriter().Write(
        output,
        PkgArchiveWriter.DefaultMagic,
        folderEntries);
    Console.WriteLine(
        $"packed {folderEntries.Length} exact folder entries as '{symbol}' into {output}");
    return 0;
}

if (args.Length > 2 && args[1] == "--dump-scene")
{
    var model = new PhyreD3D11ModelReader().Read(
        Path.GetFileNameWithoutExtension(args[2]),
        ReadClusterOrPackage(args[2]));
    foreach (var node in model.SceneNodes ?? Array.Empty<CpuSceneNode>())
    {
        Console.WriteLine(
            $"node parent={node.ParentIndex} name='{node.Name}'"
            + $" matrix={node.DefaultLocalTransform}");
    }
    foreach (var mesh in model.Meshes)
    {
        Console.WriteLine(
            $"mesh node={mesh.SceneNodeIndex} name='{mesh.Name}' purpose={mesh.Purpose}");
    }
    return 0;
}

if (args.Length > 3 && args[1] == "--dump-package-entry")
{
    var package = new PkgArchiveReader().Read(args[2]);
    var entry = package.Entries.Single(value =>
        value.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
    Console.Write(System.Text.Encoding.UTF8.GetString(package.ReadEntry(entry)));
    return 0;
}

if (args.Length > 4 && args[1] == "--extract-package-entry")
{
    var package = new PkgArchiveReader().Read(args[2]);
    var entry = package.Entries.Single(value =>
        value.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
    File.WriteAllBytes(args[4], package.ReadEntry(entry));
    return 0;
}

// Shows the material ABI carried by a model: each parameter definition beside
// the corresponding header-class child record. This is useful when deriving a
// material from an effect rather than copying a model material.
//
//   PhyreAuthoringProbe x --dump-material <package.pkg>
if (args.Length > 2 && args[1] == "--dump-material")
{
    var table = PhyreMaterialTableReader.Read(ReadClusterOrPackage(args[2]));
    var bufferHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(table.ParameterBufferObject.Span));
    Console.WriteLine(
        $"shader={table.ShaderAsset} size={table.ParameterBufferSize}"
        + $" definitions={table.DefinitionCount} children={table.Children.Count}"
        + $" bufferSha256={bufferHash}");
    foreach (var import in table.Imports)
        Console.WriteLine($"import source=0x{import.Source:X8} member={import.Member ?? "<raw>"} asset={import.Asset}");
    foreach (var pointer in table.Pointers)
        Console.WriteLine(
            $"pointer source=0x{pointer.SourceOffset:X8}"
            + $" target={pointer.TargetClass}[{pointer.TargetId}]");
    for (var index = 0; index < table.DefinitionCount; index++)
    {
        var definition = table.ParameterDefinitions[checked((int)index)].Span;
        var nameFixup = table.DefinitionArrays.FirstOrDefault(value =>
            value.ObjectId == (uint)index);
        var parameterName = "<unnamed>";
        if (nameFixup is not null && nameFixup.Offset < table.DefinitionArrayData.Length)
        {
            var nameBytes = table.DefinitionArrayData.Span[(int)nameFixup.Offset..];
            var terminator = nameBytes.IndexOf((byte)0);
            if (terminator >= 0)
                parameterName = System.Text.Encoding.ASCII.GetString(nameBytes[..terminator]);
        }
        var location = BitConverter.ToUInt16(definition[8..]);
        var size = BitConverter.ToUInt16(definition[10..]) & 0x1fff;
        var child = index < table.Children.Count ? table.Children[checked((int)index)] : null;
        Console.WriteLine(
            $"[{index,2}] {parameterName} ptype={definition[2],3} dtype={definition[3],3}"
            + $" loc={location,4} size={size,3}"
            + $" child={(child is null ? "<none>" : $"{child.TypeName}@{child.Offset}+{child.Count} flags=0x{child.Flags:X}")}");
    }
    return 0;
}

// Finds shipped model clusters that use one exact effect variant. This supplies
// a format oracle with the same shader ABI without assuming that the model being
// replaced is structurally representative.
//
//   PhyreAuthoringProbe x --find-shader-models <game data> <shader asset> [limit]
if (args.Length > 4 && args[1] == "--find-shader-models")
{
    var assetRoot = Path.Combine(args[2], "asset", "D3D11");
    var wantedShader = args[3];
    var limit = args.Length > 4 ? int.Parse(args[4]) : 20;
    var matches = 0;
    foreach (var path in Directory.EnumerateFiles(
                 assetRoot, "*.pkg", SearchOption.AllDirectories))
    {
        IPackageArchive package;
        try { package = new PkgArchiveReader().Read(path); }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or ArgumentException)
        {
            continue;
        }
        foreach (var entry in package.Entries.Where(value =>
                     value.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)))
        {
            PhyreMaterialTable table;
            PhyreClusterData cluster;
            try
            {
                var bytes = package.ReadEntry(entry);
                table = PhyreMaterialTableReader.Read(bytes);
                cluster = new PhyreClusterReader().Read(bytes);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException
                    or InvalidOperationException or ArgumentException)
            {
                continue;
            }
            if (!table.ShaderAsset.Equals(wantedShader, StringComparison.OrdinalIgnoreCase))
                continue;
            var groups = cluster.Metadata.InstanceGroups;
            var materialCount = groups
                .Where(value => value.ClassName == "PMaterial")
                .Sum(value => value.Count);
            var parameterBuffers = groups.Count(value =>
                value.ClassName == "PParameterBuffer" && value.Count != 0);
            var nodes = groups
                .Where(value => value.ClassName == "PNode")
                .Sum(value => value.Count);
            Console.WriteLine(
                $"{path} :: {entry.Name} :: materials={materialCount}"
                + $" parameterBufferGroups={parameterBuffers} nodes={nodes}"
                + $" imports={string.Join(", ", table.Imports.Select(value => value.Asset))}");
            if (++matches >= limit) return 0;
        }
    }
    Console.WriteLine($"Found {matches} matching model cluster(s).");
    return 0;
}

// Dumps an instance class from the first compiled effect embedded in a package.
if (args.Length > 3 && args[1] == "--dump-effect-class")
{
    var archive = new PkgArchiveReader().Read(args[2]);
    var entry = archive.Entries.First(value =>
        value.Name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)
        && value.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase));
    var data = new PhyreClusterReader().Read(archive.ReadEntry(entry));
    var wanted = args[3];
    foreach (var group in data.Metadata.InstanceGroups.Where(value =>
                 value.ClassName == wanted && value.Count != 0))
    {
        var bytes = data.GetGroupObjectsData(group.Index).Span;
        var each = checked((int)(group.ObjectsSize / group.Count));
        Console.WriteLine($"{entry.Name}: group={group.Index} {wanted} x{group.Count}, {each} bytes each");
        for (var id = 0; id < group.Count; id++)
        {
            var item = bytes.Slice(checked((int)id * each), each);
            var words = new List<string>();
            for (var word = 0; word < each / 4; word++)
            {
                words.Add($"+{word * 4:X2}={BitConverter.ToUInt32(item[(word * 4)..]):X8}");
            }
            Console.WriteLine($"  [{id}] " + string.Join(" ", words));
        }
    }
    return 0;
}

// Prints the two ordered ABI tables carried by a cluster. Their order is part of
// the format: class/member fixups store indices into these tables.
//
//   PhyreAuthoringProbe x --dump-schema <package.pkg>
if (args.Length > 2 && args[1] == "--dump-schema")
{
    var cluster = ReadClusterOrPackage(args[2]);
    var metadata = PhyreClusterSectionReader.Read(cluster).Metadata;
    Console.WriteLine("TYPES");
    foreach (var type in metadata.Types) Console.WriteLine($"  \"{type}\",");
    Console.WriteLine("CLASSES");
    foreach (var descriptor in metadata.Classes) Console.WriteLine($"  \"{descriptor.Name}\",");
    return 0;
}

if (args.Length > 3 && args[1] == "--compare-schema")
{
    var left = PhyreClusterSectionReader.Read(ReadClusterOrPackage(args[2])).Metadata;
    var right = PhyreClusterSectionReader.Read(ReadClusterOrPackage(args[3])).Metadata;
    var leftClasses = left.Classes.Select(value => value.Name).ToArray();
    var rightClasses = right.Classes.Select(value => value.Name).ToArray();
    Console.WriteLine($"types: {left.Types.Count}/{right.Types.Count}, "
        + (left.Types.SequenceEqual(right.Types) ? "identical" : "DIFFER"));
    Console.WriteLine($"classes: {leftClasses.Length}/{rightClasses.Length}, "
        + (leftClasses.SequenceEqual(rightClasses) ? "identical" : "DIFFER"));
    if (!leftClasses.SequenceEqual(rightClasses))
    {
        var at = 0;
        while (at < Math.Min(leftClasses.Length, rightClasses.Length)
               && leftClasses[at] == rightClasses[at]) at++;
        Console.WriteLine($"first class difference at {at}: "
            + $"{leftClasses.ElementAtOrDefault(at) ?? "<end>"} / "
            + $"{rightClasses.ElementAtOrDefault(at) ?? "<end>"}");
    }
    return 0;
}

if (args.Length > 3 && args[1] == "--dump-members")
{
    var metadata = PhyreClusterSectionReader.Read(ReadClusterOrPackage(args[2])).Metadata;
    var descriptor = metadata.Classes.Single(value => value.Name == args[3]);
    Console.WriteLine(
        $"{descriptor.Name}: size={descriptor.Size}, superClassId={descriptor.SuperClassId},"
        + $" offsetFromParent={descriptor.OffsetFromParent}");
    foreach (var member in PhyreObjectWriter.Chain(descriptor, metadata.Classes))
    {
        Console.WriteLine(
            $"  [{member.Index}] {member.Name}: {member.TypeName}"
            + $" offset={member.ValueOffset} size={member.Size}"
            + $" flags=0x{member.Flags:X} fixed={member.FixedArraySize}");
    }
    return 0;
}

// Compares the first object of every common instance class, word by word, and
// labels each differing offset with the schema member that covers it. Counts,
// bounds and geometry naturally differ between models; the output is evidence
// for deciding which values are structural rather than an automatic verdict.
if (args.Length > 3 && args[1] == "--diff-object-values")
{
    static (PhyreClusterData Data, IReadOnlyList<PhyreClassDescriptor> Classes) ReadValues(
        string path)
    {
        var cluster = ReadClusterOrPackage(path);
        var data = new PhyreClusterReader().Read(cluster);
        return (data, data.Metadata.Classes);
    }

    var left = ReadValues(args[2]);
    var right = ReadValues(args[3]);
    var leftGroups = left.Data.Metadata.InstanceGroups
        .Where(value => value.Count != 0 && value.ClassName is not null)
        .GroupBy(value => value.ClassName!, StringComparer.Ordinal)
        .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
    var rightGroups = right.Data.Metadata.InstanceGroups
        .Where(value => value.Count != 0 && value.ClassName is not null)
        .GroupBy(value => value.ClassName!, StringComparer.Ordinal)
        .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);

    foreach (var className in leftGroups.Keys.Intersect(rightGroups.Keys, StringComparer.Ordinal)
                 .OrderBy(value => value, StringComparer.Ordinal))
    {
        var aGroup = leftGroups[className];
        var bGroup = rightGroups[className];
        var aSize = checked((int)(aGroup.ObjectsSize / aGroup.Count));
        var bSize = checked((int)(bGroup.ObjectsSize / bGroup.Count));
        var size = Math.Min(aSize, bSize);
        var a = left.Data.GetGroupObjectsData(aGroup.Index).Span[..aSize];
        var b = right.Data.GetGroupObjectsData(bGroup.Index).Span[..bSize];
        var descriptor = left.Classes.First(value => value.Name == className);
        var members = PhyreObjectWriter.Chain(descriptor, left.Classes).ToArray();
        var differences = new List<string>();
        for (var offset = 0; offset + 4 <= size; offset += 4)
        {
            var aWord = BitConverter.ToUInt32(a[offset..]);
            var bWord = BitConverter.ToUInt32(b[offset..]);
            if (aWord == bWord) continue;
            var member = members.LastOrDefault(value =>
                value.ValueOffset <= offset
                && offset < value.ValueOffset
                    + value.Size * Math.Max(value.FixedArraySize, 1));
            differences.Add($"+0x{offset:X} {member?.Name ?? "?"}:"
                + $" 0x{aWord:X8}/0x{bWord:X8}");
        }
        if (aSize != bSize) differences.Add($"object-size: {aSize}/{bSize}");
        if (differences.Count == 0) continue;
        Console.WriteLine($"{className} x{aGroup.Count}/x{bGroup.Count}: "
            + string.Join(", ", differences));
    }
    return 0;
}

if (args.Length > 2 && args[1] == "--dump-user-fixups")
{
    var cluster = ReadClusterOrPackage(args[2]);
    var cut = PhyreClusterSectionReader.Read(cluster);
    var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
    foreach (var user in fixups.UserFixups)
    {
        Console.WriteLine(
            $"[{user.Id}] type={user.TypeId}/{user.TypeName ?? "?"} "
            + $"size={user.DeclaredSize} text={user.Text ?? "<binary>"} "
            + $"data={Convert.ToHexString(user.Data.Span)}");
    }
    return 0;
}

// Lists the asset ids serialized by PAssetReference and
// PAssetReferenceImport. This is deliberately fixup-driven: the bytes stored in
// a PString object are not a pointer until the engine applies its array fixup.
if (args.Length > 2 && args[1] == "--dump-asset-references")
{
    byte[] cluster;
    if (args.Length > 3)
    {
        var package = new PkgArchiveReader().Read(args[2]);
        var entry = package.Entries.Single(value =>
            value.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        cluster = package.ReadEntry(entry);
    }
    else
    {
        cluster = ReadClusterOrPackage(args[2]);
    }
    var data = new PhyreClusterReader().Read(cluster);
    foreach (var className in new[] { "PAssetReference", "PAssetReferenceImport" })
    {
        foreach (var group in data.Metadata.InstanceGroups.Where(value =>
                     value.ClassName == className && value.Count != 0))
        {
            var descriptor = data.Metadata.Classes.First(value => value.Name == className);
            var idMember = PhyreObjectWriter.Chain(descriptor, data.Metadata.Classes)
                .First(value => value.Name == "m_id");
            for (uint objectId = 0; objectId < group.Count; objectId++)
            {
                var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
                    value.SourceListIndex == group.Index
                    && value.SourceObjectId == objectId
                    && (value.SourceOffsetOrMember == (uint)idMember.Index
                        || (value.SourceOffsetOrMember & 0x7fffffffu)
                            is var source && (source == idMember.ValueOffset
                                || source + descriptor.OffsetFromParent == idMember.ValueOffset)));
                if (fixup is null)
                {
                    Console.WriteLine($"{className}[{objectId}] <no m_id array fixup>");
                    continue;
                }
                var available = checked((int)(group.ArraysSize - fixup.Offset));
                var bytes = data.GetArrayData(group.Index, fixup.Offset, (uint)available).Span;
                var end = bytes.IndexOf((byte)0);
                if (end >= 0) bytes = bytes[..end];
                Console.WriteLine(
                    $"{className}[{objectId}] {System.Text.Encoding.ASCII.GetString(bytes)}");
            }
        }
    }
    return 0;
}

// Dumps every object's raw 32-bit words for one class in a package. This is a
// format oracle rather than a model-specific check: it lets an author compare a
// field layout across unrelated shipped assets before assigning semantics to any
// offset.
//
//   PhyreAuthoringProbe x --dump-class <package-or-cluster> <class> [limit]
if (args.Length > 3 && args[1] == "--dump-class")
{
    var wantedClass = args[3];
    var limit = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;
    var input = File.ReadAllBytes(args[2]);
    var clusters = new List<(string Name, byte[] Data)>();
    if (input.AsSpan().StartsWith("RYHP"u8))
    {
        clusters.Add((Path.GetFileName(args[2]), input));
    }
    else
    {
        var package = new PkgArchiveReader().Read(args[2]);
        clusters.AddRange(package.Entries
            .Where(value => value.Name.EndsWith(
                ".dae.phyre", StringComparison.OrdinalIgnoreCase))
            .Select(value => (value.Name, package.ReadEntry(value))));
    }
    foreach (var entry in clusters)
    {
        var data = new PhyreClusterReader().Read(entry.Data);
        var group = data.Metadata.InstanceGroups.FirstOrDefault(value =>
            string.Equals(value.ClassName, wantedClass, StringComparison.Ordinal));
        if (group is null || group.Count == 0) continue;
        var objects = data.GetGroupObjectsData(group.Index).Span;
        var objectSize = checked((int)(group.ObjectsSize / group.Count));
        Console.WriteLine($"{entry.Name}: {wantedClass} x{group.Count}, {objectSize} bytes each");
        for (var id = 0; id < Math.Min((int)group.Count, limit); id++)
        {
            var objectBytes = objects.Slice(id * objectSize, objectSize);
            var words = new List<string>((objectSize + 3) / 4);
            for (var offset = 0; offset + 4 <= objectSize; offset += 4)
            {
                var bits = BitConverter.ToUInt32(objectBytes[offset..]);
                var number = BitConverter.ToSingle(objectBytes[offset..]);
                words.Add($"+{offset:X2}=0x{bits:X8}/{number:R}");
            }
            Console.WriteLine($"  [{id}] {string.Join("  ", words)}");
        }
    }
    return 0;
}

// Which members carry a link in a shipped cluster and which carry one in ours, class
// by class. Two models of the same kind cannot be compared byte for byte — their
// geometry differs — but they should be wired the same way, and a member the game
// always points from and we never do is a missing link, not a difference of content.
if (args.Length > 3 && args[1] == "--diff-wiring")
{
    static (Dictionary<string, SortedSet<string>> Links, Dictionary<string, int> Counts) Wiring(
        string path)
    {
        var bytes = ReadClusterOrPackage(path);
        var cut = PhyreClusterSectionReader.Read(bytes);
        var fixups = new PhyreFixupReader().Read(bytes, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();
        var links = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            counts[group.ClassName ?? "?"] = (int)group.Count;
        }
        foreach (var fixup in fixups.Pointers.Cast<PhyreFixup>().Concat(fixups.Arrays))
        {
            var group = cut.Metadata.InstanceGroups
                .FirstOrDefault(value => value.Index == fixup.SourceListIndex);
            if (group?.ClassName is not { } className) continue;
            var descriptor = classes.FirstOrDefault(value => value.Name == className);
            if (descriptor is null) continue;
            var chain = PhyreObjectWriter.Chain(descriptor, classes).ToList();
            var raw = (fixup.SourceOffsetOrMember & 0x80000000u) != 0;
            var offset = fixup.SourceOffsetOrMember & 0x7FFFFFFFu;
            var member = raw
                ? chain.FirstOrDefault(m => m.ValueOffset == offset || m.ValueOffset + 4 == offset)
                : chain.FirstOrDefault(m => m.Index == fixup.SourceOffsetOrMember);
            if (!links.TryGetValue(className, out var set))
            {
                links[className] = set = new SortedSet<string>(StringComparer.Ordinal);
            }
            set.Add(member?.Name ?? $"+0x{offset:X}");
        }
        return (links, counts);
    }

    var shippedWiring = Wiring(args[2]);
    var ourWiring = Wiring(args[3]);
    foreach (var className in shippedWiring.Links.Keys
                 .Union(ourWiring.Links.Keys, StringComparer.Ordinal)
                 .OrderBy(value => value, StringComparer.Ordinal))
    {
        shippedWiring.Links.TryGetValue(className, out var theirs);
        ourWiring.Links.TryGetValue(className, out var ours);
        theirs ??= new SortedSet<string>(StringComparer.Ordinal);
        ours ??= new SortedSet<string>(StringComparer.Ordinal);
        var onlyTheirs = theirs.Except(ours).ToArray();
        var onlyOurs = ours.Except(theirs).ToArray();
        if (onlyTheirs.Length == 0 && onlyOurs.Length == 0) continue;
        shippedWiring.Counts.TryGetValue(className, out var theirCount);
        ourWiring.Counts.TryGetValue(className, out var ourCount);
        Console.WriteLine($"{className} (shipped x{theirCount}, ours x{ourCount})");
        if (onlyTheirs.Length != 0)
        {
            Console.WriteLine("    only the game links: " + string.Join(", ", onlyTheirs));
        }
        if (onlyOurs.Length != 0)
        {
            Console.WriteLine("    only we link:        " + string.Join(", ", onlyOurs));
        }
    }
    return 0;
}

// Every pointer of one class, with WHERE it comes from and WHAT it reaches. The
// wiring diff says which members link; this says where they land, which is the part
// a copied block gets wrong when its offsets mean something else in the new file.
if (args.Length > 4 && args[1] == "--diff-targets")
{
    static List<string> Targets(string path, string className)
    {
        var bytes = ReadClusterOrPackage(path);
        var cut = PhyreClusterSectionReader.Read(bytes);
        var fixups = new PhyreFixupReader().Read(bytes, cut.Metadata);
        var group = cut.Metadata.InstanceGroups.FirstOrDefault(g => g.ClassName == className);
        var lines = new List<string>();
        if (group is null) return lines;
        foreach (var fixup in fixups.Pointers)
        {
            if (fixup.SourceListIndex != group.Index) continue;
            var target = cut.Metadata.InstanceGroups
                .FirstOrDefault(g => g.Index == (int)fixup.DestinationListIndex);
            lines.Add($"pointer obj {fixup.SourceObjectId} src 0x{fixup.SourceOffsetOrMember:X}"
                + $" -> {target?.ClassName ?? "user"}[{fixup.DestinationObjectId}]"
                + $"+{fixup.DestinationOffset}"
                + (fixup.UserFixupId is { } u ? $" user{u}" : string.Empty));
        }
        foreach (var fixup in fixups.Arrays)
        {
            if (fixup.SourceListIndex != group.Index) continue;
            lines.Add($"array obj {fixup.SourceObjectId} src 0x{fixup.SourceOffsetOrMember:X}"
                + $" count {fixup.Count} offset {fixup.Offset}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    var theirs = Targets(args[2], args[4]);
    var ours = Targets(args[3], args[4]);
    Console.WriteLine($"{args[4]}: {theirs.Count} pointers shipped, {ours.Count} ours");
    foreach (var line in theirs.Except(ours, StringComparer.Ordinal))
    {
        Console.WriteLine("  only the game: " + line);
    }
    foreach (var line in ours.Except(theirs, StringComparer.Ordinal))
    {
        Console.WriteLine("  only ours:     " + line);
    }
    return 0;
}

if (args.Length > 3 && args[1] == "--dump-targets")
{
    var bytes = ReadClusterOrPackage(args[2]);
    var cut = PhyreClusterSectionReader.Read(bytes);
    var fixups = new PhyreFixupReader().Read(bytes, cut.Metadata);
    var group = cut.Metadata.InstanceGroups.FirstOrDefault(value =>
        value.ClassName == args[3]);
    if (group is null) return 0;
    foreach (var fixup in fixups.Pointers.Where(value =>
                 value.SourceListIndex == group.Index))
    {
        var target = cut.Metadata.InstanceGroups.FirstOrDefault(value =>
            value.Index == (int)fixup.DestinationListIndex);
        Console.WriteLine(
            $"pointer obj {fixup.SourceObjectId} src 0x{fixup.SourceOffsetOrMember:X}"
            + $" -> {target?.ClassName ?? "user"}[{fixup.DestinationObjectId}]"
            + (fixup.UserFixupId is { } user ? $" user{user}" : string.Empty));
    }
    foreach (var fixup in fixups.Arrays.Where(value =>
                 value.SourceListIndex == group.Index))
    {
        Console.WriteLine(
            $"array obj {fixup.SourceObjectId} src 0x{fixup.SourceOffsetOrMember:X}"
            + $" count {fixup.Count} offset {fixup.Offset}");
    }
    return 0;
}

// Assembles an authored cluster twice — once from its description, once from what
// our own readers make of the first — and prints how each fixup block was packed on
// both passes. A block packed one way and read back as another is a block whose
// pointers the engine places somewhere nobody chose.
if (args.Length > 1 && args[1] == "--roundtrip-trace")
{
    var quad = new List<PhyreVertexSource>();
    foreach (var corner in new[]
             {
                 new System.Numerics.Vector3(-0.5f, 0f, -0.5f),
                 new System.Numerics.Vector3(0.5f, 0f, -0.5f),
                 new System.Numerics.Vector3(0.5f, 1f, -0.5f),
                 new System.Numerics.Vector3(-0.5f, 1f, -0.5f),
             })
    {
        quad.Add(new PhyreVertexSource(
            corner,
            new System.Numerics.Vector3(0f, 0f, -1f),
            new[]
            {
                new PhyreTexCoordSet(
                    new System.Numerics.Vector2(0f, 0f),
                    System.Numerics.Vector3.UnitX,
                    System.Numerics.Vector3.UnitY),
            },
            Array.Empty<int>(),
            Array.Empty<float>()));
    }
    var quadModel = new PhyreModelSource(
        "roundtrip",
        new[] { new PhyreMeshSource("mesh", quad, new[] { 0, 1, 2, 0, 2, 3 }) },
        Array.Empty<PhyreJointSource>());

    var firstTrace = new List<(long Offset, byte Packing, uint Mask, uint Source, int Count)>();
    PhyreFixupPacker.Trace = firstTrace;
    var first = PhyreClusterAssembler.Assemble(PhyreModelClusterWriter.Contents(
        quadModel, new PhyreShaderBinding("shaders/ed8.fx#TEST"),
        PhyreModelGeometryPacker.Pack(quadModel)));

    var cut = PhyreClusterSectionReader.Read(first);
    var data = new PhyreClusterReader().Read(first);
    var fixups = new PhyreFixupReader().Read(first, cut.Metadata);
    var classes = cut.Metadata.Classes.ToList();
    var groups = new List<PhyreGroupContents>();
    foreach (var group in cut.Metadata.InstanceGroups)
    {
        var className = group.ClassName ?? string.Empty;
        var objects = new List<PhyreObjectContents>();
        var each = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
        var stored = data.GetGroupObjectsData(group.Index).Span;
        for (uint id = 0; id < group.Count; id++)
        {
            objects.Add(PhyreObjectWriter.ReadObject(
                stored.Slice((int)(id * each), each), className, classes));
        }
        groups.Add(new PhyreGroupContents(className, objects,
            group.ArraysSize == 0
                ? ReadOnlyMemory<byte>.Empty
                : data.GetArrayData(group.Index, 0, group.ArraysSize)));
    }

    var secondTrace = new List<(long Offset, byte Packing, uint Mask, uint Source, int Count)>();
    PhyreFixupPacker.Trace = secondTrace;
    var second = PhyreClusterAssembler.Assemble(new PhyreClusterContents(
        cut.Metadata.Types, groups, fixups, fixups.UserFixups, cut.HeaderClasses,
        cut.Payload, PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
        cut.Header[(17 * sizeof(uint))..]));
    PhyreFixupPacker.Trace = null;

    Console.WriteLine($"first {first.Length} bytes, second {second.Length} bytes,"
        + $" blocks {firstTrace.Count}/{secondTrace.Count}");
    for (var at = 0; at < Math.Min(first.Length, second.Length); at++)
    {
        if (first[at] == second[at]) continue;
        Console.WriteLine($"  first differing byte at {at}: 0x{first[at]:X2} vs 0x{second[at]:X2}");
        break;
    }
    var cutFirst = PhyreClusterSectionReader.Read(first);
    Console.WriteLine($"  pointer table starts at {first.Length - cutFirst.Payload.Length
        - cutFirst.ArrayFixups.Length - cutFirst.PointerFixups.Length}"
        + $", {cutFirst.PointerFixups.Length} bytes;"
        + $" array table {cutFirst.ArrayFixups.Length} bytes");
    for (var index = 0; index < Math.Max(firstTrace.Count, secondTrace.Count); index++)
    {
        var a = index < firstTrace.Count ? firstTrace[index] : default;
        var b = index < secondTrace.Count ? secondTrace[index] : default;
        Console.WriteLine($"  block {index,2}: +{a.Offset,-5} pack={a.Packing} mask={a.Mask,-3}"
            + $" source={a.Source,-6} count={a.Count}"
            + (a.Packing == b.Packing && a.Mask == b.Mask && a.Source == b.Source
                && a.Count == b.Count ? "" : "   <-- DIFFERENT"));
    }
    return 0;
}

// Fidelity, not self-consistency: assemble a shipped cluster from its own description
// and require the GAME'S bytes back. The existing assemble-check reads our output with
// our readers and asks for the same groups — which an engine does not do. Until a
// shipped cluster comes back byte for byte, an authored one is a guess, and every
// difference this prints is a real defect that costs nothing to find.
if (args.Length > 2 && args[1] == "--fidelity")
{
    var name = args[2];
    var pkg = new PkgArchiveReader().Read(name);
    foreach (var entry in pkg.Entries.Where(e =>
                 e.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase)
                 && e.Name.Contains(".dae.", StringComparison.OrdinalIgnoreCase)))
    {
        var cluster = pkg.ReadEntry(entry);
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
            groups.Add(new PhyreGroupContents(className, objects,
                group.ArraysSize == 0
                    ? ReadOnlyMemory<byte>.Empty
                    : data.GetArrayData(group.Index, 0, group.ArraysSize)));
        }
        var schemaProfile = cut.Metadata.Classes.Any(value =>
                value.Name == "PIndexDataBlock")
            ? PhyreSchemaProfile.FalcomAssetProcessor
            : PhyreSchemaProfile.Cs1RuntimeAuthoring;
        var rebuilt = PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            cut.Metadata.Types,
            groups,
            fixups,
            fixups.UserFixups,
            cut.HeaderClasses,
            cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..],
            schemaProfile));

        var original = cluster.AsSpan();
        var made = rebuilt.AsSpan();
        var first = -1;
        var differing = 0;
        for (var at = 0; at < Math.Min(original.Length, made.Length); at++)
        {
            if (original[at] == made[at]) continue;
            if (first < 0) first = at;
            differing++;
        }
        // The counters of both namespaces, so a size difference says which part of it
        // is short rather than only that something is.
        static (uint Types, uint Classes, uint Members, uint Table, uint Size) Counters(
            ReadOnlySpan<byte> cluster)
        {
            const int at = 84;
            return (
                BitConverter.ToUInt32(cluster[(at + 8)..]),
                BitConverter.ToUInt32(cluster[(at + 12)..]),
                BitConverter.ToUInt32(cluster[(at + 16)..]),
                BitConverter.ToUInt32(cluster[(at + 20)..]),
                BitConverter.ToUInt32(cluster[(at + 4)..]));
        }
        var shippedCounts = Counters(cluster);
        var ourCounts = Counters(rebuilt);
        var ourClasses = PhyreClusterSectionReader.Read(rebuilt).Metadata.Classes
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var missing = cut.Metadata.Classes
            .Select(value => value.Name)
            .Where(value => !ourClasses.Contains(value))
            .ToArray();
        if (missing.Length != 0)
        {
            Console.WriteLine($"  classes the shipped file lists and our closure drops"
                + $" ({missing.Length}): {string.Join(", ", missing)}");
        }
        Console.WriteLine($"  namespace shipped: types {shippedCounts.Types},"
            + $" classes {shippedCounts.Classes}, members {shippedCounts.Members},"
            + $" strings {shippedCounts.Table}, size {shippedCounts.Size}");
        Console.WriteLine($"  namespace ours   : types {ourCounts.Types},"
            + $" classes {ourCounts.Classes}, members {ourCounts.Members},"
            + $" strings {ourCounts.Table}, size {ourCounts.Size}");

        Console.WriteLine($"{entry.Name}: shipped {original.Length}, rebuilt {made.Length}"
            + (first < 0 && original.Length == made.Length
                ? " — IDENTICAL"
                : $" — first difference at {first}, {differing} bytes differ"));
        if (first >= 0)
        {
            var from = Math.Max(0, first - 8);
            Console.WriteLine("    shipped " + Convert.ToHexString(
                original[from..Math.Min(original.Length, first + 24)]));
            Console.WriteLine("    ours    " + Convert.ToHexString(
                made[from..Math.Min(made.Length, first + 24)]));
        }
    }
    return 0;
}

// Two clusters side by side, read from explicit paths rather than from the game's
// asset folder — so an authored file can be held against the one it replaces.
if (args.Length > 3 && args[1] == "--diff-clusters")
{
    foreach (var path in new[] { args[2], args[3] })
    {
        var bytes = ReadClusterOrPackage(path);
        var cut = PhyreClusterSectionReader.Read(bytes);
        var head = cut.Metadata.Header;
        Console.WriteLine($"=== {Path.GetFileName(path)} — {bytes.Length} bytes");
        Console.WriteLine($"  instance groups        {cut.Metadata.InstanceGroups.Count}");
        Console.WriteLine($"  classes / types        {cut.Metadata.Classes.Count} / {cut.Metadata.Types.Count}");
        Console.WriteLine($"  object data            {cut.ObjectData.Length}");
        Console.WriteLine($"  user fixups            {head.UserFixupCount}, data {head.UserFixupDataSize}");
        Console.WriteLine($"  header class instances {head.HeaderClassInstanceCount}"
            + $", children {head.HeaderClassChildCount}, section {cut.HeaderClasses.Length}");
        Console.WriteLine($"  pointer-array fixups   {head.PointerArrayFixupSize} bytes");
        Console.WriteLine($"  pointer fixups         {head.PointerFixupSize} bytes");
        Console.WriteLine($"  array fixups           {head.ArrayFixupSize} bytes");
        Console.WriteLine($"  payload                {cut.Payload.Length}");
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            Console.WriteLine($"    group {group.Index,2} {group.ClassName,-32} x{group.Count,-5}"
                + $" objects {group.ObjectsSize,-8} arrays {group.ArraysSize}");
        }

        // The parameter buffer itself, byte for byte, and the records that describe
        // what is inside it. This is the part a mismatch is fatal in: the engine
        // reads a size here and trusts it.
        var data = new PhyreClusterReader().Read(bytes);
        var first = cut.Metadata.InstanceGroups.FirstOrDefault(g => g.ClassName == "PParameterBuffer");
        if (first is not null)
        {
            var objects = data.GetGroupObjectsData(first.Index).ToArray();
            var each = (int)(first.ObjectsSize / Math.Max(first.Count, 1u));
            Console.WriteLine($"  parameter buffer object: {each} bytes");
            Console.WriteLine("    " + Convert.ToHexString(objects.AsSpan(0, Math.Min(each, 64))));
            var section = cut.HeaderClasses.Span;
            var instances = (int)head.HeaderClassInstanceCount;
            if (section.Length >= instances * 4)
            {
                var counts = new int[instances];
                for (var i = 0; i < instances; i++) counts[i] = BitConverter.ToInt32(section[(i * 4)..]);
                Console.WriteLine("    child counts per header instance: "
                    + string.Join(", ", counts));
                var at = instances * 4;
                for (var i = 0; i < Math.Min(6, counts.Length == 0 ? 0 : counts[0]); i++)
                {
                    Console.WriteLine($"    record {i}: type {BitConverter.ToUInt32(section[(at + i * 16)..])}"
                        + $" @{BitConverter.ToUInt32(section[(at + i * 16 + 4)..])}"
                        + $" flags {BitConverter.ToUInt32(section[(at + i * 16 + 8)..])}"
                        + $" count {BitConverter.ToUInt32(section[(at + i * 16 + 12)..])}");
                }
            }
        }
    }
    return 0;
}

if (args.Length > 3 && args[1] == "--diff-schema")
{
    var left = PhyreClusterSectionReader.Read(ReadClusterOrPackage(args[2])).Metadata.Classes;
    var right = PhyreClusterSectionReader.Read(ReadClusterOrPackage(args[3])).Metadata.Classes;
    foreach (var name in left.Select(value => value.Name)
                 .Union(right.Select(value => value.Name), StringComparer.Ordinal)
                 .OrderBy(value => value, StringComparer.Ordinal))
    {
        var a = left.FirstOrDefault(value => value.Name == name);
        var b = right.FirstOrDefault(value => value.Name == name);
        if (a is null || b is null)
        {
            Console.WriteLine($"{name}: {(a is null ? "only right" : "only left")}");
            continue;
        }
        if (a.Size != b.Size || a.Alignment != b.Alignment
            || a.OffsetFromParent != b.OffsetFromParent
            || a.OffsetToBase != b.OffsetToBase
            || a.OffsetToBaseInAllocatedBlock != b.OffsetToBaseInAllocatedBlock
            || a.Flags != b.Flags)
        {
            Console.WriteLine(
                $"{name}: class"
                + $" left size={a.Size} align={a.Alignment} parent={a.OffsetFromParent}"
                + $" base={a.OffsetToBase}/{a.OffsetToBaseInAllocatedBlock} flags=0x{a.Flags:X}"
                + $" right size={b.Size} align={b.Alignment} parent={b.OffsetFromParent}"
                + $" base={b.OffsetToBase}/{b.OffsetToBaseInAllocatedBlock} flags=0x{b.Flags:X}");
        }
        foreach (var memberName in a.Members.Select(value => value.Name)
                     .Union(b.Members.Select(value => value.Name), StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var am = a.Members.FirstOrDefault(value => value.Name == memberName);
            var bm = b.Members.FirstOrDefault(value => value.Name == memberName);
            if (am is null || bm is null)
            {
                Console.WriteLine(
                    $"  {memberName}: {(am is null ? "only right" : "only left")}");
                continue;
            }
            if (am.TypeName != bm.TypeName || am.ValueOffset != bm.ValueOffset
                || am.Size != bm.Size || am.Flags != bm.Flags
                || am.FixedArraySize != bm.FixedArraySize)
            {
                Console.WriteLine(
                    $"  {memberName}:"
                    + $" left {am.TypeName}@{am.ValueOffset}+{am.Size}"
                    + $" flags=0x{am.Flags:X} fixed={am.FixedArraySize};"
                    + $" right {bm.TypeName}@{bm.ValueOffset}+{bm.Size}"
                    + $" flags=0x{bm.Flags:X} fixed={bm.FixedArraySize}");
            }
        }
    }
    return 0;
}

// Dumps a parameter buffer's object bytes, and where its shader parameters say
// their storage sits.
//
// The debugger showed the engine size that buffer eight bytes short of what it
// then copies, and the two parameters whose storage lands at the very end of the
// object — PhyreContextSwitches and PhyreMaterialSwitches — are worth exactly
// those eight. Whether their bytes are actually IN the object is a question about
// a file, so it is answered here rather than in the game.
//
//   PhyreAuthoringProbe x --buffer-tail <package.pkg>
if (args.Length > 2 && args[1] == "--buffer-tail")
{
    var cluster = ReadClusterOrPackage(args[2]);
    var read = new PhyreClusterReader().Read(cluster);
    var classes = read.Metadata.Classes;
    var buffers = read.Metadata.InstanceGroups
        .Where(value => value.ClassName == "PParameterBuffer")
        .ToArray();
    Console.WriteLine($"{Path.GetFileName(args[2])}: {buffers.Length} parameter buffer group(s)");
    foreach (var group in buffers)
    {
        var stored = read.GetGroupObjectsData(group.Index);
        var each = (int)(group.ObjectsSize / Math.Max(group.Count, 1));
        Console.WriteLine($"  group {group.Index}: {group.Count} object(s) of {each} bytes");
        var span = stored.Span;
        // The last thirty-two bytes, which is where a parameter storing at 604 or
        // 608 has to live if it lives anywhere.
        for (var at = Math.Max(0, each - 32); at < each; at += 16)
        {
            var width = Math.Min(16, each - at);
            Console.WriteLine($"    +{at,4} : {Convert.ToHexString(span.Slice(at, width))}");
        }
    }

    var definitions = read.Metadata.InstanceGroups
        .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
    if (definitions is not null)
    {
        var descriptor = classes.First(value => value.Name == "PShaderParameterDefinition");
        var members = PhyreObjectWriter.Chain(descriptor, classes).ToList();
        var where = members.First(value => value.Name == "m_bufferLoc");
        var slot = members.First(value => value.Name == "m_constantBufferLocation");
        var stored = read.GetGroupObjectsData(definitions.Index).Span;
        var each = (int)(definitions.ObjectsSize / Math.Max(definitions.Count, 1));
        var highest = 0u;
        Console.WriteLine($"  {definitions.Count} definitions of {each} bytes:");
        for (uint id = 0; id < definitions.Count; id++)
        {
            var at = (int)(id * each);
            var loc = BitConverter.ToUInt32(stored[(at + (int)where.ValueOffset)..]);
            var cb = BitConverter.ToUInt32(stored[(at + (int)slot.ValueOffset)..]);
            // The high bits are flags; the low ones are the offset into the buffer.
            var offset = loc & 0xFFFF;
            highest = Math.Max(highest, offset);
            if (id < 4 || offset >= 596)
            {
                Console.WriteLine($"    [{id}] storage @{offset} (raw 0x{loc:X}), constant buffer @{cb}");
            }
        }
        Console.WriteLine($"  highest storage offset: {highest}");
    }
    return 0;
}

// What each shader in a package declares its parameter buffer to be.
//
// A PParameterBuffer states one size; the shader's PEffectVariant states two, a
// tweakable and an untweakable one. The engine was seen allocating 620 for a
// buffer whose object says 624 and then copying 628 — three numbers, so at least
// two sources. These are the other two.
//
//   PhyreAuthoringProbe x --effect-sizes <package.pkg>
if (args.Length > 2 && args[1] == "--effect-sizes")
{
    var package = new PkgArchiveReader().Read(args[2]);
    foreach (var entry in package.Entries)
    {
        PhyreClusterData read;
        try { read = new PhyreClusterReader().Read(package.ReadEntry(entry)); }
        catch { continue; }
        var variants = read.Metadata.InstanceGroups
            .Where(value => value.ClassName == "PEffectVariant")
            .ToArray();
        if (variants.Length == 0) continue;
        Console.WriteLine($"{entry.Name}");
        var descriptor = read.Metadata.Classes.First(value => value.Name == "PEffectVariant");
        var members = PhyreObjectWriter.Chain(descriptor, read.Metadata.Classes).ToList();
        uint At(ReadOnlySpan<byte> at, string member, int width)
        {
            var field = members.FirstOrDefault(value => value.Name == member);
            if (field is null) return 0;
            return width == 2
                ? BitConverter.ToUInt16(at[(int)field.ValueOffset..])
                : BitConverter.ToUInt32(at[(int)field.ValueOffset..]);
        }
        foreach (var group in variants)
        {
            var stored = read.GetGroupObjectsData(group.Index).Span;
            var each = (int)(group.ObjectsSize / Math.Max(group.Count, 1));
            for (var id = 0; id < group.Count; id++)
            {
                var at = stored.Slice(id * each, each);
                Console.WriteLine(
                    $"  variant {id}: tweakable {At(at, "m_tweakableParameterBufferSize", 2)}"
                    + $", untweakable {At(at, "m_untweakableParameterBufferSize", 2)}"
                    + $", tweakable defs {At(at, "m_tweakableShaderParameterDefinitions", 4)}"
                    + $", untweakable defs {At(at, "m_untweakableShaderParameterDefinitions", 4)}"
                    + $", passes {At(at, "m_largestShaderPassCount", 2)}");
            }
        }
    }
    return 0;
}

// The instance groups of one cluster named by path, package or loose .phyre.
//
// --model-shape walks a game folder, which cannot reach a cluster the official
// PhyreAssetProcessor wrote into a working directory. That cluster is the ground
// truth this project never had: a model authored from a .dae by the tool the game
// shipped with, rather than reconstructed from a file it already had.
//
//   PhyreAuthoringProbe x --shape-of <cluster.phyre|package.pkg>
if (args.Length > 2 && args[1] == "--shape-of")
{
    var cluster = ReadClusterOrPackage(args[2]);
    var read = new PhyreClusterReader().Read(cluster);
    var classes = read.Metadata.Classes;
    Console.WriteLine($"{Path.GetFileName(args[2])}: {cluster.Length} bytes,"
        + $" {classes.Count} classes, {read.Metadata.Types.Count} types");
    // The header and the fixup tables, which the group list does not show. Two
    // clusters can agree on every group and still disagree on everything the
    // engine walks to reach them.
    var head = cluster.AsSpan();
    Console.WriteLine("  header words:");
    for (var word = 0; word < 21; word++)
    {
        var value = BitConverter.ToUInt32(head[(word * 4)..]);
        if (value != 0) Console.WriteLine($"    [{word,2}] {value} (0x{value:X})");
    }
    var set = new PhyreFixupReader().Read(cluster, read.Metadata);
    Console.WriteLine($"  fixups: {set.Pointers.Count} pointers,"
        + $" {set.Arrays.Count} arrays, {set.PointerArrays.Count} pointer arrays,"
        + $" {set.UserFixups.Count} user fixups");
    foreach (var user in set.UserFixups)
    {
        Console.WriteLine($"    user type {user.TypeId}"
            + $" \"{user.Text}\" ({user.Data.Length} bytes)");
    }
    // Every pointer, said in names rather than numbers, and sorted — so two
    // clusters that agree on how MANY pointers they carry can be checked on what
    // those pointers actually join. Counts have been compared for weeks; contents
    // never were, because until now there was no cluster known to load to compare
    // them against.
    string GroupName(long index) =>
        index >= 0 && index < read.Metadata.InstanceGroups.Count
            ? read.Metadata.InstanceGroups[(int)index].ClassName ?? $"?{index}"
            : $"?{index}";
    string MemberName(int groupIndex, uint key)
    {
        var group = read.Metadata.InstanceGroups.ElementAtOrDefault(groupIndex);
        if (group is null || group.ClassId == 0 || group.ClassId > classes.Count)
            return $"0x{key:X}";
        var chain = PhyreObjectWriter.Chain(classes[(int)group.ClassId - 1], classes).ToList();
        // A key is either an index into the packed member table or a raw offset
        // with the high bit set; both name a field, so both are resolved.
        var raw = key & 0x7FFFFFFF;
        var named = chain.FirstOrDefault(value => value.Index == key)
            ?? chain.FirstOrDefault(value => value.ValueOffset == raw)
            ?? chain.FirstOrDefault(value => value.ValueOffset + 4 == raw);
        return named is null ? $"0x{key:X}" : named.Name;
    }
    // Les fixups de tableaux de pointeurs, que notre writer n'emet jamais : une map
    // livree en a deux, portant douze pointeurs, et le commentaire de notre code dit
    // seulement que les MODELES STATIQUES n'en ont pas.
    foreach (var array in set.PointerArrays)
    {
        Console.WriteLine($"  tableau de pointeurs : {GroupName(array.SourceListIndex)}"
            + $"[{array.SourceObjectId}].{MemberName(array.SourceListIndex, array.SourceOffsetOrMember)}"
            + $" — {array.Count} pointeurs a l'offset {array.Offset}");
    }
    foreach (var array in set.Arrays.Take(0)) { }

    var lines = new List<string>();
    foreach (var pointer in set.Pointers)
    {
        var from = $"{GroupName(pointer.SourceListIndex)}[{pointer.SourceObjectId}]"
            + $".{MemberName(pointer.SourceListIndex, pointer.SourceOffsetOrMember)}";
        var to = pointer.UserFixupId is { } id
            ? $"user \"{set.UserFixups[(int)id].Text ?? Convert.ToHexString(set.UserFixups[(int)id].Data.Span)}\""
            : $"{GroupName(pointer.DestinationListIndex)}[{pointer.DestinationObjectId}]"
                + (pointer.DestinationOffset != 0 ? $"+{pointer.DestinationOffset}" : "")
                + (pointer.ArrayIndex != 0 ? $" x{pointer.ArrayIndex}" : "");
        lines.Add($"    {from} -> {to}");
    }
    lines.Sort(StringComparer.Ordinal);
    Console.WriteLine("  pointers:");
    foreach (var line in lines) Console.WriteLine(line);
    foreach (var group in read.Metadata.InstanceGroups)
    {
        if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
        var each = group.ObjectsSize / Math.Max(group.Count, 1);
        Console.WriteLine(
            $"  {classes[(int)group.ClassId - 1].Name} x{group.Count}"
            + $" ({each} bytes each, arrays {group.ArraysSize})");
        // Every non-zero field of every object. The graph has been compared to
        // exhaustion; the NUMBERS inside the objects never were, and a wrong
        // m_elementCount or m_offsetInVertexBuffer crashes without moving a single
        // pointer.
        {
            var members = PhyreObjectWriter.Chain(classes[(int)group.ClassId - 1], classes).ToList();
            var stored = read.GetGroupObjectsData(group.Index).Span;
            var size = (int)(group.ObjectsSize / group.Count);
            for (uint id = 0; id < group.Count && id < 64; id++)
            {
                var told = new List<string>();
                foreach (var member in members)
                {
                    var span = (int)(member.Size * Math.Max(member.FixedArraySize, 1));
                    if (member.ValueOffset + span > size || span is 0 or > 16) continue;
                    var bytes = stored.Slice((int)(id * size) + (int)member.ValueOffset, span);
                    var zero = true;
                    foreach (var b in bytes) if (b != 0) { zero = false; break; }
                    if (zero) continue;
                    told.Add($"{member.Name}={Convert.ToHexString(bytes)}");
                }
                if (told.Count != 0) Console.WriteLine($"      [{id}] {string.Join(", ", told)}");
                // A mesh segment embeds its index block at +28, whose fields belong
                // to PIndexDataBlockD3D11 and not to the segment — so no member walk
                // reaches them. They are the only bytes in the file nothing here has
                // ever looked at.
                if (classes[(int)group.ClassId - 1].Name == "PMeshSegment")
                {
                    var raw = stored.Slice((int)(id * size), size);
                    for (var at = 0; at < size; at += 16)
                    {
                        Console.WriteLine($"        +{at,3} {Convert.ToHexString(raw[at..Math.Min(at + 16, size)])}");
                    }
                }
            }
        }

        // The names a group carries live in its array data. Printing them turns a
        // size difference — the only thing left between this writer's output and a
        // cluster known to load — into the actual strings that differ.
        if (group.ArraysSize == 0) continue;
        var arrays = read.GetArrayData(group.Index, 0, group.ArraysSize).Span;
        var text = new System.Text.StringBuilder();
        foreach (var b in arrays)
        {
            if (b >= 0x20 && b < 0x7F) text.Append((char)b);
            else if (text.Length != 0 && text[^1] != '·') text.Append('·');
        }
        foreach (var word in text.ToString().Split('·', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 2) Console.WriteLine($"      \"{word}\"");
        }
    }
    return 0;
}

// Which shader each parameter buffer of a package is bound to, and its bytes.
//
// A buffer's values belong to ONE shader. Ours are synthesised neutral defaults,
// which is why an authored cube draws black: the layout is right and the numbers
// mean nothing. To copy real ones we first have to find, among a package's several
// buffers, the one bound to the shader we actually use — the first mesh's material
// is not it.
//
//   PhyreAuthoringProbe x --buffer-shaders <package.pkg> [<hash> <out.bin>]
if (args.Length > 2 && args[1] == "--buffer-shaders")
{
    var cluster = ReadClusterOrPackage(args[2]);
    var read = new PhyreClusterReader().Read(cluster);
    var fixups = new PhyreFixupReader().Read(cluster, read.Metadata);
    var groups = read.Metadata.InstanceGroups;
    var imports = groups.FirstOrDefault(value => value.ClassName == "PAssetReferenceImport");
    string ImportName(uint index)
    {
        if (imports is null || index >= imports.Count) return $"?{index}";
        var descriptor = read.Metadata.Classes.First(value => value.Name == "PAssetReferenceImport");
        var member = PhyreObjectWriter.Chain(descriptor, read.Metadata.Classes)
            .First(value => value.Name == "m_id");
        var array = fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == imports.Index
            && value.SourceObjectId == index
            && (value.SourceOffsetOrMember == member.Index
                || value.SourceOffsetOrMember == member.ValueOffset
                || value.SourceOffsetOrMember == (0x80000000u | member.ValueOffset)));
        if (array is null) return $"?{index}";
        // Clamp to what the group actually holds: a name near the end of the run
        // has fewer than 128 bytes after it.
        var room = imports.ArraysSize - array.Offset;
        var bytes = read.GetArrayData(imports.Index, array.Offset, Math.Min(128u, room)).Span;
        var end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes[..(end < 0 ? bytes.Length : end)]);
    }
    foreach (var group in groups.Where(value => value.ClassName == "PParameterBuffer"))
    {
        var bound = "(none)";
        foreach (var pointer in fixups.Pointers)
        {
            if (pointer.SourceListIndex != group.Index || pointer.UserFixupId is null) continue;
            var user = fixups.UserFixups[(int)pointer.UserFixupId.Value];
            if (user.Data.Length < 2) continue;
            var name = ImportName((uint)((user.Data.Span[0] << 8) | user.Data.Span[1]));
            if (name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)) bound = name;
        }
        var each = (int)(group.ObjectsSize / Math.Max(group.Count, 1));
        Console.WriteLine($"  group {group.Index}: {each} bytes -> {bound}");
        if (args.Length > 4 && bound.Contains(args[3], StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(args[4], read.GetGroupObjectsData(group.Index).Span[..each].ToArray());
            Console.WriteLine($"    wrote {args[4]} ({each} bytes)");
        }
    }
    return 0;
}

// Dit ce qui differe entre deux .ops, categorie par categorie.
//
// Le t1000.ops de Trista a ete edite ce matin et fait 35 octets de moins que
// celui du jeu. La map se charge — on l'a vu une dizaine de fois aujourd'hui —
// mais "ca se charge" n'est pas "rien n'a ete perdu", et un placement supprime
// en silence par un writer est le genre de degat qui se decouvre bien plus tard.
//
//   PhyreAuthoringProbe x --ops-diff <original.ops> <actuel.ops>
if (args.Length > 3 && args[1] == "--ops-diff")
{
    var opsReader = new ED8Editor.Ops.OpsReader();
    var before = opsReader.Read(args[2]);
    var after = opsReader.Read(args[3]);
    void Count(string what, int a, int b)
        => Console.WriteLine($"  {what,-10} {a,4} -> {b,4}{(a == b ? "" : "   <-- CHANGE")}");
    Count("props", before.Props.Count, after.Props.Count);
    Count("volumes", before.Volumes.Count, after.Volumes.Count);
    Count("points", before.Points.Count, after.Points.Count);
    Count("cameras", before.Cameras.Count, after.Cameras.Count);
    Count("sounds", before.Sounds.Count, after.Sounds.Count);
    Count("lights", before.Lights.Count, after.Lights.Count);
    for (var at = 0; at < Math.Min(before.Props.Count, after.Props.Count); at++)
    {
        var a = before.Props[at];
        var b = after.Props[at];
        if (a.ToString() != b.ToString())
        {
            Console.WriteLine($"  prop [{at}]");
            Console.WriteLine($"    avant : {a}");
            Console.WriteLine($"    apres : {b}");
        }
    }
    return 0;
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
var emitLibraryFile = pattern == "--emit-library-file";
var schemaUnion = pattern == "--schema-union" || emitLibrary || emitLibraryFile;
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
var locators = pattern == "--locators";
if (locators) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var physics = pattern == "--physics";
if (physics) pattern = args.Length > 3 ? args[3] : "M_*.pkg";
var entries = pattern == "--entries";
if (entries) pattern = args.Length > 3 ? args[3] : "M_A0000.pkg";
var manifest = pattern == "--manifest";
if (manifest) pattern = args.Length > 3 ? args[3] : "M_A0000.pkg";
var basis = pattern == "--basis";
if (basis) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var smallest = pattern == "--smallest";
if (smallest) pattern = args.Length > 3 ? args[3] : "*.pkg";
var shape = pattern == "--model-shape";
if (shape) pattern = args.Length > 3 ? args[3] : "O_MOVIESCREEN.pkg";
var graph = pattern == "--model-graph";
if (graph) pattern = args.Length > 3 ? args[3] : "O_MOVIESCREEN.pkg";
var build = pattern == "--build-minimal";
if (build) pattern = args.Length > 3 ? args[3] : "O_MOVIESCREEN.pkg";
var shaderParams = pattern == "--shader-params";
if (shaderParams) pattern = args.Length > 3 ? args[3] : "O_*.pkg";
var lookup = pattern == "--shader-lookup";
if (lookup) pattern = args.Length > 3 ? args[3] : "O_*.pkg";
var derive = pattern == "--derive-fields";
if (derive) pattern = args.Length > 3 ? args[3] : "O_MOVIESCREEN.pkg";
var writeModel = pattern == "--write-model";
if (writeModel) pattern = args.Length > 3 ? args[3] : "O_MOVIESCREEN.pkg";
var physicsRepair = pattern == "--physics-repair";
if (physicsRepair) pattern = args.Length > 3 ? args[3] : "M_R0510.pkg";
var vertexLayout = pattern == "--vertex-layout";
if (vertexLayout) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var packCheck = pattern == "--pack-check";
if (packCheck) pattern = args.Length > 3 ? args[3] : "C_PLY*.pkg";
var compare = pattern == "--compare";
if (compare) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var effectSource = pattern == "--effect-source";
if (effectSource) pattern = args.Length > 3 ? args[3] : "M_*.pkg";
var effectReflect = pattern == "--effect-reflect";
if (effectReflect) pattern = args.Length > 3 ? args[3] : "M_*.pkg";
var minimalEffect = pattern == "--minimal-effect";
if (minimalEffect) pattern = "M_R0510.pkg";
if (blocks || packings) pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
var reader = new PkgArchiveReader();
if (manifest)
{
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        var package = reader.Read(path);
        var entry = package.Entries.FirstOrDefault(value =>
            value.Name.Equals("asset_D3D11.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) continue;
        Console.WriteLine($"// {Path.GetFileName(path)}");
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(package.ReadEntry(entry)));
    }
    return 0;
}
if (pattern == "--compare")
{
    pattern = args.Length > 3 ? args[3] : "C_PLY000.pkg";
}
if (effectSource)
{
    var inspected = 0;
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.Contains(".fx", StringComparison.OrdinalIgnoreCase)) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var group = data.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PEffect");
        if (group is null || group.Count == 0) continue;
        inspected++;
        var member = data.Metadata.Classes
            .First(value => value.Name == "PEffect").Members
            .First(value => value.Name == "m_effectSource");
        var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == group.Index
            && value.SourceObjectId == 0
            && (value.SourceOffsetOrMember == member.Index
                || value.SourceOffsetOrMember == member.ValueOffset
                || value.SourceOffsetOrMember == (0x80000000u | member.ValueOffset)));
        byte[] bytes;
        if (fixup is not null)
        {
            bytes = data.GetArrayData(group.Index, fixup.Offset, fixup.Count).ToArray();
        }
        else
        {
            var pointer = data.Fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == group.Index
                && value.SourceObjectId == 0
                && (value.SourceOffsetOrMember == member.Index
                    || value.SourceOffsetOrMember == member.ValueOffset));
            if (pointer?.UserFixupId is null)
            {
                Console.Error.WriteLine(
                    $"{name}: PEffect source has no array/user pointer; "
                    + $"array keys [{string.Join(", ", data.Fixups.Arrays.Where(value => value.SourceListIndex == group.Index).Select(value => $"0x{value.SourceOffsetOrMember:X}"))}], "
                    + $"pointer keys [{string.Join(", ", data.Fixups.Pointers.Where(value => value.SourceListIndex == group.Index).Select(value => $"0x{value.SourceOffsetOrMember:X}/u{value.UserFixupId?.ToString() ?? "-"}"))}]");
                continue;
            }
            bytes = data.Fixups.UserFixups[(int)pointer.UserFixupId.Value].Data.ToArray();
        }
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        if (length == 0) continue;
        Console.WriteLine($"// {name}");
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes, 0, length));
        return 0;
    }
    Console.Error.WriteLine($"No effect source found in {inspected} effect cluster(s).");
    return 1;
}
if (minimalEffect)
{
    var written = PhyreMinimalEffectWriter.Write();
    var metadata = new PhyreEffectRenderPassReader().ReadMetadata(written);
    var pass = metadata.Program?.SceneRenderPasses["Opaque"].Permutations.Single()
        ?? throw new InvalidDataException("The authored effect has no Opaque program.");
    var inspector = new ED8Editor.Rendering.D3D11ShaderProgramInspector();
    var vertex = inspector.Inspect(
        pass.VertexProgram, ED8Editor.Rendering.D3D11ShaderStage.Vertex);
    Console.WriteLine(
        $"{written.Length} bytes, {pass.Inputs.Count} input, "
        + $"{vertex.ConstantBuffers.Single().Size}-byte global buffer, "
        + $"{Convert.ToHexString(pass.VertexProgram.Bytecode.AsSpan(0, 4))} bytecode");
    return 0;
}
if (effectReflect)
{
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.Contains(".fx", StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            var clusterData = new PhyreClusterReader().Read(cluster);
            foreach (var fixup in clusterData.Fixups.PointerArrays)
            {
                var sourceGroup = clusterData.Metadata.InstanceGroups[fixup.SourceListIndex];
                Console.WriteLine(
                    $"  pointer array {sourceGroup.ClassName}[{fixup.SourceObjectId}]"
                    + $".+0x{fixup.SourceOffset:X} count {fixup.Count}");
            }
            var assetGroup = clusterData.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == "PAssetReference");
            if (assetGroup is not null)
            {
                var idMember = clusterData.Metadata.Classes
                    .First(value => value.Name == "PAssetReference").Members
                    .First(value => value.Name == "m_id");
                foreach (var pointer in clusterData.Fixups.Pointers.Where(value =>
                             value.SourceListIndex == assetGroup.Index))
                {
                    Console.WriteLine(
                        $"  asset ref {pointer.SourceObjectId} -> "
                        + $"{clusterData.Metadata.InstanceGroups[(int)pointer.DestinationListIndex].ClassName}"
                        + $"[{pointer.DestinationObjectId}]");
                }
                for (uint id = 0; id < assetGroup.Count; id++)
                {
                    var idFixup = clusterData.Fixups.Arrays.FirstOrDefault(value =>
                        value.SourceListIndex == assetGroup.Index
                        && value.SourceObjectId == id
                        && (value.SourceOffsetOrMember == (uint)idMember.Index
                            || value.SourceOffset == idMember.ValueOffset));
                    if (idFixup is null) continue;
                    var bytes = clusterData.GetArrayData(
                        assetGroup.Index, idFixup.Offset,
                        assetGroup.ArraysSize - idFixup.Offset).Span;
                    var zero = bytes.IndexOf((byte)0);
                    if (zero >= 0) bytes = bytes[..zero];
                    Console.WriteLine(
                        $"  asset id {id}: {System.Text.Encoding.ASCII.GetString(bytes)}");
                }
            }
            foreach (var fixup in clusterData.Fixups.UserFixups.Where(value =>
                         value.Text is "Opaque" or "POSITION"))
                Console.WriteLine($"  user '{fixup.Text}' type {fixup.TypeId}");
            var streamGroup = clusterData.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == "PShaderStreamDefinition");
            if (streamGroup is not null && streamGroup.Count > 0)
            {
                var renderMember = clusterData.Metadata.Classes
                    .First(value => value.Name == "PShaderStreamDefinition").Members
                    .First(value => value.Name == "m_renderType");
                var renderPointer = clusterData.Fixups.Pointers.FirstOrDefault(value =>
                    value.SourceListIndex == streamGroup.Index && value.SourceObjectId == 0
                    && value.IsClassDataMember && value.SourceMemberId == (uint)renderMember.Index);
                if (renderPointer?.UserFixupId is { } renderUser)
                {
                    var user = clusterData.Fixups.UserFixups[(int)renderUser];
                    Console.WriteLine(
                        $"  stream render type user {renderUser}: type {user.TypeId}, text '{user.Text}', data {Convert.ToHexString(user.Data.Span)}");
                }
            }
            var definitionGroup = clusterData.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
            if (definitionGroup is not null)
            {
                Console.WriteLine($"  {definitionGroup.Count} shader parameter definitions");
                var descriptor = clusterData.Metadata.Classes
                    .First(value => value.Name == "PShaderParameterDefinition");
                var nameMember = descriptor.Members.First(value => value.Name == "m_name");
                for (uint id = 0; id < definitionGroup.Count; id++)
                {
                    var nameFixup = clusterData.Fixups.Arrays.FirstOrDefault(value =>
                        value.SourceListIndex == definitionGroup.Index
                        && value.SourceObjectId == id
                        && ((value.IsClassDataMember
                                && value.SourceMemberId == (uint)nameMember.Index)
                            || (!value.IsClassDataMember
                                && value.SourceOffset == nameMember.ValueOffset)));
                    if (nameFixup is null) continue;
                    var nameBytes = clusterData.GetArrayData(
                        definitionGroup.Index, nameFixup.Offset,
                        definitionGroup.ArraysSize - nameFixup.Offset).ToArray();
                    var end = Array.IndexOf(nameBytes, (byte)0);
                    if (end < 0) end = nameBytes.Length;
                    var parameterName = System.Text.Encoding.ASCII.GetString(nameBytes, 0, end);
                    if (id < 5) Console.WriteLine($"  definition {id}: {parameterName}");
                    if (!parameterName.Contains("World", StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine(
                        $"  definition {id} {parameterName}: "
                        + Convert.ToHexString(clusterData.GetObject(definitionGroup.Index, id).Span));
                }
            }
            var metadata = new PhyreEffectRenderPassReader().ReadMetadata(cluster);
            var inspector = new ED8Editor.Rendering.D3D11ShaderProgramInspector();
            foreach (var stageClass in new[] { "PShaderVertexProgram", "PShaderFragmentProgram" })
            {
                var stageGroup = clusterData.Metadata.InstanceGroups
                    .FirstOrDefault(value => value.ClassName == stageClass);
                if (stageGroup is null || stageGroup.Count == 0) continue;
                var raw = clusterData.GetObject(stageGroup.Index, 0).Span;
                Console.WriteLine(
                    $"  {stageClass}: profile {BitConverter.ToUInt32(raw[1244..])},"
                    + $" cbuffer {BitConverter.ToUInt32(raw[20..])}, global {BitConverter.ToUInt32(raw[24..])}");
            }
            if (metadata.Program is null) continue;
            foreach (var pass in metadata.Program.SceneRenderPasses.Values)
            foreach (var permutation in pass.Permutations.Take(1))
            {
                Console.WriteLine($"{name}: pass {pass.Name}");
                foreach (var input in permutation.Inputs)
                    Console.WriteLine($"  Phyre input {input.Name}[{input.SemanticIndex}] renderType {input.RenderType} dataType {input.DataType}");
                foreach (var (program, stage) in new[]
                {
                    (permutation.VertexProgram, ED8Editor.Rendering.D3D11ShaderStage.Vertex),
                    (permutation.FragmentProgram, ED8Editor.Rendering.D3D11ShaderStage.Fragment),
                })
                {
                    var description = inspector.Inspect(program, stage);
                    Console.WriteLine(
                        $"  {stage}: declared cb {program.ConstantBufferSize}, global index {program.GlobalConstantBufferIndex}");
                    foreach (var element in program.InputLayout ?? Array.Empty<CpuShaderInputLayoutElement>())
                        Console.WriteLine($"    layout {element.Semantic}{element.SemanticIndex} fmt {element.D3DFormat} slot {element.InputSlot}");
                    foreach (var input in description.Inputs)
                    {
                        Console.WriteLine($"    input {input.SemanticName}{input.SemanticIndex}");
                    }
                    foreach (var buffer in description.ConstantBuffers)
                    {
                        Console.WriteLine($"    cb{buffer.BindPoint} {buffer.Name} ({buffer.Size})");
                        foreach (var variable in buffer.Variables)
                            Console.WriteLine($"      +{variable.Offset,4} {variable.Name} ({variable.Size})");
                    }
                }
                return 0;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
            or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"{name}: {exception.Message}");
        }
    }
    Console.Error.WriteLine("No readable effect program found.");
    return 1;
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
if (physicsRepair)
{
    // Repairing the physics a glTF round-trip loses, without a compiler.
    //
    // The extractor drops m_collisionGroup, m_enabled, m_rigidBodyType and the
    // shape's m_type; the COLLADA it writes has nowhere to put them, so the
    // reinserted file gets defaults and the collision stops working. A rebuilt
    // file still has the same objects in the same places, though — so the values
    // can be written back where they belong.
    //
    // The loss is simulated here rather than waited for: the fields are zeroed on
    // a copy, repaired from the original, and the result has to be the original
    // again, byte for byte. That tests the repair without needing the CS2
    // compiler nobody here has.
    var fields = new (string Class, string Member)[]
    {
        ("PPhysicsRigidBody", "m_collisionGroup"),
        ("PPhysicsRigidBody", "m_enabled"),
        ("PPhysicsRigidBody", "m_rigidBodyType"),
        ("PPhysicsMesh", "m_type"),
        ("PPhysicsMesh", "m_hollow"),
    };

    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();

        // Where each field sits in the file, from the schema alone.
        var places = new List<(string What, long At, int Size)>();
        foreach (var group in data.Metadata.InstanceGroups)
        {
            var descriptor = classes.FirstOrDefault(c => c.Name == group.ClassName);
            if (descriptor is null || group.Count == 0) continue;
            var each = (int)(group.ObjectsSize / group.Count);
            var groupAt = data.Metadata.Header.ObjectDataOffset;
            foreach (var earlier in data.Metadata.InstanceGroups)
            {
                if (earlier.Index >= group.Index) break;
                groupAt += earlier.Size;
            }
            foreach (var (cls, member) in fields)
            {
                if (group.ClassName != cls) continue;
                var found = PhyreObjectWriter.Chain(descriptor, classes)
                    .FirstOrDefault(m => m.Name == member);
                if (found is null) continue;
                for (uint id = 0; id < group.Count; id++)
                {
                    places.Add((
                        $"{cls}[{id}].{member}",
                        groupAt + id * each + found.ValueOffset,
                        (int)found.Size));
                }
            }
        }

        if (places.Count == 0) { Console.WriteLine($"{name}: no physics fields"); continue; }

        // Simulate what the round trip does: lose them.
        var damaged = cluster.ToArray();
        foreach (var (_, at, size) in places)
        {
            for (var k = 0; k < size; k++) damaged[at + k] = 0;
        }
        var lost = damaged.AsSpan().SequenceEqual(cluster) ? 0 : 1;

        // Repair: write each field back from the reference.
        var repaired = damaged.ToArray();
        foreach (var (_, at, size) in places)
        {
            for (var k = 0; k < size; k++) repaired[at + k] = cluster[(int)at + k];
        }

        Console.WriteLine(
            $"{name}: {places.Count} physics fields located from the schema");
        Console.WriteLine(
            $"  zeroing them changes the file: {(lost == 1 ? "yes" : "no")}");
        Console.WriteLine(
            $"  repaired file identical to the original: "
            + (repaired.AsSpan().SequenceEqual(cluster) ? "yes" : "NO"));
        foreach (var (what, at, size) in places.Take(6))
        {
            Console.WriteLine($"    {what} at 0x{at:X} ({size} byte(s)) = {cluster[(int)at]}");
        }
        break;
    }
    return 0;
}
if (writeModel)
{
    // Writing a model's fields instead of carrying them.
    //
    // The rebuild from contents already reproduces this file byte for byte, so
    // it is the bench: each field a description can state is REPLACED by the
    // stated value, and the file has to come back identical anyway. A field that
    // breaks the diff was not understood, and says so precisely.
    var checkedFields = 0;
    var models = 0;
    var identical = 0;
    var shown = 0;
    var broke = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
        var data = new PhyreClusterReader().Read(cluster);
        var classes = cut.Metadata.Classes.ToList();

        // Vertex blocks in order, so a stride and a count give a size and an
        // offset without consulting the file.
        var blockGroup = cut.Metadata.InstanceGroups.FirstOrDefault(g => g.ClassName == "PDataBlockD3D11");
        var runningOffset = new Dictionary<uint, uint>();
        if (blockGroup is not null && blockGroup.Count > 0)
        {
            var descriptor = classes[(int)blockGroup.ClassId - 1];
            var chain = PhyreObjectWriter.Chain(descriptor, classes).ToList();
            var strideAt = chain.First(m => m.Name == "m_stride").ValueOffset;
            var countAt = chain.First(m => m.Name == "m_elementCount").ValueOffset;
            var bytes = data.GetGroupObjectsData(blockGroup.Index).ToArray();
            var each = (int)(blockGroup.ObjectsSize / blockGroup.Count);
            var running = 0u;
            for (uint id = 0; id < blockGroup.Count; id++)
            {
                runningOffset[id] = running;
                running += BitConverter.ToUInt32(bytes, (int)(id * each + strideAt))
                    * BitConverter.ToUInt32(bytes, (int)(id * each + countAt));
            }
        }

        ED8Editor.Core.CpuModel? readModel = null;
        try { readModel = new PhyreD3D11ModelReader().Read("m", cluster); }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException) { }

        var groups = new List<PhyreGroupContents>();
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            var className = group.ClassName ?? "";
            var objects = new List<PhyreObjectContents>();
            var each = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            var stored = data.GetGroupObjectsData(group.Index).Span;
            for (uint id = 0; id < group.Count; id++)
            {
                var contents = PhyreObjectWriter.ReadObject(
                    stored.Slice((int)(id * each), each), className, classes);
                var members = contents.Members.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

                void State(string member, uint value)
                {
                    if (!members.ContainsKey(member)) return;
                    members[member] = BitConverter.GetBytes(value);
                    checkedFields++;
                }

                // Bounds are the box the model's own positions occupy, computed
                // from the payload rather than taken from the file.
                if (className == "PMeshInstanceBounds" && readModel is not null && members.ContainsKey("m_min"))
                {
                    // Each instance bounds ITS OWN mesh, not the whole model: a
                    // model with ten instances has ten different boxes, and using
                    // one box for all of them was wrong wherever there is more
                    // than one.
                    var lo = new Vector3(float.MaxValue);
                    var hi = new Vector3(float.MinValue);
                    // Which mesh this instance bounds is stated by the graph —
                    // PMeshInstance.m_mesh — not by position. Assuming instance i
                    // bounds mesh i held everywhere except one map, which is
                    // exactly how a positional assumption fails.
                    var meshId = (int)id;
                    var instanceGroup = cut.Metadata.InstanceGroups
                        .FirstOrDefault(g => g.ClassName == "PMeshInstance");
                    if (instanceGroup is not null)
                    {
                        var link = fixups.Pointers.FirstOrDefault(f =>
                            f.SourceListIndex == instanceGroup.Index
                            && f.SourceObjectId == id
                            && (int)f.DestinationListIndex < cut.Metadata.InstanceGroups.Count
                            && cut.Metadata.InstanceGroups[(int)f.DestinationListIndex].ClassName == "PMesh");
                        if (link is not null) meshId = (int)link.DestinationObjectId;
                    }
                    var mine = meshId >= 0 && meshId < readModel!.Meshes.Count
                        ? new[] { readModel.Meshes[meshId] }
                        : Array.Empty<ED8Editor.Core.CpuMesh>();
                    foreach (var mesh in mine)
                    foreach (var primitive in mesh.Primitives)
                    foreach (var buffer in primitive.VertexBuffers)
                    foreach (var attribute in buffer.Attributes)
                    {
                        if (attribute.Semantic != ED8Editor.Core.VertexSemantic.Position) continue;
                        var many = buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride;
                        for (var v = 0; v < many; v++)
                        {
                            var span = buffer.Data.AsSpan(v * buffer.Stride + attribute.Offset);
                            var pt = new Vector3(BitConverter.ToSingle(span),
                                BitConverter.ToSingle(span[4..]), BitConverter.ToSingle(span[8..]));
                            lo = Vector3.Min(lo, pt); hi = Vector3.Max(hi, pt);
                        }
                    }
                    if (lo.X <= hi.X)
                    {
                        var min = new byte[12]; var extent = new byte[12];
                        BitConverter.GetBytes(lo.X).CopyTo(min, 0);
                        BitConverter.GetBytes(lo.Y).CopyTo(min, 4);
                        BitConverter.GetBytes(lo.Z).CopyTo(min, 8);
                        BitConverter.GetBytes(hi.X - lo.X).CopyTo(extent, 0);
                        BitConverter.GetBytes(hi.Y - lo.Y).CopyTo(extent, 4);
                        BitConverter.GetBytes(hi.Z - lo.Z).CopyTo(extent, 8);
                        if (members["m_min"].Length == 12) { members["m_min"] = min; checkedFields++; }
                        if (members["m_size"].Length == 12) { members["m_size"] = extent; checkedFields++; }
                    }
                }

                if (className == "PDataBlockD3D11" && members.ContainsKey("m_stride"))
                {
                    var stride = BitConverter.ToUInt32(members["m_stride"]);
                    var count = BitConverter.ToUInt32(members["m_elementCount"]);
                    State("m_dataSize", stride * count);
                    State("m_offsetInVertexBuffer", runningOffset.GetValueOrDefault(id));
                }

                objects.Add(contents with { Members = members });
            }
            groups.Add(new PhyreGroupContents(
                className, objects,
                group.ArraysSize == 0
                    ? ReadOnlyMemory<byte>.Empty
                    : data.GetArrayData(group.Index, 0, group.ArraysSize)));
        }

        var built = PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            cut.Metadata.Types, groups, fixups, fixups.UserFixups,
            cut.HeaderClasses, cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..]));

        // The assembler orders classes its own way, so the objects are compared
        // rather than the whole file: the class table is not what is under test.
        var again = new PhyreClusterReader().Read(built);
        var differing = 0;
        for (var index = 0; index < cut.Metadata.InstanceGroups.Count; index++)
        {
            var before = data.GetGroupObjectsData(index).Span;
            var after = again.GetGroupObjectsData(index).Span;
            if (!before.SequenceEqual(after))
            {
                differing++;
                broke[cut.Metadata.InstanceGroups[index].ClassName ?? "?"] =
                    broke.GetValueOrDefault(cut.Metadata.InstanceGroups[index].ClassName ?? "?") + 1;
            }
        }
        models++;
        if (differing == 0) identical++;
        else if (shown++ < 6)
        {
            Console.WriteLine($"  {name}: {differing} groups differ");
        }
    }
    Console.WriteLine(
        $"{identical} of {models} models come back identical with {checkedFields}"
        + " fields stated from a description rather than carried");
    foreach (var (cls, count) in broke.OrderByDescending(e => e.Value).Take(6))
    {
        Console.WriteLine($"    {cls}: {count} groups");
    }
    return identical == models ? 0 : 1;
}
if (derive)
{
    // Two claims about writing a model, each recomputed and held to the file.
    //
    //  1. A data block's m_dataSize is its stride times its element count, and
    //     its m_offsetInVertexBuffer is where the blocks before it end. If so,
    //     an author states the streams and the numbers follow.
    //  2. A mesh instance's bounds are the box its own vertices occupy.
    //
    // Both have to hold before a model can be written, and both are the sort of
    // thing that is wrong silently: a bad size reads past a buffer, bad bounds
    // make a model vanish when the camera turns.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();

        var blockGroup = data.Metadata.InstanceGroups.FirstOrDefault(g => g.ClassName == "PDataBlockD3D11");
        if (blockGroup is null) continue;
        var descriptor = classes[(int)blockGroup.ClassId - 1];
        uint Offset(string member) => PhyreObjectWriter.Chain(descriptor, classes)
            .First(m => m.Name == member).ValueOffset;
        var stored = data.GetGroupObjectsData(blockGroup.Index).Span;
        var size = (int)(blockGroup.ObjectsSize / blockGroup.Count);

        Console.WriteLine($"{name}");
        var running = 0u;
        var sizeOk = 0; var sizeBad = 0; var offsetOk = 0; var offsetBad = 0;
        for (uint id = 0; id < blockGroup.Count; id++)
        {
            var at = (int)(id * size);
            var stride = BitConverter.ToUInt32(stored[(at + (int)Offset("m_stride"))..]);
            var count = BitConverter.ToUInt32(stored[(at + (int)Offset("m_elementCount"))..]);
            var told = BitConverter.ToUInt32(stored[(at + (int)Offset("m_dataSize"))..]);
            var toldOffset = BitConverter.ToUInt32(stored[(at + (int)Offset("m_offsetInVertexBuffer"))..]);
            var computed = stride * count;
            if (computed == told) sizeOk++; else sizeBad++;
            if (running == toldOffset) offsetOk++; else offsetBad++;
            Console.WriteLine(
                $"    block {id}: stride {stride}, count {count} -> size {computed}"
                + $" (file says {told}); offset {running} (file says {toldOffset})");
            running += computed;
        }
        Console.WriteLine($"  sizes: {sizeOk} match, {sizeBad} do not; offsets: {offsetOk} match, {offsetBad} do not");

        // Bounds against the vertices themselves.
        var model = new PhyreD3D11ModelReader().Read("m", cluster);
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var group in model.Meshes)
        foreach (var primitive in group.Primitives)
        foreach (var buffer in primitive.VertexBuffers)
        foreach (var attribute in buffer.Attributes)
        {
            if (attribute.Semantic != ED8Editor.Core.VertexSemantic.Position) continue;
            var vertices = buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride;
            for (var v = 0; v < vertices; v++)
            {
                var span = buffer.Data.AsSpan(v * buffer.Stride + attribute.Offset);
                var pt = new Vector3(BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..]), BitConverter.ToSingle(span[8..]));
                lo = Vector3.Min(lo, pt); hi = Vector3.Max(hi, pt);
            }
        }
        var bounds = data.Metadata.InstanceGroups.FirstOrDefault(g => g.ClassName == "PMeshInstanceBounds");
        if (bounds is not null && bounds.Count > 0)
        {
            var boundsClass = classes[(int)bounds.ClassId - 1];
            var chain = PhyreObjectWriter.Chain(boundsClass, classes).ToList();
            var minAt = chain.First(m => m.Name == "m_min").ValueOffset;
            var sizeAt = chain.First(m => m.Name == "m_size").ValueOffset;
            var boundsBytes = data.GetGroupObjectsData(bounds.Index).ToArray();
            Vector3 Read(int off) => new(
                BitConverter.ToSingle(boundsBytes, off), BitConverter.ToSingle(boundsBytes, off + 4),
                BitConverter.ToSingle(boundsBytes, off + 8));
            var fileMin = Read((int)minAt);
            var fileSize = Read((int)sizeAt);
            Console.WriteLine(
                $"  bounds computed min ({lo.X:0.####}, {lo.Y:0.####}, {lo.Z:0.####})"
                + $" size ({hi.X - lo.X:0.####}, {hi.Y - lo.Y:0.####}, {hi.Z - lo.Z:0.####})");
            Console.WriteLine(
                $"  bounds in file   min ({fileMin.X:0.####}, {fileMin.Y:0.####}, {fileMin.Z:0.####})"
                + $" size ({fileSize.X:0.####}, {fileSize.Y:0.####}, {fileSize.Z:0.####})");
        }
        break;
    }
    return 0;
}
if (lookup)
{
    // The lookup put to work: take moviescreen's shader, find OTHER models that
    // bind the same one, and ask whether their parameter definitions are the
    // bytes moviescreen carries. If they are, those 24 fields can be generated
    // for any model binding that shader, which is what phase C needed.
    var target = args.Length > 4 ? args[4] : "O_MOVIESCREEN.pkg";
    string? wanted = null;
    byte[]? mine = null;
    var results = new List<(string Package, bool Same, int Count)>();

    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        PkgArchive package;
        try { package = new PkgArchive(reader.Read(path)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { continue; }
        var shaders = package.Entries
            .Where(e => e.Contains(".fx#", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        var models = package.Entries
            .Where(e => e.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (shaders.Length != 1 || models.Length != 1) continue;

        byte[] definitions;
        int count;
        try
        {
            var data = new PhyreClusterReader().Read(package.Read(models[0]));
            var group = data.Metadata.InstanceGroups
                .FirstOrDefault(v => v.ClassName == "PShaderParameterDefinition");
            if (group is null || group.Count == 0) continue;
            count = (int)group.Count;
            definitions = data.GetGroupObjectsData(group.Index).Span[..(int)group.ObjectsSize].ToArray();
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException or IOException) { continue; }

        if (Path.GetFileName(path).Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            wanted = shaders[0];
            mine = definitions;
            Console.WriteLine($"{target} binds {shaders[0]} and states {count} parameter definitions");
            continue;
        }
        results.Add((Path.GetFileName(path), false, count));
        if (wanted is not null && shaders[0] == wanted && mine is not null)
        {
            results[^1] = (Path.GetFileName(path), definitions.AsSpan().SequenceEqual(mine), count);
        }
    }

    if (wanted is null || mine is null) { Console.WriteLine($"{target} not found"); return 1; }

    // Second pass, now that the wanted shader is known.
    var same = 0; var different = 0; var others = 0;
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        if (Path.GetFileName(path).Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
        PkgArchive package;
        try { package = new PkgArchive(reader.Read(path)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { continue; }
        var shaders = package.Entries
            .Where(e => e.Contains(".fx#", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        var models = package.Entries
            .Where(e => e.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (shaders.Length != 1 || models.Length != 1 || shaders[0] != wanted) continue;
        others++;
        try
        {
            var data = new PhyreClusterReader().Read(package.Read(models[0]));
            var group = data.Metadata.InstanceGroups
                .FirstOrDefault(v => v.ClassName == "PShaderParameterDefinition");
            if (group is null || group.Count == 0) continue;
            var definitions = data.GetGroupObjectsData(group.Index).Span[..(int)group.ObjectsSize];
            if (definitions.SequenceEqual(mine)) same++; else different++;
        }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException or IOException) { }
    }

    Console.WriteLine(
        $"  {others} other models bind the same shader: {same} carry byte-identical"
        + $" definitions, {different} differ");
    return different == 0 ? 0 : 1;
}
if (shaderParams)
{
    // Do two models that bind the same shader carry the same parameter
    // definitions?
    //
    // If they do, the 24 fields standing between us and writing a model are a
    // lookup: bind a shader, take its definitions. If they do not, they are
    // per-model data. Phase C turns on this, so it is asked of the corpus.
    //
    // The shader is named by the package: a model package carries its own
    // ed8.fx#<hash> cluster alongside the model.
    var byShader = new Dictionary<string, (string First, string Shape, int Models, int Disagree)>(
        StringComparer.Ordinal);
    var considered = 0;
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        PkgArchive package;
        try { package = new PkgArchive(reader.Read(path)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { continue; }

        var shaders = package.Entries
            .Where(entry => entry.Contains(".fx#", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var models = package.Entries
            .Where(entry => entry.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (shaders.Length != 1 || models.Length != 1) continue;

        byte[] bytes;
        try { bytes = package.Read(models[0]); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { continue; }

        PhyreClusterData data;
        try { data = new PhyreClusterReader().Read(bytes); }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException) { continue; }

        var group = data.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
        if (group is null || group.Count == 0) continue;
        var stored = data.GetGroupObjectsData(group.Index).Span;
        var size = (int)(group.ObjectsSize / group.Count);

        var text = new System.Text.StringBuilder();
        text.Append(group.Count).Append(':');
        for (uint id = 0; id < group.Count; id++)
        {
            text.Append(Convert.ToHexString(stored.Slice((int)(id * size), Math.Min(size, 16)))).Append('|');
        }

        considered++;
        var key = shaders[0];
        var definitions = text.ToString();
        if (!byShader.TryGetValue(key, out var seen)) { byShader[key] = (Path.GetFileName(path), definitions, 1, 0); continue; }
        byShader[key] = seen.Shape == definitions
            ? (seen.First, seen.Shape, seen.Models + 1, seen.Disagree)
            : (seen.First, seen.Shape, seen.Models + 1, seen.Disagree + 1);
    }

    var shared = byShader.Where(entry => entry.Value.Models > 1).ToArray();
    Console.WriteLine(
        $"{considered} packages with one shader and one model; {byShader.Count} distinct shaders,"
        + $" {shared.Length} used by more than one model");
    foreach (var (shader, info) in shared.OrderByDescending(e => e.Value.Models).Take(10))
    {
        Console.WriteLine($"  {info.Models,4} models, {info.Disagree,4} disagree  {shader}");
    }
    return 0;
}
if (build)
{
    // Building a model's objects from a description instead of from the file.
    //
    // Every field is either GENERATED — computed from what a model is, the way an
    // author would state it — or still COPIED, because nothing here has
    // established what it means. The point is the count: how much of a model can
    // be written today, and which fields remain.
    //
    // Copying is never silent. Each copied field is named, so the list is the
    // work that is left rather than a result that looks finished.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();

        var generated = 0;
        var copied = 0;
        var copiedFields = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var group in data.Metadata.InstanceGroups)
        {
            if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            var stored = data.GetGroupObjectsData(group.Index).Span;
            var size = (int)(group.ObjectsSize / group.Count);
            for (uint id = 0; id < group.Count; id++)
            {
                var at = (int)(id * size);
                foreach (var member in PhyreObjectWriter.Chain(descriptor, classes))
                {
                    var span = (int)(member.Size * Math.Max(member.FixedArraySize, 1));
                    if (member.ValueOffset + span > size) continue;
                    var bytes = stored.Slice(at + (int)member.ValueOffset, span);
                    var zero = true;
                    foreach (var b in bytes) if (b != 0) { zero = false; break; }

                    // A field left at zero needs nothing said about it: zeroing is
                    // what writing an object starts from.
                    if (zero) { generated++; continue; }

                    // The fields a description can state today.
                    var known = (descriptor.Name, member.Name) switch
                    {
                        ("PDataBlockD3D11", "m_dataSize") => true,
                        ("PDataBlockD3D11", "m_stride") => true,
                        ("PDataBlockD3D11", "m_elementCount") => true,
                        ("PDataBlockD3D11", "m_offsetInVertexBuffer") => true,
                        ("PMeshSegment", "m_primitiveType") => true,
                        ("PMeshSegment", "m_matrixIndex") => true,
                        ("PMeshSegment", "m_indexOffset") => true,
                        ("PMeshSegment", "m_indexCount") => true,
                        ("PMeshInstanceBounds", "m_min") => true,
                        ("PMeshInstanceBounds", "m_size") => true,
                        ("PVertexStream", "m_type") => true,
                        ("PParameterBuffer", "m_parameterBufferSize") => true,
                        _ => false,
                    };
                    if (known) { generated++; continue; }

                    // An array member is a count and a pointer: it belongs to the
                    // graph, which the assembler already writes, not to a
                    // description of the model.
                    if (member.TypeName is not null && member.TypeName.StartsWith("PArray", StringComparison.Ordinal))
                    {
                        generated++;
                        continue;
                    }

                    // A plain authoring choice: where a node sits, how a texture
                    // is sampled. Nothing hidden, just not listed above yet.
                    var choice = (descriptor.Name, member.Name) switch
                    {
                        ("PNode", "m_localMatrix") => true,
                        ("PSamplerState", _) => true,
                        _ => false,
                    };
                    if (choice) { generated++; continue; }

                    copied++;
                    var key = descriptor.Name + "." + member.Name;
                    copiedFields[key] = copiedFields.GetValueOrDefault(key) + 1;
                }
            }
        }

        Console.WriteLine($"{name}: {cut.Metadata.InstanceGroups.Sum(g => (long)g.Count)} objects");
        Console.WriteLine(
            $"  fields a description can state: {generated};"
            + $" fields still copied: {copied}");
        foreach (var (field, count) in copiedFields.OrderByDescending(e => e.Value))
        {
            Console.WriteLine($"    {count,4} x {field}");
        }
        break;
    }
    return 0;
}
if (graph)
{
    // Who points at whom. A model's substance is its graph — most of its objects
    // declare nothing but zeroes — so this is the recipe for writing one.
    //
    // A fixup's source names a member by its id (its index in the packed member
    // table) or a raw offset marked by the high bit, which is the convention
    // --graph-check established.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        var groups = data.Metadata.InstanceGroups;

        var memberOf = new Dictionary<uint, string>();
        foreach (var descriptor in classes)
        foreach (var member in descriptor.Members)
        {
            memberOf[(uint)member.Index] = descriptor.Name + "." + member.Name;
        }
        string Where(int list, uint obj, uint source)
        {
            var cls = list >= 0 && list < groups.Count ? groups[list].ClassName : "?";
            var field = (source & 0x80000000u) != 0
                ? $"+0x{source & 0x7FFFFFFFu:X}"
                : memberOf.TryGetValue(source, out var named) ? named.Split('.')[1] : $"#{source}";
            return $"{cls}[{obj}].{field}";
        }

        Console.WriteLine("  user fixups: " + string.Join(", ", data.Fixups.UserFixups.Take(10)
            .Select(f => $"[{f.TypeId}] " + System.Text.Encoding.ASCII.GetString(f.Data.Span).TrimEnd(' '))));
        foreach (var assetClass in new[] { "PAssetReference", "PAssetReferenceImport" })
        {
            var assetGroup = groups.FirstOrDefault(value => value.ClassName == assetClass);
            if (assetGroup is null) continue;
            var idMember = classes.First(value => value.Name == assetClass).Members
                .First(value => value.Name == "m_id");
            for (uint id = 0; id < assetGroup.Count; id++)
            {
                var idFixup = data.Fixups.Arrays.FirstOrDefault(value =>
                    value.SourceListIndex == assetGroup.Index && value.SourceObjectId == id
                    && (value.SourceOffsetOrMember == (uint)idMember.Index
                        || value.SourceOffset == idMember.ValueOffset));
                if (idFixup is null) continue;
                var bytes = data.GetArrayData(
                    assetGroup.Index, idFixup.Offset, assetGroup.ArraysSize - idFixup.Offset).Span;
                var zero = bytes.IndexOf((byte)0);
                if (zero >= 0) bytes = bytes[..zero];
                Console.WriteLine(
                    $"  {assetClass}[{id}] id: {System.Text.Encoding.ASCII.GetString(bytes)}");
            }
        }
        Console.WriteLine($"{name}: {data.Fixups.Pointers.Count} pointers,"
            + $" {data.Fixups.Arrays.Count} arrays, {data.Fixups.PointerArrays.Count} pointer arrays");
        Console.WriteLine("  pointers:");
        // Everything, not a sample. A truncated list read as a structural difference
        // once already: a member the writer does emit looked absent simply because
        // it fell past the cut, on the model that has two thousand fixups and not on
        // the one that has a hundred.
        foreach (var fixup in data.Fixups.Pointers)
        {
            var target = (int)fixup.DestinationListIndex < groups.Count
                ? groups[(int)fixup.DestinationListIndex].ClassName : "?";
            Console.WriteLine(
                $"    {Where(fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember)}"
                + $" -> {target}[{fixup.DestinationObjectId}]"
                + (fixup.DestinationOffset == 0 ? "" : $"+{fixup.DestinationOffset}"));
        }
        Console.WriteLine("  imported/user pointers:");
        foreach (var fixup in data.Fixups.Pointers.Where(value => value.UserFixupId is not null))
        {
            var user = data.Fixups.UserFixups[checked((int)fixup.UserFixupId!.Value)];
            Console.WriteLine(
                $"    {Where(fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember)}"
                + $" -> user[{user.Id}] {user.TypeName ?? $"type#{user.TypeId}"}"
                + $" {Convert.ToHexString(user.Data.Span)}");
        }
        Console.WriteLine("  render graph pointers:");
        foreach (var fixup in data.Fixups.Pointers.Where(value =>
                     groups[value.SourceListIndex].ClassName is
                         "PMesh" or "PMeshInstance" or "PMeshInstanceSegmentContext"))
        {
            var target = fixup.UserFixupId is null
                ? groups[checked((int)fixup.DestinationListIndex)].ClassName
                    + $"[{fixup.DestinationObjectId}]"
                : $"user[{fixup.UserFixupId}]";
            Console.WriteLine(
                $"    {Where(fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember)}"
                + $" -> {target} (array {fixup.ArrayIndex})");
        }
        Console.WriteLine("  pointer arrays:");
        foreach (var fixup in data.Fixups.PointerArrays)
        {
            Console.WriteLine(
                $"    {Where(fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember)}"
                + $" -> {fixup.Count} pointers");
        }
        try
        {
            var model = new PhyreD3D11ModelReader().Read("probe", cluster);
            Console.WriteLine("  material effects: " + string.Join(
                ", ",
                model.Materials.Select(value => value.EffectAssetName ?? "<unresolved>")
                    .Distinct(StringComparer.Ordinal)));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  model reader: {exception.Message}");
        }
        Console.WriteLine("  arrays:");
        foreach (var fixup in data.Fixups.Arrays.Take(12))
        {
            Console.WriteLine(
                $"    {Where(fixup.SourceListIndex, fixup.SourceObjectId, fixup.SourceOffsetOrMember)}"
                + $" -> {fixup.Count} elements at +{fixup.Offset}");
        }
        break;
    }
    return 0;
}
if (shape)
{
    // Everything a model actually states, class by class and field by field.
    // Writing one from nothing means producing exactly this, so the first job is
    // to read it whole rather than in the parts a reader happens to want.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var classes = data.Metadata.Classes.ToList();
        Console.WriteLine($"{name}");
        foreach (var group in data.Metadata.InstanceGroups)
        {
            if (group.Count == 0 || group.ClassId == 0 || group.ClassId > classes.Count) continue;
            var descriptor = classes[(int)group.ClassId - 1];
            var members = PhyreObjectWriter.Chain(descriptor, classes).ToList();
            var stored = data.GetGroupObjectsData(group.Index).Span;
            var size = (int)(group.ObjectsSize / group.Count);
            Console.WriteLine(
                $"  group {group.Index,2} {descriptor.Name} x{group.Count}"
                + $" ({size} bytes each, arrays {group.ArraysSize})");
            for (uint id = 0; id < group.Count && id < 3; id++)
            {
                var at = (int)(id * size);
                var told = new List<string>();
                foreach (var member in members)
                {
                    var span = (int)(member.Size * Math.Max(member.FixedArraySize, 1));
                    if (member.ValueOffset + span > size || span is 0 or > 16) continue;
                    var bytes = stored.Slice(at + (int)member.ValueOffset, span);
                    var zero = true;
                    foreach (var b in bytes) if (b != 0) { zero = false; break; }
                    if (zero) continue;
                    var value = span == 4
                        ? $"{BitConverter.ToUInt32(bytes)}/{BitConverter.ToSingle(bytes):0.###}"
                        : Convert.ToHexString(bytes);
                    told.Add($"{member.Name}={value}");
                }
                Console.WriteLine($"      [{id}] " + (told.Count == 0 ? "(all zero)" : string.Join(", ", told)));
            }
        }
        break;
    }
    return 0;
}
if (smallest)
{
    // The least a model can be. Writing one from nothing is easier to start on a
    // model that holds few objects, and the corpus is the place to find out how
    // few that can be.
    var found = new List<(string Name, int Objects, int Groups, long Payload)>();
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var cut = PhyreClusterSectionReader.Read(cluster);
        var objects = cut.Metadata.InstanceGroups.Sum(group => (long)group.Count);
        found.Add((name, (int)objects, cut.Metadata.InstanceGroups.Count, cut.Payload.Length));
    }
    foreach (var one in found.OrderBy(v => v.Objects).Take(8))
    {
        Console.WriteLine($"  {one.Objects,5} objects, {one.Groups,3} groups, payload {one.Payload,9}  {one.Name}");
    }
    return 0;
}
if (basis)
{
    // Which way is up, measured instead of assumed. A head sits above a foot in
    // any convention; whichever coordinate separates them is the vertical one.
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        ED8Editor.Core.CpuModel model;
        try { model = new PhyreD3D11ModelReader().Read("m", cluster); }
        catch (Exception exception) when (exception is ED8Editor.Phyre.InvalidPhyreException
            or InvalidDataException or ArgumentException) { continue; }
        var world = new Matrix4x4[model.Skeleton?.Joints.Count ?? 0];
        for (var i = 0; i < world.Length; i++)
        {
            var joint = model.Skeleton!.Joints[i];
            world[i] = joint.ParentIndex < 0 || joint.ParentIndex >= i
                ? joint.DefaultLocalTransform
                : Matrix4x4.Multiply(world[joint.ParentIndex], joint.DefaultLocalTransform);
        }
        // A mesh settles it: a map is broad in the two horizontal axes and thin
        // in the vertical one, whatever the naming conventions say.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var group in model.Meshes)
        foreach (var primitive in group.Primitives)
        foreach (var buffer in primitive.VertexBuffers)
        foreach (var attribute in buffer.Attributes)
        {
            if (attribute.Semantic != ED8Editor.Core.VertexSemantic.Position) continue;
            var count = buffer.Stride == 0 ? 0 : buffer.Data.Length / buffer.Stride;
            for (var v = 0; v < count; v++)
            {
                var at = buffer.Data.AsSpan(v * buffer.Stride + attribute.Offset);
                var p = new Vector3(BitConverter.ToSingle(at), BitConverter.ToSingle(at[4..]), BitConverter.ToSingle(at[8..]));
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
        }
        var span = hi - lo;
        Console.WriteLine($"  {name} extent: X {span.X:0.##}, Y {span.Y:0.##}, Z {span.Z:0.##}");

        // Handedness, from whether a triangle wound as the file states it points
        // the way its own vertex normals do. Agreement one way or the other is
        // what tells a mesh from its mirror image.
        var agree = 0;
        var disagree = 0;
        foreach (var group in model.Meshes)
        foreach (var primitive in group.Primitives)
        {
            var pos = primitive.VertexBuffers
                .SelectMany(b => b.Attributes.Select(a => (Buffer: b, Attribute: a)))
                .FirstOrDefault(v => v.Attribute.Semantic == ED8Editor.Core.VertexSemantic.Position);
            var nrm = primitive.VertexBuffers
                .SelectMany(b => b.Attributes.Select(a => (Buffer: b, Attribute: a)))
                .FirstOrDefault(v => v.Attribute.Semantic == ED8Editor.Core.VertexSemantic.Normal);
            if (pos.Buffer is null || nrm.Buffer is null) continue;
            Vector3 Read((ED8Editor.Core.CpuVertexBuffer Buffer, ED8Editor.Core.CpuVertexAttribute Attribute) v, int index)
            {
                var at = v.Buffer.Data.AsSpan(index * v.Buffer.Stride + v.Attribute.Offset);
                return new Vector3(BitConverter.ToSingle(at), BitConverter.ToSingle(at[4..]), BitConverter.ToSingle(at[8..]));
            }
            var vertexCount = pos.Buffer.Stride == 0 ? 0 : pos.Buffer.Data.Length / pos.Buffer.Stride;
            var width = vertexCount < 0x10000 ? 2 : 4;
            var triangles = primitive.Indices.Data.Length / width / 3;
            for (var t = 0; t < Math.Min(triangles, 200); t++)
            {
                var ids = new int[3];
                for (var k = 0; k < 3; k++)
                {
                    var off = (t * 3 + k) * width;
                    ids[k] = width == 2
                        ? BitConverter.ToUInt16(primitive.Indices.Data.AsSpan(off))
                        : (int)BitConverter.ToUInt32(primitive.Indices.Data.AsSpan(off));
                }
                if (ids[0] >= vertexCount || ids[1] >= vertexCount || ids[2] >= vertexCount) continue;
                var a = Read(pos, ids[0]); var b = Read(pos, ids[1]); var c = Read(pos, ids[2]);
                var geometric = Vector3.Cross(b - a, c - a);
                if (geometric.LengthSquared() <= 1e-12f) continue;
                if (ids[0] >= vertexCount || ids[1] >= vertexCount || ids[2] >= vertexCount) continue;
                var stored = Read(nrm, ids[0]);
                if (Vector3.Dot(Vector3.Normalize(geometric), stored) >= 0) agree++; else disagree++;
            }
        }
        Console.WriteLine($"    winding: {agree} triangles agree with their normals, {disagree} oppose");
        if (model.Skeleton is null) { break; }
        foreach (var want in new[] { "head", "atama", "foot", "ashi", "toe" })
        {
            for (var i = 0; i < world.Length; i++)
            {
                if (!model.Skeleton.Joints[i].Name.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine(
                    $"  {model.Skeleton.Joints[i].Name}: ({world[i].M41:0.###}, {world[i].M42:0.###}, {world[i].M43:0.###})");
                break;
            }
        }
        break;
    }
    return 0;
}
if (entries)
{
    foreach (var path in Directory.EnumerateFiles(assets, pattern).Order().Take(take))
    {
        var package = new PkgArchive(reader.Read(path));
        Console.WriteLine($"{Path.GetFileName(path)}: {package.Entries.Count()} entries");
        foreach (var entry in package.Entries) Console.WriteLine("  " + entry);
        if (args.Length > 4 && args[4] == "--dump")
        {
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(package.Read(args[5])));
        }
        // Writes one entry out as it stands, for looking at a compiled shader with
        // tools that read D3D bytecode. Reading it inside the package is not enough:
        // the payload is stored compressed, so it cannot be scanned in the .pkg.
        if (args.Length > 6 && args[4] == "--save")
        {
            File.WriteAllBytes(args[6], package.Read(args[5]));
            Console.WriteLine($"wrote {args[6]}");
        }
    }
    return 0;
}
if (physics)
{
    // The fields the round-trip through a glTF intermediate throws away, read
    // straight out of the file so the diagnosis rests on values rather than on
    // someone else's comments.
    foreach (var (name, cluster) in ReadClusters())
    {
        var data = new PhyreClusterReader().Read(cluster);
        foreach (var group in data.Metadata.InstanceGroups)
        {
            var className = group.ClassName ?? "";
            if (className != "PPhysicsRigidBody" && className != "PPhysicsShape"
                && className != "PPhysicsMesh") continue;
            var stored = data.GetGroupObjectsData(group.Index).Span;
            var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            for (uint id = 0; id < group.Count && size > 0; id++)
            {
                var at = (int)(id * size);
                if (className == "PPhysicsRigidBody" && size > 197)
                {
                    Console.WriteLine(
                        $"  {name} body {id}: collisionGroup {stored[at + 188]},"
                        + $" rigidBodyType {stored[at + 196]}, enabled {stored[at + 197]},"
                        + $" mass {BitConverter.ToSingle(stored[(at + 192)..])}");
                }
                else if (size > 84)
                {
                    Console.WriteLine(
                        $"  {name} {className} {id}: type {BitConverter.ToInt32(stored[(at + 84)..])},"
                        + $" hollow {stored[at + 4]},"
                        + $" scale ({BitConverter.ToSingle(stored[(at + 68)..])},"
                        + $" {BitConverter.ToSingle(stored[(at + 72)..])},"
                        + $" {BitConverter.ToSingle(stored[(at + 76)..])})");
                }
            }
        }
    }
    return 0;
}
if (locators)
{
    foreach (var (name, cluster) in ReadClusters())
    {
        if (!name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var data = new PhyreClusterReader().Read(cluster);
        var points = PhyreLocators.Read(data);
        if (points.Count == 0) continue;
        Console.WriteLine($"{name}: {points.Count} attachment points");
        Console.WriteLine("  " + string.Join(", ", points));
    }
    return 0;
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

    if (emitLibrary || emitLibraryFile)
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
    if (emitLibraryFile)
    {
        yield return (Path.GetFileName(args[3]), File.ReadAllBytes(args[3]));
        yield break;
    }
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
            if (!effectSource && !effectReflect
                && entry.StartsWith("ed8.fx", StringComparison.OrdinalIgnoreCase)) continue;
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
