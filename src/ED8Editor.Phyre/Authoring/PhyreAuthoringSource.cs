using System.Numerics;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// An image to write as a texture cluster. Pixels are straight RGBA rows, top
/// row first; the writer is what flips them, because that is a property of the
/// file and not of whoever hands the image over.
/// </summary>
public sealed record PhyreTextureSource(
    string AssetName,
    int Width,
    int Height,
    string Format,
    byte[] Rgba);

/// <summary>A texture coordinate and the tangent frame that goes with it.</summary>
/// <param name="Bitangent">
/// Asked for rather than worked out, and written exactly as given. Rebuilding it
/// as the cross product of the normal and the tangent does not give back what
/// the game stores — measured, 0 of 16 streams — and the game writes zeros for a
/// set a mesh does not use, so standing a value in there would fill an unused
/// set with noise.
/// </param>
public sealed record PhyreTexCoordSet(
    Vector2 TexCoord,
    Vector3 Tangent,
    Vector3 Bitangent = default);

/// <summary>One vertex of a mesh being brought in.</summary>
/// <param name="Joints">Up to four skeleton joints this vertex follows.</param>
/// <param name="Weights">How much of each joint it follows, summing to one.</param>
/// <param name="TexCoords">
/// One entry per set of texture coordinates, each with its own tangent frame.
/// The game's character meshes carry four — that is what sixteen streams a mesh
/// come to, next to position, normal and the two skinning streams — and a frame
/// belongs to a set rather than to the vertex, since it is derived from that
/// set's own layout.
/// </param>
public sealed record PhyreVertexSource(
    Vector3 Position,
    Vector3 Normal,
    IReadOnlyList<PhyreTexCoordSet> TexCoords,
    int[] Joints,
    float[] Weights);

/// <summary>
/// One run of triangles drawn with one material, with the vertices it uses.
///
/// The vertices belong to the mesh, not to the model: that is how the game
/// stores them — ply000 holds sixteen of these, each with its own streams and
/// its own vertex count — and it is also what an exporter produces, one mesh per
/// material. Sharing one vertex buffer across materials would be a conversion
/// in both directions, so it is not asked for.
/// </summary>
public sealed record PhyreMeshSource(
    string MaterialName,
    IReadOnlyList<PhyreVertexSource> Vertices,
    int[] Indices);

/// <summary>A joint of the skeleton a mesh is skinned to.</summary>
public sealed record PhyreJointSource(
    string Name,
    int ParentIndex,
    Matrix4x4 LocalTransform,
    Matrix4x4 InverseBindTransform);

/// <summary>
/// A model to write as a model cluster: what an import produces, stripped of
/// everything the exchange file adds.
///
/// This is the boundary of the black box. Whoever reads the FBX — or the glTF,
/// or anything else — fills this in; nothing here knows about any of them, and
/// nothing here knows about the editor.
/// </summary>
public sealed record PhyreModelSource(
    string AssetName,
    IReadOnlyList<PhyreMeshSource> Meshes,
    IReadOnlyList<PhyreJointSource> Joints)
{
    /// <summary>Whether the model follows a skeleton.</summary>
    public bool IsSkinned => Joints.Count > 0;

    /// <summary>
    /// Reports what would stop this model from being written, so an importer can
    /// say so before anything is produced.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Meshes.Count == 0) problems.Add("the model has no meshes");
        foreach (var mesh in Meshes)
        {
            if (mesh.Vertices.Count == 0)
            {
                problems.Add($"mesh '{mesh.MaterialName}' has no vertices");
            }
            if (mesh.Indices.Length % 3 != 0)
            {
                problems.Add($"mesh '{mesh.MaterialName}' has a partial triangle");
            }
            if (mesh.Indices.Any(index => index < 0 || index >= mesh.Vertices.Count))
            {
                problems.Add($"mesh '{mesh.MaterialName}' points at a vertex that is not there");
            }
            foreach (var vertex in mesh.Vertices)
            {
                if (vertex.Joints.Length != vertex.Weights.Length)
                {
                    problems.Add($"a vertex of '{mesh.MaterialName}' has more joints than weights");
                    break;
                }
                if (vertex.Joints.Any(joint => joint < 0 || joint >= Joints.Count))
                {
                    problems.Add(
                        $"a vertex of '{mesh.MaterialName}' follows a joint the skeleton does not have");
                    break;
                }
            }
        }
        for (var index = 0; index < Joints.Count; index++)
        {
            if (Joints[index].ParentIndex >= index)
            {
                problems.Add($"joint '{Joints[index].Name}' comes before its own parent");
            }
        }
        return problems;
    }
}
