using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>A model moved onto the game's skeleton, and what the move cost.</summary>
/// <param name="Placed">
/// Game joints whose position came from the rig, because a mapping named them.
/// </param>
/// <param name="Derived">
/// Game joints the rig has nothing for — twist bones, attachment points — placed
/// from their own rest offset, scaled. They will follow their parent and deform
/// nothing, which is right for an attachment point and visible at an elbow.
/// </param>
/// <param name="Merged">
/// Game joints that several rig bones fed at once. Their weights were summed,
/// which is a guess about how the two rigs line up rather than a translation of
/// it — these are the ones worth looking at first.
/// </param>
/// <param name="DroppedWeight">
/// The most weight any single vertex lost to the four-joint limit, as a
/// fraction. Zero means nothing was lost anywhere.
/// </param>
public sealed record PhyreRigTransferReport(
    IReadOnlyList<string> Placed,
    IReadOnlyList<string> Derived,
    IReadOnlyList<string> Merged,
    float DroppedWeight);

/// <summary>
/// Moves an imported model onto the game's own skeleton, keeping the weights its
/// author painted.
///
/// This is what a modder does by hand — delete the foreign skeleton and weight
/// groups, re-rig to a Falcom skeleton, reassign weights — with the two
/// mechanical thirds done by computation.
///
/// Ending up on the game's real skeleton is not a preference. That skeleton
/// carries bones no foreign rig will ever have (<c>Bag01</c>, <c>L_cat_point</c>,
/// <c>BS01</c>, the attachment locators); keeping the author's own skeleton and
/// merely renaming its bones leaves those out, and everything that hangs off
/// them breaks.
///
/// Two observations make the automation possible:
///
/// <list type="number">
/// <item>The foreign rig already says where each bone belongs in this mesh. So
/// the game's skeleton does not have to be fitted into the geometry by search —
/// each mapped joint simply takes the position of its counterpart, and the
/// proportions of the author's character are reproduced exactly.</item>
/// <item>Weights do not have to be repainted, only re-addressed: a vertex holds
/// weights against foreign bones, the mapping sends those bones to game bones,
/// and the weights are summed per target.</item>
/// </list>
///
/// Orientations are the game's throughout, never the rig's — that is the point.
/// A clip's rotations are expressed in the rest frame its animators worked in,
/// so the fitted skeleton keeps that frame and changes only where the joints sit.
/// </summary>
public static class PhyreRigTransfer
{
    private const int JointsPerVertex = 4;

    /// <summary>
    /// Rebuilds <paramref name="model"/> on <paramref name="game"/>.
    /// <paramref name="mapping"/> reads rig bone name to game bone name.
    /// </summary>
    public static (PhyreModelSource Model, PhyreRigTransferReport Report) Apply(
        PhyreModelSource model,
        CpuSkeleton game,
        IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(mapping);

        var rigWorld = WorldTransforms(
            model.Joints.Select(joint => (joint.ParentIndex, joint.LocalTransform)).ToArray());
        var gameWorld = WorldTransforms(
            game.Joints.Select(joint => (joint.ParentIndex, joint.DefaultLocalTransform)).ToArray());

        // Which rig bone feeds which game joint. Several may feed one: two spine
        // bones onto one, when the chains are not the same length.
        var feeders = new List<int>[game.Joints.Count];
        var gameByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < game.Joints.Count; index++)
        {
            gameByName[game.Joints[index].Name] = index;
            feeders[index] = new List<int>();
        }
        for (var index = 0; index < model.Joints.Count; index++)
        {
            if (mapping.TryGetValue(model.Joints[index].Name, out var target)
                && gameByName.TryGetValue(target, out var at))
            {
                feeders[at].Add(index);
            }
        }

        var scale = Scale(rigWorld, gameWorld, feeders);
        var fitted = Fit(game, gameWorld, rigWorld, feeders, scale, out var placed, out var derived);
        var joints = new PhyreJointSource[game.Joints.Count];
        for (var index = 0; index < game.Joints.Count; index++)
        {
            var parent = game.Joints[index].ParentIndex;
            var local = parent < 0
                ? fitted[index]
                : Matrix4x4.Multiply(Invert(fitted[parent]), fitted[index]);
            // A freshly bound mesh binds at rest, so the bind matrix is simply
            // the inverse of where the joint rests.
            joints[index] = new PhyreJointSource(
                game.Joints[index].Name, parent, local, Invert(fitted[index]));
        }

        // Where each rig bone's weight now goes.
        var destination = new int[model.Joints.Count];
        Array.Fill(destination, -1);
        for (var index = 0; index < game.Joints.Count; index++)
        {
            foreach (var feeder in feeders[index]) destination[feeder] = index;
        }

        var dropped = 0f;
        var meshes = model.Meshes
            .Select(mesh => new PhyreMeshSource(
                mesh.MaterialName,
                mesh.Vertices.Select(vertex => Rebind(vertex, destination, ref dropped)).ToArray(),
                mesh.Indices))
            .ToArray();

        var merged = new List<string>();
        for (var index = 0; index < game.Joints.Count; index++)
        {
            if (feeders[index].Count > 1) merged.Add(game.Joints[index].Name);
        }

        return (
            new PhyreModelSource(model.AssetName, meshes, joints),
            new PhyreRigTransferReport(placed, derived, merged, dropped));
    }

    /// <summary>
    /// Where every game joint ends up. A mapped one takes its counterpart's
    /// place; an unmapped one keeps the offset it rests at, scaled to this
    /// character, so a twist bone stays between the joints it belongs to.
    /// </summary>
    private static Matrix4x4[] Fit(
        CpuSkeleton game,
        IReadOnlyList<Matrix4x4> gameWorld,
        IReadOnlyList<Matrix4x4> rigWorld,
        IReadOnlyList<List<int>> feeders,
        float scale,
        out List<string> placed,
        out List<string> derived)
    {
        placed = new List<string>();
        derived = new List<string>();
        var fitted = new Matrix4x4[game.Joints.Count];
        for (var index = 0; index < game.Joints.Count; index++)
        {
            var joint = game.Joints[index];
            var rotation = Rotation(gameWorld[index]);
            if (feeders[index].Count != 0)
            {
                fitted[index] = Compose(rotation, Translation(rigWorld[feeders[index][0]]));
                placed.Add(joint.Name);
                continue;
            }

            var parent = joint.ParentIndex;
            if (parent < 0 || parent >= index)
            {
                fitted[index] = Compose(rotation, Translation(gameWorld[index]) * scale);
            }
            else
            {
                var offset = (Translation(gameWorld[index]) - Translation(gameWorld[parent])) * scale;
                fitted[index] = Compose(rotation, Translation(fitted[parent]) + offset);
            }
            if (joint.Name.Length != 0) derived.Add(joint.Name);
        }
        return fitted;
    }

    /// <summary>
    /// How much bigger this character is than the game's. Taken from how far the
    /// mapped joints spread, so it holds for a child and for a giant alike.
    /// </summary>
    private static float Scale(
        IReadOnlyList<Matrix4x4> rigWorld,
        IReadOnlyList<Matrix4x4> gameWorld,
        IReadOnlyList<List<int>> feeders)
    {
        var rig = 0f;
        var game = 0f;
        for (var index = 0; index < feeders.Count; index++)
        {
            if (feeders[index].Count == 0) continue;
            rig = Math.Max(rig, Translation(rigWorld[feeders[index][0]]).Length());
            game = Math.Max(game, Translation(gameWorld[index]).Length());
        }
        return game <= 1e-6f ? 1f : rig / game;
    }

    private static PhyreVertexSource Rebind(
        PhyreVertexSource vertex, IReadOnlyList<int> destination, ref float dropped)
    {
        // Weights are summed per target: two rig bones onto one game bone means
        // one weight, not two entries fighting for the same slot.
        var gathered = new Dictionary<int, float>();
        var total = 0f;
        for (var slot = 0; slot < vertex.Joints.Length && slot < vertex.Weights.Length; slot++)
        {
            var weight = vertex.Weights[slot];
            if (weight <= 0f) continue;
            total += weight;
            var joint = vertex.Joints[slot];
            var target = joint >= 0 && joint < destination.Count ? destination[joint] : -1;
            if (target < 0) continue;
            gathered[target] = gathered.GetValueOrDefault(target) + weight;
        }

        var kept = gathered.OrderByDescending(entry => entry.Value).Take(JointsPerVertex).ToArray();
        var sum = kept.Sum(entry => entry.Value);
        if (total > 0f) dropped = Math.Max(dropped, (total - sum) / total);

        var joints = new int[JointsPerVertex];
        var weights = new float[JointsPerVertex];
        for (var slot = 0; slot < kept.Length; slot++)
        {
            joints[slot] = kept[slot].Key;
            weights[slot] = sum > 0f ? kept[slot].Value / sum : 0f;
        }
        return vertex with { Joints = joints, Weights = weights };
    }

    private static Matrix4x4[] WorldTransforms(IReadOnlyList<(int Parent, Matrix4x4 Local)> joints)
    {
        var world = new Matrix4x4[joints.Count];
        for (var index = 0; index < joints.Count; index++)
        {
            var (parent, local) = joints[index];
            world[index] = parent < 0 || parent >= index
                ? local
                : Matrix4x4.Multiply(world[parent], local);
        }
        return world;
    }

    private static Matrix4x4 Rotation(Matrix4x4 value)
    {
        var x = Safe(new Vector3(value.M11, value.M12, value.M13), Vector3.UnitX);
        var y = Safe(new Vector3(value.M21, value.M22, value.M23), Vector3.UnitY);
        var z = Safe(new Vector3(value.M31, value.M32, value.M33), Vector3.UnitZ);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    private static Vector3 Safe(Vector3 value, Vector3 fallback)
        => value.LengthSquared() <= 1e-12f ? fallback : Vector3.Normalize(value);

    private static Vector3 Translation(Matrix4x4 value) => new(value.M41, value.M42, value.M43);

    private static Matrix4x4 Compose(Matrix4x4 rotation, Vector3 translation)
    {
        rotation.M41 = translation.X;
        rotation.M42 = translation.Y;
        rotation.M43 = translation.Z;
        return rotation;
    }

    private static Matrix4x4 Invert(Matrix4x4 value)
        => Matrix4x4.Invert(value, out var inverted) ? inverted : Matrix4x4.Identity;
}
