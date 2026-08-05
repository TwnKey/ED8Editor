using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Binds a mesh with no skin of its own to a skeleton, by where its vertices
/// sit rather than by any weight an author painted — because there is none.
///
/// Each vertex takes the two nearest bone segments of the skeleton's rest
/// pose, weighted by inverse distance. A segment is one joint's local origin to
/// its parent's, which is the geometry a bone actually occupies; the joint
/// itself is what carries the weight, matching how <see cref="PhyreRigTransfer"/>
/// addresses a bone by the joint at its far end. Two bones rather than one
/// keeps a strap across a seam from creasing where the nearest bone changes.
///
/// The result is addressed in the same space <see cref="PhyreRigTransfer"/>
/// leaves its output in — hierarchy joint indices of <paramref name="game"/> —
/// so both feed the same next step: translating hierarchy indices into
/// whichever segment of the target model they end up written into.
/// </summary>
public static class PhyreProximitySkinBinder
{
    /// <summary>
    /// Scales and moves <paramref name="model"/> so its standing height matches
    /// <paramref name="game"/>'s, feet at the same level as the skeleton's
    /// lowest joint. A vertex bound by proximity means nothing until the two
    /// are roughly the same size; an import is never guaranteed to already be.
    /// </summary>
    public static PhyreModelSource FitToHeight(PhyreModelSource model, CpuSkeleton game)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(game);
        var vertices = model.Meshes.SelectMany(mesh => mesh.Vertices).ToArray();
        if (vertices.Length == 0) return model;

        var gameWorld = WorldTransforms(
            game.Joints.Select(joint => (joint.ParentIndex, joint.DefaultLocalTransform)).ToArray());
        // Measured over the same joints Bind will use, and for the same reason: an
        // attachment locator sits wherever a weapon has to hang, which can be well
        // clear of the body, and a height taken from one would shrink the import to
        // fit a point no geometry ever reaches.
        var excluded = DefaultExcluded(game);
        var deforming = Enumerable.Range(0, game.Joints.Count)
            .Where(index => !excluded.Contains(game.Joints[index].Name))
            .Select(index => gameWorld[index].Translation.Y)
            .ToArray();
        if (deforming.Length == 0) return model;
        var gameLow = deforming.Min();
        var gameHigh = deforming.Max();
        var gameHeight = gameHigh - gameLow;

        var modelLow = vertices.Min(vertex => vertex.Position.Y);
        var modelHigh = vertices.Max(vertex => vertex.Position.Y);
        var modelHeight = modelHigh - modelLow;
        if (gameHeight <= 1e-6f || modelHeight <= 1e-6f) return model;

        var scale = gameHeight / modelHeight;
        var offset = new Vector3(0f, gameLow - modelLow * scale, 0f);
        var meshes = model.Meshes.Select(mesh => mesh with
        {
            Vertices = mesh.Vertices
                .Select(vertex => vertex with { Position = vertex.Position * scale + offset })
                .ToArray(),
        }).ToArray();
        return model with { Meshes = meshes };
    }

    /// <summary>
    /// Binds every vertex of <paramref name="model"/> to the nearest one or two
    /// bones of <paramref name="game"/>'s rest pose. <paramref name="excluded"/>
    /// names joints that should never receive a bind — the attachment locators
    /// and anything else that does not deform geometry.
    /// </summary>
    public static PhyreModelSource Bind(
        PhyreModelSource model, CpuSkeleton game, IReadOnlySet<string>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(game);
        excluded ??= DefaultExcluded(game);

        var world = WorldTransforms(
            game.Joints.Select(joint => (joint.ParentIndex, joint.DefaultLocalTransform)).ToArray());
        var segments = new List<(int Joint, Vector3 From, Vector3 To)>();
        for (var index = 0; index < game.Joints.Count; index++)
        {
            if (excluded.Contains(game.Joints[index].Name)) continue;
            var parent = game.Joints[index].ParentIndex;
            var from = parent >= 0 && parent < world.Count
                ? world[parent].Translation
                : world[index].Translation;
            segments.Add((index, from, world[index].Translation));
        }
        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                "The reference skeleton has no joint left to bind to once the excluded ones are removed.");
        }

        var meshes = model.Meshes.Select(mesh => mesh with
        {
            Vertices = mesh.Vertices.Select(vertex => BindOne(vertex, segments)).ToArray(),
        }).ToArray();
        return model with { Meshes = meshes };
    }

    /// <summary>
    /// Attachment locators — names ending in the game's own conventions for
    /// one — excluded by default because binding cloth or skin to a point
    /// meant for hanging a weapon looks wrong wherever it deforms.
    /// </summary>
    private static IReadOnlySet<string> DefaultExcluded(CpuSkeleton game) => game.Joints
        .Select(joint => joint.Name)
        .Where(name => name.EndsWith("_point", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_point_", StringComparison.OrdinalIgnoreCase))
        .ToHashSet(StringComparer.Ordinal);

    private static PhyreVertexSource BindOne(
        PhyreVertexSource vertex, IReadOnlyList<(int Joint, Vector3 From, Vector3 To)> segments)
    {
        var best = (Joint: -1, Distance: float.MaxValue);
        var second = (Joint: -1, Distance: float.MaxValue);
        foreach (var (joint, from, to) in segments)
        {
            var distance = DistanceToSegment(vertex.Position, from, to);
            if (distance < best.Distance)
            {
                second = best;
                best = (joint, distance);
            }
            else if (distance < second.Distance)
            {
                second = (joint, distance);
            }
        }

        if (second.Joint < 0)
        {
            return vertex with { Joints = new[] { best.Joint, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } };
        }
        // Inverse-square falloff: close to a joint, that joint all but owns the
        // vertex; midway between two, the blend is close to even.
        var weightA = 1f / MathF.Max(best.Distance * best.Distance, 1e-6f);
        var weightB = 1f / MathF.Max(second.Distance * second.Distance, 1e-6f);
        var total = weightA + weightB;
        return vertex with
        {
            Joints = new[] { best.Joint, second.Joint, 0, 0 },
            Weights = new[] { weightA / total, weightB / total, 0f, 0f },
        };
    }

    private static float DistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        var span = to - from;
        var length = span.LengthSquared();
        if (length <= 1e-10f) return Vector3.Distance(point, from);
        var t = Math.Clamp(Vector3.Dot(point - from, span) / length, 0f, 1f);
        return Vector3.Distance(point, from + span * t);
    }

    private static IReadOnlyList<(Vector3 Translation, Matrix4x4 World)> WorldTransforms(
        IReadOnlyList<(int Parent, Matrix4x4 Local)> joints)
    {
        var world = new Matrix4x4[joints.Count];
        var result = new (Vector3, Matrix4x4)[joints.Count];
        for (var index = 0; index < joints.Count; index++)
        {
            var (parent, local) = joints[index];
            world[index] = parent < 0 || parent >= index
                ? local
                : Matrix4x4.Multiply(world[parent], local);
            result[index] = (new Vector3(world[index].M41, world[index].M42, world[index].M43), world[index]);
        }
        return result;
    }
}
