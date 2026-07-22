using System.Numerics;

namespace ED8Editor.Core;

public sealed record CpuSkeletonJoint(
    string Name,
    int ParentIndex,
    Matrix4x4 DefaultLocalTransform);

public sealed record CpuSkeleton(
    IReadOnlyList<CpuSkeletonJoint> Joints,
    IReadOnlyList<Matrix4x4> InverseBindMatrices,
    IReadOnlyList<int> SkeletonToHierarchy);

public sealed record CpuSkinBoneRemap(
    int HierarchyMatrixIndex,
    int SkeletonMatrixIndex);
