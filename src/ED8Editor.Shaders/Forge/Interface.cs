using System.Text;
using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Shaders.Forge;

/// <summary>
/// The parameter interface a shader cluster declares, with its names resolved.
///
/// A shader is copied from a template today because these tables are copied whole:
/// change the HLSL's uniforms or its vertex inputs and the tables still describe the
/// template's. Reading them next to the bytecode's own reflection is what says whether
/// they can be generated — and which of their fields the compiler already knows.
/// </summary>
public static class Interface
{
    public static int Report(string path, string? blobPath)
    {
        var image = (ReadOnlyMemory<byte>)File.ReadAllBytes(path);
        var cut = PhyreClusterSectionReader.Read(image);
        var data = new PhyreClusterReader().Read(image);
        var fixups = new PhyreFixupReader().Read(image, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();

        Console.WriteLine($"  {Path.GetFileName(path)} : {cut.Metadata.InstanceGroups.Count} groupes");
        foreach (var group in cut.Metadata.InstanceGroups)
        {
            var each = group.Count == 0 ? 0 : group.ObjectsSize / group.Count;
            Console.WriteLine($"     {group.ClassName,-42} x{group.Count,-5} {each,4} o"
                + $"   tableaux {group.ArraysSize} o");
        }

        Definitions(data, cut, fixups, classes);
        Streams(data, cut, fixups, classes);
        if (blobPath is not null) Against(blobPath);
        return 0;
    }

    /// <summary>Every parameter the effect names, with the two locations it carries.</summary>
    private static void Definitions(
        PhyreClusterData data, PhyreClusterSections cut,
        PhyreFixupSet fixups, IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
        if (group is null) return;
        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderParameterDefinition"), classes)
            .ToList();
        var nameAt = chain.First(value => value.Name == "m_name").ValueOffset;
        var objects = data.GetGroupObjectsData(group.Index).Span;
        var arrays = group.ArraysSize == 0
            ? ReadOnlyMemory<byte>.Empty
            : data.GetArrayData(group.Index, 0, group.ArraysSize);
        var size = checked((int)(group.ObjectsSize / group.Count));

        Console.WriteLine();
        Console.WriteLine($"  PShaderParameterDefinition x{group.Count}");
        for (var id = 0; id < group.Count; id++)
        {
            var one = objects.Slice(id * size, size);
            var name = Named(fixups, arrays, group.Index, (uint)id, nameAt);
            Console.WriteLine($"     [{id,3}] type {one[2],3} donnee {one[3],3}"
                + $" x{BitConverter.ToUInt16(one)}"
                + $"  bufferLoc 0x{BitConverter.ToUInt32(one[8..]):X8}"
                + $"  cb 0x{BitConverter.ToUInt32(one[12..]):X8}   {name}");
        }
    }

    /// <summary>The vertex streams the effect expects, likewise named.</summary>
    private static void Streams(
        PhyreClusterData data, PhyreClusterSections cut,
        PhyreFixupSet fixups, IReadOnlyList<PhyreClassDescriptor> classes)
    {
        foreach (var className in new[] { "PShaderStreamDefinition", "PStreamInputDescD3D11" })
        {
            var group = cut.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == className);
            if (group is null || group.Count == 0) continue;
            var chain = PhyreObjectWriter
                .Chain(classes.First(value => value.Name == className), classes)
                .ToList();
            var objects = data.GetGroupObjectsData(group.Index).Span;
            var arrays = group.ArraysSize == 0
                ? ReadOnlyMemory<byte>.Empty
                : data.GetArrayData(group.Index, 0, group.ArraysSize);
            var size = checked((int)(group.ObjectsSize / group.Count));

            Console.WriteLine();
            Console.WriteLine($"  {className} x{group.Count}, {size} o : "
                + string.Join(", ", chain.Select(value => $"+{value.ValueOffset} {value.Name}")));
            for (var id = 0; id < Math.Min(group.Count, 12); id++)
            {
                var one = objects.Slice(id * size, size);
                var words = new List<string>();
                for (var at = 0; at + 4 <= size; at += 4)
                {
                    words.Add($"0x{BitConverter.ToUInt32(one[at..]):X8}");
                }
                var named = chain
                    .Where(value => value.Name.Contains("ame", StringComparison.Ordinal))
                    .Select(value => Named(fixups, arrays, group.Index, (uint)id, value.ValueOffset))
                    .FirstOrDefault(value => value.Length != 0);
                Console.WriteLine($"     [{id,2}] {string.Join(" ", words)}  {named}");
            }
        }
    }

    /// <summary>What the compiler reports for a blob, for the same two lists.</summary>
    private static void Against(string blobPath)
    {
        var read = Reflection.Read(File.ReadAllBytes(blobPath));
        Console.WriteLine();
        Console.WriteLine($"  reflexion de {Path.GetFileName(blobPath)} :"
            + $" {read.Constants.Count} constantes dans {read.BufferSize} octets");
        foreach (var one in read.Constants.OrderBy(value => value.Offset))
        {
            Console.WriteLine($"     +{one.Offset,-6} {one.Size,4} o   {one.Name}");
        }
        Console.WriteLine($"  entrees : {string.Join(", ",
            read.Inputs.Select(value => $"{value.Name}{value.Index}@r{value.Register}"))}");
    }

    /// <summary>The string an array fixup puts at a member, or empty.</summary>
    private static string Named(
        PhyreFixupSet fixups, ReadOnlyMemory<byte> arrays,
        int groupIndex, uint id, uint memberOffset)
    {
        foreach (var one in fixups.Arrays)
        {
            if (one.SourceListIndex != groupIndex || one.SourceObjectId != id) continue;
            if (one.SourceOffsetOrMember != (0x80000000u | memberOffset)) continue;
            var span = arrays.Span[(int)one.Offset..];
            var end = span.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
        }
        return string.Empty;
    }
}
