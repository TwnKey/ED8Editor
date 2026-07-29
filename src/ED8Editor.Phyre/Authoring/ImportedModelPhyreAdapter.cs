using System.Numerics;
using ED8Editor.Models;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Explicit target policy for the boundary between a lossless imported scene
/// and CS1's model geometry contract.
/// </summary>
public sealed record ImportedModelPhyreOptions(
    int MaximumVertexInfluences = 4,
    int TextureCoordinateSetCount = 1,
    bool ConvertUnitsToMeters = true);

/// <summary>
/// Adapts the format-neutral import graph to the source object consumed by the
/// Phyre writer. The importer itself never drops weights or UV sets; the limits
/// imposed here are target-format decisions and are therefore visible.
/// </summary>
public static class ImportedModelPhyreAdapter
{
    public static PhyreModelSource Convert(
        ImportedModelScene imported,
        ImportedModelPhyreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imported);
        options ??= new ImportedModelPhyreOptions();
        if (options.MaximumVertexInfluences is < 1 or > 4)
            throw new ArgumentOutOfRangeException(
                nameof(options), "CS1 vertices support between one and four influences.");
        if (options.TextureCoordinateSetCount < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), "At least one texture-coordinate set is required.");
        var errors = ImportedModelValidator.Validate(imported)
            .Where(value => value.Severity == ImportedDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidDataException(
                "The imported model is invalid: "
                + string.Join("; ", errors.Select(value => value.Message)));

        var scale = options.ConvertUnitsToMeters
            ? imported.CoordinateSystem.UnitScaleMeters
            : 1f;
        var joints = imported.Nodes.Select(node => new PhyreJointSource(
            node.Name,
            node.ParentIndex,
            ScaleTranslation(node.LocalTransform, scale),
            Matrix4x4.Identity)).ToArray();
        var inverseBindByNode = new Dictionary<int, Matrix4x4>();
        foreach (var binding in imported.Meshes
                     .Where(mesh => mesh.Skin is not null)
                     .SelectMany(mesh => mesh.Skin!.InverseBindMatrices))
        {
            var scaled = ScaleTranslation(binding.Value, scale);
            if (inverseBindByNode.TryGetValue(binding.Key, out var prior)
                && !ApproximatelyEqual(prior, scaled))
            {
                throw new InvalidDataException(
                    $"Node '{imported.Nodes[binding.Key].Name}' has different inverse"
                    + " bind matrices in separate meshes; the current Phyre model"
                    + " source has one bind matrix per joint.");
            }
            inverseBindByNode[binding.Key] = scaled;
        }
        for (var index = 0; index < joints.Length; index++)
        {
            joints[index] = joints[index] with
            {
                InverseBindTransform = inverseBindByNode.GetValueOrDefault(
                    index, Matrix4x4.Identity),
            };
        }

        var meshes = imported.Meshes.Select(mesh =>
        {
            var materialName = (uint)mesh.MaterialIndex < imported.Materials.Count
                ? imported.Materials[mesh.MaterialIndex].Name
                : $"material_{mesh.MaterialIndex}";
            var vertices = mesh.Vertices.Select(vertex =>
            {
                var influences = vertex.Influences
                    .Where(value => value.Weight > 0f)
                    .OrderByDescending(value => value.Weight)
                    .Take(options.MaximumVertexInfluences)
                    .ToArray();
                var total = influences.Sum(value => value.Weight);
                var jointIndices = influences.Select(value => value.NodeIndex).ToArray();
                var weights = total > 0f
                    ? influences.Select(value => value.Weight / total).ToArray()
                    : Array.Empty<float>();
                var texCoords = Enumerable.Range(0, options.TextureCoordinateSetCount)
                    .Select(index => new PhyreTexCoordSet(
                        index < vertex.TexCoords.Count
                            ? vertex.TexCoords[index]
                            : Vector2.Zero,
                        index == 0 ? vertex.Tangent : Vector3.Zero,
                        index == 0 ? vertex.Bitangent : Vector3.Zero))
                    .ToArray();
                return new PhyreVertexSource(
                    vertex.Position * scale,
                    vertex.Normal,
                    texCoords,
                    jointIndices,
                    weights);
            }).ToArray();
            return new PhyreMeshSource(materialName, vertices, mesh.Indices);
        }).ToArray();
        return new PhyreModelSource(imported.Name, meshes, joints);
    }

    private static Matrix4x4 ScaleTranslation(Matrix4x4 matrix, float scale)
    {
        matrix.M41 *= scale;
        matrix.M42 *= scale;
        matrix.M43 *= scale;
        return matrix;
    }

    private static bool ApproximatelyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        const float epsilon = 1e-5f;
        return MathF.Abs(left.M11 - right.M11) <= epsilon
            && MathF.Abs(left.M12 - right.M12) <= epsilon
            && MathF.Abs(left.M13 - right.M13) <= epsilon
            && MathF.Abs(left.M14 - right.M14) <= epsilon
            && MathF.Abs(left.M21 - right.M21) <= epsilon
            && MathF.Abs(left.M22 - right.M22) <= epsilon
            && MathF.Abs(left.M23 - right.M23) <= epsilon
            && MathF.Abs(left.M24 - right.M24) <= epsilon
            && MathF.Abs(left.M31 - right.M31) <= epsilon
            && MathF.Abs(left.M32 - right.M32) <= epsilon
            && MathF.Abs(left.M33 - right.M33) <= epsilon
            && MathF.Abs(left.M34 - right.M34) <= epsilon
            && MathF.Abs(left.M41 - right.M41) <= epsilon
            && MathF.Abs(left.M42 - right.M42) <= epsilon
            && MathF.Abs(left.M43 - right.M43) <= epsilon
            && MathF.Abs(left.M44 - right.M44) <= epsilon;
    }
}
