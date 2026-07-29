using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>A rig re-expressed in the game's frame, and what could not be.</summary>
/// <param name="Joints">
/// The conformed joints, carrying the game's names. Their order is the one the
/// rig had, so anything that indexes into it — skin weights above all — keeps
/// pointing at the same bone.
/// </param>
/// <param name="Unmapped">
/// Names of rig joints no mapping covered. They keep their own frame and their
/// own name, so they simply receive no animation.
/// </param>
/// <param name="Missing">
/// Names the game's skeleton has that the rig does not. Clips will drive nothing
/// for these — twist bones, most often — and the mesh will show it at the elbows
/// and forearms if they matter.
/// </param>
public sealed record PhyreConformedSkeleton(
    IReadOnlyList<PhyreJointSource> Joints,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Missing);

/// <summary>
/// Re-expresses an imported rig in the frame the game's animations were authored
/// in, so those animations drive it directly.
///
/// The reason this works at all was measured rather than assumed: a clip's
/// channels name their target, and every target of ply000's clips resolves to a
/// joint of its skeleton — 65 of 65 on one, 72 of 73 on another, the odd one out
/// being a locator. So a rig whose bones carry the game's names is driven by
/// every clip the game ships, and no animation ever has to be written.
///
/// What stands in the way is that a rotation only means something in a frame. The
/// game's clips hold local rotations expressed in the rest orientation of its own
/// skeleton; handing them to a rig whose bones rest along different axes gives
/// nonsense. So each mapped bone is given the game's rest orientation while
/// keeping its own position — proportions stay the author's, since a rotation
/// does not know how long a bone is — and the bind matrices are corrected so the
/// mesh does not move a millimetre while this happens.
///
/// That correction is exact, not approximate. Skinning evaluates
/// <c>world · inverseBind</c>; giving a joint a new rest world transform
/// <c>W'</c> and setting <c>inverseBind' = W'⁻¹ · W · inverseBind</c> leaves that
/// product unchanged, which is what <see cref="RestPoseError"/> measures.
/// </summary>
public static class PhyreSkeletonConform
{
    /// <summary>
    /// Conforms <paramref name="rig"/> to <paramref name="game"/>.
    /// <paramref name="mapping"/> reads rig bone name to game bone name; a bone
    /// it does not name is left alone rather than guessed at.
    /// </summary>
    public static PhyreConformedSkeleton Conform(
        IReadOnlyList<PhyreJointSource> rig,
        CpuSkeleton game,
        IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(mapping);

        var rigWorld = World(rig.Select(joint => (joint.ParentIndex, joint.LocalTransform)).ToArray());
        var gameWorld = World(game.Joints
            .Select(joint => (joint.ParentIndex, joint.DefaultLocalTransform)).ToArray());
        var gameByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < game.Joints.Count; index++)
        {
            gameByName[game.Joints[index].Name] = index;
        }

        // Every joint's new rest transform: its own place, the game's axes.
        var conformedWorld = new Matrix4x4[rig.Count];
        var names = new string[rig.Count];
        var unmapped = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rig.Count; index++)
        {
            names[index] = rig[index].Name;
            conformedWorld[index] = rigWorld[index];
            if (!mapping.TryGetValue(rig[index].Name, out var target)
                || !gameByName.TryGetValue(target, out var at))
            {
                unmapped.Add(rig[index].Name);
                continue;
            }
            names[index] = target;
            taken.Add(target);
            conformedWorld[index] = Compose(Rotation(gameWorld[at]), Translation(rigWorld[index]));
        }

        // Local transforms again, from the new rest world transforms, and bind
        // matrices corrected so the skin sits exactly where it did.
        var joints = new PhyreJointSource[rig.Count];
        for (var index = 0; index < rig.Count; index++)
        {
            var parent = rig[index].ParentIndex;
            var local = parent < 0
                ? conformedWorld[index]
                : Multiply(Invert(conformedWorld[parent]), conformedWorld[index]);
            var bind = Multiply(
                Multiply(Invert(conformedWorld[index]), rigWorld[index]),
                rig[index].InverseBindTransform);
            joints[index] = new PhyreJointSource(names[index], parent, local, bind);
        }

        var missing = game.Joints
            .Select(joint => joint.Name)
            .Where(name => name.Length != 0 && !taken.Contains(name))
            .ToArray();
        return new PhyreConformedSkeleton(joints, unmapped, missing);
    }

    /// <summary>
    /// How far the conformed rig moves the skin at rest. It has to be zero, up to
    /// what floating point allows: the whole correction is worthless if the mesh
    /// shifts while being re-expressed.
    /// </summary>
    public static float RestPoseError(
        IReadOnlyList<PhyreJointSource> rig,
        PhyreConformedSkeleton conformed)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(conformed);

        var before = World(rig.Select(joint => (joint.ParentIndex, joint.LocalTransform)).ToArray());
        var after = World(conformed.Joints
            .Select(joint => (joint.ParentIndex, joint.LocalTransform)).ToArray());
        var worst = 0f;
        for (var index = 0; index < rig.Count; index++)
        {
            var was = Multiply(before[index], rig[index].InverseBindTransform);
            var now = Multiply(after[index], conformed.Joints[index].InverseBindTransform);
            worst = Math.Max(worst, Difference(was, now));
        }
        return worst;
    }

    private static Matrix4x4[] World(IReadOnlyList<(int Parent, Matrix4x4 Local)> joints)
    {
        var world = new Matrix4x4[joints.Count];
        for (var index = 0; index < joints.Count; index++)
        {
            var (parent, local) = joints[index];
            world[index] = parent < 0 || parent >= index
                ? local
                : Multiply(world[parent], local);
        }
        return world;
    }

    /// <summary>The rotation of a transform, with any scale divided out.</summary>
    private static Matrix4x4 Rotation(Matrix4x4 value)
    {
        var x = Vector3.Normalize(new Vector3(value.M11, value.M12, value.M13));
        var y = Vector3.Normalize(new Vector3(value.M21, value.M22, value.M23));
        var z = Vector3.Normalize(new Vector3(value.M31, value.M32, value.M33));
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
    }

    private static Vector3 Translation(Matrix4x4 value) => new(value.M41, value.M42, value.M43);

    private static Matrix4x4 Compose(Matrix4x4 rotation, Vector3 translation)
    {
        rotation.M41 = translation.X;
        rotation.M42 = translation.Y;
        rotation.M43 = translation.Z;
        return rotation;
    }

    private static Matrix4x4 Multiply(Matrix4x4 left, Matrix4x4 right)
        => Matrix4x4.Multiply(left, right);

    private static Matrix4x4 Invert(Matrix4x4 value)
        => Matrix4x4.Invert(value, out var inverted)
            ? inverted
            : throw new InvalidOperationException(
                "A joint's rest transform cannot be inverted; the rig has a bone of zero scale.");

    private static float Difference(Matrix4x4 left, Matrix4x4 right)
    {
        var worst = 0f;
        worst = Math.Max(worst, Math.Abs(left.M11 - right.M11));
        worst = Math.Max(worst, Math.Abs(left.M22 - right.M22));
        worst = Math.Max(worst, Math.Abs(left.M33 - right.M33));
        worst = Math.Max(worst, Math.Abs(left.M41 - right.M41));
        worst = Math.Max(worst, Math.Abs(left.M42 - right.M42));
        worst = Math.Max(worst, Math.Abs(left.M43 - right.M43));
        return worst;
    }
}
