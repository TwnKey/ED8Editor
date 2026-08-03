using ED8Editor.Core;
using ED8Editor.Phyre;
using System.Text;

namespace ED8Editor.Shaders;

/// <summary>
/// Extracts the complete HLSL source and switch definitions from a .fx.phyre.
/// All .fx.phyre files contain the same HLSL source; the CRC in the filename
/// identifies which combination of #define switches was active at compile time.
/// </summary>
public sealed class PhyreShaderSourceExtractor
{
    /// <summary>
    /// Extracts the full HLSL source from a .fx.phyre cluster.
    /// The source is stored in the PEffect group's array data as PString objects.
    /// </summary>
    public string ExtractHlsl(byte[] fxPhyreData)
    {
        var meta = new PhyreClusterMetadataReader().Read(fxPhyreData);
        var data = new PhyreClusterReader().Read(fxPhyreData);

        var effectGroup = meta.InstanceGroups
            .Select((g, i) => (g, i))
            .First(x => x.g.ClassName == "PEffect");

        var arrayData = data.GetArrayData(effectGroup.i, 0, effectGroup.g.ArraysSize);
        return System.Text.Encoding.UTF8.GetString(arrayData.Span);
    }

    /// <summary>
    /// Parses all #define SWITCH_NAME from HLSL source.
    /// Returns the switch names in order of appearance.
    /// </summary>
    public static IReadOnlyList<string> ParseDefines(string hlsl)
    {
        var defines = new List<string>();
        foreach (var line in hlsl.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#define ")) continue;

            // Extract the define name (second token, before any space or comment)
            var parts = trimmed.Substring(8).TrimStart().Split(
                new[] { ' ', '\t', '/', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0 && IsMaterialSwitch(parts[0]))
                defines.Add(parts[0]);
        }
        return defines;
    }

    /// <summary>
    /// Reads the active material switches and context switches from a .fx.phyre.
    /// </summary>
    public ShaderSwitchState ReadActiveSwitches(byte[] fxPhyreData)
    {
        var meta = new PhyreClusterMetadataReader().Read(fxPhyreData);
        var data = new PhyreClusterReader().Read(fxPhyreData);
        var fixups = data.Fixups;

        var materialSwitches = new Dictionary<string, string>();
        var contextSwitches = new Dictionary<string, uint>();

        // Read PMaterialSwitch objects (name/value pairs)
        foreach (var group in meta.InstanceGroups.Where(g => g.ClassName == "PMaterialSwitch"))
        {
            for (uint id = 0; id < group.Count; id++)
            {
                var name = ReadString(data, fixups, group.Index, id, "m_name");
                var value = ReadString(data, fixups, group.Index, id, "m_value");
                if (name != null && value != null)
                    materialSwitches[name] = value;
            }
        }

        // Read PNodeContext objects (runtime context switches, packed as uint values)
        // The switch names are in PEffect.m_contextSwitches
        var effectGroup = meta.InstanceGroups.First(g => g.ClassName == "PEffect");
        var effectObj = data.GetObject(effectGroup.Index, 0).Span;
        var ctxSwitchNames = ReadStringArray(data, fixups, effectGroup.Index, 0, "m_contextSwitches");

        foreach (var group in meta.InstanceGroups.Where(g => g.ClassName == "PNodeContext"))
        {
            for (uint id = 0; id < group.Count; id++)
            {
                var obj = data.GetObject(group.Index, id).Span;
                // m_packedSwitches is PSharray<PUInt32>: pointer at offset 4 (after count at offset 0)
                var ptrFixup = fixups.Pointers.FirstOrDefault(p =>
                    p.SourceListIndex == group.Index && p.SourceObjectId == id &&
                    (p.SourceOffsetOrMember & 0x80000000u) != 0 &&
                    (p.SourceOffsetOrMember & 0x7FFFFFFFu) == 4); // pointer word at +4

                if (ptrFixup == null) continue;
                var targetGroup = meta.InstanceGroups[(int)ptrFixup.DestinationListIndex];
                var packedData = data.GetObject(targetGroup.Index, ptrFixup.DestinationObjectId).Span;

                // Read array of uint32s; map by switch name index
                var count = BitConverter.ToUInt32(obj[..4]);
                for (int s = 0; s < count && s < (ctxSwitchNames?.Count ?? 0); s++)
                {
                    var val = BitConverter.ToUInt32(packedData[(s * 4)..]);
                    var swName = ctxSwitchNames![s];
                    if (!contextSwitches.ContainsKey(swName))
                        contextSwitches[swName] = val;
                }
            }
        }

        return new ShaderSwitchState(materialSwitches, contextSwitches);
    }

    private static string? ReadString(
        PhyreClusterData data, PhyreFixupSet fixups, int groupIndex, uint objectId, string member)
    {
        var arrayFixup = fixups.Arrays.FirstOrDefault(a =>
            a.SourceListIndex == groupIndex && a.SourceObjectId == objectId);
        if (arrayFixup == null) return null;
        var group = data.Metadata.InstanceGroups[groupIndex];
        var bytes = data.GetArrayData(groupIndex, arrayFixup.Offset, group.ArraysSize - arrayFixup.Offset).Span;
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static IReadOnlyList<string>? ReadStringArray(
        PhyreClusterData data, PhyreFixupSet fixups, int groupIndex, uint objectId, string member)
    {
        var ptrFixup = fixups.Pointers.FirstOrDefault(p =>
            p.SourceListIndex == groupIndex && p.SourceObjectId == objectId);
        if (ptrFixup == null) return null;

        var targetGroup = data.Metadata.InstanceGroups[(int)ptrFixup.DestinationListIndex];
        var names = new List<string>();
        for (uint i = 0; i < targetGroup.Count; i++)
        {
            var strFixup = fixups.Arrays.FirstOrDefault(a =>
                a.SourceListIndex == targetGroup.Index && a.SourceObjectId == i);
            if (strFixup == null) continue;
            var bytes = data.GetArrayData(targetGroup.Index, strFixup.Offset,
                targetGroup.ArraysSize - strFixup.Offset).Span;
            var zero = bytes.IndexOf((byte)0);
            if (zero >= 0) bytes = bytes[..zero];
            names.Add(System.Text.Encoding.UTF8.GetString(bytes));
        }
        return names;
    }

    private static bool IsMaterialSwitch(string name)
    {
        // Filter out non-switch defines like FUNCTION, FLOATVALUE, etc.
        return name.EndsWith("_ENABLED", StringComparison.Ordinal)
            || name.StartsWith("NO_", StringComparison.Ordinal)
            || name.StartsWith("FORCE_", StringComparison.Ordinal);
    }
}

/// <summary>
/// The set of switches active for a particular shader CRC.
/// </summary>
public sealed record ShaderSwitchState(
    IReadOnlyDictionary<string, string> MaterialSwitches,
    IReadOnlyDictionary<string, uint> ContextSwitches);

/// <summary>
/// Complete information about a shader: its CRC, source, and all known switches.
/// </summary>
public sealed record ShaderInfo(
    string Crc,
    string ShaderAsset,
    IReadOnlyList<string> AllDefines,
    ShaderSwitchState ActiveSwitches);
