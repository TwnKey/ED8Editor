using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// The named points a model offers for something to be hung on it.
///
/// This is what a weapon attaches to. <c>t_attach.tbl</c> holds, per character,
/// the equipment model and the name of the point it goes on — the schema calls
/// them <c>character</c>, <c>model</c> and <c>attach_point</c> — and the names it
/// uses are these: <c>R_arm_point</c>, <c>L_arm_point</c>, <c>Left_SB_point</c>,
/// <c>head_point</c>, <c>megane_point</c>. So attaching is a lookup by name, and
/// the model has to be asked what names it has.
///
/// They are more than markers: a locator is animated like a bone. One of
/// ply000's clips drives <c>locator10</c> directly — it was the single channel
/// target that did not resolve to a joint — so a weapon follows the animation
/// through its point rather than being pinned to a static place.
/// </summary>
public static class PhyreLocators
{
    /// <summary>Every attachment point the model names, in the order it lists them.</summary>
    public static IReadOnlyList<string> Read(PhyreClusterData cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        var group = -1;
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            if (cluster.Metadata.InstanceGroups[index].ClassName != "PLocator") continue;
            group = index;
            break;
        }
        if (group < 0) return Array.Empty<string>();

        var names = new List<string>();
        var count = cluster.Metadata.InstanceGroups[group].Count;
        for (uint id = 0; id < count; id++)
        {
            // A locator's name is an array of characters the object points at,
            // the same shape every named thing in a cluster uses.
            var fixup = cluster.Fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group && value.SourceObjectId == id);
            names.Add(fixup is null ? string.Empty : ReadString(cluster, group, fixup.Offset));
        }
        return names;
    }

    private static string ReadString(PhyreClusterData cluster, int groupIndex, uint offset)
    {
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        if (offset >= group.ArraysSize) return string.Empty;
        var data = cluster.GetArrayData(groupIndex, offset, group.ArraysSize - offset).Span;
        var zero = data.IndexOf((byte)0);
        return zero < 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(data[..zero]);
    }
}
