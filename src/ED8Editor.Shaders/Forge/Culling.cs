using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Shaders.Forge;

/// <summary>
/// Makes each pass's culling agree with the switches the variant declares.
///
/// DOUBLE_SIDED is not a define the shader code reads. It is the cull mode of every
/// pass's raster state, which a forge that only recompiles would keep from whatever
/// template it started on — so a variant could declare itself double sided and still
/// be culled like the ground it was copied from.
///
/// Measured over the twelve ed8.fx variants of Trista, and it is exact: every one
/// without the switch culls 2 on the opaque and transparent passes and 3 on the edge
/// and shadow ones, and both that carry it cull 1 everywhere except the edge pass,
/// which keeps 3.
/// </summary>
public static class Culling
{
    private const uint None = 1;
    private const uint Front = 2;
    private const uint Back = 3;

    /// <summary>The pass whose culling the switch never changes.</summary>
    private const string Edge = "EdgeTransparent";

    public static void Apply(
        List<PhyreGroupContents> groups,
        PhyreClusterSections cut,
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<string> switches,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(switches);
        var passes = Group(cut, "PShaderPass");
        if (passes < 0) return;

        var chain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderPass"), classes).ToList();
        var state = chain.First(value => value.Name == "m_state").ValueOffset;
        var raster = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShaderPassStateD3D11"), classes)
            .First(value => value.Name == "m_rasterDesc").ValueOffset;
        // A rasteriser description opens with its fill mode, then its cull mode.
        var cullAt = (int)(state + raster + sizeof(uint));

        // Forcing it separates the two things the switch does. Declaring it changes
        // the list a material's switch word indexes into; culling is what actually
        // shows both sides of a plane. A test that wants one without the other asks
        // for it here.
        var doubleSided = force || switches.Contains("DOUBLE_SIDED", StringComparer.Ordinal);
        var edge = EdgePasses(cut, fixups, classes);
        var size = checked((int)(cut.Metadata.InstanceGroups[passes].ObjectsSize
            / cut.Metadata.InstanceGroups[passes].Count));

        var objects = groups[passes].Objects.ToList();
        var changed = 0;
        for (var id = 0; id < objects.Count; id++)
        {
            if (edge.Contains((uint)id)) continue;
            var bytes = PhyreObjectWriter.WriteObject(objects[id], classes, size);
            var was = BitConverter.ToUInt32(bytes, cullAt);
            // Without the switch, the shadow pass culls the back and the drawn passes
            // cull the front — which is what every shipped variant does.
            var wanted = doubleSided ? None : was == None ? Front : was;
            if (was == wanted) continue;
            BitConverter.GetBytes(wanted).CopyTo(bytes.AsSpan(cullAt));
            objects[id] = PhyreObjectWriter.ReadObject(bytes, "PShaderPass", classes);
            changed++;
        }
        groups[passes] = groups[passes] with { Objects = objects };
        if (changed != 0)
        {
            Console.WriteLine($"  faces : {changed} passe(s) passees en"
                + $" {(doubleSided ? "double face" : "simple face")}");
        }
    }

    /// <summary>
    /// The passes of the edge render pass, which keep their culling whatever the
    /// switches say. The cluster names them: a scene render pass carries its type as
    /// a user fixup and owns a run of shaders, and each shader owns its pass.
    /// </summary>
    private static HashSet<uint> EdgePasses(
        PhyreClusterSections cut, PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes)
    {
        var found = new HashSet<uint>();
        var scene = Group(cut, "PSceneRenderPass");
        var shaders = Group(cut, "PShader");
        if (scene < 0 || shaders < 0) return found;

        var sceneChain = PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PSceneRenderPass"), classes).ToList();
        var typeId = (uint)sceneChain.First(value => value.Name == "m_passType").Index;
        var shadersAt = 0x80000000u
            | (sceneChain.First(value => value.Name == "m_shaders").ValueOffset + sizeof(uint));
        var passesAt = 0x80000000u | (PhyreObjectWriter
            .Chain(classes.First(value => value.Name == "PShader"), classes)
            .First(value => value.Name == "m_passes").ValueOffset + sizeof(uint));

        foreach (var one in fixups.Pointers)
        {
            if (one.SourceListIndex != scene || one.SourceOffsetOrMember != typeId) continue;
            var named = one.UserFixupId is { } id
                ? fixups.UserFixups.FirstOrDefault(value => value.Id == id)?.Text
                : null;
            if (!string.Equals(named, Edge, StringComparison.Ordinal)) continue;

            var run = fixups.Pointers.FirstOrDefault(value =>
                value.SourceListIndex == scene && value.SourceObjectId == one.SourceObjectId
                && value.SourceOffsetOrMember == shadersAt);
            if (run is null) continue;
            for (var at = 0u; at < run.ArrayIndex; at++)
            {
                var shader = run.DestinationObjectId + at;
                foreach (var owned in fixups.Pointers)
                {
                    if (owned.SourceListIndex != shaders || owned.SourceObjectId != shader) continue;
                    if (owned.SourceOffsetOrMember != passesAt) continue;
                    for (var which = 0u; which < Math.Max(owned.ArrayIndex, 1); which++)
                    {
                        found.Add(owned.DestinationObjectId + which);
                    }
                }
            }
        }
        return found;
    }

    private static int Group(PhyreClusterSections cut, string className)
    {
        for (var at = 0; at < cut.Metadata.InstanceGroups.Count; at++)
        {
            if (cut.Metadata.InstanceGroups[at].ClassName == className) return at;
        }
        return -1;
    }
}
