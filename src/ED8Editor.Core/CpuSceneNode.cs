using System.Numerics;

namespace ED8Editor.Core;

public sealed record CpuSceneNode(
    string Name,
    int ParentIndex,
    Matrix4x4 DefaultLocalTransform);

public sealed record CpuSceneNodePose(
    IReadOnlyList<Matrix4x4> LocalTransforms,
    IReadOnlyList<Matrix4x4> WorldTransforms);
