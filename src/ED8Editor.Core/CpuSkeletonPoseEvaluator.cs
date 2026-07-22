using System.Numerics;

namespace ED8Editor.Core;

public sealed record CpuSkeletonPose(
    IReadOnlyList<Matrix4x4> LocalTransforms,
    IReadOnlyList<Matrix4x4> WorldTransforms,
    IReadOnlyList<Matrix4x4> SkinMatrices);

public sealed class CpuSkeletonPoseEvaluator
{
    public CpuSkeletonPose Evaluate(CpuSkeleton skeleton, CpuAnimationClip? clip, float time)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var jointCount = skeleton.Joints.Count;
        var translations = new Vector3[jointCount];
        var rotations = new Quaternion[jointCount];
        var scales = new Vector3[jointCount];
        var jointIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < jointCount; index++)
        {
            var joint = skeleton.Joints[index];
            if (!Matrix4x4.Decompose(joint.DefaultLocalTransform, out scales[index], out rotations[index], out translations[index]))
                throw new InvalidDataException($"Default transform for skeleton joint '{joint.Name}' cannot be decomposed.");
            if (joint.Name.Length != 0 && !jointIndices.TryAdd(joint.Name, index))
                throw new InvalidDataException($"Skeleton contains duplicate joint name '{joint.Name}'.");
        }

        if (clip is not null)
        {
            foreach (var channel in clip.Channels)
            {
                if (!jointIndices.TryGetValue(channel.TargetName, out var jointIndex))
                    throw new InvalidDataException(
                        $"Animation '{clip.Name}' targets joint '{channel.TargetName}', which is absent from the model skeleton.");
                var value = CpuAnimationSampler.Sample(channel, time);
                switch (channel.Path)
                {
                    case CpuAnimationPath.Translation:
                        translations[jointIndex] = new Vector3(value.X, value.Y, value.Z);
                        break;
                    case CpuAnimationPath.Rotation:
                        rotations[jointIndex] = Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
                        break;
                    case CpuAnimationPath.Scale:
                        scales[jointIndex] = new Vector3(value.X, value.Y, value.Z);
                        break;
                    default:
                        throw new InvalidDataException($"Animation '{clip.Name}' contains unsupported path {channel.Path}.");
                }
            }
        }

        var local = new Matrix4x4[jointCount];
        var world = new Matrix4x4[jointCount];
        var resolved = new bool[jointCount];
        var visiting = new bool[jointCount];
        for (var index = 0; index < jointCount; index++)
            local[index] = Matrix4x4.CreateScale(scales[index])
                * Matrix4x4.CreateFromQuaternion(rotations[index])
                * Matrix4x4.CreateTranslation(translations[index]);

        Matrix4x4 ResolveWorld(int index)
        {
            if (resolved[index]) return world[index];
            if (visiting[index]) throw new InvalidDataException($"Skeleton hierarchy contains a cycle at joint {index}.");
            visiting[index] = true;
            var parent = skeleton.Joints[index].ParentIndex;
            world[index] = parent >= 0 ? local[index] * ResolveWorld(parent) : local[index];
            visiting[index] = false;
            resolved[index] = true;
            return world[index];
        }
        for (var index = 0; index < jointCount; index++) ResolveWorld(index);

        if (skeleton.InverseBindMatrices.Count != skeleton.SkeletonToHierarchy.Count)
            throw new InvalidDataException("Skeleton inverse-bind and hierarchy-map counts differ.");
        var skin = new Matrix4x4[skeleton.InverseBindMatrices.Count];
        for (var index = 0; index < skin.Length; index++)
        {
            var hierarchyIndex = skeleton.SkeletonToHierarchy[index];
            if ((uint)hierarchyIndex >= world.Length)
                throw new InvalidDataException($"Skin joint {index} maps outside the skeleton hierarchy.");
            skin[index] = skeleton.InverseBindMatrices[index] * world[hierarchyIndex];
        }
        return new CpuSkeletonPose(local, world, skin);
    }

}
