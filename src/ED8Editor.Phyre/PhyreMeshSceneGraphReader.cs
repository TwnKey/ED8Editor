using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

internal sealed record PhyreMeshSceneEntry(string Name, CpuMeshPurpose Purpose);

internal sealed class PhyreMeshSceneGraphReader
{
    public IReadOnlyDictionary<uint, PhyreMeshSceneEntry> Read(PhyreClusterData cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var meshGroup = FindGroup(cluster, "PMesh");
        var instanceGroup = FindGroup(cluster, "PMeshInstance");
        var nodeGroup = FindGroup(cluster, "PNode");
        var worldMatrixGroup = FindGroup(cluster, "PWorldMatrix");
        if (meshGroup is null || instanceGroup is null || nodeGroup is null || worldMatrixGroup is null)
        {
            return new Dictionary<uint, PhyreMeshSceneEntry>();
        }

        var nodesByWorldMatrix = new Dictionary<uint, uint>();
        var nodeNames = new Dictionary<uint, string>();
        for (uint nodeId = 0; nodeId < nodeGroup.Value.Group.Count; nodeId++)
        {
            var matrix = FindDestinationPointer(cluster, nodeGroup.Value.Index, nodeId, worldMatrixGroup.Value.Index);
            if (matrix is not null)
            {
                nodesByWorldMatrix[matrix.DestinationObjectId] = nodeId;
            }
            var name = ReadNodeName(cluster, nodeGroup.Value.Index, nodeId);
            if (!string.IsNullOrEmpty(name)) nodeNames[nodeId] = name;
        }

        var collisionNodes = ReadCollisionTargetNodes(cluster, nodeGroup.Value.Index);
        var entries = new Dictionary<uint, PhyreMeshSceneEntry>();
        for (uint instanceId = 0; instanceId < instanceGroup.Value.Group.Count; instanceId++)
        {
            var mesh = FindDestinationPointer(cluster, instanceGroup.Value.Index, instanceId, meshGroup.Value.Index);
            var matrix = FindDestinationPointer(cluster, instanceGroup.Value.Index, instanceId, worldMatrixGroup.Value.Index);
            if (mesh is null) continue;
            var nodeId = matrix is not null
                && nodesByWorldMatrix.TryGetValue(matrix.DestinationObjectId, out var referencedNode)
                    ? referencedNode
                    : (uint?)null;
            var name = nodeId is { } namedNode && nodeNames.TryGetValue(namedNode, out var nodeName)
                ? nodeName
                : $"mesh:{mesh.DestinationObjectId}";
            var purpose = nodeId is { } collisionNode && collisionNodes.Contains(collisionNode)
                ? CpuMeshPurpose.Collision
                : CpuMeshPurpose.Render;
            entries[mesh.DestinationObjectId] = new PhyreMeshSceneEntry(name, purpose);
        }
        return entries;
    }

    private static HashSet<uint> ReadCollisionTargetNodes(PhyreClusterData cluster, int nodeGroupIndex)
    {
        var rigidBodyGroup = FindGroup(cluster, "PPhysicsRigidBody");
        var targets = new HashSet<uint>();
        if (rigidBodyGroup is null) return targets;
        for (uint rigidBodyId = 0; rigidBodyId < rigidBodyGroup.Value.Group.Count; rigidBodyId++)
        {
            var target = FindDestinationPointer(cluster, rigidBodyGroup.Value.Index, rigidBodyId, nodeGroupIndex);
            if (target is not null)
            {
                targets.Add(target.DestinationObjectId);
            }
        }
        return targets;
    }

    private static string? ReadNodeName(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var fixup = cluster.Fixups.Arrays.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && !value.IsClassDataMember);
        if (fixup is null) return null;
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        if (fixup.Offset >= group.ArraysSize) return null;
        var data = cluster.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = data.IndexOf((byte)0);
        if (zero < 0) throw new InvalidPhyreException($"PNode {objectId} name is not zero terminated.");
        return Encoding.ASCII.GetString(data[..zero]);
    }

    private static PhyrePointerFixup? FindDestinationPointer(
        PhyreClusterData cluster, int groupIndex, uint objectId, int destinationGroupIndex)
    {
        var pointers = cluster.Fixups.Pointers.Where(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && value.UserFixupId is null
            && value.DestinationListIndex == destinationGroupIndex).ToArray();
        if (pointers.Length == 0) return null;
        var destinationObjectId = pointers[0].DestinationObjectId;
        if (pointers.Any(value => value.DestinationObjectId != destinationObjectId))
        {
            throw new InvalidPhyreException(
                $"{cluster.Metadata.InstanceGroups[groupIndex].ClassName} {objectId} has ambiguous pointers to "
                + $"{cluster.Metadata.InstanceGroups[destinationGroupIndex].ClassName}.");
        }
        return pointers[0];
    }

    private static (int Index, PhyreInstanceGroup Group)? FindGroup(PhyreClusterData cluster, string className)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            var group = cluster.Metadata.InstanceGroups[index];
            if (group.ClassName == className) return (index, group);
        }
        return null;
    }
}
