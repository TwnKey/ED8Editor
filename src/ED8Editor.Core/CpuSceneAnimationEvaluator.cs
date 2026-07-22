using System.Numerics;

namespace ED8Editor.Core;

public sealed class CpuSceneAnimationEvaluator
{
    public CpuSceneNodePose Evaluate(IReadOnlyList<CpuSceneNode> nodes, CpuAnimationClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(clip);
        var translations = new Vector3[nodes.Count];
        var rotations = new Quaternion[nodes.Count];
        var scales = new Vector3[nodes.Count];
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            if (!Matrix4x4.Decompose(nodes[index].DefaultLocalTransform,
                    out scales[index], out rotations[index], out translations[index]))
                throw new InvalidDataException($"Default transform for scene node '{nodes[index].Name}' cannot be decomposed.");
            if (nodes[index].Name.Length != 0 && !indices.TryAdd(nodes[index].Name, index))
                throw new InvalidDataException($"Scene graph contains duplicate node name '{nodes[index].Name}'.");
        }
        foreach (var channel in clip.Channels)
        {
            if (!indices.TryGetValue(channel.TargetName, out var index))
                throw new InvalidDataException(
                    $"Animation '{clip.Name}' targets node '{channel.TargetName}', which is absent from the model scene graph.");
            var value = CpuAnimationSampler.Sample(channel, time);
            switch (channel.Path)
            {
                case CpuAnimationPath.Translation: translations[index] = new Vector3(value.X, value.Y, value.Z); break;
                case CpuAnimationPath.Rotation:
                    rotations[index] = Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
                    break;
                case CpuAnimationPath.Scale: scales[index] = new Vector3(value.X, value.Y, value.Z); break;
            }
        }
        var local = new Matrix4x4[nodes.Count];
        var world = new Matrix4x4[nodes.Count];
        var resolved = new bool[nodes.Count];
        var visiting = new bool[nodes.Count];
        for (var index = 0; index < nodes.Count; index++)
            local[index] = Matrix4x4.CreateScale(scales[index])
                * Matrix4x4.CreateFromQuaternion(rotations[index])
                * Matrix4x4.CreateTranslation(translations[index]);
        Matrix4x4 Resolve(int index)
        {
            if (resolved[index]) return world[index];
            if (visiting[index]) throw new InvalidDataException($"Scene hierarchy contains a cycle at node {index}.");
            visiting[index] = true;
            var parent = nodes[index].ParentIndex;
            world[index] = parent >= 0 ? local[index] * Resolve(parent) : local[index];
            visiting[index] = false;
            resolved[index] = true;
            return world[index];
        }
        for (var index = 0; index < nodes.Count; index++) Resolve(index);
        return new CpuSceneNodePose(local, world);
    }
}

internal static class CpuAnimationSampler
{
    public static Vector4 Sample(CpuAnimationChannel channel, float time)
    {
        if (channel.Times.Count == 0 || channel.Values.Count != channel.Times.Count)
            throw new InvalidDataException($"Animation channel '{channel.TargetName}' has inconsistent key data.");
        if (channel.Times.Count == 1 || time <= channel.Times[0]) return channel.Values[0];
        var last = channel.Times.Count - 1;
        if (time >= channel.Times[last]) return channel.Values[last];
        var upper = 1;
        while (upper < channel.Times.Count && channel.Times[upper] < time) upper++;
        var lower = upper - 1;
        if (channel.Interpolation == CpuAnimationInterpolation.Step) return channel.Values[lower];
        var span = channel.Times[upper] - channel.Times[lower];
        var amount = span > 0 ? (time - channel.Times[lower]) / span : 0f;
        if (channel.Path != CpuAnimationPath.Rotation)
            return Vector4.Lerp(channel.Values[lower], channel.Values[upper], amount);
        var aValue = channel.Values[lower];
        var bValue = channel.Values[upper];
        var result = Quaternion.Normalize(Quaternion.Slerp(
            Quaternion.Normalize(new Quaternion(aValue.X, aValue.Y, aValue.Z, aValue.W)),
            Quaternion.Normalize(new Quaternion(bValue.X, bValue.Y, bValue.Z, bValue.W)), amount));
        return new Vector4(result.X, result.Y, result.Z, result.W);
    }
}
