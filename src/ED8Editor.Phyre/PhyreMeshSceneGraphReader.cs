using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

internal sealed record PhyreMeshSceneEntry(
    string Name,
    CpuMeshPurpose Purpose,
    Matrix4x4 LocalTransform);

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

        var parentMember = FindRequiredMember(cluster, "PNode", "m_parent");
        var localMatrixMember = FindRequiredMember(cluster, "PNode", "m_localMatrix");
        var nodesByWorldMatrix = new Dictionary<uint, uint>();
        var nodeNames = new Dictionary<uint, string>();
        var nodeLocalTransforms = new Dictionary<uint, Matrix4x4>();
        var nodeParents = new Dictionary<uint, uint?>();
        for (uint nodeId = 0; nodeId < nodeGroup.Value.Group.Count; nodeId++)
        {
            var matrix = FindDestinationPointer(cluster, nodeGroup.Value.Index, nodeId, worldMatrixGroup.Value.Index);
            if (matrix is not null)
            {
                nodesByWorldMatrix[matrix.DestinationObjectId] = nodeId;
            }
            nodeLocalTransforms[nodeId] = ReadNodeLocalTransform(
                cluster, nodeGroup.Value.Index, nodeId, localMatrixMember);
            nodeParents[nodeId] = ReadParentNode(
                cluster, nodeGroup.Value.Index, nodeId, parentMember);
            var name = ReadNodeName(cluster, nodeGroup.Value.Index, nodeId);
            if (!string.IsNullOrEmpty(name)) nodeNames[nodeId] = name;
        }
        var nodeWorldTransforms = ResolveWorldTransforms(nodeLocalTransforms, nodeParents);

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
            var localTransform = nodeId is { } transformNode
                ? nodeWorldTransforms[transformNode]
                : Matrix4x4.Identity;
            entries[mesh.DestinationObjectId] = new PhyreMeshSceneEntry(name, purpose, localTransform);
        }
        return entries;
    }

    private static Matrix4x4 ReadNodeLocalTransform(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        PhyreDataMember matrixMember)
    {
        if (matrixMember.TypeName != "PMatrix4" || matrixMember.Size != 16 * sizeof(float))
        {
            throw new InvalidPhyreException(
                $"PNode.m_localMatrix is {matrixMember.TypeName}/{matrixMember.Size} bytes instead of PMatrix4/64 bytes.");
        }

        var source = cluster.GetObject(groupIndex, objectId).Span;
        if ((ulong)matrixMember.ValueOffset + matrixMember.Size > (ulong)source.Length)
        {
            throw new InvalidPhyreException($"PNode {objectId} local matrix exceeds its object data.");
        }
        var data = source.Slice(checked((int)matrixMember.ValueOffset), checked((int)matrixMember.Size));
        var values = new float[16];
        for (var index = 0; index < values.Length; index++)
        {
            var bytes = data.Slice(index * sizeof(float), sizeof(float));
            var bits = cluster.Metadata.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(bytes)
                : BinaryPrimitives.ReadInt32LittleEndian(bytes);
            values[index] = BitConverter.Int32BitsToSingle(bits);
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidPhyreException($"PNode {objectId} local matrix contains a non-finite value.");
            }
        }

        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static uint? ReadParentNode(
        PhyreClusterData cluster,
        int nodeGroupIndex,
        uint nodeId,
        PhyreDataMember parentMember)
    {
        var parent = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == nodeGroupIndex
            && value.SourceObjectId == nodeId
            && value.IsClassDataMember
            && value.SourceMemberId == (uint)parentMember.Index);
        if (parent is null) return null;
        if (parent.UserFixupId is not null || parent.DestinationListIndex != nodeGroupIndex)
        {
            throw new InvalidPhyreException($"PNode {nodeId} has an invalid parent pointer.");
        }
        return parent.DestinationObjectId;
    }

    private static IReadOnlyDictionary<uint, Matrix4x4> ResolveWorldTransforms(
        IReadOnlyDictionary<uint, Matrix4x4> localTransforms,
        IReadOnlyDictionary<uint, uint?> parents)
    {
        var resolved = new Dictionary<uint, Matrix4x4>();
        var visiting = new HashSet<uint>();

        Matrix4x4 Resolve(uint nodeId)
        {
            if (resolved.TryGetValue(nodeId, out var transform)) return transform;
            if (!localTransforms.TryGetValue(nodeId, out var local))
            {
                throw new InvalidPhyreException($"PNode {nodeId} has no local transform.");
            }
            if (!visiting.Add(nodeId))
            {
                throw new InvalidPhyreException($"PNode hierarchy contains a cycle at node {nodeId}.");
            }
            try
            {
                transform = parents.GetValueOrDefault(nodeId) is { } parentId
                    ? local * Resolve(parentId)
                    : local;
                resolved.Add(nodeId, transform);
                return transform;
            }
            finally
            {
                visiting.Remove(nodeId);
            }
        }

        foreach (var nodeId in localTransforms.Keys) Resolve(nodeId);
        return resolved;
    }

    private static PhyreDataMember FindRequiredMember(
        PhyreClusterData cluster,
        string className,
        string memberName)
    {
        var descriptor = cluster.Metadata.Classes.SingleOrDefault(value => value.Name == className)
            ?? throw new InvalidPhyreException($"Missing {className} class descriptor.");
        return descriptor.Members.SingleOrDefault(value => value.Name == memberName)
            ?? throw new InvalidPhyreException($"Missing {className}.{memberName} metadata.");
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
