using System.Text;
using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.ShaderForge;

/// <summary>
/// Compiles every program a shipped shader carries, from the source that shader
/// itself embeds, and puts our size beside the game's for each.
///
/// Nothing is written. The remaining unknown is the set of defines a given slot was
/// built with — the entry point and profile are named in the file, but the context
/// switches are packed numbers whose meaning is Phyre's, not the source's. Compiling
/// with a stated model and comparing sizes says, slot by slot, whether that model is
/// right; a slot whose size lands on the game's was built the way we think.
/// </summary>
public static class Variants
{
    public sealed record Slot(int Context, int Pass, string Entry, string Profile);

    public static int Report(string templatePath, string sourcePath, Func<string, IReadOnlyList<string>, byte[]?> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(templatePath);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var classes = cut.Metadata.Classes.ToList();

        var material = Strings(data, cut, "PMaterialSwitch")
            .Where(value => value.Length > 1)
            .ToArray();
        Console.WriteLine($"  commutateurs de materiau : {string.Join(" ", material)}");

        var contexts = ContextValues(data, cut);
        var switchNames = new[] { "NUM_LIGHTS", "INSTANCING_ENABLED", "SHADER_LOD_LEVEL" };
        Console.WriteLine($"  {contexts.Count} contextes");

        // The names the effect declares for what it supports; the packed values
        // index these.
        var lightTypes = new[] { "DirectionalLight", "PointLight", "SpotLight" };
        var shadowTypes = new[] { "CombinedCascadedShadowMap", "PCFShadowMap" };
        var passes = PassEntryPoints(data, cut);
        foreach (var (index, pass) in passes.Select((value, index) => (index, value)))
        {
            Console.WriteLine($"    passe {index} : {pass.Vertex} / {pass.Fragment}");
        }

        var shipped = ProgramSizes(data, cut, classes, "PShaderVertexProgram");
        var shippedFp = ProgramSizes(data, cut, classes, "PShaderFragmentProgram");

        Console.WriteLine();
        Console.WriteLine($"  {"emplacement",-28} {"nous",8} {"jeu",8}   ");
        var matched = 0;
        var total = 0;
        for (var context = 0; context < contexts.Count; context++)
        {
            for (var pass = 0; pass < passes.Count; pass++)
            {
                foreach (var vertex in new[] { true, false })
                {
                    // Fragment programs have no per-instancing variant, so their
                    // contexts are the light configurations alone — twelve where the
                    // vertex side has twenty-four.
                    var slot = vertex ? context * 4 + pass : (context / 2) * 4 + pass;
                    var sizes = vertex ? shipped : shippedFp;
                    if (!vertex && context % 2 != 0) continue;
                    if (slot >= sizes.Count) continue;

                    // The context values are Phyre's packing, not counts: a shipped
                    // shader states 0, 17 and 1553 for its three light configurations
                    // where the source guards on "NUM_LIGHTS > MAX_NUM_LIGHTS" with a
                    // maximum of one. Passing 17 makes the preprocessor stop on that
                    // guard. What the source wants is the COUNT, plus a LIGHTTYPE_n
                    // naming the struct of each light — "LIGHTTYPE_0 Light0 : LIGHT0"
                    // is a declaration whose type is the macro.
                    // RECEIVE_SHADOWS is kept everywhere, which is right for thirty
                    // five of the thirty six. The exception is the default PIXEL
                    // program of the one-light-no-shadow context: dropping the switch
                    // brings it to 6816 against the game's 6828, exactly. But the
                    // same drop takes that context's VERTEX program to 5796 against
                    // 5928 — so the two are not compiled from one switch set, and
                    // what tells them apart is not established. Keeping the switch is
                    // the state that costs one slot rather than four.
                    var defines = new List<string>(material) { "PHYRE_D3DFX" };
                    for (var at = 0; at < switchNames.Length && at < contexts[context].Count; at++)
                    {
                        var value = contexts[context][at];
                        switch (switchNames[at])
                        {
                            case "INSTANCING_ENABLED":
                                if (value != 0) defines.Add("INSTANCING_ENABLED");
                                break;
                            case "NUM_LIGHTS":
                                foreach (var one in LightDefines(value, lightTypes, shadowTypes))
                                {
                                    defines.Add(one);
                                }
                                break;
                            default:
                                defines.Add($"{switchNames[at]}={value}");
                                break;
                        }
                    }

                    var entry = vertex ? passes[pass].Vertex : passes[pass].Fragment;
                    var built = compile(entry, defines);
                    total++;
                    var ours = built?.Length ?? 0;
                    // Identical but for the compiler's own name. The shipped blobs
                    // carry "HLSL Shader Compiler 9.29.952.3111" where ours say
                    // "10.1" — nine characters more, eight or twelve bytes once
                    // padded — and that string sits in RDEF, which the engine reads
                    // for the parameter layout and not for the code. Measured on one
                    // slot: ISGN, OSGN and SHEX come back byte for byte.
                    var gap = sizes[slot] - ours;
                    var same = ours != 0 && (gap == 8 || gap == 12);
                    if (same) matched++;
                    Console.WriteLine(
                        $"  ctx{context} {(vertex ? "VP" : "FP")} {entry,-22}"
                        + $" {ours,8} {sizes[slot],8}   {(same ? "= au nom du compilateur pres" : "different")}");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine($"  {matched} sur {total} a la taille du jeu, au nom du compilateur pres");
        return 0;
    }

    /// <summary>Whether any light of a packed switch casts a shadow.</summary>
    private static bool HasShadow(uint packed)
    {
        const int CountBits = 4;
        const int LightBits = 5;
        const int ShadowBits = 5;
        var count = (int)(packed & ((1u << CountBits) - 1));
        var at = CountBits + LightBits;
        for (var light = 0; light < count; light++)
        {
            if (((packed >> at) & ((1u << ShadowBits) - 1)) != 0) return true;
            at += LightBits + ShadowBits;
        }
        return false;
    }

    /// <summary>
    /// The defines a packed light switch stands for.
    ///
    /// Read off the engine: four bits of light count, then five bits of light type
    /// and five of shadow type per light — PhyreContextSwitch.h names the widths, and
    /// PContextSwitchLights::getStringsFromPackedState builds exactly these strings.
    /// A shipped shader states 0, 17 and 1553, which come out as no light, one
    /// directional light without a shadow caster, and the same light WITH one. That
    /// last is why its pixel program measures 11 712 bytes against 6 828.
    /// </summary>
    private static IEnumerable<string> LightDefines(
        uint packed, IReadOnlyList<string> lightTypes, IReadOnlyList<string> shadowTypes)
    {
        const int CountBits = 4;
        const int LightBits = 5;
        const int ShadowBits = 5;
        var count = (int)(packed & ((1u << CountBits) - 1));
        yield return $"NUM_LIGHTS={count}";
        var at = CountBits;
        for (var light = 0; light < count; light++)
        {
            var lightType = (int)((packed >> at) & ((1u << LightBits) - 1));
            at += LightBits;
            var shadowType = (int)((packed >> at) & ((1u << ShadowBits) - 1));
            at += ShadowBits;
            // The ids are mask bit ids, so one-based over what the effect supports.
            if (lightType > 0 && lightType <= lightTypes.Count)
            {
                yield return $"LIGHTTYPE_{light}={lightTypes[lightType - 1]}";
            }
            // The id is a mask bit over every shadow caster type the ENGINE knows,
            // not an index into what this effect supports — a shipped shader states 3
            // while declaring one type. So the effect's own declaration is what gets
            // named.
            if (shadowType > 0 && shadowTypes.Count != 0)
            {
                yield return $"SHADOWTYPE_{light}={shadowTypes[0]}";
            }
        }
    }

    private static IReadOnlyList<string> Strings(
        PhyreClusterData data, PhyreClusterSections cut, string className)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == className && value.ArraysSize != 0);
        if (group is null) return Array.Empty<string>();
        return Encoding.ASCII
            .GetString(data.GetArrayData(group.Index, 0, group.ArraysSize).Span)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.Trim().Length != 0)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<uint>> ContextValues(
        PhyreClusterData data, PhyreClusterSections cut)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PNodeContext" && value.ArraysSize != 0);
        if (group is null) return Array.Empty<IReadOnlyList<uint>>();
        var bytes = data.GetArrayData(group.Index, 0, group.ArraysSize).Span;
        var each = (int)(group.ArraysSize / group.Count / sizeof(uint));
        var found = new List<IReadOnlyList<uint>>();
        for (var at = 0; at < group.Count; at++)
        {
            var one = new uint[each];
            for (var part = 0; part < each; part++)
            {
                one[part] = BitConverter.ToUInt32(bytes[((at * each + part) * 4)..]);
            }
            found.Add(one);
        }
        return found;
    }

    private sealed record PassEntry(string Vertex, string Fragment);

    /// <summary>
    /// The entry points each render pass names. Read off the names the pass-info
    /// group carries rather than guessed: a shipped shader lists seven for four
    /// passes, the first two sharing their vertex program.
    /// </summary>
    private static IReadOnlyList<PassEntry> PassEntryPoints(
        PhyreClusterData data, PhyreClusterSections cut)
    {
        var names = Strings(data, cut, "PShaderPassInfo");
        string Pick(string ending, params string[] preferred)
        {
            foreach (var one in preferred)
            {
                var found = names.FirstOrDefault(value =>
                    value.StartsWith(one, StringComparison.Ordinal)
                    && value.EndsWith(ending, StringComparison.Ordinal));
                if (found is not null) return found;
            }
            return names.FirstOrDefault(value =>
                value.EndsWith(ending, StringComparison.Ordinal)) ?? "";
        }
        return new[]
        {
            new PassEntry(Pick("VPShader", "Default"), Pick("FPShader", "Default")),
            new PassEntry(Pick("VPShader", "Default"), Pick("FPShader", "ForceTransparent")),
            new PassEntry(Pick("VPShader", "Edge"), Pick("FPShader", "Edge")),
            new PassEntry(Pick("VPShader", "Shadow"), Pick("FPShader", "Shadow")),
        };
    }

    private static IReadOnlyList<int> ProgramSizes(
        PhyreClusterData data,
        PhyreClusterSections cut,
        IReadOnlyList<PhyreClassDescriptor> classes,
        string className)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == className && value.Count != 0);
        if (group is null) return Array.Empty<int>();
        var member = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == className), classes)
            .First(value => value.Name == "m_compiledCode");
        var objects = data.GetGroupObjectsData(group.Index).Span;
        var each = (int)(group.ObjectsSize / group.Count);
        var found = new List<int>();
        for (var at = 0; at < group.Count; at++)
        {
            found.Add((int)BitConverter.ToUInt32(
                objects[(at * each + (int)member.ValueOffset)..]));
        }
        return found;
    }
}
