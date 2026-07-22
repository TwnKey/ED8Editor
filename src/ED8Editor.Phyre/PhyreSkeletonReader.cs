using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreSkeletonReader
{
    public CpuSkeleton? Read(PhyreClusterData cluster, int meshGroupIndex, uint meshObjectId)
    {
        var mesh = cluster.GetObject(meshGroupIndex, meshObjectId).Span;
        var inverseBindCount = ReadUInt32(mesh, 0x08, cluster.Metadata.IsBigEndian);
        var boundsCount = ReadUInt32(mesh, 0x10, cluster.Metadata.IsBigEndian);
        var poseCount = ReadUInt32(mesh, 0x18, cluster.Metadata.IsBigEndian);
        var nameCount = ReadUInt32(mesh, 0x20, cluster.Metadata.IsBigEndian);
        var parentCount = ReadUInt32(mesh, 0x28, cluster.Metadata.IsBigEndian);
        if (inverseBindCount == 0 && boundsCount == 0 && poseCount == 0 && nameCount == 0 && parentCount == 0)
            return null;
        if (poseCount == 0 || nameCount != poseCount || parentCount != poseCount)
            throw new InvalidPhyreException(
                $"PMesh {meshObjectId} has inconsistent hierarchy arrays: poses={poseCount}, names={nameCount}, parents={parentCount}.");
        if (boundsCount != inverseBindCount)
            throw new InvalidPhyreException(
                $"PMesh {meshObjectId} has {inverseBindCount} inverse-bind matrices for {boundsCount} skeleton bounds.");

        var matrixGroup = FindRequiredGroup(cluster, "PMatrix4");
        var stringGroup = FindRequiredGroup(cluster, "PString");
        var boundsGroup = FindRequiredGroup(cluster, "PSkeletonJointBounds");
        var inverseBindPointer = RequirePointer(cluster, meshGroupIndex, meshObjectId, 0x0c);
        var boundsPointer = RequirePointer(cluster, meshGroupIndex, meshObjectId, 0x14);
        var posePointer = RequirePointer(cluster, meshGroupIndex, meshObjectId, 0x1c);
        var namesPointer = RequirePointer(cluster, meshGroupIndex, meshObjectId, 0x24);
        ValidateRange(inverseBindPointer, matrixGroup, inverseBindCount, "inverse-bind matrices");
        ValidateRange(boundsPointer, boundsGroup, boundsCount, "skeleton bounds");
        ValidateRange(posePointer, matrixGroup, poseCount, "default poses");
        ValidateRange(namesPointer, stringGroup, nameCount, "matrix names");

        var parentFixup = cluster.Fixups.Arrays.SingleOrDefault(value =>
            value.SourceListIndex == meshGroupIndex && value.SourceObjectId == meshObjectId
            && !value.IsClassDataMember && value.SourceOffset == 0x2c)
            ?? throw new InvalidPhyreException($"PMesh {meshObjectId} has no hierarchy parent array.");
        if (parentFixup.Count < parentCount)
            throw new InvalidPhyreException($"PMesh {meshObjectId} hierarchy parent array is truncated.");
        var parentBytes = cluster.GetArrayData(meshGroupIndex, parentFixup.Offset, checked(parentCount * sizeof(int))).Span;

        var joints = new CpuSkeletonJoint[checked((int)poseCount)];
        for (uint index = 0; index < poseCount; index++)
        {
            var parent = ReadInt32(parentBytes, checked((int)index * sizeof(int)), cluster.Metadata.IsBigEndian);
            if (parent < -1 || parent >= poseCount || parent == index)
                throw new InvalidPhyreException($"Skeleton joint {index} has invalid parent {parent}.");
            joints[index] = new CpuSkeletonJoint(
                ReadString(cluster, stringGroup.Index, checked(namesPointer.DestinationObjectId + index)),
                parent,
                ReadMatrix(cluster.GetObject(matrixGroup.Index, checked(posePointer.DestinationObjectId + index)).Span,
                    cluster.Metadata.IsBigEndian));
        }

        var inverseBinds = new Matrix4x4[checked((int)inverseBindCount)];
        var skeletonToHierarchy = new int[checked((int)inverseBindCount)];
        for (uint index = 0; index < inverseBindCount; index++)
        {
            inverseBinds[index] = ReadMatrix(
                cluster.GetObject(matrixGroup.Index, checked(inverseBindPointer.DestinationObjectId + index)).Span,
                cluster.Metadata.IsBigEndian);
            var bounds = cluster.GetObject(boundsGroup.Index, checked(boundsPointer.DestinationObjectId + index)).Span;
            var hierarchyIndex = checked((int)ReadUInt32(bounds, 0x0c, cluster.Metadata.IsBigEndian));
            if ((uint)hierarchyIndex >= poseCount)
                throw new InvalidPhyreException($"Skeleton matrix {index} maps to missing hierarchy joint {hierarchyIndex}.");
            skeletonToHierarchy[index] = hierarchyIndex;
        }
        return new CpuSkeleton(joints, inverseBinds, skeletonToHierarchy);
    }

    private static string ReadString(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var fixup = cluster.Fixups.Arrays.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && SourceMatches(cluster, value, 0));
        // PString represents the empty string with a null m_buffer pointer.
        if (fixup is null) return string.Empty;
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        var bytes = cluster.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = bytes.IndexOf((byte)0);
        if (zero < 0) throw new InvalidPhyreException($"PString {objectId} is not zero terminated.");
        return Encoding.UTF8.GetString(bytes[..zero]);
    }

    private static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> source, bool bigEndian)
    {
        Span<float> values = stackalloc float[16];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadSingle(source, index * sizeof(float), bigEndian);
            if (!float.IsFinite(values[index])) throw new InvalidPhyreException("Skeleton matrix contains a non-finite value.");
        }
        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static void ValidateRange(PhyrePointerFixup pointer, (int Index, PhyreInstanceGroup Group) group,
        uint count, string label)
    {
        if (pointer.UserFixupId is not null || pointer.DestinationListIndex != group.Index
            || (ulong)pointer.DestinationObjectId + count > group.Group.Count)
            throw new InvalidPhyreException($"Invalid {label} pointer.");
    }

    private static PhyrePointerFixup RequirePointer(PhyreClusterData cluster, int groupIndex, uint objectId, uint offset)
        => cluster.Fixups.Pointers.SingleOrDefault(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId && SourceMatches(cluster, value, offset))
            ?? throw new InvalidPhyreException($"Missing skeleton pointer at {groupIndex}:{objectId}+0x{offset:X}.");

    private static (int Index, PhyreInstanceGroup Group) FindRequiredGroup(PhyreClusterData cluster, string className)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
            if (cluster.Metadata.InstanceGroups[index].ClassName == className)
                return (index, cluster.Metadata.InstanceGroups[index]);
        throw new InvalidPhyreException($"Missing {className} instance group.");
    }

    private static bool SourceMatches(PhyreClusterData cluster, PhyreFixup fixup, uint offset)
    {
        if (!fixup.IsClassDataMember) return fixup.SourceOffset == offset;
        var member = cluster.Metadata.Classes.SelectMany(value => value.Members)
            .SingleOrDefault(value => value.Index == fixup.SourceMemberId);
        return member is not null && member.ValueOffset == offset;
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(source[offset..]) : BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(source[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static float ReadSingle(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        => BitConverter.Int32BitsToSingle(ReadInt32(source, offset, bigEndian));
}
