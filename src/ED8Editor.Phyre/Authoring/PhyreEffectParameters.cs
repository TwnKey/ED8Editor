using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One parameter an effect declares.</summary>
/// <param name="Semantic">
/// What the engine gives it. The material block fills the material's own —
/// an arbitrary constant, a colour, a texture, a sampler — and supplies nothing
/// for the rest, which the scene, the node or the light being drawn with fill.
/// </param>
/// <param name="DataType">
/// Its shape: 0 a float, 1/2/3 a vector of two, three or four, 8 an integer,
/// 49 a 4x4 matrix, 52 a texture or a sampler.
/// </param>
public sealed record PhyreEffectParameter(
    string Name, byte Semantic, byte DataType, uint Size, uint Capture);

/// <summary>
/// Reads what an effect says it takes, from the effect itself.
///
/// An effect declares every parameter it wants: its name, what the engine gives
/// it, and its shape. Nothing outside the file has to know any of that, which is
/// what lets a material be pointed at an arbitrary shader and still be filled in
/// correctly — including one whose parameters nothing has seen before.
/// </summary>
public static class PhyreEffectParameters
{
    /// <summary>Every parameter the cluster declares, by name.</summary>
    public static IReadOnlyDictionary<string, PhyreEffectParameter> Read(
        ReadOnlyMemory<byte> cluster)
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
        return Read(data, cut, fixups, cut.Metadata.Classes.ToList());
    }

    /// <summary>The same, when the cluster has already been opened.</summary>
    public static IReadOnlyDictionary<string, PhyreEffectParameter> Read(
        PhyreClusterData data,
        PhyreClusterSections cut,
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(fixups);
        ArgumentNullException.ThrowIfNull(classes);

        var found = new Dictionary<string, PhyreEffectParameter>(StringComparer.Ordinal);
        var group = cut.Metadata.InstanceGroups
            .FirstOrDefault(value => value.ClassName == "PShaderParameterDefinition");
        if (group is null || group.Count == 0) return found;

        var nameAt = 0x80000000u | PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderParameterDefinition"), classes)
            .First(value => value.Name == "m_name").ValueOffset;
        var objects = data.GetGroupObjectsData(group.Index).Span;
        var arrays = data.GetArrayData(group.Index, 0, group.ArraysSize);
        var size = checked((int)(group.ObjectsSize / group.Count));

        for (var id = 0u; id < group.Count; id++)
        {
            var named = fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index && value.SourceObjectId == id
                && value.SourceOffsetOrMember == nameAt);
            if (named is null) continue;
            var text = arrays.Span[(int)named.Offset..];
            var end = text.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
            if (name.Length == 0) continue;

            var body = objects.Slice((int)id * size, size);
            // m_bufferLoc packs the size above the capture offset.
            var location = BitConverter.ToUInt32(body[8..]);
            found.TryAdd(name, new PhyreEffectParameter(
                name, body[2], body[3], location >> 16, location & 0xFFFF));
        }
        return found;
    }
}
