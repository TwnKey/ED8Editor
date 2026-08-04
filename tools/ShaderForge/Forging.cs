using System.Text;
using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.ShaderForge;

/// <summary>
/// Writes a shader variant: every one of its thirty-six programs recompiled from the
/// source the template embeds, with the material switches the caller asks for, under
/// a new asset name.
///
/// A template is used rather than a blank cluster because a shipped shader carries
/// far more than its programs — 4208 capture-buffer locations, 193 parameter
/// definitions, the render passes and their state blocks — none of which the switch
/// set changes. What the switches DO change is the code, and that is what this
/// replaces.
/// </summary>
public static class Forging
{
    public static int Forge(
        string templatePath,
        string sourcePath,
        string outputPath,
        IReadOnlyList<string>? switches,
        string? assetName,
        Func<string, string, IReadOnlyList<string>, byte[]?> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(templatePath);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        var material = switches ?? Variants.MaterialSwitches(data, cut);
        Console.WriteLine($"  commutateurs : {string.Join(" ", material)}");

        var contexts = Variants.ContextValues(data, cut);
        var passes = Variants.PassEntryPoints(data, cut);

        // Every program, in the order its group lays them out.
        var vertex = Build(contexts, passes, material, true, compile, sourcePath);
        var fragment = Build(contexts, passes, material, false, compile, sourcePath);
        if (vertex is null || fragment is null) return 1;
        Console.WriteLine($"  compile : {vertex.Count} programmes sommet,"
            + $" {fragment.Count} pixel");

        var groups = new List<PhyreGroupContents>();
        var moved = fixups.Arrays.ToList();
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            var className = group.ClassName ?? "";
            var replacement = className switch
            {
                "PShaderVertexProgram" => vertex,
                "PShaderFragmentProgram" => fragment,
                _ => null,
            };
            var size = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
            var stored = data.GetGroupObjectsData(group.Index).ToArray();
            var arrays = group.ArraysSize == 0
                ? ReadOnlyMemory<byte>.Empty
                : data.GetArrayData(group.Index, 0, group.ArraysSize);

            if (replacement is not null)
            {
                if (replacement.Count != group.Count)
                {
                    Console.WriteLine(
                        $"  {className} : {replacement.Count} programmes pour"
                        + $" {group.Count} emplacements — abandon");
                    return 1;
                }
                var member = PhyreObjectWriter
                    .Chain(classes.First(value => value.Name == className), classes)
                    .First(value => value.Name == "m_compiledCode");
                var laid = new MemoryStream();
                for (var id = 0; id < replacement.Count; id++)
                {
                    var at = (uint)laid.Length;
                    laid.Write(replacement[id]);
                    BitConverter.GetBytes((uint)replacement[id].Length)
                        .CopyTo(stored, id * size + (int)member.ValueOffset);
                    // By raw offset, and on the array's POINTER field — the member
                    // holds a count then a pointer, and the fixup names the pointer,
                    // so its source is the member's offset plus four. Matching on the
                    // member index instead found nothing: every offset stayed where
                    // the template had put it while the blobs moved underneath, and
                    // each program but the first read another one's code.
                    //
                    // The fixup's own count is the run's length in bytes, so it moves
                    // with the blob.
                    var pointerAt = 0x80000000u | (member.ValueOffset + sizeof(uint));
                    for (var one = 0; one < moved.Count; one++)
                    {
                        if (moved[one].SourceListIndex != group.Index
                            || moved[one].SourceObjectId != (uint)id
                            || moved[one].SourceOffsetOrMember != pointerAt)
                        {
                            continue;
                        }
                        moved[one] = moved[one] with
                        {
                            Offset = at,
                            Count = (uint)replacement[id].Length,
                        };
                    }
                }
                arrays = laid.ToArray();
            }

            // How many switches the variant names. An array member is a count then
            // a pointer, and the pointer is what a fixup patches — so a longer set
            // means restating the count here, in the object.
            if (switches is not null && className == "PEffectVariant")
            {
                var run = PhyreObjectWriter
                    .Chain(classes.First(value => value.Name == className), classes)
                    .FirstOrDefault(value => value.Name == "m_switches");
                if (run is not null)
                {
                    BitConverter.GetBytes((uint)material.Count)
                        .CopyTo(stored, (int)run.ValueOffset);
                }
            }

            var objects = new List<PhyreObjectContents>();
            for (uint id = 0; id < group.Count; id++)
            {
                objects.Add(PhyreObjectWriter.ReadObject(
                    stored.AsSpan((int)(id * size), size), className, classes));
            }
            groups.Add(new PhyreGroupContents(className, objects, arrays));
        }

        // The switches the file DECLARES, so they say what its code was built with.
        //
        // The group's array region is the shared value "1" first, then each name,
        // every one starting on an even offset — the same rule a model's asset names
        // follow. Both members are strings: m_value points at that "1" and m_name at
        // its own. Changing the set changes the object count, so the pointer array
        // the effect variant holds is restated as well.
        if (switches is not null)
        {
            var at = groups.FindIndex(value => value.ClassName == "PMaterialSwitch");
            if (at >= 0)
            {
                var region = new MemoryStream();
                region.Write(Encoding.ASCII.GetBytes("1\0"));
                var where = new List<uint>();
                foreach (var one in material)
                {
                    if (region.Length % 2 != 0) region.WriteByte(0);
                    where.Add((uint)region.Length);
                    region.Write(Encoding.ASCII.GetBytes(one + "\0"));
                }
                if (region.Length % 2 != 0) region.WriteByte(0);

                var descriptor = classes.First(value => value.Name == "PMaterialSwitch");
                var chain = PhyreObjectWriter.Chain(descriptor, classes).ToList();
                var nameMember = chain.First(value => value.Name == "m_name");
                var valueMember = chain.First(value => value.Name == "m_value");
                var made = new List<PhyreObjectContents>();
                for (var one = 0; one < material.Count; one++)
                {
                    made.Add(new PhyreObjectContents(
                        "PMaterialSwitch",
                        new Dictionary<string, byte[]>(StringComparer.Ordinal),
                        ReadOnlyMemory<byte>.Empty));
                }
                groups[at] = new PhyreGroupContents(
                    "PMaterialSwitch", made, region.ToArray());

                var group = cut.Metadata.InstanceGroups[at];
                moved.RemoveAll(value => value.SourceListIndex == group.Index);
                for (var one = 0; one < material.Count; one++)
                {
                    // By RAW OFFSET, with the high bit, which is how the shipped
                    // file writes them — 0x80000000 for m_name at zero and the same
                    // plus four for m_value. Our reader takes a member index too, so
                    // the names read back correctly and nothing looked wrong; the
                    // engine reads one form only.
                    moved.Add(new PhyreArrayFixup(
                        group.Index, (uint)one,
                        0x80000000u | nameMember.ValueOffset, 0, where[one]));
                    moved.Add(new PhyreArrayFixup(
                        group.Index, (uint)one,
                        0x80000000u | valueMember.ValueOffset, 0, 0));
                }
                Console.WriteLine($"  declare : {material.Count} commutateur(s)");
            }
        }

        // The name it answers to. A cluster naming the shader it was copied from
        // would be a second file claiming to be the first.
        if (assetName is not null)
        {
            var at = groups.FindIndex(value => value.ClassName == "PAssetReference");
            if (at >= 0)
            {
                groups[at] = groups[at] with
                {
                    ArrayData = Encoding.ASCII.GetBytes(assetName + "\0"),
                };
                Console.WriteLine($"  nomme : {assetName}");
            }
        }

        var written = PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            cut.Metadata.Types,
            groups,
            fixups with { Arrays = moved },
            fixups.UserFixups,
            cut.HeaderClasses,
            cut.Payload,
            PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
            cut.Header[(17 * sizeof(uint))..],
            PhyreSchemaProfile.Cs1Native,
            classes.Select(value => value.Name).ToArray()));
        File.WriteAllBytes(outputPath, written);
        Console.WriteLine($"  ecrit : {written.Length} octets (modele {image.Length})");
        return 0;
    }

    private static List<byte[]>? Build(
        IReadOnlyList<IReadOnlyList<uint>> contexts,
        IReadOnlyList<Variants.PassEntry> passes,
        IReadOnlyList<string> material,
        bool vertex,
        Func<string, string, IReadOnlyList<string>, byte[]?> compile,
        string sourcePath)
    {
        var built = new List<byte[]>();
        // The pixel side ignores INSTANCING_ENABLED, so its contexts are the light
        // configurations alone — the technique says so, and the counts agree.
        var wanted = vertex
            ? Enumerable.Range(0, contexts.Count)
            : Enumerable.Range(0, contexts.Count).Where(value => value % 2 == 0);
        foreach (var context in wanted)
        {
            foreach (var pass in passes)
            {
                var defines = Variants.DefinesFor(material, contexts[context]);
                var entry = vertex ? pass.Vertex : pass.Fragment;
                var blob = compile(sourcePath, entry, defines);
                if (blob is null)
                {
                    Console.WriteLine($"  {entry} du contexte {context} n'a pas compile");
                    return null;
                }
                built.Add(blob);
            }
        }
        return built;
    }
}
