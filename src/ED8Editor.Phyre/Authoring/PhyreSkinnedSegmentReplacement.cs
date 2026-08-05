using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>What replacing one segment of a skinned model cost.</summary>
/// <param name="Segments">How many segments the target model has in total.</param>
/// <param name="Vertices">How many vertices the replaced segment carries.</param>
/// <param name="Walked">
/// Vertices whose bone was not in the segment's own local table and were
/// carried by the nearest ancestor that was instead — a shoulder pad bound to
/// a wrist the segment never deforms with lands on the forearm, say. Large
/// numbers here mean the segment is the wrong one for this geometry.
/// </param>
/// <param name="Dropped">
/// The most weight any one vertex lost because every bone it named was
/// outside the segment's table and had no mapped ancestor either — the rest
/// pose the segment was authored in has no root joint listed, which does not
/// happen on a game model but can on a hand-built one.
/// </param>
public sealed record PhyreSegmentReplacementReport(
    int Segments, int Vertices, int Walked, float Dropped);

/// <summary>
/// Puts a retargeted mesh into one segment of an existing, skinned model,
/// leaving every other segment exactly as it was.
///
/// <see cref="PhyreModelReplacement"/> rewrites buffers in place and touches
/// nothing else — not the count of segments, not which bones each one can
/// address. That second part matters here specifically: a segment's vertex
/// stream does not hold a hierarchy joint index, it holds a LOCAL index into
/// that segment's own <c>PSkinBoneRemap</c> table, a short list of just the
/// bones its own geometry was ever painted against — measured on ply000, one
/// segment addresses 30 of its 82 joints, another only 4. A hierarchy index
/// written where a local one belongs points at whatever unrelated bone
/// happens to sit at that position in the wrong table, which is silent and
/// ugly rather than a crash.
///
/// So a hierarchy-addressed vertex — what <see cref="PhyreRigTransfer"/> and
/// <see cref="PhyreProximitySkinBinder"/> both produce — is translated here
/// into the chosen segment's own table, walking up the skeleton to the
/// nearest ancestor the segment does address when the exact bone is not one
/// of them. And because <see cref="PhyreModelReplacement"/> needs a mesh for
/// every segment the target has, the segments not being replaced are read
/// back with <see cref="PhyreMeshSourceReader"/> and passed through untouched.
/// </summary>
public static class PhyreSkinnedSegmentReplacement
{
    /// <summary>
    /// Builds the full mesh list <see cref="PhyreModelReplacement.Replace"/>
    /// needs: <paramref name="retargeted"/>'s vertices — hierarchy-addressed,
    /// against <paramref name="skeleton"/> — written into segment
    /// <paramref name="segmentIndex"/> of <paramref name="target"/>, every
    /// other segment carried over unchanged.
    /// </summary>
    public static (PhyreModelSource Source, PhyreSegmentReplacementReport Report) Build(
        CpuModel target,
        CpuSkeleton skeleton,
        int segmentIndex,
        PhyreMeshSource retargeted)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(retargeted);

        var segments = target.Meshes.SelectMany(mesh => mesh.Primitives).ToArray();
        if (segmentIndex < 0 || segmentIndex >= segments.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentIndex),
                $"the model has {segments.Length} segment(s), and {segmentIndex} is not one of them.");
        }

        var parent = skeleton.Joints.Select(joint => joint.ParentIndex).ToArray();
        var meshes = new List<PhyreMeshSource>(segments.Length);
        var walked = 0;
        var dropped = 0f;
        for (var index = 0; index < segments.Length; index++)
        {
            if (index != segmentIndex)
            {
                var kept = PhyreMeshSourceReader.ReadVerbatim(segments[index], $"segment{index}")
                    ?? throw new InvalidOperationException(
                        $"segment {index} of the target model could not be read back whole,"
                        + " so it cannot be left unchanged while segment"
                        + $" {segmentIndex} is replaced.");
                meshes.Add(kept);
                continue;
            }

            var local = LocalTable(segments[index]);
            var translated = retargeted.Vertices
                .Select(vertex => Translate(vertex, local, parent, ref walked, ref dropped))
                .ToArray();
            meshes.Add(retargeted with { Vertices = translated });
        }

        var report = new PhyreSegmentReplacementReport(
            segments.Length, retargeted.Vertices.Count, walked, dropped);
        // PhyreModelReplacement.Replace never reads this list — it only tests
        // whether it is empty, to decide whether a mesh carries skin streams at
        // all. The joints it addresses are the target's own, already written
        // into the file and left untouched; nothing here restates them.
        var placeholderJoints = Enumerable.Range(0, skeleton.Joints.Count)
            .Select(index => new PhyreJointSource(
                $"j{index}", -1, System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity))
            .ToArray();
        var source = new PhyreModelSource(target.AssetId, meshes, placeholderJoints);
        return (source, report);
    }

    /// <summary>Hierarchy joint index to that segment's own local slot.</summary>
    private static Dictionary<int, int> LocalTable(CpuMeshPrimitive segment)
    {
        var table = new Dictionary<int, int>();
        if (segment.SkinBones is not { } bones) return table;
        for (var local = 0; local < bones.Count; local++)
        {
            table.TryAdd(bones[local].HierarchyMatrixIndex, local);
        }
        return table;
    }

    private static PhyreVertexSource Translate(
        PhyreVertexSource vertex,
        IReadOnlyDictionary<int, int> local,
        IReadOnlyList<int> parent,
        ref int walked,
        ref float dropped)
    {
        var joints = new int[4];
        var weights = new float[4];
        var slot = 0;
        var total = 0f;
        var kept = 0f;
        var anyWalked = false;
        for (var influence = 0; influence < vertex.Joints.Length && influence < vertex.Weights.Length; influence++)
        {
            var weight = vertex.Weights[influence];
            if (weight <= 0f) continue;
            total += weight;

            var hierarchy = vertex.Joints[influence];
            var steps = 0;
            while (hierarchy >= 0 && !local.TryGetValue(hierarchy, out _))
            {
                hierarchy = hierarchy < parent.Count ? parent[hierarchy] : -1;
                steps++;
            }
            if (hierarchy < 0 || !local.TryGetValue(hierarchy, out var slotIndex))
            {
                continue;
            }
            if (steps > 0) anyWalked = true;
            if (slot < joints.Length)
            {
                joints[slot] = slotIndex;
                weights[slot] = weight;
                slot++;
            }
            kept += weight;
        }
        if (anyWalked) walked++;
        if (total > 0f) dropped = Math.Max(dropped, (total - kept) / total);
        if (kept > 0f)
        {
            for (var index = 0; index < slot; index++) weights[index] /= kept;
        }
        return vertex with { Joints = joints, Weights = weights };
    }
}
