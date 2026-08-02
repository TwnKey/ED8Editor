using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One physics field, and where it sits in a cluster.</summary>
public sealed record PhyrePhysicsField(string What, long Offset, int Size);

/// <summary>What a repair found and did.</summary>
public sealed record PhyrePhysicsRepairResult(
    byte[] Cluster,
    IReadOnlyList<PhyrePhysicsField> Fields,
    int Restored,
    IReadOnlyList<string> Problems);

/// <summary>
/// Puts back the physics a round trip through an exchange format loses.
///
/// A map's collision does not survive being extracted to glTF and reinserted,
/// and the loss happens at extraction: <c>ed8pkg2gltf</c> writes a physics JSON
/// that omits <c>m_collisionGroup</c>, <c>m_enabled</c>, <c>m_rigidBodyType</c>
/// and the shape's <c>m_type</c> — its own comments list them — and COLLADA has
/// nowhere to carry them, so the compiler fills in defaults. Measured on
/// <c>M_R0510</c> from CS2, the file states <c>m_collisionGroup = 1</c>,
/// <c>m_enabled = 1</c>, <c>m_rigidBodyType = 0</c> and <c>m_type = 7</c>; any of
/// those coming back zero is enough for the collision to stop working, and
/// <c>m_type</c> is what declares the shape a mesh at all.
///
/// So the values are written back where the schema says they live. Nothing is
/// decoded and re-encoded: the rest of the file is left exactly as it came.
///
/// This only works while the rebuilt file keeps the reference's object layout —
/// same classes, same counts. That is checked, and a mismatch is refused rather
/// than written into the wrong place.
/// </summary>
public static class PhyrePhysicsRepair
{
    private static readonly (string Class, string Member)[] Wanted =
    {
        ("PPhysicsRigidBody", "m_collisionGroup"),
        ("PPhysicsRigidBody", "m_enabled"),
        ("PPhysicsRigidBody", "m_rigidBodyType"),
        ("PPhysicsMesh", "m_type"),
        ("PPhysicsMesh", "m_hollow"),
    };

    /// <summary>Where a cluster keeps the fields a round trip drops.</summary>
    public static IReadOnlyList<PhyrePhysicsField> Fields(PhyreClusterData cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var classes = cluster.Metadata.Classes.ToList();
        var found = new List<PhyrePhysicsField>();

        var groupAt = cluster.Metadata.Header.ObjectDataOffset;
        foreach (var group in cluster.Metadata.InstanceGroups)
        {
            var descriptor = classes.FirstOrDefault(value => value.Name == group.ClassName);
            if (descriptor is not null && group.Count != 0)
            {
                var each = (int)(group.ObjectsSize / group.Count);
                foreach (var (className, member) in Wanted)
                {
                    if (group.ClassName != className) continue;
                    var field = PhyreObjectWriter.Chain(descriptor, classes)
                        .FirstOrDefault(value => value.Name == member);
                    if (field is null) continue;
                    for (uint id = 0; id < group.Count; id++)
                    {
                        found.Add(new PhyrePhysicsField(
                            $"{className}[{id}].{member}",
                            groupAt + id * each + field.ValueOffset,
                            (int)field.Size));
                    }
                }
            }
            groupAt += group.Size;
        }
        return found;
    }

    /// <summary>
    /// Writes <paramref name="reference"/>'s physics values into
    /// <paramref name="rebuilt"/>. The reference is the file the game shipped;
    /// the rebuilt one is what came back from a round trip.
    /// </summary>
    public static PhyrePhysicsRepairResult Repair(
        ReadOnlyMemory<byte> reference,
        ReadOnlyMemory<byte> rebuilt)
    {
        var referenceData = new PhyreClusterReader().Read(reference);
        var rebuiltData = new PhyreClusterReader().Read(rebuilt);
        var fields = Fields(referenceData);

        var problems = new List<string>();
        var here = referenceData.Metadata.InstanceGroups;
        var there = rebuiltData.Metadata.InstanceGroups;
        if (here.Count != there.Count)
        {
            problems.Add($"the rebuilt file lists {there.Count} instance groups against {here.Count}");
        }
        else
        {
            for (var index = 0; index < here.Count; index++)
            {
                if (here[index].ClassName == there[index].ClassName
                    && here[index].Count == there[index].Count
                    && here[index].ObjectsSize == there[index].ObjectsSize)
                {
                    continue;
                }
                problems.Add(
                    $"group {index} is {there[index].ClassName} x{there[index].Count} in the rebuilt"
                    + $" file and {here[index].ClassName} x{here[index].Count} in the reference");
                break;
            }
        }
        if (problems.Count != 0)
        {
            return new PhyrePhysicsRepairResult(rebuilt.ToArray(), fields, 0, problems);
        }

        var output = rebuilt.ToArray();
        var restored = 0;
        foreach (var field in fields)
        {
            if (field.Offset + field.Size > output.Length) continue;
            var was = output.AsSpan((int)field.Offset, field.Size);
            var wanted = reference.Span.Slice((int)field.Offset, field.Size);
            if (was.SequenceEqual(wanted)) continue;
            wanted.CopyTo(was);
            restored++;
        }
        return new PhyrePhysicsRepairResult(output, fields, restored, problems);
    }
}
