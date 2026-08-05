using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Shaders.Forge;

/// <summary>
/// The parameter interface an effect must declare, computed from compiled bytecode
/// instead of copied from a template.
///
/// Everything here was measured against CS1's own effects and is checked by
/// <c>ShaderForge plan</c>, which rebuilds the table from a shipped effect's own
/// programs and compares it with what that effect declares. A rule that cannot
/// reproduce the shipped table is a rule that is wrong.
/// </summary>
public static class Generation
{
    /// <summary>One parameter, as the effect will declare it.</summary>
    public sealed record Parameter(
        string Name, byte Semantic, byte DataType, uint Size,
        uint Capture, uint ConstantBufferLocation);

    /// <summary>Parameters the engine binds when it draws, not when it loads.</summary>
    public static bool BoundPerDraw(byte semantic) => semantic >= 194;

    /// <summary>
    /// Every <c>name : SEMANTIC</c> a piece of HLSL declares, struct members included.
    ///
    /// The semantics are read here rather than from the bytecode because D3D does
    /// not keep them: a uniform's semantic is gone by the time the blob exists, and
    /// the effect's own tables are the only place it survives. An entry is taken
    /// only when the semantic ends the declaration — a colon inside an expression or
    /// a comment leads nowhere that matches.
    /// </summary>
    public static Dictionary<string, string> Declarations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^[ \t]*(?:uniform[ \t]+)?[A-Za-z_][A-Za-z0-9_]*[ \t]+([A-Za-z_][A-Za-z0-9_]*)"
            // A semantic ends the declaration, at a semicolon, at the annotation block
            // ed8.fx opens with '<', or simply at the end of the line — Falcom write
            // the game material's parameters that last way.
            + @"[ \t]*:[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*(?:[;<]|\r?$)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match one in pattern.Matches(source))
        {
            found.TryAdd(one.Groups[1].Value, one.Groups[2].Value);
        }
        return found;
    }

    /// <summary>
    /// What a parameter is, given what the source says about it. A name nothing
    /// recognises falls to the material: a constant, or a texture of the dimension
    /// the bytecode binds, or a sampler.
    /// </summary>
    public static byte SemanticOf(
        string name, IReadOnlyDictionary<string, string> declarations, byte fallback)
    {
        declarations.TryGetValue(name, out var semantic);
        var found = Semantic.Of(name, semantic);
        return found is null or Semantic.Unknown ? fallback : found.Value;
    }

    /// <summary>The material parameter a binding falls back to.</summary>
    public static byte ResourceFallback(Reflection.Binding one) => one.Type switch
    {
        // A texture, by its dimension: 4 is 2D, 8 is 3D and 9 a cube.
        2 => one.Dimension switch { 8 => Semantic.Texture3D, 9 => Semantic.TextureCube, _ => Semantic.Texture2D },
        _ => Semantic.Sampler,
    };

    /// <summary>
    /// The data type and the byte size Phyre gives a constant. Measured on all 193
    /// definitions of a shipped effect: 0 is a float, 8 an integer, 1/2/3 a vector of
    /// two, three or four, 49 a 4x4 matrix, 52 a texture or a sampler.
    /// </summary>
    public static (byte DataType, uint Size) Shape(Reflection.Constant one) => one.Class switch
    {
        // A scalar. D3D calls a float 3 and anything integral 1 or 2.
        0 => one.Type == 3 ? ((byte)0, 4u) : ((byte)8, 4u),
        1 => one.Columns switch
        {
            2 => ((byte)1, 8u),
            3 => ((byte)2, 12u),
            _ => ((byte)3, 16u),
        },
        _ => ((byte)49, 64u),
    };

    /// <summary>Textures and samplers alike: a sixteen byte handle, no constant buffer.</summary>
    public const byte ResourceDataType = 52;

    /// <summary>
    /// Where each parameter sits in the capture buffer — the engine side store a
    /// material fills before a draw.
    ///
    /// Two pools, each starting at sixteen: one for the matrices, one for everything
    /// else, both packed tight with the largest data first. This is our own choice
    /// only in the sense that nothing outside the effect reads it; it is written the
    /// way the shipped effects write it so the two can be compared.
    /// </summary>
    public static IReadOnlyList<Parameter> Assign(IEnumerable<Parameter> parameters)
    {
        var all = parameters.ToList();
        var placed = new List<Parameter>();
        var matrices = 16u;
        var rest = 16u;
        foreach (var one in all.OrderByDescending(value => value.DataType == 49)
                     .ThenByDescending(value => value.Size)
                     .ThenBy(value => value.ConstantBufferLocation))
        {
            if (one.DataType == 49)
            {
                placed.Add(one with { Capture = matrices });
                matrices += one.Size;
            }
            else
            {
                placed.Add(one with { Capture = rest });
                rest += one.Size;
            }
        }
        return placed;
    }

    /// <summary>Every parameter a compiled program declares, before placement.</summary>
    public static IEnumerable<Parameter> Of(
        Reflection.Program program,
        ISet<string> engineFed,
        IReadOnlyDictionary<string, string> declarations)
    {
        foreach (var one in program.Constants)
        {
            if (engineFed.Contains(one.Name)) continue;
            var (dataType, size) = Shape(one);
            yield return new Parameter(
                one.Name, SemanticOf(one.Name, declarations, Semantic.Constant),
                dataType, size, 0, one.Offset);
        }

        // Type 0 is the constant buffer itself; 2 is a texture and 3 a sampler. A
        // model 4 shader declaring "sampler2D X" binds X as both, and the effect
        // then calls it a texture — so the texture binding is taken first.
        foreach (var one in program.Bindings.Where(value => value.Type is 2 or 3)
                     .Where(value => !engineFed.Contains(value.Name))
                     .OrderBy(value => value.Type)
                     .DistinctBy(value => value.Name, StringComparer.Ordinal))
        {
            yield return new Parameter(
                one.Name,
                SemanticOf(one.Name, declarations, ResourceFallback(one)),
                ResourceDataType, 16, 0, 0xFFFFFFFF);
        }
    }

    /// <summary>
    /// Rebuilds an effect's parameter table from its own compiled programs and says
    /// where the result differs from what the effect declares.
    /// </summary>
    public static int Plan(string templatePath)
    {
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(templatePath);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        var programs = new List<Reflection.Program>();
        foreach (var className in new[] { "PShaderVertexProgram", "PShaderFragmentProgram" })
        {
            foreach (var blob in Blobs(data, cut, fixups, classes, className))
            {
                programs.Add(Reflection.Read(blob));
            }
        }
        Console.WriteLine($"  {programs.Count} programmes lus dans {Path.GetFileName(templatePath)}");

        // What the effect declares, by name, so the comparison is on the same set.
        var declared = Declared(data, cut, fixups, classes);
        // Whatever a program uses and the effect does not declare is bound by the
        // engine, not by a material: the scene's own view and projection, and in
        // video.fx the screen texture the player is watching.
        var engineFed = programs
            .SelectMany(value => value.Constants.Select(one => one.Name)
                .Concat(value.Bindings.Select(one => one.Name)))
            .Distinct(StringComparer.Ordinal)
            .Where(value => !declared.ContainsKey(value))
            .ToHashSet(StringComparer.Ordinal);
        Console.WriteLine($"  {engineFed.Count} nom(s) alimentes par le moteur,"
            + $" {declared.Count} declares");

        // The source the effect carries, which is where the semantics are written.
        var declarations = Declarations(Source(data, cut));
        Console.WriteLine($"  {declarations.Count} declaration(s) avec semantique dans la source");

        var union = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (var program in programs)
        {
            foreach (var one in Of(program, engineFed, declarations))
            {
                // The lowest constant buffer offset a program gives it, so the order
                // is stable whichever program is read first.
                if (union.TryGetValue(one.Name, out var already)
                    && already.ConstantBufferLocation <= one.ConstantBufferLocation)
                {
                    continue;
                }
                union[one.Name] = one;
            }
        }

        var built = Assign(union.Values.Where(value => !BoundPerDraw(value.Semantic)));
        Console.WriteLine($"  reconstruit : {built.Count} parametres");

        // What is compared, and what is not.
        //
        // The semantic, the data type and the size are derived, so they must agree
        // with the shipped table exactly — a rule that gets one of them wrong is
        // wrong. The capture offset is not compared: the shipped table describes
        // every parameter the FX source declares, so a material may set any of them
        // whichever variant is loaded, while a rebuild only sees the ones this
        // variant's programs kept. Both layouts are internally consistent, and
        // nothing outside the effect reads either.
        var wrong = 0;
        foreach (var one in built.OrderBy(value => value.Capture))
        {
            if (!declared.TryGetValue(one.Name, out var shipped))
            {
                Console.WriteLine($"     ABSENT DU LIVRE  {one.Name}");
                wrong++;
                continue;
            }
            if (shipped.Semantic == one.Semantic
                && shipped.DataType == one.DataType
                && shipped.Size == one.Size)
            {
                continue;
            }
            Console.WriteLine($"     DIFFERE   {one.Name,-34}"
                + $" nous type {one.Semantic} donnee {one.DataType} taille {one.Size}"
                + $" | livre type {shipped.Semantic} donnee {shipped.DataType}"
                + $" taille {shipped.Size}");
            wrong++;
        }
        var unused = declared
            .Where(value => !BoundPerDraw(value.Value.Semantic))
            .Count(value => built.All(one => one.Name != value.Key));
        Console.WriteLine($"  {unused} parametre(s) declares par l'effet"
            + " qu'aucun programme de cette variante n'utilise");
        Console.WriteLine(wrong == 0
            ? $"  les {built.Count} parametres reconstruits ont la sematique,"
                + " le type et la taille du livre"
            : $"  {wrong} parametre(s) ne concordent pas");
        return wrong == 0 ? 0 : 1;
    }

    /// <summary>
    /// The name to semantic table the game's own effects imply, so the registry above
    /// is measured rather than guessed. Reports any name two effects disagree on —
    /// there should be none, and one would mean the semantic is not a property of the
    /// name alone.
    /// </summary>
    public static int Semantics(IReadOnlyList<string> paths)
    {
        var found = new Dictionary<string, byte>(StringComparer.Ordinal);
        var from = new Dictionary<string, string>(StringComparer.Ordinal);
        var clashes = 0;
        foreach (var path in paths)
        {
            var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(path);
            if (!image.Span[..4].SequenceEqual("RYHP"u8)) continue;
            var cut = PhyreClusterSectionReader.Read(image);
            var data = new PhyreClusterReader().Read(image);
            var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
            var classes = cut.Metadata.Classes.ToList();
            var here = Path.GetFileName(path);
            foreach (var (name, one) in Declared(data, cut, fixups, classes))
            {
                if (found.TryGetValue(name, out var already) && already != one.Semantic)
                {
                    Console.WriteLine($"  DESACCORD {name} : {already} dans {from[name]},"
                        + $" {one.Semantic} dans {here}");
                    clashes++;
                    continue;
                }
                found[name] = one.Semantic;
                from[name] = here;
            }
        }

        // The material fallbacks are not part of the table: they are what an unknown
        // name gets, and writing them down would turn every material parameter of
        // every effect into an entry.
        foreach (var one in found
                     .Where(value => value.Value is not (Semantic.Constant or Semantic.Color
                         or Semantic.Texture2D or Semantic.Texture3D or Semantic.TextureCube
                         or Semantic.Sampler))
                     .OrderBy(value => value.Value))
        {
            Console.WriteLine($"        [\"{one.Key}\"] = {one.Value},");
        }
        Console.WriteLine($"  {found.Count} noms lus, {clashes} desaccord(s)");
        return clashes == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every vertex stream the game's effects declare, with the hash each carries.
    /// A stream is matched to a mesh's data by that hash, so writing one means
    /// knowing how it is computed — and that takes more than the four names a
    /// single effect happens to use.
    /// </summary>
    public static int Streams(IReadOnlyList<string> paths)
    {
        var found = new Dictionary<string, (uint Hash, byte DataType, byte Index)>(
            StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(path);
            if (image.Length < 4 || !image.Span[..4].SequenceEqual("RYHP"u8)) continue;
            var cut = PhyreClusterSectionReader.Read(image);
            var data = new PhyreClusterReader().Read(image);
            var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
            var classes = cut.Metadata.Classes.ToList();
            var group = cut.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == "PShaderStreamDefinition");
            if (group is null || group.Count == 0) continue;
            var chain = PhyreObjectWriter
                .Chain(classes.First(value => value.Name == "PShaderStreamDefinition"), classes)
                .ToList();
            var nameAt = 0x80000000u | chain.First(value => value.Name == "m_name").ValueOffset;
            var objects = data.GetGroupObjectsData(group.Index).Span;
            var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize);
            var size = checked((int)(group.ObjectsSize / group.Count));
            for (var id = 0; id < group.Count; id++)
            {
                var one = objects.Slice(id * size, size);
                var named = fixups.Arrays.FirstOrDefault(value =>
                    value.SourceListIndex == group.Index
                    && value.SourceObjectId == (uint)id
                    && value.SourceOffsetOrMember == nameAt);
                if (named is null) continue;
                var text = arrays.Span[(int)named.Offset..];
                var end = text.IndexOf((byte)0);
                var name = System.Text.Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
                found[$"{name}{one[15]}"] = (BitConverter.ToUInt16(one[12..]), one[14], one[15]);
            }
        }
        var wrong = 0;
        foreach (var (key, one) in found.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            // The name without its index, which is what the engine hashes.
            var name = key[..^1];
            var ours = Semantic.Hash(name);
            if (ours != one.Hash) wrong++;
            Console.WriteLine($"  {key,-18} hachage 0x{one.Hash:X4}  calcule 0x{ours:X4}"
                + $"  {(ours == one.Hash ? "" : "DIFFERE ")}donnee {one.DataType}  index {one.Index}");
        }
        Console.WriteLine($"  {found.Count} flux distincts, {wrong} hachage(s) faux");
        return wrong == 0 ? 0 : 1;
    }

    /// <summary>
    /// Where every constant of an effect sits in the capture buffer, its own and the
    /// scene's alike.
    ///
    /// The effect names only what a material fills; the scene's parameters are in the
    /// programs and in the pass location tables, but nowhere by name. They can still
    /// be recovered, because the two halves compose: a program's reflection gives
    /// name to constant buffer offset, and its pass's location table gives constant
    /// buffer offset to capture offset. Which matters because adding a single uniform
    /// relays the whole constant buffer, so every entry has to be written again —
    /// and an entry cannot be written without knowing where in the capture buffer it
    /// reads from.
    /// </summary>
    public sealed record Placed(uint Capture, uint Size, byte Type, byte Frequency);

    public static Dictionary<string, Placed> CaptureOffsets(string templatePath)
    {
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(templatePath);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
        return CaptureOffsets(data, cut, fixups, cut.Metadata.Classes.ToList());
    }

    public static Dictionary<string, Placed> CaptureOffsets(
        PhyreClusterData data, PhyreClusterSections cut, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var located = cut.Metadata.InstanceGroups
            .First(value => value.ClassName == "PShaderParameterCaptureBufferLocationTypeConstantBuffer");
        var entries = data.GetGroupObjectsData(located.Index).Span;
        var entrySize = checked((int)(located.ObjectsSize / located.Count));

        // Which entries belong to which pass, and whether they describe its vertex
        // program or its fragment one. The member the fixup names says so.
        var pass = cut.Metadata.InstanceGroups.First(value => value.ClassName == "PShaderPass");
        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderPass"), classes)
            .ToList();
        var vertexAt = 0x80000000u
            | (chain.First(value => value.Name == "m_vertexParameterLocation").ValueOffset + 16 + 4);
        var fragmentAt = 0x80000000u
            | (chain.First(value => value.Name == "m_fragmentParameterLocation").ValueOffset + 16 + 4);

        // The starts and counts belong to the block embedded in the pass, not to the
        // pass itself, so they are read from that class and offset by where it sits.
        var block = PhyreObjectWriter
            .Chain(
                classes.First(value =>
                    value.Name == "PShaderPassParameterLocationTypesConstantBuffer"),
                classes)
            .ToList();
        var starts = block.First(value => value.Name == "m_parameterStart").ValueOffset;
        var counts = block.First(value => value.Name == "m_parameterCount").ValueOffset;
        var passObjects = data.GetGroupObjectsData(pass.Index).Span;
        var passSize = checked((int)(pass.ObjectsSize / pass.Count));

        var found = new Dictionary<string, Placed>(StringComparer.Ordinal);
        var programs = new Dictionary<string, List<Reflection.Program>>(StringComparer.Ordinal);
        foreach (var className in new[] { "PShaderVertexProgram", "PShaderFragmentProgram" })
        {
            programs[className] = Blobs(data, cut, fixups, classes, className)
                .Select(Reflection.Read)
                .ToList();
        }

        foreach (var one in fixups.Pointers)
        {
            if (one.SourceListIndex != pass.Index) continue;
            var vertex = one.SourceOffsetOrMember == vertexAt;
            if (!vertex && one.SourceOffsetOrMember != fragmentAt) continue;

            // Which program this pass draws with, so its reflection can be read.
            var member = vertex ? "m_vertexProgram" : "m_fragmentProgram";
            // A pointer straight at an object names its member by index, not by
            // offset — only the array pointers carry a raw offset with the high bit.
            var link = fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == pass.Index
                && value.SourceObjectId == one.SourceObjectId
                && value.SourceOffsetOrMember
                    == (uint)chain.First(two => two.Name == member).Index);
            if (link is null) continue;
            var reflected = programs[vertex ? "PShaderVertexProgram" : "PShaderFragmentProgram"];
            if (link.DestinationObjectId >= reflected.Count) continue;
            var byOffset = reflected[(int)link.DestinationObjectId].Constants
                .ToDictionary(value => value.Offset, value => value.Name);

            // How many of the run belong to each frequency, so a constant's own can be
            // read off. The pass holds four starts and four counts — scene, material,
            // node, node context — and they run end to end over the array.
            var passBytes = passObjects.Slice((int)(one.SourceObjectId * passSize), passSize);
            var side = (int)(vertex
                ? chain.First(two => two.Name == "m_vertexParameterLocation").ValueOffset
                : chain.First(two => two.Name == "m_fragmentParameterLocation").ValueOffset);
            var frequencyOf = new byte[one.ArrayIndex];
            for (byte frequency = 0; frequency < 4; frequency++)
            {
                var from = BitConverter.ToUInt16(passBytes[(side + (int)starts + frequency * 2)..]);
                var many = BitConverter.ToUInt16(passBytes[(side + (int)counts + frequency * 2)..]);
                for (var at = from; at < from + many && at < frequencyOf.Length; at++)
                {
                    frequencyOf[at] = frequency;
                }
            }

            for (var at = 0u; at < one.ArrayIndex; at++)
            {
                var entry = entries.Slice(
                    (int)((one.DestinationObjectId + at) * entrySize), entrySize);
                var location = BitConverter.ToUInt32(entry[4..]);
                if (!byOffset.TryGetValue(location, out var name)) continue;
                found.TryAdd(name, new Placed(
                    BitConverter.ToUInt16(entry), BitConverter.ToUInt32(entry[8..]),
                    entry[12], frequencyOf[at]));
            }
        }
        return found;
    }

    /// <summary>
    /// Recovers the capture layout of an effect and checks it against what the effect
    /// itself declares. A parameter the effect names must land on the offset it
    /// declares; the scene's, which it does not name, are recovered all the same.
    /// </summary>
    public static int Capture(string templatePath)
    {
        var recovered = CaptureOffsets(templatePath);
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(templatePath);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
        var declared = Declared(data, cut, fixups, cut.Metadata.Classes.ToList());

        var wrong = 0;
        var checkedCount = 0;
        foreach (var (name, one) in recovered)
        {
            if (!declared.TryGetValue(name, out var says)) continue;
            checkedCount++;
            if (says.Capture == one.Capture && says.Size == one.Size) continue;
            Console.WriteLine($"     DIFFERE {name,-34} retrouve cap {one.Capture}"
                + $" taille {one.Size} | declare cap {says.Capture} taille {says.Size}");
            wrong++;
        }
        foreach (var frequency in recovered.GroupBy(value => value.Value.Frequency)
                     .OrderBy(value => value.Key))
        {
            var kind = frequency.Key switch
            {
                0 => "scene", 1 => "materiau", 2 => "noeud", _ => "contexte de noeud",
            };
            Console.WriteLine($"     frequence {frequency.Key} ({kind}) : {frequency.Count()}"
                + $"   ex. {string.Join(", ", frequency.Take(3).Select(value => value.Key))}");
        }
        Console.WriteLine($"  {recovered.Count} constantes situees dans le tampon de capture,"
            + $" dont {checkedCount} que l'effet nomme");
        Console.WriteLine(wrong == 0
            ? "  toutes celles que l'effet nomme tombent sur l'offset qu'il declare"
            : $"  {wrong} desaccord(s)");
        return wrong == 0 ? 0 : 1;
    }

    /// <summary>The effect source a cluster carries, held in the PEffect's arrays.</summary>
    public static string Source(PhyreClusterData data, PhyreClusterSections cut)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PEffect");
        if (group is null || group.ArraysSize == 0) return string.Empty;
        var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize).Span;
        return System.Text.Encoding.ASCII.GetString(arrays);
    }

    /// <summary>Every compiled blob of a program group, in order.</summary>
    private static IEnumerable<byte[]> Blobs(
        PhyreClusterData data, PhyreClusterSections cut, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes, string className)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == className);
        if (group is null || group.Count == 0) yield break;
        var member = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == className), classes)
            .First(value => value.Name == "m_compiledCode");
        var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize);
        var pointerAt = 0x80000000u | (member.ValueOffset + sizeof(uint));
        for (uint id = 0; id < group.Count; id++)
        {
            var found = fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index
                && value.SourceObjectId == id
                && value.SourceOffsetOrMember == pointerAt);
            if (found is null) continue;
            yield return arrays.Slice((int)found.Offset, (int)found.Count).ToArray();
        }
    }

    /// <summary>The parameters the effect declares, read back with their names.</summary>
    public static Dictionary<string, Parameter> Declared(
        PhyreClusterData data, PhyreClusterSections cut, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var found = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
        if (group is null) return found;
        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderParameterDefinition"), classes)
            .ToList();
        var nameAt = 0x80000000u | chain.First(value => value.Name == "m_name").ValueOffset;
        var objects = data.GetGroupObjectsData(group.Index).Span;
        var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize);
        var size = checked((int)(group.ObjectsSize / group.Count));
        for (var id = 0; id < group.Count; id++)
        {
            var one = objects.Slice(id * size, size);
            var named = fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index
                && value.SourceObjectId == (uint)id
                && value.SourceOffsetOrMember == nameAt);
            if (named is null) continue;
            var text = arrays.Span[(int)named.Offset..];
            var end = text.IndexOf((byte)0);
            var name = System.Text.Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
            var loc = BitConverter.ToUInt32(one[8..]);
            found.TryAdd(name, new Parameter(
                name, one[2], one[3], loc >> 16, loc & 0xFFFF,
                BitConverter.ToUInt32(one[12..])));
        }
        return found;
    }
}
