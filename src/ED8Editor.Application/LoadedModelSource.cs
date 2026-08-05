using System.Numerics;
using ED8Editor.Core;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>
/// Reads a model the editor has already loaded back into the form the writers take.
///
/// This is what lets a shader be changed without a new model being imported: the
/// package is written again, from geometry that is read out and written back rather
/// than copied byte for byte, with the new material bindings in place. It is the
/// same direction an import goes, so a model that survives this survives an import
/// too — and the reverse.
///
/// A mesh's node transform is baked in, exactly as the importer bakes an assimp
/// node's chain. The writers place every vertex in the model's own space; a mesh
/// left in its node's space would be written at the origin instead of where it sits.
/// </summary>
public static class LoadedModelSource
{
    /// <summary>
    /// The loaded model as authoring geometry, or null when something in it is not
    /// readable this way.
    /// </summary>
    /// <param name="problems">
    /// What stopped it, when it did — said plainly, since the answer is a decision
    /// the author has to make rather than a fault to hide.
    /// </param>
    public static PhyreModelSource? From(
        CpuModel model,
        string assetName,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = new List<string>();
        var meshes = new List<PhyreMeshSource>();

        for (var index = 0; index < model.Meshes.Count; index++)
        {
            var mesh = model.Meshes[index];
            foreach (var primitive in mesh.Primitives)
            {
                var material = primitive.MaterialIndex >= 0
                    && primitive.MaterialIndex < model.Materials.Count
                        ? model.Materials[primitive.MaterialIndex].Name
                        : "material" + index;
                var source = PhyreMeshSourceReader.ReadVerbatim(primitive, material);
                if (source is null)
                {
                    found.Add($"'{mesh.Name}' does not carry the vertex streams a written"
                        + " model needs, so it cannot be read back.");
                    continue;
                }
                meshes.Add(Placed(source, mesh.LocalTransform));
            }
        }

        if (model.Skeleton is not null && model.Skeleton.Joints.Count != 0)
        {
            found.Add("The model follows a skeleton; reading it back this way would"
                + " drop the skinning, so it is refused rather than flattened.");
        }
        if (meshes.Count == 0) found.Add("Nothing in the model could be read back.");

        problems = found;
        return found.Count == 0
            ? new PhyreModelSource(assetName, meshes, Array.Empty<PhyreJointSource>())
            : null;
    }

    /// <summary>The mesh in the model's space rather than its node's.</summary>
    private static PhyreMeshSource Placed(PhyreMeshSource mesh, Matrix4x4 transform)
    {
        if (transform.IsIdentity) return mesh;
        // Normals and the tangent frame follow the inverse transpose, which is the
        // same matrix only while the transform has no non-uniform scale.
        var normals = Matrix4x4.Invert(transform, out var inverted)
            ? Matrix4x4.Transpose(inverted)
            : transform;
        return mesh with
        {
            Vertices = mesh.Vertices
                .Select(vertex => vertex with
                {
                    Position = Vector3.Transform(vertex.Position, transform),
                    Normal = Vector3.Normalize(
                        Vector3.TransformNormal(vertex.Normal, normals)),
                    TexCoords = vertex.TexCoords
                        .Select(set => set with
                        {
                            Tangent = Vector3.TransformNormal(set.Tangent, normals),
                            Bitangent = Vector3.TransformNormal(set.Bitangent, normals),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
    }
}
