using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Shaders.Forge;

/// <summary>
/// Writes an effect's parameter interface, rather than keeping the one its template
/// came with.
///
/// This is what a shader with its own uniforms needs. Adding a single one relays the
/// whole constant buffer, so every table that names an offset into it has to be
/// written again: the location run of each pass, the counts each pass keeps per
/// frequency, the definitions the effect declares, and the buffer size each program
/// reports.
///
/// What is not rewritten is the capture buffer's layout for anything the template
/// already had. Those offsets are shared with the engine — the scene fills its own
/// parameters there — so they are recovered from the template and kept, and only a
/// uniform the template never had is given a new place.
/// </summary>
public static class Emission
{
    /// <summary>The two parameters a material may not set: Phyre's own switch words.</summary>
    private static readonly HashSet<string> Untweakable = new(StringComparer.Ordinal)
    {
        "PhyreContextSwitches", "PhyreMaterialSwitches",
    };

    /// <summary>One entry of a pass's location run.</summary>
    private readonly record struct Location(ushort Capture, uint Where, uint Size, byte Type);

    /// <summary>
    /// Rewrites the interface in place. <paramref name="groups"/>, <paramref name="arrays"/>
    /// and <paramref name="pointers"/> are the cluster being assembled; the rest is
    /// the template it came from and the programs that will replace its own.
    /// </summary>
    public static bool Apply(
        List<PhyreGroupContents> groups,
        List<PhyreArrayFixup> arrays,
        List<PhyrePointerFixup> pointers,
        PhyreClusterSections cut,
        PhyreClusterData data,
        PhyreFixupSet templateFixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<byte[]> vertex,
        IReadOnlyList<byte[]> fragment,
        string source)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var placed = Generation.CaptureOffsets(data, cut, templateFixups, classes);
        var declarations = Generation.Declarations(source);
        var reflected = new Dictionary<string, List<Reflection.Program>>(StringComparer.Ordinal)
        {
            ["PShaderVertexProgram"] = vertex.Select(Reflection.Read).ToList(),
            ["PShaderFragmentProgram"] = fragment.Select(Reflection.Read).ToList(),
        };

        // What the template declares, textures included. The recovery above walks the
        // constant runs only, so a texture — which has no constant buffer offset to be
        // found by — would otherwise have no place at all.
        var semantics = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var (name, one) in Generation.Declared(data, cut, templateFixups, classes))
        {
            placed.TryAdd(name, new Generation.Placed(
                one.Capture, one.Size, one.DataType, Frequency(one.Semantic)));
            semantics[name] = one.Semantic;
        }

        // A uniform the template never had gets a place of its own, past everything
        // the template uses, so nothing the engine already fills is disturbed.
        var next = placed.Count == 0
            ? 16u
            : (uint)((placed.Values.Max(one => one.Capture + one.Size) + 15) / 16 * 16);
        foreach (var program in reflected.Values.SelectMany(value => value))
        {
            foreach (var one in program.Constants)
            {
                if (placed.ContainsKey(one.Name)) continue;
                var (dataType, size) = Generation.Shape(one);
                placed[one.Name] = new Generation.Placed(next, size, dataType, Frequency(
                    Generation.SemanticOf(one.Name, declarations, Semantic.Constant)));
                next += (size + 15) / 16 * 16;
            }
            foreach (var one in program.Bindings.Where(value => value.Type is 2 or 3))
            {
                // Its semantic whether or not it is new, since the data type alone
                // cannot say a texture from the sampler beside it.
                semantics.TryAdd(one.Name, Generation.SemanticOf(
                    one.Name, declarations, Generation.ResourceFallback(one)));
                if (placed.ContainsKey(one.Name)) continue;
                placed[one.Name] = new Generation.Placed(
                    next, 16, Generation.ResourceDataType, 1);
                next += 16;
            }
        }

        var passes = Passes(cut, templateFixups, classes);
        if (passes.Count == 0)
        {
            Console.WriteLine("  aucune passe lisible dans le modele — interface inchangee");
            return false;
        }

        var built = Runs(passes, reflected, placed, cut, templateFixups, classes, data);
        Write(groups, arrays, pointers, cut, data, templateFixups, classes, built, placed,
            declarations, semantics, reflected, passes);
        Console.WriteLine($"  interface : {built.Entries.Count} localisations,"
            + $" {built.Definitions.Count} definitions, {placed.Count} parametres situes");
        return true;
    }

    /// <summary>Which buffer a parameter is filled from, by the block its semantic is in.</summary>
    private static byte Frequency(byte semantic) => (byte)(semantic >> 6);

    private readonly record struct Pass(uint Id, uint Vertex, uint Fragment);

    /// <summary>Each pass and the two programs it draws with.</summary>
    private static List<Pass> Passes(
        PhyreClusterSections cut, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var group = cut.Metadata.InstanceGroups.FirstOrDefault(value => value.ClassName == "PShaderPass");
        if (group is null) return new List<Pass>();
        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderPass"), classes)
            .ToList();
        var vertexId = (uint)chain.First(value => value.Name == "m_vertexProgram").Index;
        var fragmentId = (uint)chain.First(value => value.Name == "m_fragmentProgram").Index;
        var found = new List<Pass>();
        for (var id = 0u; id < group.Count; id++)
        {
            var one = id;
            var vertex = fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == group.Index && value.SourceObjectId == one
                && value.SourceOffsetOrMember == vertexId);
            var fragment = fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == group.Index && value.SourceObjectId == one
                && value.SourceOffsetOrMember == fragmentId);
            if (vertex is null || fragment is null) continue;
            found.Add(new Pass(id, vertex.DestinationObjectId, fragment.DestinationObjectId));
        }
        return found;
    }

    private sealed record Built(
        List<Location> Entries,
        List<(uint Pass, int Which, int Start, int Count, ushort[] Starts, ushort[] Counts)> Runs,
        List<Generation.Parameter> Definitions,
        Dictionary<uint, (int Start, int Count)> ShaderRuns);

    /// <summary>
    /// The four location runs of every pass, laid end to end, plus the definitions the
    /// effect will declare.
    /// </summary>
    private static Built Runs(
        List<Pass> passes,
        Dictionary<string, List<Reflection.Program>> reflected,
        Dictionary<string, Generation.Placed> placed,
        PhyreClusterSections cut,
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        PhyreClusterData data)
    {
        var entries = new List<Location>();
        var runs = new List<(uint, int, int, int, ushort[], ushort[])>();

        // Every texture the effect knows, in capture order. Both texture runs list all
        // of them; only the register differs, and a program that does not bind one
        // says so with the unbound marker.
        var textures = placed
            .Where(value => value.Value.Type == Generation.ResourceDataType)
            .OrderBy(value => value.Value.Capture)
            .ToList();

        foreach (var pass in passes)
        {
            var programs = new[]
            {
                reflected["PShaderVertexProgram"][(int)pass.Vertex],
                reflected["PShaderFragmentProgram"][(int)pass.Fragment],
            };

            // Constants first, for the vertex program then the fragment one, each run
            // sorted by frequency so the pass can say where each begins.
            for (var which = 0; which < 2; which++)
            {
                var start = entries.Count;
                var starts = new ushort[4];
                var counts = new ushort[4];
                var ordered = programs[which].Constants
                    .Where(one => placed.ContainsKey(one.Name))
                    .Select(one => (one, placed[one.Name]))
                    .OrderBy(one => one.Item2.Frequency)
                    .ThenBy(one => one.one.Offset)
                    .ToList();
                for (byte frequency = 0; frequency < 4; frequency++)
                {
                    starts[frequency] = (ushort)(entries.Count - start);
                    foreach (var (constant, where) in ordered.Where(one => one.Item2.Frequency == frequency))
                    {
                        entries.Add(new Location(
                            (ushort)where.Capture, constant.Offset, where.Size, where.Type));
                        counts[frequency]++;
                    }
                }
                runs.Add((pass.Id, which, start, entries.Count - start, starts, counts));
            }

            // Then the textures, again vertex before fragment.
            for (var which = 0; which < 2; which++)
            {
                var start = entries.Count;
                var bound = programs[which].Bindings
                    .Where(one => one.Type is 2 or 3)
                    .ToDictionary(one => one.Name, one => one.BindPoint, StringComparer.Ordinal);
                foreach (var (name, where) in textures)
                {
                    entries.Add(new Location(
                        (ushort)where.Capture,
                        bound.TryGetValue(name, out var register) ? register : 0xFFFFFFFF,
                        0, Generation.ResourceDataType));
                }
                runs.Add((pass.Id, 2 + which, start, entries.Count - start,
                    new ushort[4], new ushort[4]));
            }
        }

        // What the effect declares: the material's and the node's, kept apart from the
        // node context's, which belong to each shader's own run.
        var declared = new List<Generation.Parameter>();
        var shaderRuns = new Dictionary<uint, (int, int)>();
        var effect = placed
            .Where(value => value.Value.Frequency is 1 or 2)
            .OrderBy(value => Untweakable.Contains(value.Key) ? 0 : 1)
            .ThenBy(value => value.Value.Capture)
            .ToList();
        foreach (var (name, where) in effect)
        {
            declared.Add(new Generation.Parameter(
                name, 0, where.Type, where.Size, where.Capture, Located(reflected, name)));
        }

        var shaders = cut.Metadata.InstanceGroups.FirstOrDefault(value => value.ClassName == "PShader");
        if (shaders is not null)
        {
            foreach (var pass in passes)
            {
                var start = declared.Count;
                var used = new List<string>();
                foreach (var which in new[] { pass.Vertex, pass.Fragment })
                {
                    var program = which == pass.Vertex
                        ? reflected["PShaderVertexProgram"][(int)pass.Vertex]
                        : reflected["PShaderFragmentProgram"][(int)pass.Fragment];
                    foreach (var one in program.Constants)
                    {
                        if (!placed.TryGetValue(one.Name, out var where) || where.Frequency != 3) continue;
                        if (used.Contains(one.Name, StringComparer.Ordinal)) continue;
                        used.Add(one.Name);
                        declared.Add(new Generation.Parameter(
                            one.Name, 0, where.Type, where.Size, where.Capture, one.Offset));
                    }
                }
                shaderRuns[pass.Id] = (start, declared.Count - start);
            }
        }
        return new Built(entries, runs, declared, shaderRuns);
    }

    /// <summary>The constant buffer offset the first program that has it gives a name.</summary>
    private static uint Located(
        Dictionary<string, List<Reflection.Program>> reflected, string name)
    {
        foreach (var program in reflected.Values.SelectMany(value => value))
        {
            foreach (var one in program.Constants)
            {
                if (one.Name.Equals(name, StringComparison.Ordinal)) return one.Offset;
            }
        }
        return 0xFFFFFFFF;
    }

    /// <summary>Lays the built tables into the groups and moves every link onto them.</summary>
    private static void Write(
        List<PhyreGroupContents> groups,
        List<PhyreArrayFixup> arrays,
        List<PhyrePointerFixup> pointers,
        PhyreClusterSections cut,
        PhyreClusterData data,
        PhyreFixupSet templateFixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        Built built,
        Dictionary<string, Generation.Placed> placed,
        IReadOnlyDictionary<string, string> declarations,
        IReadOnlyDictionary<string, byte> semantics,
        Dictionary<string, List<Reflection.Program>> reflected,
        List<Pass> passes)
    {
        var locations = Index(cut, "PShaderParameterCaptureBufferLocationTypeConstantBuffer");
        var definitions = Index(cut, "PShaderParameterDefinition");
        var passGroup = Index(cut, "PShaderPass");
        var shaderGroup = Index(cut, "PShader");

        // The location run, one sixteen byte record each.
        var laid = new List<PhyreObjectContents>();
        foreach (var one in built.Entries)
        {
            laid.Add(Object("PShaderParameterCaptureBufferLocationTypeConstantBuffer",
                ("m_offset", BitConverter.GetBytes(one.Capture)),
                ("m_constantBufferLocation", BitConverter.GetBytes(one.Where)),
                ("m_size", BitConverter.GetBytes(one.Size)),
                ("m_type", new[] { one.Type })));
        }
        groups[locations] = groups[locations] with
        {
            Objects = laid, ArrayData = ReadOnlyMemory<byte>.Empty,
        };

        // The definitions, with their names after them on even offsets.
        var names = new MemoryStream();
        var made = new List<PhyreObjectContents>();
        var at = new List<uint>();
        foreach (var one in built.Definitions)
        {
            if (names.Length % 2 != 0) names.WriteByte(0);
            at.Add((uint)names.Length);
            names.Write(System.Text.Encoding.ASCII.GetBytes(one.Name + "\0"));
            var semantic = semantics.TryGetValue(one.Name, out var known)
                ? known
                : Generation.SemanticOf(one.Name, declarations, Semantic.Constant);
            made.Add(Object("PShaderParameterDefinition",
                ("m_parameterType", new[] { semantic }),
                ("m_dataType", new[] { one.DataType }),
                ("m_arrayElementCount", BitConverter.GetBytes((ushort)0)),
                ("m_bufferLoc", BitConverter.GetBytes((one.Size << 16) | one.Capture)),
                ("m_constantBufferLocation", BitConverter.GetBytes(one.ConstantBufferLocation))));
        }
        if (names.Length % 2 != 0) names.WriteByte(0);
        groups[definitions] = groups[definitions] with
        {
            Objects = made, ArrayData = names.ToArray(),
        };

        var nameAt = 0x80000000u | PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderParameterDefinition"), classes)
            .First(value => value.Name == "m_name").ValueOffset;
        arrays.RemoveAll(value => value.SourceListIndex == definitions);
        for (var id = 0; id < made.Count; id++)
        {
            arrays.Add(new PhyreArrayFixup(definitions, (uint)id, nameAt, 0, at[id]));
        }

        Relink(groups, pointers, cut, classes, built, passGroup, shaderGroup, locations,
            definitions, passes, reflected, data, templateFixups);
    }

    /// <summary>
    /// Moves every pointer and every count onto the tables just written: each pass's
    /// four runs and their per-frequency starts, each shader's definition run, and the
    /// buffer size each program reports.
    /// </summary>
    private static void Relink(
        List<PhyreGroupContents> groups,
        List<PhyrePointerFixup> pointers,
        PhyreClusterSections cut,
        IReadOnlyList<PhyreClassDescriptor> classes,
        Built built,
        int passGroup,
        int shaderGroup,
        int locations,
        int definitions,
        List<Pass> passes,
        Dictionary<string, List<Reflection.Program>> reflected,
        PhyreClusterData data,
        PhyreFixupSet templateFixups)
    {
        var passChain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderPass"), classes).ToList();
        var block = PhyreObjectWriter
            .Chain(
                classes.First(value => value.Name == "PShaderPassParameterLocationTypesConstantBuffer"),
                classes)
            .ToList();
        var startsAt = block.First(value => value.Name == "m_parameterStart").ValueOffset;
        var countsAt = block.First(value => value.Name == "m_parameterCount").ValueOffset;
        var arrayAt = block.First(value => value.Name == "m_parameterLocations").ValueOffset;
        var sides = new[]
        {
            passChain.First(value => value.Name == "m_vertexParameterLocation").ValueOffset,
            passChain.First(value => value.Name == "m_fragmentParameterLocation").ValueOffset,
            passChain.First(value => value.Name == "m_vertexTexParameterLocation").ValueOffset,
            passChain.First(value => value.Name == "m_fragmentTexParameterLocation").ValueOffset,
        };

        var passObjects = groups[passGroup].Objects.ToList();
        foreach (var (pass, which, start, count, starts, counts) in built.Runs)
        {
            var members = new Dictionary<string, byte[]>(
                passObjects[(int)pass].Members, StringComparer.Ordinal);
            var side = sides[which];

            // The block sits inside the pass, so its own members are written by hand
            // at the offset the pass gives it. There is no member name for them.
            var whole = PhyreObjectWriter.WriteObject(
                passObjects[(int)pass], classes,
                (int)(cut.Metadata.InstanceGroups[passGroup].ObjectsSize
                    / cut.Metadata.InstanceGroups[passGroup].Count));
            for (var frequency = 0; frequency < 4; frequency++)
            {
                BitConverter.GetBytes(starts[frequency])
                    .CopyTo(whole.AsSpan((int)(side + startsAt) + frequency * 2));
                BitConverter.GetBytes(counts[frequency])
                    .CopyTo(whole.AsSpan((int)(side + countsAt) + frequency * 2));
            }
            BitConverter.GetBytes((uint)count).CopyTo(whole.AsSpan((int)(side + arrayAt)));
            passObjects[(int)pass] = PhyreObjectWriter.ReadObject(whole, "PShaderPass", classes);

            var pointerAt = 0x80000000u | (side + arrayAt + sizeof(uint));
            pointers.RemoveAll(value =>
                value.SourceListIndex == passGroup && value.SourceObjectId == pass
                && value.SourceOffsetOrMember == pointerAt);
            if (count == 0) continue;
            pointers.Add(new PhyrePointerFixup(
                passGroup, pass, pointerAt, (uint)locations, (uint)start, 0, (uint)count, null));
        }
        groups[passGroup] = groups[passGroup] with { Objects = passObjects };

        // Each shader's own run of definitions, and the effect's two.
        var shaderChain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShader"), classes).ToList();
        var runAt = shaderChain.First(value => value.Name == "m_parameterDefinitionsForPasses");
        var shaderObjects = groups[shaderGroup].Objects.ToList();
        var passesOf = shaderChain.First(value => value.Name == "m_passes");
        for (var id = 0u; id < shaderObjects.Count; id++)
        {
            var link = templateFixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == shaderGroup && value.SourceObjectId == id
                && value.SourceOffsetOrMember == (0x80000000u | (passesOf.ValueOffset + 4)));
            if (link is null || !built.ShaderRuns.TryGetValue(link.DestinationObjectId, out var run))
            {
                continue;
            }
            var members = new Dictionary<string, byte[]>(
                shaderObjects[(int)id].Members, StringComparer.Ordinal)
            {
                [runAt.Name] = BitConverter.GetBytes((uint)run.Count)
                    .Concat(new byte[4]).ToArray(),
            };
            shaderObjects[(int)id] = shaderObjects[(int)id] with { Members = members };

            var pointerAt = 0x80000000u | (runAt.ValueOffset + sizeof(uint));
            pointers.RemoveAll(value =>
                value.SourceListIndex == shaderGroup && value.SourceObjectId == id
                && value.SourceOffsetOrMember == pointerAt);
            if (run.Count == 0) continue;
            pointers.Add(new PhyrePointerFixup(
                shaderGroup, id, pointerAt, (uint)definitions, (uint)run.Start, 0,
                (uint)run.Count, null));
        }
        groups[shaderGroup] = groups[shaderGroup] with { Objects = shaderObjects };

        Effect(groups, pointers, cut, classes, built, definitions);
        Sizes(groups, cut, classes, reflected);
    }

    /// <summary>The two runs the effect variant declares, tweakable and not.</summary>
    private static void Effect(
        List<PhyreGroupContents> groups,
        List<PhyrePointerFixup> pointers,
        PhyreClusterSections cut,
        IReadOnlyList<PhyreClassDescriptor> classes,
        Built built,
        int definitions)
    {
        var index = Index(cut, "PEffectVariant");
        if (index < 0) return;
        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PEffectVariant"), classes).ToList();
        var untweakable = built.Definitions.Count(one => Untweakable.Contains(one.Name));
        var tweakable = built.Definitions.Count(one => !Untweakable.Contains(one.Name)
            && one.Capture < uint.MaxValue) - built.ShaderRuns.Values.Sum(one => one.Count);

        var objects = groups[index].Objects.ToList();
        var members = new Dictionary<string, byte[]>(objects[0].Members, StringComparer.Ordinal);
        foreach (var (name, start, count) in new[]
                 {
                     ("m_untweakableShaderParameterDefinitions", 0, untweakable),
                     ("m_tweakableShaderParameterDefinitions", untweakable, tweakable),
                 })
        {
            var member = chain.FirstOrDefault(value => value.Name == name);
            if (member is null) continue;
            members[name] = BitConverter.GetBytes((uint)count).Concat(new byte[4]).ToArray();
            var pointerAt = 0x80000000u | (member.ValueOffset + sizeof(uint));
            pointers.RemoveAll(value =>
                value.SourceListIndex == index && value.SourceObjectId == 0
                && value.SourceOffsetOrMember == pointerAt);
            if (count <= 0) continue;
            pointers.Add(new PhyrePointerFixup(
                index, 0, pointerAt, (uint)definitions, (uint)start, 0, (uint)count, null));
        }
        objects[0] = objects[0] with { Members = members };
        groups[index] = groups[index] with { Objects = objects };
    }

    /// <summary>What each program says its constant buffer measures.</summary>
    private static void Sizes(
        List<PhyreGroupContents> groups,
        PhyreClusterSections cut,
        IReadOnlyList<PhyreClassDescriptor> classes,
        Dictionary<string, List<Reflection.Program>> reflected)
    {
        foreach (var (className, programs) in reflected)
        {
            var index = Index(cut, className);
            if (index < 0) continue;
            var member = PhyreObjectWriter
                .Chain(classes.First(value => value.Name == className), classes)
                .FirstOrDefault(value => value.Name == "m_constantBufferSize");
            if (member is null) continue;
            var objects = groups[index].Objects.ToList();
            for (var id = 0; id < objects.Count && id < programs.Count; id++)
            {
                var members = new Dictionary<string, byte[]>(
                    objects[id].Members, StringComparer.Ordinal)
                {
                    ["m_constantBufferSize"] = BitConverter.GetBytes(programs[id].BufferSize),
                };
                objects[id] = objects[id] with { Members = members };
            }
            groups[index] = groups[index] with { Objects = objects };
        }
    }

    private static int Index(PhyreClusterSections cut, string className)
    {
        for (var at = 0; at < cut.Metadata.InstanceGroups.Count; at++)
        {
            if (cut.Metadata.InstanceGroups[at].ClassName == className) return at;
        }
        return -1;
    }

    private static PhyreObjectContents Object(
        string className, params (string Name, byte[] Value)[] members)
    {
        var held = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, value) in members) held[name] = value;
        return new PhyreObjectContents(className, held, ReadOnlyMemory<byte>.Empty);
    }
}
